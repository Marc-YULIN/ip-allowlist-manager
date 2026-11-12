using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Allowlist.Core;
using Allowlist.Infrastructure;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace Allowlist.Api.Auth;

public sealed class JwtOptions
{
    [Required] public string SigningKey { get; set; } = "";
    [Required] public string Issuer { get; set; } = "allowlist-manager";
    [Required] public string Audience { get; set; } = "allowlist-manager";
}

public static class Authentication
{
    public static IServiceCollection AddAllowlistAuthentication(this IServiceCollection services)
    {
        services.AddOptions<JwtOptions>().BindConfiguration("Jwt").ValidateDataAnnotations()
            .Validate(x => Encoding.UTF8.GetByteCount(x.SigningKey) >= 32, "Signing key must contain at least 32 bytes.").ValidateOnStart();
        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer().AddScheme<AuthenticationSchemeOptions, ApiKeyHandler>("ApiKey", _ => { });
        services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme).Configure<IOptions<JwtOptions>>((options, jwt) =>
        {
            options.MapInboundClaims = false;
            options.TokenValidationParameters = new()
            {
                ValidateIssuer = true, ValidateAudience = true, ValidateLifetime = true, ValidateIssuerSigningKey = true,
                ValidIssuer = jwt.Value.Issuer, ValidAudience = jwt.Value.Audience,
                IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.Value.SigningKey)),
                NameClaimType = "name", RoleClaimType = "role", ClockSkew = TimeSpan.FromSeconds(15)
            };
        });
        services.AddAuthorization(options =>
        {
            options.AddPolicy("AdminOnly", p => p.AddAuthenticationSchemes("Bearer").RequireAuthenticatedUser().RequireRole("Admin"));
            options.AddPolicy("AdminOrViewer", p => p.AddAuthenticationSchemes("Bearer").RequireAuthenticatedUser().RequireRole("Admin", "Viewer"));
            foreach (var (policy, scope) in new[] { ("ScopeRead", "allowlist:read"), ("ScopeWrite", "allowlist:write"), ("ScopeCheck", "ip:check") })
                options.AddPolicy(policy, p => p.AddAuthenticationSchemes("Bearer", "ApiKey").RequireAuthenticatedUser()
                    .RequireAssertion(c => c.User.HasClaim("scope", scope) || c.User.IsInRole("Admin") || (policy != "ScopeWrite" && c.User.IsInRole("Viewer"))));
        });
        services.AddSingleton<LoginLimiter>();
        return services;
    }

    public static Actor Actor(this HttpContext context) => new(
        context.User.FindFirstValue("client_name") ?? context.User.Identity?.Name ?? "anonymous",
        context.User.HasClaim(x => x.Type == "client_name") ? ActorType.Integration : ActorType.Admin, context.TraceIdentifier);
}

public sealed class ApiKeyHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger,
    UrlEncoder encoder, AllowlistDbContext db, TimeProvider clock) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue("X-Api-Key", out var header)) return AuthenticateResult.NoResult();
        var key = header.ToString();
        if (!ApiKeyFormat.TryParse(key, out var prefix)) return AuthenticateResult.Fail("Invalid API key.");
        var client = await db.IntegrationClients.AsNoTracking().SingleOrDefaultAsync(x => x.KeyPrefix == prefix, Context.RequestAborted);
        if (client is null || client.RevokedAt is not null || !CryptographicOperations.FixedTimeEquals(
            Convert.FromHexString(client.KeyHash), Convert.FromHexString(ApiKeyFormat.Hash(key))))
            return AuthenticateResult.Fail("Invalid API key.");
        var now = clock.GetUtcNow().UtcDateTime;
        await db.IntegrationClients.Where(x => x.Id == client.Id && (x.LastUsedAt == null || x.LastUsedAt <= now.AddMinutes(-1)))
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.LastUsedAt, now), Context.RequestAborted);
        var claims = client.Scopes.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(x => new Claim("scope", x)).ToList();
        claims.Add(new("client_name", client.Name));
        claims.Add(new(ClaimTypes.NameIdentifier, client.Id.ToString()));
        return AuthenticateResult.Success(new(new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme.Name)), Scheme.Name));
    }
}

public sealed class LoginLimiter(TimeProvider clock)
{
    private readonly object gate = new();
    private readonly Dictionary<string, Queue<DateTimeOffset>> failures = [];
    public bool IsLimited(string ip, bool failed = false)
    {
        lock (gate)
        {
            var cutoff = clock.GetUtcNow().AddMinutes(-5);
            foreach (var key in failures.Keys.ToArray())
            {
                while (failures[key].TryPeek(out var at) && at <= cutoff) failures[key].Dequeue();
                if (failures[key].Count == 0) failures.Remove(key);
            }
            if (!failures.TryGetValue(ip, out var queue))
            {
                if (!failed) return false;
                if (failures.Count >= 10_000) return true;
                failures[ip] = queue = new();
            }
            if (failed) queue.Enqueue(clock.GetUtcNow());
            return queue.Count >= 10;
        }
    }
}
