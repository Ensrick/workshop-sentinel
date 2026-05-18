using System.IO;
using System.Linq;
using WorkshopSentinel.Services;
using Xunit;

namespace WorkshopSentinel.Tests;

public class SteamFriendsResolverTests
{
    [Fact]
    public void ParseLocalConfig_extracts_friends_and_skips_self_and_scalars()
    {
        // Trimmed snapshot of a real localconfig.vdf shape: the friends sub-object holds the
        // logged-in user (key matches selfAccountId), some scalar metadata keys, and the rest
        // are friend entries keyed by accountid32.
        var src = """
            "UserLocalConfigStore"
            {
                "friends"
                {
                    "250855163"
                    {
                        "name" "Ensrick"
                        "avatar" "44b9d2eb"
                    }
                    "PersonaName" "Ensrick"
                    "communitypreferences" "189cd"
                    "107010611"
                    {
                        "name" "Silent Pockets"
                        "avatar" "83131420"
                    }
                    "121946418"
                    {
                        "name" "ramos-"
                        "avatar" "76f0e8f6"
                    }
                }
            }
            """;
        var tmp = Path.Combine(Path.GetTempPath(), $"ws-friends-{Path.GetRandomFileName()}.vdf");
        File.WriteAllText(tmp, src);
        try
        {
            var friends = SteamFriendsResolver.ParseLocalConfig(tmp, selfAccountId: 250855163).ToList();
            Assert.Equal(2, friends.Count);
            Assert.Contains(friends, f => f.PersonaName == "Silent Pockets" && f.AccountId32 == 107010611u);
            Assert.Contains(friends, f => f.PersonaName == "ramos-"         && f.AccountId32 == 121946418u);
            Assert.DoesNotContain(friends, f => f.PersonaName == "Ensrick");
        }
        finally
        {
            File.Delete(tmp);
        }
    }

    [Fact]
    public void AccountIdToSteamId64_matches_documented_offset()
    {
        // The user's own account id 250855163 → SteamID64 76561198211120891 (memory-of-record).
        Assert.Equal("76561198211120891", SteamFriendsResolver.AccountIdToSteamId64(250855163));
        // Boundary check: accountid 1 → 76561197960265729.
        Assert.Equal("76561197960265729", SteamFriendsResolver.AccountIdToSteamId64(1));
    }

    [Fact]
    public void ParseLocalConfig_returns_empty_when_file_missing()
    {
        var friends = SteamFriendsResolver.ParseLocalConfig("Z:\\does-not-exist.vdf", 0).ToList();
        Assert.Empty(friends);
    }

    [Fact]
    public void ParseLocalConfig_returns_empty_when_no_friends_node()
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"ws-friends-{Path.GetRandomFileName()}.vdf");
        File.WriteAllText(tmp, "\"UserLocalConfigStore\" { \"something_else\" { } }");
        try
        {
            Assert.Empty(SteamFriendsResolver.ParseLocalConfig(tmp, 0));
        }
        finally
        {
            File.Delete(tmp);
        }
    }
}

public class FriendOnlineStateTests
{
    [Theory]
    [InlineData("<profile><onlineState>online</onlineState></profile>",  FriendOnlineState.Online)]
    [InlineData("<profile><onlineState>offline</onlineState></profile>", FriendOnlineState.Offline)]
    [InlineData("<profile><onlineState>in-game</onlineState></profile>", FriendOnlineState.InGame)]
    [InlineData("<profile><stateMessage>Last Online ...</stateMessage></profile>", FriendOnlineState.Unknown)]
    [InlineData("<profile>private</profile>",                            FriendOnlineState.Unknown)]
    [InlineData("",                                                       FriendOnlineState.Unknown)]
    public void ParseOnlineState_recognises_each_real_value(string xml, FriendOnlineState expected)
    {
        Assert.Equal(expected, FriendSubscriptionsClient.ParseOnlineState(xml));
    }
}
