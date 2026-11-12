namespace Allowlist.Core;

public sealed record RemoteListItem(string Id, string Cidr, string? Comment = null);
public sealed record RemoteAddItem(string Cidr, string Comment);
public sealed record BulkOperation(BulkState State, string? Error = null);

public interface ICloudflareListClient
{
    Task<IReadOnlyList<RemoteListItem>> GetItemsAsync(string listId, CancellationToken cancellationToken);
    Task<string> AddItemsAsync(string listId, IReadOnlyList<RemoteAddItem> items, CancellationToken cancellationToken);
    Task<string> DeleteItemsAsync(string listId, IReadOnlyList<string> itemIds, CancellationToken cancellationToken);
    Task<BulkOperation> GetBulkOperationAsync(string operationId, CancellationToken cancellationToken);
}
