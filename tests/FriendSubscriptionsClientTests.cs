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

public class FriendSubscriptionsClientTests
{
    [Theory]
    [InlineData("76561198211120891",                                   "76561198211120891", null)]
    [InlineData("https://steamcommunity.com/profiles/76561198211120891", "76561198211120891", null)]
    [InlineData("https://steamcommunity.com/profiles/76561198211120891/", "76561198211120891", null)]
    [InlineData("https://steamcommunity.com/id/ensrick",               null, "ensrick")]
    [InlineData("https://steamcommunity.com/id/ensrick/myworkshopfiles?section=mysubscriptions", null, "ensrick")]
    [InlineData("ensrick",                                              null, "ensrick")]
    [InlineData("EnsRick",                                              null, "EnsRick")]
    public void ParseIdentityInput_handles_every_common_input_shape(string input, string? expectedSid, string? expectedVanity)
    {
        var (sid, vanity) = FriendSubscriptionsClient.ParseIdentityInput(input);
        Assert.Equal(expectedSid, sid);
        Assert.Equal(expectedVanity, vanity);
    }

    [Fact]
    public async Task ResolveAsync_returns_steamid_for_raw_sid_input()
    {
        var http = new HttpClient(new StubHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("<profile><steamID><![CDATA[Bob]]></steamID></profile>")
            }));
        var client = new FriendSubscriptionsClient(http);
        var id = await client.ResolveAsync("76561198211120891");
        Assert.NotNull(id);
        Assert.Equal("76561198211120891", id!.SteamId64);
    }

    [Fact]
    public async Task ResolveAsync_resolves_vanity_via_xml_endpoint()
    {
        const string xml = "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n" +
            "<profile>\n" +
            "  <steamID64>76561198211120891</steamID64>\n" +
            "  <steamID><![CDATA[Ensrick]]></steamID>\n" +
            "</profile>";

        var http = new HttpClient(new StubHandler(req =>
        {
            Assert.Contains("/id/ensrick/", req.RequestUri!.AbsoluteUri);
            Assert.Contains("xml=1", req.RequestUri.AbsoluteUri);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(xml) };
        }));
        var client = new FriendSubscriptionsClient(http);
        var id = await client.ResolveAsync("ensrick");
        Assert.NotNull(id);
        Assert.Equal("76561198211120891", id!.SteamId64);
        Assert.Equal("Ensrick",            id.DisplayName);
        Assert.Equal("ensrick",            id.VanitySlug);
    }

    [Fact]
    public async Task ResolveAsync_returns_null_when_vanity_xml_has_no_steamid()
    {
        var http = new HttpClient(new StubHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<profile></profile>") }));
        var client = new FriendSubscriptionsClient(http);
        Assert.Null(await client.ResolveAsync("doesnotexist"));
    }

    [Fact]
    public async Task FetchSubscriptionsAsync_extracts_ids_from_one_page()
    {
        var page =
            "<html>" +
            "<a href=\"https://steamcommunity.com/sharedfiles/filedetails/?id=111\">img</a>" +
            "<a href=\"https://steamcommunity.com/sharedfiles/filedetails/?id=111\">title</a>" +
            "<a href=\"https://steamcommunity.com/sharedfiles/filedetails/?id=222\">x</a>" +
            "<a href=\"https://steamcommunity.com/sharedfiles/filedetails/?id=333\">y</a>" +
            "</html>";

        int calls = 0;
        var http = new HttpClient(new StubHandler(req =>
        {
            calls++;
            // First page → data; second page → no IDs → terminate.
            var content = calls == 1 ? page : "<html>(empty)</html>";
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(content) };
        }));

        var client = new FriendSubscriptionsClient(http);
        var friend = new FriendIdentity("76561198211120891", null, null);
        var result = await client.FetchSubscriptionsAsync(friend, appId: 552500);

        Assert.Equal(FriendScrapeOutcome.Ok, result.Outcome);
        Assert.Equal(new ulong[] { 111, 222, 333 }, result.PublishedFileIds.ToArray());
        Assert.Equal(2, calls);   // paginated until empty
    }

    [Fact]
    public async Task FetchSubscriptionsAsync_paginates_across_two_pages()
    {
        var pages = new Dictionary<int, string>
        {
            [1] = "<a href=\"https://steamcommunity.com/sharedfiles/filedetails/?id=1\">x</a><a href=\"https://steamcommunity.com/sharedfiles/filedetails/?id=2\">x</a>",
            [2] = "<a href=\"https://steamcommunity.com/sharedfiles/filedetails/?id=3\">x</a>",
            [3] = "<html>(empty)</html>",
        };
        var http = new HttpClient(new StubHandler(req =>
        {
            var url = req.RequestUri!.Query;
            var page = int.Parse(System.Text.RegularExpressions.Regex.Match(url, @"p=(\d+)").Groups[1].Value);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(pages[page]) };
        }));

        var client = new FriendSubscriptionsClient(http);
        var friend = new FriendIdentity("76561198211120891", null, null);
        var result = await client.FetchSubscriptionsAsync(friend, appId: 552500);

        Assert.Equal(FriendScrapeOutcome.Ok, result.Outcome);
        Assert.Equal(new ulong[] { 1, 2, 3 }, result.PublishedFileIds.ToArray());
    }

    [Fact]
    public async Task FetchSubscriptionsAsync_flags_private_profile()
    {
        var http = new HttpClient(new StubHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("This profile is private — sorry"),
            }));

        var client = new FriendSubscriptionsClient(http);
        var friend = new FriendIdentity("76561198211120891", null, null);
        var result = await client.FetchSubscriptionsAsync(friend, appId: 552500);
        Assert.Equal(FriendScrapeOutcome.ProfilePrivate, result.Outcome);
        Assert.Empty(result.PublishedFileIds);
    }

    [Fact]
    public async Task FetchSubscriptionsAsync_returns_NoSubscriptions_when_public_but_empty()
    {
        var http = new HttpClient(new StubHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent("<html>(no items)</html>") }));

        var client = new FriendSubscriptionsClient(http);
        var friend = new FriendIdentity("76561198211120891", null, null);
        var result = await client.FetchSubscriptionsAsync(friend, appId: 552500);
        Assert.Equal(FriendScrapeOutcome.NoSubscriptions, result.Outcome);
    }

    [Fact]
    public async Task FetchSubscriptionsAsync_surfaces_http_errors()
    {
        var http = new HttpClient(new StubHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)));

        var client = new FriendSubscriptionsClient(http);
        var friend = new FriendIdentity("76561198211120891", null, null);
        var result = await client.FetchSubscriptionsAsync(friend, appId: 552500);
        Assert.Equal(FriendScrapeOutcome.NetworkError, result.Outcome);
    }

    // --- tiny HttpMessageHandler stub ---
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;
        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) { _respond = respond; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
            => Task.FromResult(_respond(req));
    }
}
