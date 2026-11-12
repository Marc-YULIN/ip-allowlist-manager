using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Allowlist.Core;
using Microsoft.Extensions.Options;

namespace Allowlist.Infrastructure;

public sealed class CloudflareListClient(HttpClient http, IOptions<CloudflareOptions> options) : ICloudflareListClient
{
    private readonly CloudflareOptions settings = options.Value;
    private string ItemsPath(string listId) => $"accounts/{Uri.EscapeDataString(settings.AccountId)}/rules/lists/{Uri.EscapeDataString(listId)}/items";

    public async Task<IReadOnlyList<RemoteListItem>> GetItemsAsync(string listId, CancellationToken cancellationToken)
    {
        var items = new List<RemoteListItem>();
        string? cursor = null;
        do
        {
            var path = ItemsPath(listId) + "?per_page=500" + (cursor is null ? "" : "&cursor=" + Uri.EscapeDataString(cursor));
            using var doc = await SendAsync(HttpMethod.Get, path, null, cancellationToken);
            foreach (var item in doc.RootElement.GetProperty("result").EnumerateArray())
                items.Add(new(item.GetProperty("id").GetString()!, item.GetProperty("ip").GetString()!,
                    item.TryGetProperty("comment", out var comment) ? comment.GetString() : null));
            cursor = doc.RootElement.TryGetProperty("result_info", out var info) &&
                info.TryGetProperty("cursors", out var cursors) && cursors.TryGetProperty("after", out var after)
                ? after.GetString() : null;
        } while (!string.IsNullOrEmpty(cursor));
        return items;
    }

    public async Task<string> AddItemsAsync(string listId, IReadOnlyList<RemoteAddItem> items, CancellationToken cancellationToken)
    {
        using var doc = await SendAsync(HttpMethod.Post, ItemsPath(listId),
            items.Select(x => new { ip = x.Cidr, comment = x.Comment }), cancellationToken);
        return doc.RootElement.GetProperty("result").GetProperty("operation_id").GetString()!;
    }
    public async Task<string> DeleteItemsAsync(string listId, IReadOnlyList<string> itemIds, CancellationToken cancellationToken)
    {
        using var doc = await SendAsync(HttpMethod.Delete, ItemsPath(listId),
            new { items = itemIds.Select(x => new { id = x }) }, cancellationToken);
        return doc.RootElement.GetProperty("result").GetProperty("operation_id").GetString()!;
    }
    public async Task<BulkOperation> GetBulkOperationAsync(string operationId, CancellationToken cancellationToken)
    {
        using var doc = await SendAsync(HttpMethod.Get,
            $"accounts/{Uri.EscapeDataString(settings.AccountId)}/rules/lists/bulk_operations/{Uri.EscapeDataString(operationId)}", null, cancellationToken);
        var result = doc.RootElement.GetProperty("result");
        var state = result.GetProperty("status").GetString() switch
        { "pending" => BulkState.Pending, "running" => BulkState.Running, "completed" => BulkState.Completed, "failed" => BulkState.Failed,
            _ => throw new HttpRequestException("Unknown bulk operation status.") };
        return new(state, result.TryGetProperty("error", out var error) ? error.ToString() : null);
    }
    private async Task<JsonDocument> SendAsync(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, new Uri(new Uri(settings.ApiBase.TrimEnd('/') + "/"), path));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.ApiToken);
        if (body is not null) request.Content = JsonContent.Create(body);
        using var response = await http.SendAsync(request, ct);
        if ((int)response.StatusCode >= 500 || response.StatusCode == System.Net.HttpStatusCode.RequestTimeout)
            throw new HttpRequestException("Remote server could not confirm the request result.");
        var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        if (!response.IsSuccessStatusCode || !document.RootElement.TryGetProperty("success", out var success) || !success.GetBoolean())
        {
            var codes = document.RootElement.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array
                ? string.Join(",", errors.EnumerateArray().Select(x => x.TryGetProperty("code", out var code) ? code.ToString() : "unknown")) : "unknown";
            document.Dispose();
            throw new RemoteRejectedException($"Remote request rejected (HTTP {(int)response.StatusCode}, codes {codes}).");
        }
        return document;
    }
}
public sealed class RemoteRejectedException(string message) : Exception(message);
