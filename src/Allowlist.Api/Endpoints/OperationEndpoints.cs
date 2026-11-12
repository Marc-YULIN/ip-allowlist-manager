using Allowlist.Api.Auth;
using Allowlist.Core;
using Allowlist.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Allowlist.Api.Endpoints;

public static class OperationEndpoints
{
    public static RouteGroupBuilder MapOperations(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/operations").WithTags("Operations");
        group.MapGet("", async (AllowlistDbContext db, CancellationToken ct, string? state = null, int page = 1) =>
        {
            var query = db.Operations.AsNoTracking().AsQueryable();
            if (!string.IsNullOrEmpty(state)) query = query.Where(x => x.State == EntryEndpoints.Parse<OperationState>(state));
            var result = await EntryEndpoints.PaginateAsync(query.OrderByDescending(x => x.CreatedAt).ThenBy(x => x.Id), page, ct);
            var ids = result.Items.Select(x => x.Id).ToArray();
            var logs = await db.DispatchLogs.AsNoTracking().Where(x => ids.Contains(x.OperationId)).OrderBy(x => x.At).ToListAsync(ct);
            return new { items = result.Items.Select(operation => new { operation, dispatchLogs = logs.Where(x => x.OperationId == operation.Id) }), result.Total, result.Page, result.PageSize };
        }).RequireAuthorization("AdminOrViewer").WithSummary("Inspect outbox operations and dispatch evidence");
        group.MapPost("/{id:guid}/retry", async (Guid id, AllowlistDbContext db, HttpContext context, TimeProvider clock, CancellationToken ct) =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var op = await db.Operations.SingleOrDefaultAsync(x => x.Id == id, ct)
                ?? throw new DomainException("operation_not_found", "Operation not found.", 404);
            if (op.State is not (OperationState.Failed or OperationState.DeadLetter)) throw new DomainException("not_retryable", "Only failed or dead-letter operations can be retried.", 409);
            if (op.EntryId is { } entryId)
            {
                if (await db.Operations.AnyAsync(x => x.EntryId == entryId && x.Id != id && x.State != OperationState.Succeeded && x.State != OperationState.DeadLetter && x.State != OperationState.Failed, ct))
                    throw new DomainException("entry_busy", "Another operation is still pending.", 409);
                var entry = await db.Entries.SingleAsync(x => x.Id == entryId, ct);
                if (entry.State != EntryState.Failed) throw new DomainException("not_retryable", "The entry has already moved on from this operation.", 409);
                entry.State = op.Action == OperationAction.Add ? EntryState.PendingAdd : EntryState.PendingRemove;
                entry.UpdatedAt = clock.GetUtcNow().UtcDateTime;
            }
            op.State = OperationState.Pending;
            op.NextAttemptAt = clock.GetUtcNow().UtcDateTime;
            op.CompletedAt = null;
            op.ClaimedBy = null;
            op.LeaseUntil = null;
            op.RemoteOperationId = null;
            op.LastError = null;
            Audit.Write(db, context.Actor(), "operation.retry_requested", "RemoteOperation", id, null, new { op.State }, op.NextAttemptAt);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return Results.Accepted();
        }).RequireAuthorization("AdminOnly").WithSummary("Retry a definitively failed operation");
        var drift = app.MapGroup("/api/environments/{code}").WithTags("Drift");
        drift.MapPost("/reconcile", async (string code, EntryService entries, DriftReconciler reconciler, HttpContext context, CancellationToken ct, bool autoFix = false) =>
            await reconciler.ReconcileAsync((await entries.EnvironmentAsync(code, ct)).Id, autoFix, context.Actor(), ct))
            .RequireAuthorization("AdminOnly").WithSummary("Compare local and remote rules and optionally queue repairs");
        drift.MapGet("/snapshots", async (string code, EntryService entries, AllowlistDbContext db, CancellationToken ct) =>
        {
            var env = await entries.EnvironmentAsync(code, ct);
            return await db.Snapshots.AsNoTracking().Where(x => x.EnvironmentId == env.Id).OrderByDescending(x => x.TakenAt).Take(100).ToListAsync(ct);
        }).RequireAuthorization("AdminOrViewer").WithSummary("Read the latest drift snapshots");
        return group;
    }
}
