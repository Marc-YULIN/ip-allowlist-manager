using Allowlist.Api.Auth;
using Allowlist.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Allowlist.Api.Endpoints;

public sealed record CreateClientRequest(string Name, string[] Scopes);
public static class AdministrationEndpoints
{
    public static RouteGroupBuilder MapAdministration(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/integration-clients").WithTags("Integration clients").RequireAuthorization("AdminOnly");
        group.MapGet("", async (AllowlistDbContext db, CancellationToken ct) => await db.IntegrationClients.AsNoTracking().OrderByDescending(x => x.CreatedAt)
            .Select(x => new { x.Id, x.Name, x.KeyPrefix, x.Scopes, x.CreatedAt, x.LastUsedAt, x.RevokedAt }).ToListAsync(ct))
            .WithSummary("List integration clients without secret material");
        group.MapPost("", async (CreateClientRequest request, IntegrationClientService service, HttpContext context, CancellationToken ct) =>
        {
            var result = await service.CreateAsync(request.Name, request.Scopes, context.Actor(), ct);
            context.Response.Headers.CacheControl = "no-store";
            return Results.Created($"/api/integration-clients/{result.Id}", result);
        }).WithSummary("Create an integration key shown exactly once");
        group.MapPost("/{id:guid}/revoke", async (Guid id, IntegrationClientService service, HttpContext context, CancellationToken ct) =>
        { await service.RevokeAsync(id, context.Actor(), ct); return Results.NoContent(); }).WithSummary("Revoke an integration key");
        app.MapGroup("/api/audit-logs").WithTags("Audit").RequireAuthorization("AdminOnly")
            .MapGet("", async (AllowlistDbContext db, CancellationToken ct, string? actor = null, string? action = null, DateTime? from = null, DateTime? to = null, int page = 1) =>
            {
                var query = db.AuditLogs.AsNoTracking().AsQueryable();
                if (!string.IsNullOrWhiteSpace(actor)) query = query.Where(x => x.Actor.Contains(actor));
                if (!string.IsNullOrWhiteSpace(action)) query = query.Where(x => x.Action.Contains(action));
                if (from.HasValue) query = query.Where(x => x.At >= from.Value.ToUniversalTime());
                if (to.HasValue) query = query.Where(x => x.At <= to.Value.ToUniversalTime());
                return await EntryEndpoints.PaginateAsync(query.OrderByDescending(x => x.At).ThenBy(x => x.Id), page, ct);
            }).WithSummary("Search audited changes");
        return group;
    }
}
