using System.Text.Json;
using Allowlist.Core;
using Allowlist.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Allowlist.Tests;

public sealed class DriftReconcilerTests
{
    private static readonly Actor Operator = new("operator", ActorType.Admin, "drift-test");

    [Theory]
    [InlineData(EntryKind.Allow, EntryState.PendingAdd, null, true)]
    [InlineData(EntryKind.Block, EntryState.PendingAdd, null, true)]
    [InlineData(EntryKind.Allow, EntryState.PendingRemove, null, true)]
    [InlineData(EntryKind.Block, EntryState.PendingRemove, null, true)]
    [InlineData(EntryKind.Allow, EntryState.PendingAdd, null, false)]
    [InlineData(EntryKind.Block, EntryState.PendingRemove, null, false)]
    [InlineData(EntryKind.Allow, EntryState.Active, OperationState.Pending, false)]
    [InlineData(EntryKind.Block, EntryState.Active, OperationState.Pending, true)]
    [InlineData(EntryKind.Allow, EntryState.Active, OperationState.InFlight, true)]
    [InlineData(EntryKind.Block, EntryState.Active, OperationState.InFlight, false)]
    [InlineData(EntryKind.Allow, EntryState.Active, OperationState.Uncertain, false)]
    [InlineData(EntryKind.Block, EntryState.Active, OperationState.Uncertain, true)]
    public async Task ReconcileAsync_InFlightRule_ExcludesDriftOnlyFromItsOwnList(
        EntryKind kind, EntryState state, OperationState? operationState, bool remoteExists)
    {
        await using var fixture = new DatabaseFixture(); fixture.Options.Fake.TimeoutRate = 1;
        await fixture.InitializeAsync();
        var opposite = kind == EntryKind.Allow ? EntryKind.Block : EntryKind.Allow;
        await fixture.ReadAsync(async db =>
        {
            var now = fixture.Clock.GetUtcNow().UtcDateTime;
            var entry = new IpEntry { EnvironmentId = fixture.EnvironmentId, Kind = kind, Cidr = "192.0.2.1/32",
                State = state, CreatedAt = now, UpdatedAt = now };
            db.Entries.Add(entry);
            if (!remoteExists)
                db.Entries.Add(new() { EnvironmentId = fixture.EnvironmentId, Kind = opposite, Cidr = entry.Cidr,
                    State = EntryState.Active, CreatedAt = now, UpdatedAt = now });
            if (operationState is { } opState)
                db.Operations.Add(new() { EnvironmentId = fixture.EnvironmentId, EntryId = entry.Id, ListId = ListId(kind),
                    Action = OperationAction.Add, State = opState, CreatedAt = now, NextAttemptAt = now, LeaseUntil = now.AddMinutes(1) });
            return await db.SaveChangesAsync();
        });
        if (remoteExists)
        {
            await AddRemoteAsync(fixture, kind, "192.0.2.1");
            await AddRemoteAsync(fixture, opposite, "192.0.2.1");
        }
        var snapshots = await ReconcileAsync(fixture, true);
        Assert.Equal(2, snapshots.Count);
        var own = Assert.Single(snapshots, x => x.ListKind == kind);
        Assert.Empty(Missing(own)); Assert.Empty(Extra(own));
        var other = Assert.Single(snapshots, x => x.ListKind == opposite);
        if (remoteExists) Assert.Single(Extra(other));
        else Assert.Equal(["192.0.2.1/32"], Missing(other));
        Assert.False(await fixture.ReadAsync(db => db.Operations.AnyAsync(x => x.ListId == ListId(kind) &&
            (operationState == null || x.State != operationState))));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReconcileAsync_TwoListsWithDrift_QueuesRepairsForTheCorrectList(bool allowRemoveUnknown)
    {
        await using var fixture = new DatabaseFixture(); fixture.Options.Fake.TimeoutRate = 1;
        fixture.Options.AllowRemoveUnknown = allowRemoveUnknown;
        await fixture.InitializeAsync();
        await fixture.ReadAsync(async db =>
        {
            var now = fixture.Clock.GetUtcNow().UtcDateTime;
            db.Entries.AddRange(
                new() { EnvironmentId = fixture.EnvironmentId, Kind = EntryKind.Allow, Cidr = "192.0.2.1/32", State = EntryState.Active, CreatedAt = now, UpdatedAt = now },
                new() { EnvironmentId = fixture.EnvironmentId, Kind = EntryKind.Block, Cidr = "198.51.100.1/32", State = EntryState.Active, CreatedAt = now, UpdatedAt = now });
            return await db.SaveChangesAsync();
        });
        await AddRemoteAsync(fixture, EntryKind.Allow, "198.51.100.1");
        await AddRemoteAsync(fixture, EntryKind.Block, "192.0.2.1");
        var snapshots = await ReconcileAsync(fixture, true);
        Assert.Equal(2, snapshots.Count);
        foreach (var snapshot in snapshots)
        {
            Assert.Single(Missing(snapshot)); Assert.Single(Extra(snapshot));
            Assert.Equal(1, snapshot.LocalCount); Assert.Equal(1, snapshot.RemoteCount);
            Assert.True(snapshot.AutoFixed);
        }
        var operations = await fixture.ReadAsync(db => db.Operations.ToListAsync());
        var entries = await fixture.ReadAsync(db => db.Entries.ToListAsync());
        Assert.Equal(allowRemoveUnknown ? 4 : 2, operations.Count);
        foreach (var op in operations.Where(x => x.Action == OperationAction.Add))
        {
            var entry = Assert.Single(entries, x => x.Id == op.EntryId);
            Assert.Equal(ListId(entry.Kind), op.ListId); Assert.Equal(EntryState.PendingAdd, entry.State);
        }
        foreach (var op in operations.Where(x => x.Action == OperationAction.Remove))
        {
            Assert.Null(op.EntryId);
            var remote = await fixture.Client.GetItemsAsync(op.ListId, CancellationToken.None);
            Assert.Equal(Assert.Single(remote).Id, op.RemoteItemId);
        }
        var repeated = await ReconcileAsync(fixture, true);
        Assert.All(repeated, snapshot => Assert.Empty(Missing(snapshot)));
        if (allowRemoveUnknown) Assert.All(repeated, snapshot => Assert.Empty(Extra(snapshot)));
        Assert.Equal(operations.Count, await fixture.ReadAsync(db => db.Operations.CountAsync()));
        Assert.Equal(4, await fixture.ReadAsync(db => db.Snapshots.CountAsync()));
    }

    [Theory]
    [InlineData(EntryKind.Allow)]
    [InlineData(EntryKind.Block)]
    public async Task ProcessAsync_EnvironmentMappingChanges_UsesPersistedListForDispatchAndUncertainResolution(EntryKind removeKind)
    {
        await using var fixture = new DatabaseFixture(); fixture.Options.Fake.TimeoutRate = 1;
        await fixture.InitializeAsync();
        await using (var scope = fixture.Provider.CreateAsyncScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<EntryService>();
            await service.CreateAsync("dev", new("192.0.2.1", EntryKind.Allow), Operator, CancellationToken.None);
            await service.CreateAsync("dev", new("192.0.2.1", EntryKind.Block), Operator, CancellationToken.None);
        }
        await ChangeMappingAsync(fixture, "replacement-allow", "replacement-block");
        await fixture.Dispatcher().RunOnceAsync(CancellationToken.None);
        Assert.Equal(2, await fixture.ReadAsync(db => db.Operations.CountAsync(x => x.State == OperationState.Uncertain)));
        await ResolveAsync(fixture);
        Assert.Equal(2, await fixture.ReadAsync(db => db.Operations.CountAsync(x => x.State == OperationState.Succeeded)));
        var entries = await fixture.ReadAsync(db => db.Entries.ToListAsync());
        foreach (var entry in entries)
        {
            var remote = Assert.Single(await fixture.Client.GetItemsAsync(ListId(entry.Kind), CancellationToken.None));
            Assert.Equal("192.0.2.1/32", remote.Cidr); Assert.Equal(remote.Id, entry.RemoteItemId);
            Assert.Equal(EntryState.Active, entry.State);
        }
        Assert.Empty(await fixture.Client.GetItemsAsync("replacement-allow", CancellationToken.None));
        Assert.Empty(await fixture.Client.GetItemsAsync("replacement-block", CancellationToken.None));
        await ChangeMappingAsync(fixture, DatabaseFixture.AllowListId, DatabaseFixture.BlockListId);
        var removed = Assert.Single(entries, x => x.Kind == removeKind);
        await using (var scope = fixture.Provider.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<EntryService>().RemoveAsync("dev", removed.Id, Operator, CancellationToken.None);
        await ChangeMappingAsync(fixture, "replacement-allow", "replacement-block");
        await fixture.Dispatcher().RunOnceAsync(CancellationToken.None);
        await ResolveAsync(fixture);
        Assert.Empty(await fixture.Client.GetItemsAsync(ListId(removeKind), CancellationToken.None));
        var retained = Assert.Single(entries, x => x.Kind != removeKind);
        Assert.Single(await fixture.Client.GetItemsAsync(ListId(retained.Kind), CancellationToken.None));
        Assert.Equal(EntryState.Removed, (await fixture.ReadAsync(db => db.Entries.SingleAsync(x => x.Id == removed.Id))).State);
        Assert.Equal(EntryState.Active, (await fixture.ReadAsync(db => db.Entries.SingleAsync(x => x.Id == retained.Id))).State);
        Assert.Equal(3, await fixture.ReadAsync(db => db.Operations.CountAsync(x => x.State == OperationState.Succeeded)));
    }

    private static string ListId(EntryKind kind) => kind == EntryKind.Allow ? DatabaseFixture.AllowListId : DatabaseFixture.BlockListId;
    private static string[] Missing(DriftSnapshot snapshot) => JsonSerializer.Deserialize<string[]>(snapshot.MissingRemoteJson)!;
    private static RemoteListItem[] Extra(DriftSnapshot snapshot) => JsonSerializer.Deserialize<RemoteListItem[]>(snapshot.ExtraRemoteJson)!;

    private static async Task AddRemoteAsync(DatabaseFixture fixture, EntryKind kind, string cidr) =>
        await Assert.ThrowsAsync<TaskCanceledException>(() => fixture.Client.AddItemsAsync(ListId(kind), [new(cidr, "")], CancellationToken.None));

    private static async Task<IReadOnlyList<DriftSnapshot>> ReconcileAsync(DatabaseFixture fixture, bool autoFix)
    {
        await using var scope = fixture.Provider.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<DriftReconciler>().ReconcileAsync(fixture.EnvironmentId, autoFix, Operator, CancellationToken.None);
    }

    private static async Task ResolveAsync(DatabaseFixture fixture)
    {
        await using var scope = fixture.Provider.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<DriftReconciler>().ResolveUncertainAsync(fixture.EnvironmentId, CancellationToken.None);
    }

    private static async Task ChangeMappingAsync(DatabaseFixture fixture, string allowId, string blockId) =>
        await fixture.ReadAsync(async db =>
        {
            var env = await db.Environments.SingleAsync(); env.AllowListId = allowId; env.BlockListId = blockId;
            return await db.SaveChangesAsync();
        });
}
