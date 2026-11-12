using Allowlist.Core;
using Microsoft.EntityFrameworkCore;

namespace Allowlist.Infrastructure;

public interface IApiKeyGenerator { string Generate(); }
public sealed class ApiKeyGenerator : IApiKeyGenerator { public string Generate() => ApiKeyFormat.Generate(); }
public sealed record CreatedIntegrationClient(Guid Id, string Name, string KeyPrefix, string Scopes, string Key);

public sealed class IntegrationClientService(AllowlistDbContext db, TimeProvider clock, IApiKeyGenerator generator)
{
    public static readonly string[] ValidScopes = ["allowlist:read", "allowlist:write", "ip:check"];
    public async Task<CreatedIntegrationClient> CreateAsync(string name, string[] scopes, Actor actor, CancellationToken ct)
    {
        name = (name ?? "").Trim();
        if (name.Length is < 1 or > 100 || scopes is null || scopes.Length == 0 || scopes.Any(x => !ValidScopes.Contains(x)))
            throw new DomainException("invalid_client", "Supply a name and at least one valid scope.");
        if (await db.IntegrationClients.AnyAsync(x => x.Name == name, ct))
            throw new DomainException("duplicate_name", "Client name already exists.", 409);
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var key = generator.Generate();
            if (!ApiKeyFormat.TryParse(key, out var prefix)) throw new InvalidOperationException("Invalid generated key.");
            if (await db.IntegrationClients.AnyAsync(x => x.KeyPrefix == prefix, ct)) continue;
            var client = new IntegrationClient { Name = name, KeyPrefix = prefix, KeyHash = ApiKeyFormat.Hash(key),
                Scopes = string.Join(',', scopes.Distinct().Order(StringComparer.Ordinal)), CreatedAt = clock.GetUtcNow().UtcDateTime };
            db.IntegrationClients.Add(client);
            var audit = new AuditLog { Actor = actor.Name, ActorType = actor.Type, Action = "client.created", TargetType = "IntegrationClient",
                TargetId = client.Id.ToString(), TraceId = actor.TraceId, At = client.CreatedAt };
            db.AuditLogs.Add(audit);
            try { await db.SaveChangesAsync(ct); return new(client.Id, client.Name, prefix, client.Scopes, key); }
            catch (DbUpdateException)
            {
                db.Entry(client).State = EntityState.Detached;
                db.Entry(audit).State = EntityState.Detached;
                if (await db.IntegrationClients.AnyAsync(x => x.Name == name, ct)) throw new DomainException("duplicate_name", "Client name already exists.", 409);
                if (!await db.IntegrationClients.AnyAsync(x => x.KeyPrefix == prefix, ct)) throw;
            }
        }
        throw new DomainException("overlapping_key_prefix", "Unable to allocate a unique key prefix.", 409);
    }
    public async Task RevokeAsync(Guid id, Actor actor, CancellationToken ct)
    {
        var client = await db.IntegrationClients.SingleOrDefaultAsync(x => x.Id == id, ct)
            ?? throw new DomainException("client_not_found", "Integration client not found.", 404);
        client.RevokedAt ??= clock.GetUtcNow().UtcDateTime;
        Audit.Write(db, actor, "client.revoked", "IntegrationClient", id, null, new { client.RevokedAt }, client.RevokedAt.Value);
        await db.SaveChangesAsync(ct);
    }
}
