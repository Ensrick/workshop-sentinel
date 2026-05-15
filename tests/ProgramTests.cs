using Xunit;

namespace WorkshopSentinel.Tests;

public sealed class ProgramTests
{
    [Fact]
    public void Zero_args_routes_to_GUI()
    {
        Assert.False(Program.IsHeadlessInvocation(System.Array.Empty<string>()));
    }

    [Fact]
    public void Gui_flag_routes_to_GUI_even_with_verbs_present()
    {
        Assert.False(Program.IsHeadlessInvocation(new[] { "list", "--gui" }));
        Assert.False(Program.IsHeadlessInvocation(new[] { "--gui" }));
        Assert.False(Program.IsHeadlessInvocation(new[] { "--GUI" }));
    }

    [Fact]
    public void Any_other_args_route_to_CLI()
    {
        Assert.True(Program.IsHeadlessInvocation(new[] { "list" }));
        Assert.True(Program.IsHeadlessInvocation(new[] { "--no-banner" }));
        Assert.True(Program.IsHeadlessInvocation(new[] { "audit", "--game", "552500" }));
    }
}
