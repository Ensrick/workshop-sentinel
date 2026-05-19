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
    // Canonical XML profile bodies for the visibility pre-flight. Mirrors what the live
    // steamcommunity.com endpoint returns (verified 2026-05-19 by the research agent).
    private const string PublicProfileXml =
        "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n" +
        "<profile><steamID64>76561198211120891</steamID64><steamID><![CDATA[Ensrick]]></steamID>" +
        "<visibilityState>3</visibilityState></profile>";
    private const string FriendsOnlyProfileXml =
        "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n" +
        "<profile><steamID64>76561198211120891</steamID64><visibilityState>2</visibilityState></profile>";
    private const string PrivateProfileXml =
        "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n" +
        "<profile><steamID64>76561198211120891</steamID64><visibilityState>1</visibilityState></profile>";

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

    [Theory]
    [InlineData("<visibilityState>3</visibilityState>", ProfileVisibility.Public)]
    [InlineData("<visibilityState>2</visibilityState>", ProfileVisibility.FriendsOnly)]
    [InlineData("<visibilityState>1</visibilityState>", ProfileVisibility.Private)]
    [InlineData("<visibilityState>  3  </visibilityState>", ProfileVisibility.Public)]
    [InlineData("<profile>no field</profile>",          ProfileVisibility.Unknown)]
    public void ParseVisibility_reads_steam_visibility_state(string xml, ProfileVisibility expected)
    {
        Assert.Equal(expected, FriendSubscriptionsClient.ParseVisibility(xml));
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

        int scrapeCalls = 0;
        var http = new HttpClient(new StubHandler(req =>
        {
            if (IsXmlProbe(req)) return Xml(PublicProfileXml);
            scrapeCalls++;
            // First page → data; second page → no IDs → terminate.
            var content = scrapeCalls == 1 ? page : "<html>(empty)</html>";
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(content) };
        }));

        var client = new FriendSubscriptionsClient(http);
        var friend = new FriendIdentity("76561198211120891", null, null);
        var result = await client.FetchSubscriptionsAsync(friend, appId: 552500);

        Assert.Equal(FriendScrapeOutcome.Ok, result.Outcome);
        Assert.Equal(new ulong[] { 111, 222, 333 }, result.PublishedFileIds.ToArray());
        Assert.Equal(2, scrapeCalls);   // paginated until empty
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
            if (IsXmlProbe(req)) return Xml(PublicProfileXml);
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
    public async Task FetchSubscriptionsAsync_flags_friends_only_profile_via_xml_probe()
    {
        // The whole point of the new privacy detection: a friends-only profile is detected
        // by visibilityState=2 in the XML, NOT by any HTML phrase. Modern Steam renders
        // public-with-zero-subs and friends-only-with-hidden-subs identically in HTML.
        var http = new HttpClient(new StubHandler(req =>
        {
            if (IsXmlProbe(req)) return Xml(FriendsOnlyProfileXml);
            // If the client even tries to scrape after seeing FriendsOnly, that's a bug.
            throw new InvalidOperationException("Should not scrape subs page on FriendsOnly profile.");
        }));

        var client = new FriendSubscriptionsClient(http);
        var friend = new FriendIdentity("76561198211120891", null, null);
        var result = await client.FetchSubscriptionsAsync(friend, appId: 552500);
        Assert.Equal(FriendScrapeOutcome.ProfilePrivate, result.Outcome);
        Assert.Empty(result.PublishedFileIds);
        Assert.Contains("FriendsOnly", result.ErrorDetail ?? "");
    }

    [Fact]
    public async Task FetchSubscriptionsAsync_flags_private_profile_via_xml_probe()
    {
        var http = new HttpClient(new StubHandler(req =>
        {
            if (IsXmlProbe(req)) return Xml(PrivateProfileXml);
            throw new InvalidOperationException("Should not scrape subs page on Private profile.");
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
        var http = new HttpClient(new StubHandler(req =>
        {
            if (IsXmlProbe(req)) return Xml(PublicProfileXml);
            return new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent("<html>(no items)</html>") };
        }));

        var client = new FriendSubscriptionsClient(http);
        var friend = new FriendIdentity("76561198211120891", null, null);
        var result = await client.FetchSubscriptionsAsync(friend, appId: 552500);
        Assert.Equal(FriendScrapeOutcome.NoSubscriptions, result.Outcome);
    }

    [Fact]
    public async Task FetchSubscriptionsAsync_surfaces_http_errors_on_subs_page()
    {
        var http = new HttpClient(new StubHandler(req =>
        {
            if (IsXmlProbe(req)) return Xml(PublicProfileXml);
            return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        }));

        var client = new FriendSubscriptionsClient(http);
        var friend = new FriendIdentity("76561198211120891", null, null);
        var result = await client.FetchSubscriptionsAsync(friend, appId: 552500);
        Assert.Equal(FriendScrapeOutcome.NetworkError, result.Outcome);
    }

    [Fact]
    public async Task FetchSubscriptionsAsync_proceeds_on_xml_probe_failure()
    {
        // Network blip on the XML probe — we don't want to false-positive as Private.
        // Falling through to the scrape (which may itself fail or come up empty) is fine.
        var http = new HttpClient(new StubHandler(req =>
        {
            if (IsXmlProbe(req)) return new HttpResponseMessage(HttpStatusCode.GatewayTimeout);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "<a href=\"https://steamcommunity.com/sharedfiles/filedetails/?id=42\">x</a>")
            };
        }));

        var client = new FriendSubscriptionsClient(http);
        var friend = new FriendIdentity("76561198211120891", null, null);
        var result = await client.FetchSubscriptionsAsync(friend, appId: 552500);
        Assert.Equal(FriendScrapeOutcome.Ok, result.Outcome);
        Assert.Equal(new ulong[] { 42 }, result.PublishedFileIds.ToArray());
    }

    // --- helpers ---
    private static bool IsXmlProbe(HttpRequestMessage req)
        => req.RequestUri!.AbsoluteUri.Contains("?xml=1");
    private static HttpResponseMessage Xml(string body)
        => new(HttpStatusCode.OK) { Content = new StringContent(body) };

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;
        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) { _respond = respond; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
            => Task.FromResult(_respond(req));
    }
}
