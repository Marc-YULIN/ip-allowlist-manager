using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Allowlist.Core;
using Microsoft.EntityFrameworkCore;

namespace Allowlist.Infrastructure;

public sealed record CreateEntryRequest(string Cidr, EntryKind Kind, string Description = "", string Owner = "", DateTime? ExpiresAt = null);
public sealed record CreateEntryResult(IpEntry Entry, IReadOnlyList<string> Warnings);

public sealed class EntryService(AllowlistDbContext db, TimeProvider clock)
{
    public async Task<ManagedEnvironment> EnvironmentAsync(string code, CancellationToken ct) =>
        await db.Environments.SingleOrDefaultAsync(x => x.Code == code, ct)
        ?? throw new DomainException("environment_not_found", "Environment not found.", 404);

    public async Task<CreateEntryResult> CreateAsync(string code, CreateEntryRequest request, Actor actor, CancellationToken ct)
    {
        if (!Enum.IsDefined(request.Kind)) throw new DomainException("invalid_kind", "Invalid entry kind.");
        if (!CidrNormalizer.TryNormalize(request.Cidr, out var network, out var error))
            throw new DomainException("invalid_cidr", error!);
        if (request.Description is null || request.Owner is null || request.Description.EnumerateRunes().Count() > 200 || request.Owner.Length > 200)
            throw new DomainException("invalid_description", "Description must contain at most 200 Unicode scalars; owner at most 200 characters.");
        var now = clock.GetUtcNow().UtcDateTime;
        if (request.ExpiresAt is { } expires && expires.ToUniversalTime() <= now)
            throw new DomainException("invalid_expiry", "Expiry must be in the future.");
        await using var tx = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, ct);
        var env = await EnvironmentAsync(code, ct);
        var existing = await db.Entries.Where(x => x.EnvironmentId == env.Id && x.Kind == request.Kind && x.State != EntryState.Removed).ToListAsync(ct);
        var overlap = OverlapChecker.Check(network, existing);
        if (overlap.Error is not null) throw new DomainException(overlap.Error, $"Rule conflicts with an existing entry ({overlap.Error}).", 409);
        var entry = new IpEntry { EnvironmentId = env.Id, Cidr = network.ToString(), Kind = request.Kind,
            Description = request.Description, Owner = request.Owner, ExpiresAt = request.ExpiresAt?.ToUniversalTime(),
            State = EntryState.PendingAdd, CreatedBy = actor.Name, CreatedAt = now, UpdatedAt = now };
        db.Entries.Add(entry);
        Queue(entry, env, OperationAction.Add, now);
        Audit.Write(db, actor, "entry.created", "IpEntry", entry.Id, null, entry, now);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException) { throw new DomainException("duplicate", "A concurrent entry conflicts with this rule.", 409); }
        await tx.CommitAsync(ct);
        return new(entry, overlap.Warnings);
    }

    public async Task RemoveAsync(string code, Guid id, Actor actor, CancellationToken ct)
    {
        var env = await EnvironmentAsync(code, ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var entry = await db.Entries.SingleOrDefaultAsync(x => x.Id == id && x.EnvironmentId == env.Id, ct)
            ?? throw new DomainException("entry_not_found", "Entry not found.", 404);
        if (entry.State is EntryState.Removed or EntryState.PendingRemove) return;
        if (await db.Operations.AnyAsync(x => x.EntryId == id && x.State != OperationState.Succeeded && x.State != OperationState.DeadLetter && x.State != OperationState.Failed, ct))
            throw new DomainException("entry_busy", "Wait for the existing operation to settle before deleting.", 409);
        var before = JsonSerializer.Serialize(entry);
        var now = clock.GetUtcNow().UtcDateTime;
        entry.State = EntryState.PendingRemove;
        entry.UpdatedAt = now;
        Queue(entry, env, OperationAction.Remove, now);
        Audit.Write(db, actor, "entry.remove_requested", "IpEntry", entry.Id, before, entry, now);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }
    private void Queue(IpEntry entry, ManagedEnvironment env, OperationAction action, DateTime now) => db.Operations.Add(new RemoteOperation
    { EnvironmentId = entry.EnvironmentId, ListId = entry.Kind == EntryKind.Allow ? env.AllowListId : env.BlockListId,
        EntryId = entry.Id, Action = action, State = OperationState.Pending, CreatedAt = now, NextAttemptAt = now });

    /// <summary>Reassert shared demand after an overlapping removal, including demand whose dispatch is still in flight.</summary>
    public static async Task QueueSharedRepairAsync(AllowlistDbContext db, IpEntry removed, string listId, DateTime now, CancellationToken ct)
    {
        var shared = await db.Entries.Where(x => x.EnvironmentId == removed.EnvironmentId && x.Kind == removed.Kind && x.Id != removed.Id && x.Cidr == removed.Cidr &&
            (x.State == EntryState.Active || x.State == EntryState.PendingAdd)).ToListAsync(ct);
        foreach (var entry in shared)
        {
            entry.State = EntryState.PendingAdd;
            entry.UpdatedAt = now;
            db.Operations.Add(new() { EnvironmentId = entry.EnvironmentId, ListId = listId, EntryId = entry.Id, Action = OperationAction.Add,
                State = OperationState.Pending, CreatedAt = now, NextAttemptAt = now });
            Audit.Write(db, new("shared-rule-repair", ActorType.System, removed.Id.ToString()), "entry.repair_queued", "IpEntry", entry.Id,
                null, new { entry.State }, now);
        }
    }

    public async Task ExpireAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var expired = await db.Entries.Where(x => x.State == EntryState.Active && x.ExpiresAt <= now).Select(x => new { x.Id, x.EnvironmentId }).ToListAsync(ct);
        foreach (var entry in expired)
        {
            var code = await db.Environments.Where(x => x.Id == entry.EnvironmentId).Select(x => x.Code).SingleAsync(ct);
            await RemoveAsync(code, entry.Id, new("expiry", ActorType.System, Activity.Current?.Id ?? "expiry"), ct);
        }
    }
}

public static class Audit
{
    public static void Write(AllowlistDbContext db, Actor actor, string action, string targetType, Guid id, string? before, object? after, DateTime now)
        => db.AuditLogs.Add(new AuditLog { Actor = actor.Name, ActorType = actor.Type, Action = action, TargetType = targetType,
            TargetId = id.ToString(), BeforeJson = before, AfterJson = after is null ? null : JsonSerializer.Serialize(after), TraceId = actor.TraceId, At = now });
}
