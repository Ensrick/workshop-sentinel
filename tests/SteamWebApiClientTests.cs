using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using WorkshopSentinel.Services;
using Xunit;

namespace WorkshopSentinel.Tests;

public sealed class SteamWebApiClientTests
{
    [Fact]
    public void ParseResponse_extracts_public_item_fields()
    {
        var json = """
            { "response": {
                "result": 1, "resultcount": 1,
                "publishedfiledetails": [{
                    "publishedfileid": "3712929235",
                    "result": 1,
                    "file_size": "1399903",
                    "title": "Tweaker: Chaos Wastes",
                    "time_created": 1776993035,
                    "time_updated": 1778811142,
                    "visibility": 0,
                    "banned": 0
                }]
            }}
            """;

        var parsed = SteamWebApiClient.ParseResponse(json, new[] { 3712929235ul });

        Assert.Single(parsed);
        var r = parsed[3712929235ul];
        Assert.Equal("Tweaker: Chaos Wastes", r.Title);
        Assert.Equal(1778811142L, r.RemoteTimeUpdated);
        Assert.Equal(1399903L, r.RemoteSizeBytes);
        Assert.Equal(0, r.Visibility);
        Assert.Equal(false, r.Banned);
        Assert.Equal(1, r.ApiResult);
    }

    [Fact]
    public void ParseResponse_handles_result9_friends_only_item()
    {
        var json = """
            { "response": {
                "result": 1, "resultcount": 1,
                "publishedfiledetails": [{ "publishedfileid": "3712896117", "result": 9 }]
            }}
            """;

        var parsed = SteamWebApiClient.ParseResponse(json, new[] { 3712896117ul });

        var r = parsed[3712896117ul];
        Assert.Equal(9, r.ApiResult);
        Assert.Null(r.Title);
        Assert.Null(r.RemoteTimeUpdated);
        Assert.Null(r.Banned);
    }

    [Fact]
    public void ParseResponse_marks_unreturned_ids_as_failed()
    {
        var json = """
            { "response": { "result": 1, "resultcount": 0, "publishedfiledetails": [] } }
            """;

        var parsed = SteamWebApiClient.ParseResponse(json, new[] { 100ul, 200ul });

        Assert.Equal(2, parsed.Count);
        Assert.Equal(-1, parsed[100ul].ApiResult);
        Assert.Equal(-1, parsed[200ul].ApiResult);
    }

    [Fact]
    public void ParseResponse_corrupt_json_marks_all_failed()
    {
        var parsed = SteamWebApiClient.ParseResponse("not json at all", new[] { 1ul, 2ul });

        Assert.Equal(-1, parsed[1ul].ApiResult);
        Assert.Equal(-1, parsed[2ul].ApiResult);
    }

    [Fact]
    public async Task GetPublishedFileDetails_batches_above_100_into_two_requests()
    {
        var ids = Enumerable.Range(1, 150).Select(i => (ulong)i).ToList();
        var seenBatchSizes = new List<int>();

        var handler = new MockHandler(req =>
        {
            var body = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            // FormUrlEncodedContent URL-encodes the `[` in `publishedfileids[0]` to `%5B`.
            // Match the encoded form.
            var count = body.Split('&').Count(p => p.StartsWith("publishedfileids%5B"));
            seenBatchSizes.Add(count);
            return RespondWith(BuildResultArray(count));
        });

        var client = new SteamWebApiClient(new HttpClient(handler));
        var result = await client.GetPublishedFileDetailsAsync(ids);

        Assert.Equal(new[] { 100, 50 }, seenBatchSizes);
        Assert.Equal(150, result.Count);
    }

    [Fact]
    public async Task GetPublishedFileDetails_retries_on_429_then_succeeds()
    {
        var attempts = 0;
        var handler = new MockHandler(req =>
        {
            attempts++;
            return attempts < 3
                ? new HttpResponseMessage(HttpStatusCode.TooManyRequests)
                : RespondWith(BuildResultArray(1));
        });
        var client = new SteamWebApiClient(new HttpClient(handler), maxAttempts: 3, backoff: _ => TimeSpan.Zero);

        // BuildResultArray(1) returns id=1, so request id=1 to match.
        var result = await client.GetPublishedFileDetailsAsync(new[] { 1ul });

        Assert.Equal(3, attempts);
        Assert.Single(result);
        Assert.Equal(1, result[1ul].ApiResult);
    }

    [Fact]
    public async Task GetPublishedFileDetails_gives_up_after_max_attempts_marks_failed()
    {
        var attempts = 0;
        var handler = new MockHandler(_ =>
        {
            attempts++;
            return new HttpResponseMessage(HttpStatusCode.InternalServerError);
        });
        var client = new SteamWebApiClient(new HttpClient(handler), maxAttempts: 3, backoff: _ => TimeSpan.Zero);

        var result = await client.GetPublishedFileDetailsAsync(new[] { 42ul });

        Assert.Equal(3, attempts);
        Assert.Equal(-1, result[42ul].ApiResult);
    }

    [Fact]
    public async Task GetPublishedFileDetails_does_not_retry_on_4xx_other_than_429()
    {
        var attempts = 0;
        var handler = new MockHandler(_ =>
        {
            attempts++;
            return new HttpResponseMessage(HttpStatusCode.BadRequest);
        });
        var client = new SteamWebApiClient(new HttpClient(handler), maxAttempts: 3, backoff: _ => TimeSpan.Zero);

        var result = await client.GetPublishedFileDetailsAsync(new[] { 42ul });

        Assert.Equal(1, attempts);
        Assert.Equal(-1, result[42ul].ApiResult);
    }

    // ---------- helpers ----------

    private static HttpResponseMessage RespondWith(string responseBody) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(responseBody, System.Text.Encoding.UTF8, "application/json"),
        };

    private static string BuildResultArray(int count)
    {
        var entries = string.Join(",", Enumerable.Range(1, count).Select(i =>
            $$"""{ "publishedfileid": "{{i}}", "result": 1, "time_updated": 1700000000, "title": "Item {{i}}" }"""));
        return $$"""{ "response": { "result": 1, "resultcount": {{count}}, "publishedfiledetails": [{{entries}}] } }""";
    }

    private sealed class MockHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;
        public MockHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) { _respond = respond; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(_respond(request));
    }
}
