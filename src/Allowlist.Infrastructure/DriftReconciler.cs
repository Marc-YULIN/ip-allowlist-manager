using System.Text.Json;
using Allowlist.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Allowlist.Infrastructure;

public sealed class DriftReconciler(AllowlistDbContext db, ICloudflareListClient client, TimeProvider clock, IOptions<CloudflareOptions> options)
{
    public async Task<IReadOnlyList<DriftSnapshot>> ReconcileAsync(Guid environmentId, bool autoFix, Actor actor, CancellationToken ct)
    {
        var env = await db.Environments.SingleOrDefaultAsync(x => x.Id == environmentId, ct)
            ?? throw new DomainException("environment_not_found", "Environment not found.", 404);
        await ResolveUncertainAsync(environmentId, ct);
        var allowItems = await client.GetItemsAsync(env.AllowListId, ct);
        var blockItems = await client.GetItemsAsync(env.BlockListId, ct);
        await using var tx = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, ct);
        var now = clock.GetUtcNow().UtcDateTime;
        var snapshots = new List<DriftSnapshot>();
        foreach (var (kind, listId, remote) in new[] { (EntryKind.Allow, env.AllowListId, allowItems), (EntryKind.Block, env.BlockListId, blockItems) })
        {
            var local = await db.Entries.Where(x => x.EnvironmentId == environmentId && x.Kind == kind && x.State == EntryState.Active).ToListAsync(ct);
            var inFlightCidrs = (await db.Entries.Where(x => x.EnvironmentId == environmentId && x.Kind == kind &&
                (x.State == EntryState.PendingAdd || x.State == EntryState.PendingRemove || db.Operations.Any(op => op.EntryId == x.Id &&
                    (op.State == OperationState.Pending || op.State == OperationState.InFlight || op.State == OperationState.Uncertain))))
                .Select(x => x.Cidr).ToListAsync(ct)).ToHashSet(StringComparer.Ordinal);
            var removingItemIds = await db.Operations.Where(x => x.EnvironmentId == environmentId && x.ListId == listId &&
                x.EntryId == null && x.Action == OperationAction.Remove &&
                (x.State == OperationState.Pending || x.State == OperationState.InFlight || x.State == OperationState.Uncertain))
                .Select(x => x.RemoteItemId).ToListAsync(ct);
            inFlightCidrs.UnionWith(remote.Where(x => removingItemIds.Contains(x.Id)).Select(x => x.Cidr));
            var drift = DriftCalculator.Compute(local, remote, inFlightCidrs);
            var snapshot = new DriftSnapshot { EnvironmentId = environmentId, ListKind = kind, TakenAt = now, LocalCount = local.Count,
                RemoteCount = remote.Count, MissingRemoteJson = JsonSerializer.Serialize(drift.Missing), ExtraRemoteJson = JsonSerializer.Serialize(drift.Extra), AutoFixed = autoFix };
            db.Snapshots.Add(snapshot);
            snapshots.Add(snapshot);
            if (autoFix)
            {
                foreach (var cidr in drift.Missing)
                    foreach (var entry in local.Where(x => x.Cidr == cidr))
                        if (!await HasOpenAsync(entry.Id, ct))
                        {
                            entry.State = EntryState.PendingAdd;
                            entry.UpdatedAt = now;
                            db.Operations.Add(new() { EnvironmentId = environmentId, ListId = listId, EntryId = entry.Id, Action = OperationAction.Add,
                                State = OperationState.Pending, CreatedAt = now, NextAttemptAt = now });
                        }
                if (options.Value.AllowRemoveUnknown)
                    foreach (var item in drift.Extra)
                        if (!await db.Entries.AnyAsync(x => x.EnvironmentId == environmentId && x.Kind == kind && x.Cidr == RemoteOperationDispatcher.Normalize(item.Cidr) &&
                            x.State != EntryState.Removed, ct) && !await db.Operations.AnyAsync(x => x.EnvironmentId == environmentId && x.ListId == listId &&
                            x.RemoteItemId == item.Id && x.State != OperationState.Succeeded && x.State != OperationState.DeadLetter, ct))
                            db.Operations.Add(new() { EnvironmentId = environmentId, ListId = listId, RemoteItemId = item.Id, Action = OperationAction.Remove,
                                State = OperationState.Pending, CreatedAt = now, NextAttemptAt = now });
            }
            Audit.Write(db, actor, "drift.reconciled", "ManagedEnvironment", environmentId, null,
                new { snapshot.Id, snapshot.ListKind, ListId = listId, Missing = drift.Missing.Count, Extra = drift.Extra.Count, AutoFixRequested = autoFix }, now);
        }
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return snapshots;
    }

    private Task<bool> HasOpenAsync(Guid id, CancellationToken ct) => db.Operations.AnyAsync(x => x.EntryId == id &&
        x.State != OperationState.Succeeded && x.State != OperationState.DeadLetter && x.State != OperationState.Failed, ct);

    public async Task ResolveUncertainAsync(Guid environmentId, CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var ids = await db.Operations.Where(x => x.EnvironmentId == environmentId && x.State == OperationState.Uncertain &&
            (x.LeaseUntil == null || x.LeaseUntil < now)).Select(x => x.Id).ToListAsync(ct);
        foreach (var id in ids)
        {
            var owner = Guid.NewGuid().ToString("N");
            if (await db.Operations.Where(x => x.Id == id && x.State == OperationState.Uncertain && (x.LeaseUntil == null || x.LeaseUntil < now))
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.ClaimedBy, owner).SetProperty(x => x.LeaseUntil, now.AddSeconds(60)), ct) != 1) continue;
            var op = await db.Operations.SingleAsync(x => x.Id == id, ct);
            await db.Entry(op).ReloadAsync(ct);
            if (op.RemoteOperationId is { } bulkId)
            {
                var bulk = await client.GetBulkOperationAsync(bulkId, ct);
                if (bulk.State is BulkState.Pending or BulkState.Running) continue;
            }
            var remote = await client.GetItemsAsync(op.ListId, ct);
            var entry = op.EntryId is { } entryId ? await db.Entries.SingleAsync(x => x.Id == entryId, ct) : null;
            var match = remote.FirstOrDefault(x => entry is null ? x.Id == op.RemoteItemId : RemoteOperationDispatcher.Normalize(x.Cidr) == entry.Cidr);
            var shared = entry is not null && await db.Entries.AnyAsync(x => x.EnvironmentId == environmentId && x.Kind == entry.Kind && x.Id != entry.Id &&
                x.Cidr == entry.Cidr && (x.State == EntryState.Active || x.State == EntryState.PendingAdd), ct);
            var success = op.Action == OperationAction.Add ? match is not null : match is null || shared;
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var checkedAt = clock.GetUtcNow().UtcDateTime;
            if (await db.Operations.Where(x => x.Id == id && x.State == OperationState.Uncertain && x.ClaimedBy == owner && x.LeaseUntil > checkedAt)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.LeaseUntil, checkedAt.AddSeconds(60)), ct) != 1) continue;
            if (!success) op.Attempts++;
            op.State = success ? OperationState.Succeeded : op.Attempts >= options.Value.MaxAttempts ? OperationState.DeadLetter : OperationState.Pending;
            op.NextAttemptAt = checkedAt.AddSeconds(Math.Min(600, 5 * Math.Pow(2, op.Attempts)));
            op.ClaimedBy = null;
            op.LeaseUntil = null;
            op.RemoteOperationId = null;
            op.CompletedAt = op.State is OperationState.Succeeded or OperationState.DeadLetter ? checkedAt : null;
            op.LastError = success ? null : "Reconciliation did not observe the desired remote state.";
            if (entry is not null)
            {
                if (success)
                { entry.State = op.Action == OperationAction.Add ? EntryState.Active : EntryState.Removed; entry.RemoteItemId = match?.Id; }
                else if (op.State == OperationState.DeadLetter) entry.State = EntryState.Failed;
                entry.UpdatedAt = checkedAt;
                if (success && op.Action == OperationAction.Remove && match is null)
                    await EntryService.QueueSharedRepairAsync(db, entry, op.ListId, checkedAt, ct);
            }
            db.DispatchLogs.Add(new() { OperationId = id, Attempt = op.Attempts, Phase = success ? DispatchPhase.Completed : DispatchPhase.Unknown,
                Detail = "Uncertain result reconciled against the remote list.", At = checkedAt });
            Audit.Write(db, new("reconciler", ActorType.System, id.ToString()), "operation.reconciled", "RemoteOperation", id,
                "{\"State\":\"Uncertain\"}", new { op.State }, checkedAt);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
    }
}
