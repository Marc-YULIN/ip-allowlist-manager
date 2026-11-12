using Allowlist.Core;
using Allowlist.Infrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Allowlist.Tests;

public sealed class DatabaseFixture : IAsyncDisposable
{
    public const string AllowListId = "list-dev";
    public const string BlockListId = "block-dev";
    private readonly string path = Path.Combine(Path.GetTempPath(), $"allowlist-{Guid.NewGuid():N}.db");
    public ManualTimeProvider Clock { get; } = new();
    public ServiceProvider Provider { get; private set; } = null!;
    public CloudflareOptions Options { get; } = new();
    public Guid EnvironmentId { get; private set; }
    public ICloudflareListClient Client { get; private set; } = null!;

    public async Task InitializeAsync(ICloudflareListClient? client = null)
    {
        Client = client ?? new FakeCloudflareListClient(Clock, Microsoft.Extensions.Options.Options.Create(Options));
        var services = new ServiceCollection();
        services.AddDbContext<AllowlistDbContext>(b => b.UseSqlite($"Data Source={path};Default Timeout=30"));
        services.AddSingleton<TimeProvider>(Clock);
        services.AddSingleton(Client);
        services.AddSingleton<IOptions<CloudflareOptions>>(Microsoft.Extensions.Options.Options.Create(Options));
        services.AddScoped<EntryService>();
        services.AddScoped<DriftReconciler>();
        Provider = services.BuildServiceProvider();
        await using var scope = Provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AllowlistDbContext>();
        await db.Database.EnsureCreatedAsync();
        var env = new ManagedEnvironment { Code = "dev", Name = "Development", AllowListId = AllowListId, AllowListName = "allowlist_dev",
            BlockListId = BlockListId, BlockListName = "blocklist_dev", CreatedAt = Clock.GetUtcNow().UtcDateTime };
        db.Environments.Add(env);
        await db.SaveChangesAsync();
        EnvironmentId = env.Id;
    }

    public RemoteOperationDispatcher Dispatcher() => new(Provider.GetRequiredService<IServiceScopeFactory>(), Clock, NullLogger<RemoteOperationDispatcher>.Instance);
    public async Task<Guid[]> SeedAsync(int count = 1, EntryKind kind = EntryKind.Allow)
    {
        await using var scope = Provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AllowlistDbContext>();
        var ids = new List<Guid>();
        for (var i = 0; i < count; i++)
        {
            var entry = new IpEntry { EnvironmentId = EnvironmentId, Cidr = $"192.0.2.{i + 1}/32", Kind = kind,
                State = EntryState.PendingAdd, CreatedAt = Clock.GetUtcNow().UtcDateTime, UpdatedAt = Clock.GetUtcNow().UtcDateTime };
            var op = new RemoteOperation { EntryId = entry.Id, EnvironmentId = EnvironmentId,
                ListId = kind == EntryKind.Allow ? AllowListId : BlockListId, Action = OperationAction.Add,
                State = OperationState.Pending, CreatedAt = Clock.GetUtcNow().UtcDateTime, NextAttemptAt = Clock.GetUtcNow().UtcDateTime };
            db.Entries.Add(entry); db.Operations.Add(op); ids.Add(op.Id);
        }
        await db.SaveChangesAsync();
        return ids.ToArray();
    }
    public async Task<T> ReadAsync<T>(Func<AllowlistDbContext, Task<T>> read)
    {
        await using var scope = Provider.CreateAsyncScope();
        return await read(scope.ServiceProvider.GetRequiredService<AllowlistDbContext>());
    }
    public async ValueTask DisposeAsync()
    {
        if (Provider is not null) await Provider.DisposeAsync();
        using var connection = new SqliteConnection($"Data Source={path};Default Timeout=30");
        SqliteConnection.ClearPool(connection);
        foreach (var suffix in new[] { "", "-wal", "-shm" }) File.Delete(path + suffix);
    }
}
