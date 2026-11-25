using Horizon.Platform.Desktop;

namespace Horizon.Tests.Platform;

/// <summary>What a double click on a Mac window's caption does, by the Dock setting, apart from any Mac.</summary>
public class MacCaptionActionTests
{
    [Theory]
    [InlineData(null, MacCaptionAction.Zoom)]
    [InlineData("", MacCaptionAction.Zoom)]
    [InlineData("Maximize", MacCaptionAction.Zoom)]
    [InlineData("Fill", MacCaptionAction.Zoom)]
    [InlineData("Minimize", MacCaptionAction.Miniaturize)]
    [InlineData("minimize", MacCaptionAction.Miniaturize)]
    [InlineData(" Minimize ", MacCaptionAction.Miniaturize)]
    [InlineData("None", MacCaptionAction.None)]
    [InlineData("none", MacCaptionAction.None)]
    [InlineData("Something else", MacCaptionAction.Zoom)]
    public void The_dock_setting_decides_what_a_double_click_does(string? value, MacCaptionAction expected) =>
        Assert.Equal(expected, MacCaptionActions.FromDefault(value));

    [Fact]
    public void The_defaults_key_is_the_systems() => Assert.Equal("AppleActionOnDoubleClick", MacCaptionActions.DefaultsKey);
}
