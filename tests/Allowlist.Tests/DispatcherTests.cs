using Allowlist.Core;
using Allowlist.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Allowlist.Tests;

public sealed class DispatcherTests
{
    [Fact]
    public async Task ClaimAsync_TwoConcurrentWorkers_ClaimsEachOperationExactlyOnce()
    {
        await using var fixture = new DatabaseFixture(); await fixture.InitializeAsync();
        var expected = await fixture.SeedAsync(20);
        var first = fixture.Dispatcher(); var second = fixture.Dispatcher();
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var a = Task.Run(async () => { await ready.Task; return await first.ClaimAsync(CancellationToken.None); });
        var b = Task.Run(async () => { await ready.Task; return await second.ClaimAsync(CancellationToken.None); });
        ready.SetResult();
        var results = await Task.WhenAll(a, b);
        var claimed = results.SelectMany(x => x).ToArray();
        Assert.Equal(20, claimed.Length); Assert.Equal(20, claimed.Distinct().Count());
        Assert.True(expected.ToHashSet().SetEquals(claimed));
        Assert.Equal(20, await fixture.ReadAsync(db => db.AuditLogs.CountAsync(x => x.Action == "operation.claimed")));
    }

    [Fact]
    public async Task ClaimAsync_ExpiredLease_CanBeReclaimed()
    {
        await using var fixture = new DatabaseFixture(); await fixture.InitializeAsync(); await fixture.SeedAsync();
        var first = fixture.Dispatcher(); var second = fixture.Dispatcher();
        var original = Assert.Single(await first.ClaimAsync(CancellationToken.None));
        Assert.Empty(await second.ClaimAsync(CancellationToken.None));
        fixture.Clock.Advance(TimeSpan.FromSeconds(61));
        Assert.Equal(original, Assert.Single(await second.ClaimAsync(CancellationToken.None)));
    }

