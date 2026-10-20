using Horizon.HIDL.Runtime;
using Horizon.UI.Components;
using Horizon.UI.Scripting;

namespace Horizon.Tests.UI;

/// <summary>A layout a script drives, the keys it's handed and what it does when a call of the script's goes wrong.</summary>
public class UIScriptedLayoutTests
{
    // A script that writes down what it was handed and takes only what it's told to
    private sealed class Script(params string[] takes) : IUIScriptOwner
    {
        public readonly List<string> Handed = [];
        public string Name => "script";
        public string? Failed { get; set; }

        public bool Invoke(IRuntimeValue function, IRuntimeValue[] arguments, out IRuntimeValue returned)
        {
            string key = ((StringValue)arguments[1]).Value;
            Handed.Add(key);
            returned = Values.Bool(takes.Contains(key));
            return Failed is null;
        }
    }

    private static UIScriptedLayout Made(Script script)
    {
        var made = new UIScriptedLayout(script, "view", null!, new Panel(), null, null);
        made.Set("on_key", new StringValue("on_key"));
        return made;
    }

    [Fact]
    public void A_key_with_shift_is_offered_with_it_first_and_on_its_own_if_that_wasnt_wanted()
    {
        var script = new Script();
        Made(script).Press("left", shift: true, alt: false, _ => { });
        Assert.Equal(["shift+left", "left"], script.Handed);
    }

    [Fact]
    public void One_taken_with_shift_isnt_offered_again()
    {
        var script = new Script("alt+shift+z");
        Made(script).Press("z", shift: true, alt: true, _ => { });
        Assert.Equal(["alt+shift+z"], script.Handed);
    }

    [Fact]
    public void A_script_that_went_wrong_is_said_to_and_handed_nothing_after()
    {
        var script = new Script { Failed = "it threw" };
        IUIScriptOwner? said = null;

        Assert.False(Made(script).Press("up", owner => said = owner));
        Assert.Empty(script.Handed);
        Assert.Null(said);
    }

    [Fact]
    public void The_keys_a_view_is_handed_are_each_named_once()
    {
        Assert.Equal(UIScriptedLayout.Keys.Length, UIScriptedLayout.Keys.Select(key => key.Name).Distinct().Count());
        Assert.Contains(UIScriptedLayout.Keys, key => key.Name == "page_up");
    }
}
