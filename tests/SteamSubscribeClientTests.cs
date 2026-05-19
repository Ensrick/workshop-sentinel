using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using WorkshopSentinel.Services;
using Xunit;

namespace WorkshopSentinel.Tests;

public class SteamSubscribeClientTests
{
    private static readonly SteamSessionCookies FakeCookies =
        new("76561198211120891%7Cfake-jwt", "fakesession123");

    [Fact]
    public void ParseResponse_success_1_is_Ok()
    {
        var r = SteamSubscribeClient.ParseResponse("{\"success\":1}");
        Assert.Equal(SubscribeOutcome.Ok, r.Outcome);
        Assert.True(r.Success);
    }

    [Fact]
    public void ParseResponse_success_non_one_is_ServerRejected()
    {
        var r = SteamSubscribeClient.ParseResponse("{\"success\":29}");
        Assert.Equal(SubscribeOutcome.ServerRejected, r.Outcome);
        Assert.False(r.Success);
        Assert.Contains("success=29", r.ErrorDetail ?? "");
    }

    [Fact]
    public void ParseResponse_no_success_field_is_MalformedResponse()
    {
        var r = SteamSubscribeClient.ParseResponse("{\"foo\":\"bar\"}");
        Assert.Equal(SubscribeOutcome.MalformedResponse, r.Outcome);
    }

    [Fact]
    public void ParseResponse_html_login_page_is_MalformedResponse()
    {
        // Steam serves the login page (HTML) when cookies are stale on some endpoints
        // — we shouldn't crash on it.
        var r = SteamSubscribeClient.ParseResponse("<!DOCTYPE html><html><body>Login</body></html>");
        Assert.Equal(SubscribeOutcome.MalformedResponse, r.Outcome);
    }

    [Fact]
    public void ParseResponse_empty_body_is_MalformedResponse()
    {
        Assert.Equal(SubscribeOutcome.MalformedResponse,
            SteamSubscribeClient.ParseResponse("").Outcome);
        Assert.Equal(SubscribeOutcome.MalformedResponse,
            SteamSubscribeClient.ParseResponse("   ").Outcome);
    }

    [Fact]
    public async Task SubscribeAsync_posts_form_body_with_correct_fields()
    {
        HttpRequestMessage? captured = null;
        string? body = null;
        var http = new HttpClient(new StubHandler(async req =>
        {
            captured = req;
            body = await req.Content!.ReadAsStringAsync();
            return new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent("{\"success\":1}") };
        }));

        var client = new SteamSubscribeClient(http, FakeCookies);
        var result = await client.SubscribeAsync(appId: 552500, publishedFileId: 3712929235);

        Assert.True(result.Success);
        Assert.Equal(SubscribeOutcome.Ok, result.Outcome);

        Assert.NotNull(captured);
        Assert.Equal(HttpMethod.Post, captured!.Method);
        Assert.Equal("https://steamcommunity.com/sharedfiles/subscribe", captured.RequestUri!.AbsoluteUri);

        // Form body must include id, appid, sessionid (URL-encoded form params).
        Assert.NotNull(body);
        Assert.Contains("id=3712929235", body);
        Assert.Contains("appid=552500", body);
        Assert.Contains("sessionid=fakesession123", body);
    }

    [Fact]
    public async Task SubscribeAsync_sends_required_headers_and_cookies()
    {
        HttpRequestMessage? captured = null;
        var http = new HttpClient(new StubHandler(req =>
        {
            captured = req;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent("{\"success\":1}") });
        }));

        var client = new SteamSubscribeClient(http, FakeCookies);
        _ = await client.SubscribeAsync(552500, 100);

        Assert.NotNull(captured);
        Assert.Equal("https://steamcommunity.com", captured!.Headers.GetValues("Origin").First());
        Assert.Equal("XMLHttpRequest",             captured.Headers.GetValues("X-Requested-With").First());
        var cookieHeader = string.Join(";", captured.Headers.GetValues("Cookie"));
        Assert.Contains("sessionid=fakesession123",                            cookieHeader);
        Assert.Contains("steamLoginSecure=76561198211120891%7Cfake-jwt",        cookieHeader);
    }

    [Fact]
    public async Task SubscribeAsync_maps_401_to_AuthFailed()
    {
        var http = new HttpClient(new StubHandler(_ =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized))));
        var client = new SteamSubscribeClient(http, FakeCookies);
        var r = await client.SubscribeAsync(552500, 100);
        Assert.Equal(SubscribeOutcome.AuthFailed, r.Outcome);
        Assert.False(r.Success);
        Assert.Contains("logged in", r.ErrorDetail ?? "", StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SubscribeAsync_maps_403_to_AuthFailed()
    {
        var http = new HttpClient(new StubHandler(_ =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden))));
        var client = new SteamSubscribeClient(http, FakeCookies);
        var r = await client.SubscribeAsync(552500, 100);
        Assert.Equal(SubscribeOutcome.AuthFailed, r.Outcome);
    }

    [Fact]
    public async Task SubscribeAsync_maps_5xx_to_NetworkError()
    {
        var http = new HttpClient(new StubHandler(_ =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadGateway))));
        var client = new SteamSubscribeClient(http, FakeCookies);
        var r = await client.SubscribeAsync(552500, 100);
        Assert.Equal(SubscribeOutcome.NetworkError, r.Outcome);
    }

    [Fact]
    public async Task UnsubscribeAsync_posts_to_unsubscribe_endpoint()
    {
        HttpRequestMessage? captured = null;
        var http = new HttpClient(new StubHandler(req =>
        {
            captured = req;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent("{\"success\":1}") });
        }));

        var client = new SteamSubscribeClient(http, FakeCookies);
        var result = await client.UnsubscribeAsync(552500, 3712929235);

        Assert.True(result.Success);
        Assert.NotNull(captured);
        Assert.Equal("https://steamcommunity.com/sharedfiles/unsubscribe", captured!.RequestUri!.AbsoluteUri);
    }

    [Fact]
    public async Task SubscribeAsync_handles_HttpRequestException()
    {
        var http = new HttpClient(new ThrowingHandler(new HttpRequestException("dns failed")));
        var client = new SteamSubscribeClient(http, FakeCookies);
        var r = await client.SubscribeAsync(552500, 100);
        Assert.Equal(SubscribeOutcome.NetworkError, r.Outcome);
        Assert.Contains("dns failed", r.ErrorDetail ?? "");
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, Task<HttpResponseMessage>> _respond;
        public StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) { _respond = respond; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
            => _respond(req);
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        private readonly Exception _ex;
        public ThrowingHandler(Exception ex) { _ex = ex; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
            => throw _ex;
    }
}
