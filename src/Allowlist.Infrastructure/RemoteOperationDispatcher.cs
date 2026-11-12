using System.Diagnostics;
using System.Text.Json;
using Allowlist.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Allowlist.Infrastructure;

public sealed class RemoteOperationDispatcher(IServiceScopeFactory scopes, TimeProvider clock,
    ILogger<RemoteOperationDispatcher> logger) : BackgroundService
{
    private static readonly Action<ILogger, string, Exception?> LogCycleFailure = LoggerMessage.Define<string>(LogLevel.Error, new EventId(1, "DispatchCycleFailure"),
        "Remote dispatch cycle failed for worker {WorkerId}; durable operations will be recovered.");
    public string WorkerId { get; } = Guid.NewGuid().ToString("N");

    public async Task<IReadOnlyList<Guid>> ClaimAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AllowlistDbContext>();
        var now = clock.GetUtcNow().UtcDateTime;
        var lease = now.AddSeconds(60);
        var candidates = await db.Operations.AsNoTracking().Where(x =>
            (x.State == OperationState.Pending && x.NextAttemptAt <= now) ||
            (x.State == OperationState.InFlight && x.LeaseUntil < now))
            .OrderBy(x => x.CreatedAt).Select(x => x.Id).Take(20).ToListAsync(ct);
        foreach (var id in candidates)
            await db.Operations.Where(x => x.Id == id &&
                ((x.State == OperationState.Pending && x.NextAttemptAt <= now) || (x.State == OperationState.InFlight && x.LeaseUntil < now)))
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.State, OperationState.InFlight)
                    .SetProperty(x => x.ClaimedBy, WorkerId).SetProperty(x => x.LeaseUntil, lease), ct);
        var claimed = await db.Operations.Where(x => candidates.Contains(x.Id) && x.ClaimedBy == WorkerId && x.LeaseUntil == lease)
            .ToListAsync(ct);
        foreach (var op in claimed) Audit.Write(db, new(WorkerId, ActorType.System, op.Id.ToString()),
            "operation.claimed", "RemoteOperation", op.Id, null, new { op.State, op.ClaimedBy, op.LeaseUntil }, now);
        await db.SaveChangesAsync(ct);
        return claimed.Select(x => x.Id).ToArray();
    }

    public async Task RunOnceAsync(CancellationToken ct)
    {
        var ids = await ClaimAsync(ct);
        await Task.WhenAll(ids.Select(id => ProcessAsync(id, ct)));
    }

    public async Task ProcessAsync(Guid id, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AllowlistDbContext>();
        var client = scope.ServiceProvider.GetRequiredService<ICloudflareListClient>();
        var settings = scope.ServiceProvider.GetRequiredService<IOptions<CloudflareOptions>>().Value;
        var op = await db.Operations.SingleAsync(x => x.Id == id, ct);
        if (op.ClaimedBy != WorkerId || op.State != OperationState.InFlight || op.LeaseUntil <= clock.GetUtcNow().UtcDateTime) return;
        var entry = op.EntryId is { } entryId ? await db.Entries.SingleAsync(x => x.Id == entryId, ct) : null;
        var mutationAccepted = false;
        var bulkFailed = false;
        var remoteRemovalObserved = false;
        if (await db.DispatchLogs.AnyAsync(x => x.OperationId == id && x.Phase == DispatchPhase.Dispatching && x.Attempt == op.Attempts + 1, ct))
        {
            await TransitionAsync(OperationState.Uncertain, "Recovered a dispatch whose result was not persisted.", null);
            return;
        }
        try
        {
            var remote = await client.GetItemsAsync(op.ListId, ct);
            var matches = remote.Where(x => Normalize(x.Cidr) == entry?.Cidr).ToArray();
            remoteRemovalObserved = op.Action == OperationAction.Remove && matches.Length == 0;
            var keepSharedRule = entry is not null && op.Action == OperationAction.Remove && await db.Entries.AnyAsync(
                x => x.EnvironmentId == entry.EnvironmentId && x.Kind == entry.Kind && x.Id != entry.Id && x.Cidr == entry.Cidr &&
                (x.State == EntryState.Active || x.State == EntryState.PendingAdd), ct);
            if ((op.Action == OperationAction.Add && matches.Length > 0) ||
                (op.Action == OperationAction.Remove && (keepSharedRule || (entry is not null ? matches.Length == 0 : !remote.Any(x => x.Id == op.RemoteItemId)))))
            {
                await TransitionAsync(OperationState.Succeeded, "Desired remote state already observed.", matches.FirstOrDefault()?.Id);
                return;
            }
            if (!await RenewAsync()) return;
            db.DispatchLogs.Add(new() { OperationId = id, Attempt = op.Attempts + 1, Phase = DispatchPhase.Dispatching,
                Detail = "Mutation intent persisted before the remote call.", At = clock.GetUtcNow().UtcDateTime });
            await db.SaveChangesAsync(ct);
            op.RemoteOperationId = op.Action == OperationAction.Add
                ? await client.AddItemsAsync(op.ListId, [new(entry!.Cidr, entry.Description)], ct)
                : await client.DeleteItemsAsync(op.ListId,
                    entry is null ? [op.RemoteItemId!] : matches.Select(x => x.Id).ToArray(), ct);
            mutationAccepted = true;
            db.DispatchLogs.Add(new() { OperationId = id, Attempt = op.Attempts + 1, Phase = DispatchPhase.Accepted,
                Detail = "Remote bulk operation accepted.", At = clock.GetUtcNow().UtcDateTime });
            await db.SaveChangesAsync(ct);
            var deadline = clock.GetUtcNow().AddSeconds(30);
            while (clock.GetUtcNow() < deadline)
            {
                if (!await RenewAsync()) return;
                var bulk = await client.GetBulkOperationAsync(op.RemoteOperationId, ct);
                if (bulk.State == BulkState.Failed)
                { bulkFailed = true; throw new RemoteRejectedException("Remote bulk operation failed."); }
                if (bulk.State == BulkState.Completed)
                {
                    remoteRemovalObserved = op.Action == OperationAction.Remove;
                    var items = await client.GetItemsAsync(op.ListId, ct);
                    var remoteId = items.FirstOrDefault(x => Normalize(x.Cidr) == entry?.Cidr)?.Id;
                    if (op.Action == OperationAction.Add && remoteId is null)
                        throw new HttpRequestException("Completed operation is not yet visible.");
                    await TransitionAsync(OperationState.Succeeded, "Remote bulk operation completed.", remoteId);
                    return;
                }
                await Task.Delay(TimeSpan.FromMilliseconds(250), clock, ct);
            }
            await TransitionAsync(OperationState.Uncertain, "Bulk operation polling timed out.", null);
        }
        catch (RemoteRejectedException)
        {
            if (mutationAccepted && !bulkFailed)
                await TransitionAsync(OperationState.Uncertain, "Remote operation was accepted but subsequent observation failed.", null);
            else
            {
                op.Attempts++;
                await TransitionAsync(op.Attempts >= settings.MaxAttempts ? OperationState.DeadLetter : OperationState.Pending,
                    "Remote operation was explicitly rejected.", null);
            }
        }
        catch (Exception ex) when (ex is TaskCanceledException or HttpRequestException or JsonException)
        {
            ct.ThrowIfCancellationRequested();
            await TransitionAsync(OperationState.Uncertain, "Remote result could not be determined.", null);
        }

        async Task<bool> RenewAsync()
        {
            var now = clock.GetUtcNow().UtcDateTime;
            return await db.Operations.Where(x => x.Id == id && x.State == OperationState.InFlight && x.ClaimedBy == WorkerId && x.LeaseUntil > now)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.LeaseUntil, now.AddSeconds(60)), ct) == 1;
        }
        async Task TransitionAsync(OperationState state, string detail, string? remoteId)
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            if (!await RenewAsync()) return;
            var now = clock.GetUtcNow().UtcDateTime;
            var before = JsonSerializer.Serialize(new { op.State });
            op.State = state;
            op.ClaimedBy = null;
            op.LeaseUntil = null;
            op.LastError = state == OperationState.Succeeded ? null : detail;
            if (state == OperationState.Pending)
            {
                op.NextAttemptAt = now.AddSeconds(Math.Min(600, 5 * Math.Pow(2, op.Attempts)));
                op.RemoteOperationId = null;
            }
            if (state is OperationState.Succeeded or OperationState.DeadLetter) op.CompletedAt = now;
            if (entry is not null)
            {
                if (state == OperationState.Succeeded)
                {
                    entry.State = op.Action == OperationAction.Add ? EntryState.Active : EntryState.Removed;
                    entry.RemoteItemId = op.Action == OperationAction.Add ? remoteId : null;
                }
                else if (state == OperationState.DeadLetter) entry.State = EntryState.Failed;
                entry.UpdatedAt = now;
                if (state == OperationState.Succeeded && remoteRemovalObserved)
                    await EntryService.QueueSharedRepairAsync(db, entry, op.ListId, now, ct);
            }
            db.DispatchLogs.Add(new() { OperationId = id, Attempt = op.Attempts + (state == OperationState.Pending || state == OperationState.DeadLetter ? 0 : 1),
                Phase = state == OperationState.Succeeded ? DispatchPhase.Completed : state == OperationState.Uncertain ? DispatchPhase.Unknown : DispatchPhase.Failed,
                Detail = detail, At = now });
            Audit.Write(db, new(WorkerId, ActorType.System, id.ToString()), "operation." + state.ToString().ToLowerInvariant(),
                "RemoteOperation", id, before, new { op.State, op.Attempts }, now);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
    }
    public static string Normalize(string cidr) => CidrNormalizer.TryNormalize(cidr, out var network, out _) ? network.ToString() : cidr;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await RunOnceAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception) { LogCycleFailure(logger, WorkerId, null); }
            await Task.Delay(TimeSpan.FromSeconds(2), clock, stoppingToken);
        }
    }
}
