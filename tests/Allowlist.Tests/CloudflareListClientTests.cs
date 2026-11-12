using System.Net;
using System.Text;
using System.Text.Json;
using Allowlist.Core;
using Allowlist.Infrastructure;
using Microsoft.Extensions.Options;

namespace Allowlist.Tests;

public sealed class CloudflareListClientTests
{
    [Fact]
    public async Task GetItemsAsync_PaginatedEnvelope_SendsAuthenticatedRequestsAndCombinesItems()
    {
        var calls = 0;
        using var http = new HttpClient(new StubHandler((request, _) =>
        {
            AssertRequest(request, HttpMethod.Get, "/client/v4/accounts/account-demo/rules/lists/list-demo/items");
            calls++;
            if (calls == 1)
            {
                Assert.Equal("?per_page=500", request.RequestUri!.Query);
                return Task.FromResult(JsonResponse("""
                    {"success":true,"errors":[],"result":[{"id":"item-one","ip":"192.0.2.1","comment":"Office"}],"result_info":{"cursors":{"after":"next+/="}}}
                    """));
            }
            Assert.Equal(2, calls);
            Assert.Equal("?per_page=500&cursor=next%2B%2F%3D", request.RequestUri!.Query);
            return Task.FromResult(JsonResponse("""
                {"success":true,"errors":[],"result":[{"id":"item-two","ip":"2001:db8::/64"}],"result_info":{"cursors":{"after":null}}}
                """));
        }));
        var client = CreateClient(http);

        var items = await client.GetItemsAsync("list-demo", CancellationToken.None);

        Assert.Equal(2, calls);
        Assert.Collection(items,
            item => Assert.Equal(new RemoteListItem("item-one", "192.0.2.1", "Office"), item),
            item => Assert.Equal(new RemoteListItem("item-two", "2001:db8::/64"), item));
    }

    [Fact]
    public async Task AddItemsAsync_ValidEnvelope_PostsItemsAndReturnsOperationId()
    {
        var calls = 0;
        using var http = new HttpClient(new StubHandler(async (request, ct) =>
        {
            calls++;
            AssertRequest(request, HttpMethod.Post, "/client/v4/accounts/account-demo/rules/lists/list-demo/items");
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            var item = Assert.Single(body.RootElement.EnumerateArray());
            Assert.Equal("192.0.2.0/24", item.GetProperty("ip").GetString());
            Assert.Equal("Office network", item.GetProperty("comment").GetString());
            return JsonResponse("""{"success":true,"errors":[],"result":{"operation_id":"add-operation"}}""");
        }));
        var client = CreateClient(http);

        var id = await client.AddItemsAsync("list-demo", [new("192.0.2.0/24", "Office network")], CancellationToken.None);

        Assert.Equal("add-operation", id);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task DeleteItemsAsync_ValidEnvelope_SendsItemIdsAndReturnsOperationId()
    {
        var calls = 0;
        using var http = new HttpClient(new StubHandler(async (request, ct) =>
        {
            calls++;
            AssertRequest(request, HttpMethod.Delete, "/client/v4/accounts/account-demo/rules/lists/list-demo/items");
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            Assert.Collection(body.RootElement.GetProperty("items").EnumerateArray(),
                item => Assert.Equal("item-one", item.GetProperty("id").GetString()),
                item => Assert.Equal("item-two", item.GetProperty("id").GetString()));
            return JsonResponse("""{"success":true,"errors":[],"result":{"operation_id":"delete-operation"}}""");
        }));
        var client = CreateClient(http);

        var id = await client.DeleteItemsAsync("list-demo", ["item-one", "item-two"], CancellationToken.None);

        Assert.Equal("delete-operation", id);
        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData("pending", BulkState.Pending, null)]
    [InlineData("running", BulkState.Running, null)]
    [InlineData("completed", BulkState.Completed, null)]
    [InlineData("failed", BulkState.Failed, "Unable to update list")]
    public async Task GetBulkOperationAsync_ValidEnvelope_ParsesStateAndError(string status, BulkState expected, string? error)
    {
        var calls = 0;
        using var http = new HttpClient(new StubHandler((request, _) =>
        {
            calls++;
            AssertRequest(request, HttpMethod.Get, "/client/v4/accounts/account-demo/rules/lists/bulk_operations/operation-demo");
            var result = new Dictionary<string, string> { ["status"] = status };
            if (error is not null) result["error"] = error;
            return Task.FromResult(JsonResponse(JsonSerializer.Serialize(new
            {
                success = true,
                errors = Array.Empty<object>(),
                result
            })));
        }));
        var client = CreateClient(http);

        var operation = await client.GetBulkOperationAsync("operation-demo", CancellationToken.None);

        Assert.Equal(expected, operation.State);
        Assert.Equal(error, operation.Error);
        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData(HttpStatusCode.OK)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task GetItemsAsync_UnsuccessfulEnvelope_ThrowsRejectionWithErrorCodes(HttpStatusCode status)
    {
        using var http = new HttpClient(new StubHandler((_, _) => Task.FromResult(JsonResponse("""
            {"success":false,"errors":[{"code":10001,"message":"Rejected"},{"code":10002,"message":"Invalid list"}],"result":null}
            """, status))));
        var client = CreateClient(http);

        var exception = await Assert.ThrowsAsync<RemoteRejectedException>(() => client.GetItemsAsync("list-demo", CancellationToken.None));

        Assert.Contains($"HTTP {(int)status}", exception.Message);
        Assert.Contains("10001,10002", exception.Message);
        Assert.DoesNotContain("sample-token", exception.Message);
    }

    private static CloudflareListClient CreateClient(HttpClient http) => new(http, Options.Create(new CloudflareOptions
    {
        ApiBase = "https://remote.invalid/client/v4/",
        AccountId = "account-demo",
        ApiToken = "sample-token"
    }));

    private static void AssertRequest(HttpRequestMessage request, HttpMethod method, string path)
    {
        Assert.Equal(method, request.Method);
        Assert.Equal("remote.invalid", request.RequestUri!.Host);
        Assert.Equal(path, request.RequestUri.AbsolutePath);
        Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
        Assert.Equal("sample-token", request.Headers.Authorization?.Parameter);
    }

    private static HttpResponseMessage JsonResponse(string body, HttpStatusCode status = HttpStatusCode.OK) => new(status)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json")
    };

    private sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            respond(request, cancellationToken);
    }
}
