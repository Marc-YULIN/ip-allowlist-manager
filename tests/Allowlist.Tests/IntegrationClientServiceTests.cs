using Allowlist.Core;
using Allowlist.Infrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Allowlist.Tests;

public sealed class IntegrationClientServiceTests
{
    private static readonly Actor Admin = new("test-admin", ActorType.Admin, "trace-demo");

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CreateAsync_DuplicateName_ReturnsConflictWithoutGeneratingKey(bool revoked)
    {
        await using var database = await TestDatabase.CreateAsync();
        await SeedClientAsync(database, revoked);
        var generator = new SequenceKeyGenerator([]);
        var service = new IntegrationClientService(database.Db, database.Clock, generator);

        var exception = await Assert.ThrowsAsync<DomainException>(() =>
            service.CreateAsync("  Existing client  ", ["ip:check"], Admin, CancellationToken.None));

        Assert.Equal("duplicate_name", exception.Code);
        Assert.Equal(409, exception.Status);
        Assert.Equal(0, generator.Calls);
        Assert.Equal(1, await database.Db.IntegrationClients.CountAsync(CancellationToken.None));
        Assert.Empty(await database.Db.AuditLogs.ToListAsync(CancellationToken.None));
    }

    [Theory]
    [InlineData(false, 1)]
    [InlineData(true, 1)]
    [InlineData(true, 4)]
    public async Task CreateAsync_ConflictingPrefix_RetriesAndPersistsUniqueKey(bool revoked, int collisions)
    {
        await using var database = await TestDatabase.CreateAsync();
        await SeedClientAsync(database, revoked);
        var uniqueKey = Key("Unique02");
        var generator = new SequenceKeyGenerator(Enumerable.Repeat(Key("Existing"), collisions).Append(uniqueKey));
        var service = new IntegrationClientService(database.Db, database.Clock, generator);

        var created = await service.CreateAsync("New client", ["ip:check", "allowlist:read", "ip:check"], Admin, CancellationToken.None);

        Assert.Equal(collisions + 1, generator.Calls);
        Assert.Equal(uniqueKey, created.Key);
        Assert.Equal("Unique02", created.KeyPrefix);
        Assert.Equal("allowlist:read,ip:check", created.Scopes);
        database.Db.ChangeTracker.Clear();
        var persisted = await database.Db.IntegrationClients.SingleAsync(client => client.Id == created.Id, CancellationToken.None);
        Assert.Equal("New client", persisted.Name);
        Assert.Equal("Unique02", persisted.KeyPrefix);
        Assert.Equal(ApiKeyFormat.Hash(uniqueKey), persisted.KeyHash);
        Assert.NotEqual(uniqueKey, persisted.KeyHash);
        Assert.Equal(created.Scopes, persisted.Scopes);
        Assert.Equal(database.Clock.GetUtcNow().UtcDateTime, persisted.CreatedAt);
        Assert.Equal(2, await database.Db.IntegrationClients.CountAsync(CancellationToken.None));
        var audit = await database.Db.AuditLogs.SingleAsync(CancellationToken.None);
        Assert.Equal("client.created", audit.Action);
        Assert.Equal(created.Id.ToString(), audit.TargetId);
        Assert.Equal(Admin.Name, audit.Actor);
        Assert.Equal(Admin.Type, audit.ActorType);
        Assert.Equal(Admin.TraceId, audit.TraceId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CreateAsync_FiveConflictingPrefixes_ReturnsConflictWithoutPersistingClient(bool revoked)
    {
        await using var database = await TestDatabase.CreateAsync();
        await SeedClientAsync(database, revoked);
        var generator = new SequenceKeyGenerator(Enumerable.Repeat(Key("Existing"), 5));
        var service = new IntegrationClientService(database.Db, database.Clock, generator);

        var exception = await Assert.ThrowsAsync<DomainException>(() =>
            service.CreateAsync("New client", ["ip:check"], Admin, CancellationToken.None));

        Assert.Equal("overlapping_key_prefix", exception.Code);
        Assert.Equal(409, exception.Status);
        Assert.Equal(5, generator.Calls);
        database.Db.ChangeTracker.Clear();
        Assert.Equal(1, await database.Db.IntegrationClients.CountAsync(CancellationToken.None));
        Assert.False(await database.Db.IntegrationClients.AnyAsync(client => client.Name == "New client", CancellationToken.None));
        Assert.Empty(await database.Db.AuditLogs.ToListAsync(CancellationToken.None));
    }

    private static string Key(string prefix) => $"ak_{prefix}_{new string('S', 32)}";

    private static async Task SeedClientAsync(TestDatabase database, bool revoked)
    {
        database.Db.IntegrationClients.Add(new IntegrationClient
        {
            Name = "Existing client",
            KeyPrefix = "Existing",
            KeyHash = ApiKeyFormat.Hash(Key("Existing")),
            Scopes = "ip:check",
            CreatedAt = database.Clock.GetUtcNow().UtcDateTime,
            RevokedAt = revoked ? database.Clock.GetUtcNow().UtcDateTime : null
        });
        await database.Db.SaveChangesAsync(CancellationToken.None);
    }

    private sealed class SequenceKeyGenerator(IEnumerable<string> keys) : IApiKeyGenerator
    {
        private readonly Queue<string> remaining = new(keys);
        public int Calls { get; private set; }

        public string Generate()
        {
            Calls++;
            return remaining.Dequeue();
        }
    }

    private sealed class TestDatabase(SqliteConnection connection, AllowlistDbContext db) : IAsyncDisposable
    {
        public AllowlistDbContext Db { get; } = db;
        public ManualTimeProvider Clock { get; } = new();

        public static async Task<TestDatabase> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync(CancellationToken.None);
            var db = new AllowlistDbContext(new DbContextOptionsBuilder<AllowlistDbContext>().UseSqlite(connection).Options);
            await db.Database.EnsureCreatedAsync(CancellationToken.None);
            return new(connection, db);
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await connection.DisposeAsync();
        }
    }
}
