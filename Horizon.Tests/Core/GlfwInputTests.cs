using Horizon.Input;

using Silk.NET.GLFW;
using Silk.NET.Input;

namespace Horizon.Tests.Core;

/// <summary>
/// The keyboard of the input context without gamepads hands on GLFW's keys as Silk's by the same number, which only
/// works while the two number them alike.
/// </summary>
public class GlfwInputTests
{
    [Fact]
    public void Every_glfw_key_is_the_silk_key_of_the_same_name()
    {
        foreach (Keys glfw in Enum.GetValues<Keys>())
        {
            Assert.True(Enum.TryParse(glfw.ToString(), out Key silk), $"Silk has no key called {glfw}.");
            Assert.Equal(silk, GlfwKeyboard.FromGlfw(glfw));
            Assert.Equal(glfw, GlfwKeyboard.ToGlfw(silk));
        }
    }
}
