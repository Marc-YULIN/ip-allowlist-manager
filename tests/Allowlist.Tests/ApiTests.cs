using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Allowlist.Core;
using Allowlist.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Allowlist.Tests;

public sealed class ApiTests
{
    private static readonly string[] CheckScopes = ["ip:check"];
    [Theory]
    [InlineData("/api/environments")]
    [InlineData("/api/auth/me")]
    [InlineData("/api/check?ip=192.0.2.1&env=dev")]
    public async Task Get_Anonymous_ReturnsUnauthorized(string path)
    {
        await using var app = new ApiFactory();
        using var client = app.CreateClient();
        using var response = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task CreateEntry_Viewer_ReturnsForbidden()
    {
        await using var app = new ApiFactory();
        using var client = app.CreateClient();
        await SignInAsync(client, "viewer");
        using var response = await client.PostAsJsonAsync("/api/environments/dev/entries", NewEntry());
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        await using var scope = app.Services.CreateAsyncScope();
        Assert.Empty(await scope.ServiceProvider.GetRequiredService<AllowlistDbContext>().Entries.ToListAsync());
    }

    [Fact]
    public async Task CreateEntry_Admin_PersistsEntryOperationAndAuditTogether()
    {
        await using var app = new ApiFactory();
        using var client = app.CreateClient();
        await SignInAsync(client, "admin");
        using var response = await client.PostAsJsonAsync("/api/environments/dev/entries", NewEntry());
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var id = body.GetProperty("entry").GetProperty("id").GetGuid();
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AllowlistDbContext>();
        var entry = await db.Entries.SingleAsync();
        var operation = await db.Operations.SingleAsync();
        Assert.Equal(id, entry.Id);
        Assert.Equal(EntryState.PendingAdd, entry.State);
        Assert.Equal(id, operation.EntryId);
        Assert.Equal(OperationAction.Add, operation.Action);
        Assert.Equal(OperationState.Pending, operation.State);
        Assert.Equal((await db.Environments.SingleAsync(x => x.Code == "dev")).AllowListId, operation.ListId);
        Assert.Contains(await db.AuditLogs.ToListAsync(), x => x.TargetId == id.ToString() && x.Actor == "admin");
    }

    [Fact]
    public async Task CreateEntry_CheckOnlyKey_ReturnsForbiddenAndAllowsCheck()
    {
        await using var app = new ApiFactory();
        using var admin = app.CreateClient();
        await SignInAsync(admin, "admin");
        var key = await CreateKeyAsync(admin);
        using var integration = app.CreateClient();
        integration.DefaultRequestHeaders.Add("X-Api-Key", key.Key);
        using var check = await integration.GetAsync("/api/check?ip=192.0.2.1&env=dev");
        Assert.Equal(HttpStatusCode.OK, check.StatusCode);
        using var write = await integration.PostAsJsonAsync("/api/integration/dev/entries", NewEntry());
        Assert.Equal(HttpStatusCode.Forbidden, write.StatusCode);
    }

    [Fact]
    public async Task Check_RevokedKey_ReturnsUnauthorized()
    {
        await using var app = new ApiFactory();
        using var admin = app.CreateClient();
        await SignInAsync(admin, "admin");
        var key = await CreateKeyAsync(admin);
        using var integration = app.CreateClient();
        integration.DefaultRequestHeaders.Add("X-Api-Key", key.Key);
        using var before = await integration.GetAsync("/api/check?ip=192.0.2.1&env=dev");
        Assert.Equal(HttpStatusCode.OK, before.StatusCode);
        using var revoked = await admin.PostAsync($"/api/integration-clients/{key.Id}/revoke", null);
        Assert.Equal(HttpStatusCode.NoContent, revoked.StatusCode);
        using var after = await integration.GetAsync("/api/check?ip=192.0.2.1&env=dev");
        Assert.Equal(HttpStatusCode.Unauthorized, after.StatusCode);
    }

    [Fact]
    public async Task Check_KeyWithCorrectPrefixAndWrongSecret_ReturnsUnauthorized()
    {
        await using var app = new ApiFactory();
        using var admin = app.CreateClient();
        await SignInAsync(admin, "admin");
        var key = await CreateKeyAsync(admin);
        using var integration = app.CreateClient();
        integration.DefaultRequestHeaders.Add("X-Api-Key", key.Key[..^1] + (key.Key[^1] == 'A' ? "B" : "A"));
        using var response = await integration.GetAsync("/api/check?ip=192.0.2.1&env=dev");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Check_OverlappingActiveRules_BlockTakesPrecedence()
    {
        await using var app = new ApiFactory();
        using var client = app.CreateClient();
        await SignInAsync(client, "admin");
        Guid blockedId;
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AllowlistDbContext>();
            var env = await db.Environments.SingleAsync(x => x.Code == "dev");
            var now = app.Services.GetRequiredService<TimeProvider>().GetUtcNow().UtcDateTime;
            var block = new IpEntry { EnvironmentId = env.Id, Cidr = "192.0.2.1/32", Kind = EntryKind.Block,
                State = EntryState.Active, CreatedAt = now, UpdatedAt = now };
            blockedId = block.Id;
            db.Entries.AddRange(block, new IpEntry { EnvironmentId = env.Id, Cidr = "192.0.2.0/24", Kind = EntryKind.Allow,
                State = EntryState.Active, CreatedAt = now, UpdatedAt = now });
            await db.SaveChangesAsync();
        }
        using var response = await client.GetAsync("/api/check?ip=192.0.2.1&env=dev");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(result.GetProperty("allowed").GetBoolean());
        Assert.True(result.GetProperty("blocked").GetBoolean());
        Assert.Equal(blockedId, result.GetProperty("matchedEntryId").GetGuid());
        Assert.Equal("192.0.2.1/32", result.GetProperty("matchedCidr").GetString());
    }

    [Fact]
    public async Task Login_TenFailures_ReturnsRateLimitUntilWindowExpires()
    {
        await using var app = new ApiFactory();
        using var client = app.CreateClient();
        for (var attempt = 1; attempt <= 10; attempt++)
        {
            using var response = await client.PostAsJsonAsync("/api/auth/login", new { username = "admin", password = "wrong" });
            Assert.Equal(attempt < 10 ? HttpStatusCode.Unauthorized : HttpStatusCode.TooManyRequests, response.StatusCode);
            var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(attempt < 10 ? "invalid_credentials" : "login_limited", problem.GetProperty("code").GetString());
        }
        using var limited = await client.PostAsJsonAsync("/api/auth/login", new { username = "admin", password = ApiFactory.Password });
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        app.Clock.Advance(TimeSpan.FromMinutes(5));
        using var restored = await client.PostAsJsonAsync("/api/auth/login", new { username = "admin", password = ApiFactory.Password });
        Assert.Equal(HttpStatusCode.OK, restored.StatusCode);
    }

    [Fact]
    public async Task Login_UnknownUserAndIncorrectPassword_ReturnsSameProblem()
    {
        await using var app = new ApiFactory();
        using var client = app.CreateClient();
        using var unknown = await client.PostAsJsonAsync("/api/auth/login", new { username = "missing", password = "wrong" });
        using var incorrect = await client.PostAsJsonAsync("/api/auth/login", new { username = "admin", password = "wrong" });
        Assert.Equal(HttpStatusCode.Unauthorized, unknown.StatusCode);
        Assert.Equal(unknown.StatusCode, incorrect.StatusCode);
        var first = await unknown.Content.ReadFromJsonAsync<JsonElement>();
        var second = await incorrect.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(first.GetProperty("detail").GetString(), second.GetProperty("detail").GetString());
        Assert.Equal(first.GetProperty("code").GetString(), second.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Reconcile_SeededEnvironments_ReturnsTwoTypedSnapshotsAndSixSeparateLists()
    {
        await using var app = new ApiFactory();
        using var client = app.CreateClient();
        await SignInAsync(client, "admin");
        var environments = (await client.GetFromJsonAsync<JsonElement>("/api/environments")).EnumerateArray().ToArray();
        Assert.Equal(3, environments.Length);
        var listIds = new List<string>();
        foreach (var env in environments)
        {
            var code = env.GetProperty("code").GetString();
            Assert.Equal("allowlist_" + code, env.GetProperty("allowListName").GetString());
            Assert.Equal("blocklist_" + code, env.GetProperty("blockListName").GetString());
            listIds.Add(env.GetProperty("allowListId").GetString()!);
            listIds.Add(env.GetProperty("blockListId").GetString()!);
        }
        Assert.Equal(6, listIds.Distinct().Count());
        using var response = await client.PostAsync("/api/environments/dev/reconcile?autoFix=false", null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var snapshots = (await response.Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray().ToArray();
        Assert.Equal(2, snapshots.Length);
        Assert.Equal(["Allow", "Block"], snapshots.Select(x => x.GetProperty("listKind").GetString()!).Order().ToArray());
        var history = (await client.GetFromJsonAsync<JsonElement>("/api/environments/dev/snapshots")).EnumerateArray().ToArray();
        Assert.Equal(2, history.Length);
        Assert.True(snapshots.Select(x => x.GetProperty("id").GetGuid()).ToHashSet()
            .SetEquals(history.Select(x => x.GetProperty("id").GetGuid())));
    }

    private static object NewEntry() => new { cidr = "192.0.2.1", kind = "Allow", description = "Example rule", owner = "Demo Operations" };

    private static async Task SignInAsync(HttpClient client, string username)
    {
        using var response = await client.PostAsJsonAsync("/api/auth/login", new { username, password = ApiFactory.Password });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", body.GetProperty("token").GetString());
    }

    private static async Task<(Guid Id, string Key)> CreateKeyAsync(HttpClient admin)
    {
        using var response = await admin.PostAsJsonAsync("/api/integration-clients", new { name = "Demo Monitor", scopes = CheckScopes });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return (body.GetProperty("id").GetGuid(), body.GetProperty("key").GetString()!);
    }
}

public sealed class ApiFactory : WebApplicationFactory<Program>
{
    public const string Password = "Example!2026";
    private readonly string path = Path.Combine(Path.GetTempPath(), $"allowlist-api-{Guid.NewGuid():N}.db");
    public ManualTimeProvider Clock { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Database:Provider"] = "Sqlite", ["ConnectionStrings:Sqlite"] = $"Data Source={path}",
            ["Jwt:SigningKey"] = "test-signing-key-with-at-least-thirty-two-bytes",
            ["Seed:AdminUsername"] = "admin", ["Seed:AdminPassword"] = Password, ["Seed:ViewerPassword"] = Password,
            ["Cloudflare:Mode"] = "Fake", ["Cloudflare:Fake:FailureRate"] = "0", ["Cloudflare:Fake:TimeoutRate"] = "0",
            ["Logging:LogLevel:Default"] = "None"
        }));
        builder.ConfigureServices(services =>
        {
            foreach (var descriptor in services.Where(x => x.ServiceType == typeof(IHostedService) &&
                (x.ImplementationType == typeof(RemoteOperationDispatcher) || x.ImplementationType == typeof(MaintenanceWorker))).ToArray())
                services.Remove(descriptor);
            foreach (var descriptor in services.Where(x => x.ServiceType == typeof(TimeProvider)).ToArray()) services.Remove(descriptor);
            services.AddSingleton<TimeProvider>(Clock);
        });
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        using var connection = new SqliteConnection($"Data Source={path}");
        SqliteConnection.ClearPool(connection);
        foreach (var suffix in new[] { "", "-wal", "-shm" }) File.Delete(path + suffix);
    }
}
