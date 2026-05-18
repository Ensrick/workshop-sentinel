using WorkshopSentinel.Cli.Verbs;
using Xunit;

namespace WorkshopSentinel.Tests;

public sealed class SelfUpdateCommandTests
{
    [Theory]
    [InlineData(SelfUpdateCommand.Outcome.Latest,    0)]
    [InlineData(SelfUpdateCommand.Outcome.Updated,   0)]
    [InlineData(SelfUpdateCommand.Outcome.Available, 10)]
    [InlineData(SelfUpdateCommand.Outcome.Failed,    1)]
    public void ExitCodeFor_maps_each_outcome(SelfUpdateCommand.Outcome outcome, int expected)
    {
        Assert.Equal(expected, SelfUpdateCommand.ExitCodeFor(outcome));
    }
}