    [Fact]
    public async Task ProcessAsync_TimeoutAfterWrite_ReconcilesWithoutDuplicateDispatch()
    {
        await using var fixture = new DatabaseFixture(); fixture.Options.Fake.TimeoutRate = 1;
        await fixture.InitializeAsync(); var id = Assert.Single(await fixture.SeedAsync());
        await fixture.Dispatcher().RunOnceAsync(CancellationToken.None);
        Assert.Equal(OperationState.Uncertain, (await fixture.ReadAsync(db => db.Operations.SingleAsync())).State);
        await using (var scope = fixture.Provider.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<DriftReconciler>().ResolveUncertainAsync(fixture.EnvironmentId, CancellationToken.None);
        Assert.Equal(OperationState.Succeeded, (await fixture.ReadAsync(db => db.Operations.SingleAsync())).State);
        Assert.Single(await fixture.Client.GetItemsAsync("list-dev", CancellationToken.None));
        Assert.Equal(1, await fixture.ReadAsync(db => db.DispatchLogs.CountAsync(x => x.OperationId == id && x.Phase == DispatchPhase.Dispatching)));
        Assert.Equal(EntryState.Active, (await fixture.ReadAsync(db => db.Entries.SingleAsync())).State);
    }

    [Fact]
    public async Task ProcessAsync_ExplicitFailure_BacksOffThenDeadLetters()
    {
        await using var fixture = new DatabaseFixture(); fixture.Options.Fake.FailureRate = 1;
        await fixture.InitializeAsync(); await fixture.SeedAsync();
        var dispatcher = fixture.Dispatcher();
        for (var attempt = 1; attempt <= 6; attempt++)
        {
            await dispatcher.RunOnceAsync(CancellationToken.None);
            var op = await fixture.ReadAsync(db => db.Operations.SingleAsync());
            Assert.Equal(attempt, op.Attempts);
            if (attempt < 6)
            {
                Assert.Equal(OperationState.Pending, op.State);
                Assert.Equal(TimeSpan.FromSeconds(Math.Min(600, 5 * Math.Pow(2, attempt))), op.NextAttemptAt - fixture.Clock.GetUtcNow().UtcDateTime);
                Assert.Empty(await fixture.Dispatcher().ClaimAsync(CancellationToken.None));
                fixture.Clock.Advance(TimeSpan.FromMinutes(11));
            }
            else Assert.Equal(OperationState.DeadLetter, op.State);
        }
        Assert.Equal(EntryState.Failed, (await fixture.ReadAsync(db => db.Entries.SingleAsync())).State);
    }

    [Fact]
    public async Task ProcessAsync_RemoteMutation_PersistsDispatchingBeforeCall()
    {
        await using var fixture = new DatabaseFixture();
        var client = Substitute.For<ICloudflareListClient>();
        client.GetItemsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<RemoteListItem>>([]));
        var inspected = false;
        client.AddItemsAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<RemoteAddItem>>(), Arg.Any<CancellationToken>())
            .Returns(async _ =>
            {
                inspected = await fixture.ReadAsync(db => db.DispatchLogs.AnyAsync(x => x.Phase == DispatchPhase.Dispatching));
                return await Task.FromException<string>(new TaskCanceledException("Accepted before timeout."));
            });
        await fixture.InitializeAsync(client); await fixture.SeedAsync();
        await fixture.Dispatcher().RunOnceAsync(CancellationToken.None);
        Assert.True(inspected);
    }

    [Fact]
    public async Task ProcessAsync_AcceptedOperationThenPollingRejected_PreservesBulkIdAsUncertain()
    {
        await using var fixture = new DatabaseFixture();
        var client = Substitute.For<ICloudflareListClient>();
        client.GetItemsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<RemoteListItem>>([]));
        client.AddItemsAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<RemoteAddItem>>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult("accepted-bulk"));
        client.GetBulkOperationAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Task.FromException<BulkOperation>(new RemoteRejectedException("Observation denied.")));
        await fixture.InitializeAsync(client); await fixture.SeedAsync();
        await fixture.Dispatcher().RunOnceAsync(CancellationToken.None);
        var op = await fixture.ReadAsync(db => db.Operations.SingleAsync());
        Assert.Equal(OperationState.Uncertain, op.State); Assert.Equal("accepted-bulk", op.RemoteOperationId); Assert.Equal(0, op.Attempts);
    }

    [Fact]
    public async Task ProcessAsync_ExpiredDispatchIntent_ReconcilesInsteadOfResending()
    {
        await using var fixture = new DatabaseFixture(); await fixture.InitializeAsync(); var id = Assert.Single(await fixture.SeedAsync());
        await fixture.Dispatcher().ClaimAsync(CancellationToken.None);
        await fixture.ReadAsync(async db => { db.DispatchLogs.Add(new() { OperationId = id, Attempt = 1, Phase = DispatchPhase.Dispatching, At = fixture.Clock.GetUtcNow().UtcDateTime }); return await db.SaveChangesAsync(); });
        fixture.Clock.Advance(TimeSpan.FromSeconds(61));
        await fixture.Dispatcher().RunOnceAsync(CancellationToken.None);
        Assert.Equal(OperationState.Uncertain, (await fixture.ReadAsync(db => db.Operations.SingleAsync())).State);
        Assert.Empty(await fixture.Client.GetItemsAsync("list-dev", CancellationToken.None));
    }

    [Fact]
    public async Task ResolveUncertainAsync_FakeRestartLosesBulkId_QueuesSafeRetryAfterObservation()
    {
        await using var fixture = new DatabaseFixture(); await fixture.InitializeAsync(); await fixture.SeedAsync();
        await fixture.ReadAsync(async db => { var op = await db.Operations.SingleAsync(); op.State = OperationState.Uncertain; op.RemoteOperationId = "lost-bulk"; return await db.SaveChangesAsync(); });
        await using var scope = fixture.Provider.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<DriftReconciler>().ResolveUncertainAsync(fixture.EnvironmentId, CancellationToken.None);
        Assert.Equal(OperationState.Pending, (await fixture.ReadAsync(db => db.Operations.SingleAsync())).State);
    }

    [Fact]
    public async Task ProcessAsync_OppositeKindAddedDuringRemoval_LeavesBlockListUntouched()
    {
        await using var fixture = new DatabaseFixture();
        var lists = new Dictionary<string, List<RemoteListItem>>
        {
            [DatabaseFixture.AllowListId] = [new("old", "192.0.2.1/32")],
            [DatabaseFixture.BlockListId] = []
        };
        var client = Substitute.For<ICloudflareListClient>();
        client.GetItemsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult<IReadOnlyList<RemoteListItem>>(lists[call.ArgAt<string>(0)].ToArray()));
        client.GetBulkOperationAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult(new BulkOperation(BulkState.Completed)));
        client.AddItemsAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<RemoteAddItem>>(), Arg.Any<CancellationToken>())
            .Returns(call => { lists[call.ArgAt<string>(0)].Add(new("block-item", "192.0.2.1/32")); return Task.FromResult("add-bulk"); });
        client.DeleteItemsAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>()).Returns(async call =>
        {
            await using (var scope = fixture.Provider.CreateAsyncScope())
                await scope.ServiceProvider.GetRequiredService<EntryService>().CreateAsync("dev", new("192.0.2.1", EntryKind.Block),
                    new("operator", ActorType.Admin, "concurrent-block"), CancellationToken.None);
            await fixture.Dispatcher().RunOnceAsync(CancellationToken.None);
            lists[call.ArgAt<string>(0)].Clear();
            return "remove-bulk";
        });
        await fixture.InitializeAsync(client); await fixture.SeedAsync();
        await fixture.ReadAsync(async db =>
        {
            var entry = await db.Entries.SingleAsync(); entry.State = EntryState.PendingRemove; entry.RemoteItemId = "old";
            var op = await db.Operations.SingleAsync(); op.Action = OperationAction.Remove;
            return await db.SaveChangesAsync();
        });
        await fixture.Dispatcher().RunOnceAsync(CancellationToken.None);
        var block = await fixture.ReadAsync(db => db.Entries.SingleAsync(x => x.Kind == EntryKind.Block));
        Assert.Equal(EntryState.Active, block.State);
        Assert.Equal("block-item", block.RemoteItemId);
        Assert.Equal(EntryState.Removed, (await fixture.ReadAsync(db => db.Entries.SingleAsync(x => x.Kind == EntryKind.Allow))).State);
        Assert.Empty(lists[DatabaseFixture.AllowListId]);
        Assert.Equal("block-item", Assert.Single(lists[DatabaseFixture.BlockListId]).Id);
        Assert.Equal(2, await fixture.ReadAsync(db => db.Operations.CountAsync(x => x.State == OperationState.Succeeded)));
        Assert.Equal(2, await fixture.ReadAsync(db => db.Operations.CountAsync()));
    }
}
