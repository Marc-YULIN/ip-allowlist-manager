using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Allowlist.Api.Auth;
using Allowlist.Core;
using Allowlist.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace Allowlist.Api.Endpoints;

public sealed record LoginRequest(string Username, string Password);
public static class AuthEndpoints
{
    private static readonly string DummyHash = PasswordHasher.Hash("invalid-password-placeholder");
    public static RouteGroupBuilder MapAuth(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/auth").WithTags("Authentication");
        group.MapPost("/login", async (LoginRequest request, HttpContext context, AllowlistDbContext db,
            LoginLimiter limiter, IOptions<JwtOptions> options, TimeProvider clock, CancellationToken ct) =>
        {
            var ip = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            if (limiter.IsLimited(ip)) throw new DomainException("login_limited", "Too many failed login attempts. Try again in five minutes.", 429);
            var user = await db.AdminUsers.SingleOrDefaultAsync(x => x.Username == request.Username, ct);
            var valid = PasswordHasher.Verify(request.Password ?? "", user?.PasswordHash ?? DummyHash);
            if (user is null || !valid)
                throw new DomainException(limiter.IsLimited(ip, true) ? "login_limited" : "invalid_credentials", "Login failed.", limiter.IsLimited(ip) ? 429 : 401);
            var now = clock.GetUtcNow().UtcDateTime;
            var token = new JwtSecurityToken(options.Value.Issuer, options.Value.Audience,
                [new Claim("sub", user.Id.ToString()), new Claim("name", user.Username), new Claim("role", user.Role.ToString())],
                now, now.AddHours(8), new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(options.Value.SigningKey)), SecurityAlgorithms.HmacSha256));
            return Results.Ok(new { token = new JwtSecurityTokenHandler().WriteToken(token), expiresAt = now.AddHours(8), user = new { user.Username, user.Role } });
        }).WithSummary("Sign in for an eight-hour administrator session");
        group.MapGet("/me", (HttpContext context) => Results.Ok(new { username = context.User.Identity!.Name, role = context.User.FindFirstValue("role") }))
            .RequireAuthorization("AdminOrViewer").WithSummary("Read the current administrator identity");
        return group;
    }
}
