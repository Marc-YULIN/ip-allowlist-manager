using System.Net;
using Allowlist.Api.Auth;
using Allowlist.Core;
using Allowlist.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Allowlist.Api.Endpoints;

public sealed record PageResult<T>(IReadOnlyList<T> Items, int Total, int Page, int PageSize);
public static class EntryEndpoints
{
    public static RouteGroupBuilder MapEntries(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/environments").WithTags("Entries");
        group.MapGet("", async (AllowlistDbContext db, CancellationToken ct) => await db.Environments.AsNoTracking().OrderBy(x => x.Code).ToListAsync(ct))
            .RequireAuthorization("AdminOrViewer").WithSummary("List managed environments");
        group.MapGet("/{code}/entries", GetEntries).RequireAuthorization("AdminOrViewer").WithSummary("Search and paginate environment rules");
        group.MapPost("/{code}/entries", Create).RequireAuthorization("AdminOnly").WithSummary("Queue a new rule for remote synchronization");
        group.MapDelete("/{code}/entries/{id:guid}", Remove).RequireAuthorization("AdminOnly").WithSummary("Queue removal of a rule");
        var integration = app.MapGroup("/api/integration/{code}/entries").WithTags("Integration");
        integration.MapGet("", GetEntries).RequireAuthorization("ScopeRead").WithSummary("Read rules with allowlist:read scope");
        integration.MapPost("", Create).RequireAuthorization("ScopeWrite").WithSummary("Create a rule with allowlist:write scope");
        integration.MapDelete("/{id:guid}", Remove).RequireAuthorization("ScopeWrite").WithSummary("Remove a rule with allowlist:write scope");
        app.MapGroup("/api/check").WithTags("IP decision").MapGet("", async (string ip, string env, EntryService service,
            AllowlistDbContext db, TimeProvider clock, CancellationToken ct) =>
        {
            if (!IPAddress.TryParse(ip, out var address)) throw new DomainException("invalid_ip", "Invalid IP address.");
            var environment = await service.EnvironmentAsync(env, ct);
            var now = clock.GetUtcNow().UtcDateTime;
            var entries = await db.Entries.AsNoTracking().Where(x => x.EnvironmentId == environment.Id && x.State == EntryState.Active &&
                (x.ExpiresAt == null || x.ExpiresAt > now)).ToListAsync(ct);
            return IpDecision.Evaluate(address, entries);
        }).RequireAuthorization("ScopeCheck").WithSummary("Evaluate an IP with Block taking precedence");
        return group;
    }

    private static async Task<PageResult<IpEntry>> GetEntries(string code, EntryService service, AllowlistDbContext db, CancellationToken ct,
        string? kind = null, string? state = null, string? q = null, int page = 1)
    {
        var env = await service.EnvironmentAsync(code, ct);
        var query = db.Entries.AsNoTracking().Where(x => x.EnvironmentId == env.Id);
        if (!string.IsNullOrEmpty(kind)) query = query.Where(x => x.Kind == Parse<EntryKind>(kind));
        if (!string.IsNullOrEmpty(state)) query = query.Where(x => x.State == Parse<EntryState>(state));
        else query = query.Where(x => x.State != EntryState.Removed);
        if (!string.IsNullOrWhiteSpace(q)) query = query.Where(x => x.Cidr.Contains(q) || x.Description.Contains(q) || x.Owner.Contains(q));
        return await PaginateAsync(query.OrderByDescending(x => x.CreatedAt).ThenBy(x => x.Id), page, ct);
    }
    private static async Task<IResult> Create(string code, CreateEntryRequest request, EntryService service, HttpContext context, CancellationToken ct)
    {
        var result = await service.CreateAsync(code, request, context.Actor(), ct);
        return Results.Created($"/api/environments/{code}/entries/{result.Entry.Id}", result);
    }
    private static async Task<IResult> Remove(string code, Guid id, EntryService service, HttpContext context, CancellationToken ct)
    { await service.RemoveAsync(code, id, context.Actor(), ct); return Results.NoContent(); }
    public static T Parse<T>(string value) where T : struct, Enum => Enum.TryParse<T>(value, true, out var result) && Enum.IsDefined(result)
        ? result : throw new DomainException("invalid_filter", "Invalid filter value.");
    public static async Task<PageResult<T>> PaginateAsync<T>(IQueryable<T> query, int page, CancellationToken ct)
    {
        if (page is < 1 or > 100000) throw new DomainException("invalid_page", "Page must be between 1 and 100000.");
        return new(await query.Skip((page - 1) * 20).Take(20).ToListAsync(ct), await query.CountAsync(ct), page, 20);
    }
}
