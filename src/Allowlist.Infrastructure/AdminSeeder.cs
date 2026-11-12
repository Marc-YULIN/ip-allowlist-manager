using Allowlist.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Allowlist.Infrastructure;

public sealed class AdminSeeder(AllowlistDbContext db, IOptions<SeedOptions> options, IHostEnvironment host, TimeProvider clock, ILogger<AdminSeeder> logger)
{
    private static readonly Action<ILogger, Exception?> LogSeedSkipped = LoggerMessage.Define(LogLevel.Warning, new EventId(3, "SeedSkipped"),
        "Administrator seeding skipped because no password was configured.");
    public async Task SeedAsync(CancellationToken ct)
    {
        var settings = options.Value;
        if (!await db.AdminUsers.AnyAsync(x => x.Role == AdminRole.Admin, ct))
        {
            var password = settings.AdminPassword ?? (host.IsDevelopment() ? "ChangeMe!2026" : null);
            if (string.IsNullOrWhiteSpace(password)) LogSeedSkipped(logger, null);
            else
            {
                db.AdminUsers.Add(new() { Username = settings.AdminUsername, PasswordHash = PasswordHasher.Hash(password), Role = AdminRole.Admin });
                var viewerPassword = settings.ViewerPassword ?? (host.IsDevelopment() ? "ChangeMe!2026" : null);
                if (!string.IsNullOrWhiteSpace(viewerPassword) && !await db.AdminUsers.AnyAsync(x => x.Username == "viewer", ct))
                    db.AdminUsers.Add(new() { Username = "viewer", PasswordHash = PasswordHasher.Hash(viewerPassword), Role = AdminRole.Viewer });
            }
        }
        var allowListIds = new[] { settings.DevAllowListId, settings.StagingAllowListId, settings.ProdAllowListId };
        var blockListIds = new[] { settings.DevBlockListId, settings.StagingBlockListId, settings.ProdBlockListId };
        var codes = new[] { "dev", "staging", "prod" };
        for (var i = 0; i < codes.Length; i++)
        {
            var code = codes[i];
            if (!await db.Environments.AnyAsync(x => x.Code == code, ct))
                db.Environments.Add(new() { Code = code, Name = code,
                    AllowListId = allowListIds[i], AllowListName = "allowlist_" + code,
                    BlockListId = blockListIds[i], BlockListName = "blocklist_" + code,
                    CreatedAt = clock.GetUtcNow().UtcDateTime });
        }
        await db.SaveChangesAsync(ct);
    }
}
