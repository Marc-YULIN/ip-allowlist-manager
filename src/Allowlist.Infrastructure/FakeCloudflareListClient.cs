using Allowlist.Core;
using Microsoft.Extensions.Options;

namespace Allowlist.Infrastructure;

public sealed class FakeCloudflareListClient(TimeProvider clock, IOptions<CloudflareOptions> options) : ICloudflareListClient
{
    private readonly object gate = new();
    private readonly Dictionary<string, List<RemoteListItem>> lists = [];
    private readonly Dictionary<string, (DateTime ReadyAt, Action Apply)> operations = [];
    private List<RemoteListItem> List(string id)
    {
        if (!lists.TryGetValue(id, out var list)) lists[id] = list = [];
        return list;
    }
    private void CompleteReady()
    {
        foreach (var op in operations.Values.Where(x => x.ReadyAt <= clock.GetUtcNow().UtcDateTime)) op.Apply();
    }
    public Task<IReadOnlyList<RemoteListItem>> GetItemsAsync(string listId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate) { CompleteReady(); return Task.FromResult<IReadOnlyList<RemoteListItem>>(List(listId).ToArray()); }
    }
    public Task<string> AddItemsAsync(string listId, IReadOnlyList<RemoteAddItem> items, CancellationToken cancellationToken)
        => Dispatch(() =>
        {
            foreach (var item in items)
                if (!List(listId).Any(x => x.Cidr == item.Cidr)) List(listId).Add(new(Guid.NewGuid().ToString("N"), item.Cidr, item.Comment));
        }, cancellationToken);
    public Task<string> DeleteItemsAsync(string listId, IReadOnlyList<string> itemIds, CancellationToken cancellationToken)
        => Dispatch(() => List(listId).RemoveAll(x => itemIds.Contains(x.Id)), cancellationToken);
    private Task<string> Dispatch(Action action, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (gate)
        {
            if (Random.Shared.NextDouble() < options.Value.Fake.FailureRate) throw new RemoteRejectedException("Simulated rejection.");
            if (Random.Shared.NextDouble() < options.Value.Fake.TimeoutRate)
            { action(); throw new TaskCanceledException("Simulated timeout after acceptance."); }
            var id = Guid.NewGuid().ToString("N");
            var applied = false;
            operations[id] = (clock.GetUtcNow().UtcDateTime.AddSeconds(Random.Shared.Next(1, 4)), () =>
            { if (!applied) { action(); applied = true; } });
            return Task.FromResult(id);
        }
    }
    public Task<BulkOperation> GetBulkOperationAsync(string operationId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            if (!operations.TryGetValue(operationId, out var op))
                return Task.FromResult(new BulkOperation(BulkState.Failed, "Simulated operation was lost after restart; observe the list before retrying."));
            var ready = op.ReadyAt <= clock.GetUtcNow().UtcDateTime;
            if (ready) op.Apply();
            return Task.FromResult(new BulkOperation(ready ? BulkState.Completed : BulkState.Running));
        }
    }
}
