using WorkshopSentinel.Cli;
using Xunit;

namespace WorkshopSentinel.Tests;

public sealed class CliFlagParsingTests
{
    [Fact]
    public void Extracts_no_banner_flag_anywhere_in_argv()
    {
        var (rest, flags) = CliRunner.ExtractGlobalFlags(new[] { "audit", "--no-banner", "--game", "552500" });

        Assert.True(flags.NoBanner);
        Assert.Equal(new[] { "audit", "--game", "552500" }, rest);
    }

    [Fact]
    public void Extracts_config_path_with_value()
    {
        var (rest, flags) = CliRunner.ExtractGlobalFlags(new[] { "--config", @"C:\my\settings.json", "list" });

        Assert.Equal(@"C:\my\settings.json", flags.ConfigPath);
        Assert.Equal(new[] { "list" }, rest);
    }

    [Fact]
    public void Empty_argv_extracts_to_no_flags_no_verbs()
    {
        var (rest, flags) = CliRunner.ExtractGlobalFlags(System.Array.Empty<string>());

        Assert.False(flags.NoBanner);
        Assert.Null(flags.ConfigPath);
        Assert.Empty(rest);
    }

    [Fact]
    public void Flag_matching_is_case_insensitive()
    {
        var (rest, flags) = CliRunner.ExtractGlobalFlags(new[] { "--NO-BANNER", "doctor" });

        Assert.True(flags.NoBanner);
        Assert.Equal(new[] { "doctor" }, rest);
    }
}
