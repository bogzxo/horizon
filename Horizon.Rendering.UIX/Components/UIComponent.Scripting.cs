using System.Numerics;
using System.Runtime.CompilerServices;

using Bogz.Logging;

using Horizon.Core.Tweening;
using Horizon.HIDL.Runtime;
using Horizon.Rendering.UIX.Drawing;
using Horizon.Rendering.UIX.Scripting;
using Horizon.Rendering.UIX.Skinning;

namespace Horizon.Rendering.UIX.Components;

// How a component shows itself to scripts, which is also how a layout file sets it up and how an editor reads it.
public abstract partial class UIComponent
{
    /* Scripting */

    /// <summary>
    /// Declares the properties scripts can read and write on this component, with the Expose methods.
    /// </summary>
    protected virtual void DefineScript()
    {
        Expose("pos", () => Position, value => Position = value);
        Expose("size", () => Size, value => Size = value);
        Expose("scale", () => Scale, value => Scale = value);
        Expose("visible", () => Visible, value => Visible = value);
        Expose("layer", () => Layer, value => Layer = value.Trim());
        Expose("enabled", () => Enabled, value => Enabled = value);
        Expose("tooltip", () => Tooltip, value => Tooltip = value);

        Expose(
            "anchor",
            () => UIScript.FromEnum(Anchor),
            value => Anchor = UIScript.ToEnum<Origin>(value, "anchor"));
        Expose(
            "pivot",
            () => UIScript.FromEnum(Pivot ?? Anchor),
            value => Pivot = UIScript.ToEnum<Origin>(value, "pivot"));
        Expose(
            "fill",
            () => UIScript.FromEnum(Fill),
            value => Fill = UIScript.ToEnum<UIFill>(value, "fill"));
        Expose(
            "padding",
            () => UIScript.FromEdges(Padding),
            value => Padding = UIScript.ToEdges(value, "padding"));

        Expose(
            "intro",
            () => UIScript.FromEnum(Intro),
            value => Intro = UIScript.ToEnum<UIIntro>(value, "intro"));
        Expose("intro_time", () => IntroTime, value => IntroTime = MathF.Max(0.0f, value));
        Expose("intro_delay", () => IntroDelay, value => IntroDelay = MathF.Max(0.0f, value));
        Expose("intro_offset", () => IntroOffset, value => IntroOffset = value);

        Expose(
            "parent",
            () => Parent is { } parent ? parent.Object : (IRuntimeValue)new NullValue(),
            value =>
            {
                var parent = FromScript(value) ?? throw new Exception("parent has to be a component.");
                parent.Add(this);
            });
    }

    /// <summary>Makes a property available to scripts under a name, as whatever value the callbacks deal in.</summary>
    protected void Expose(string name, Func<IRuntimeValue> get, Action<IRuntimeValue> set) =>
        script![name] = new NativeValue(get, set);

    protected void Expose(string name, Func<float> get, Action<float> set) =>
        Expose(name, () => new NumberValue(get()), value => set(UIScript.ToNumber(value, name)));

    protected void Expose(string name, Func<bool> get, Action<bool> set) =>
        Expose(name, () => new BooleanValue(get()), value => set(UIScript.ToBoolean(value, name)));

    protected void Expose(string name, Func<string> get, Action<string> set) =>
        Expose(name, () => new StringValue(get()), value => set(UIScript.ToText(value, name)));

    protected void Expose(string name, Func<string[]> get, Action<string[]> set) =>
        Expose(name, () => UIScript.FromTexts(get()), value => set(UIScript.ToTexts(value, name)));

    protected void Expose(string name, Func<Vector2> get, Action<Vector2> set) =>
        Expose(name, () => new Vector2Value(get()), value => set(UIScript.ToVector2(value, name)));

    protected void Expose(string name, Func<Vector4> get, Action<Vector4> set) =>
        Expose(name, () => new Vector4Value(get()), value => set(UIScript.ToColor(value, name)));

    /// <summary>
    /// Sets every property named in an object a script wrote, e.g. { label: "Play", pos: vec(0, 32) }.
    /// </summary>
    internal void ApplyScript(ObjectValue values)
    {
        var properties = Object.Properties;

        foreach (var (name, value) in values.Properties)
        {
            if (!properties.TryGetValue(name, out var property) || property is not NativeValue native)
                throw new Exception($"{GetType().Name} has no property called '{name}'.");

            native.MutatorCallback(value);
        }
    }

    /// <summary>
    /// Calls a function a script assigned to one of this component's handlers. Errors in the script are
    /// logged rather than thrown, so a broken handler can't take the game down with it.
    /// </summary>
    protected void InvokeScript(IRuntimeValue? handler, params IRuntimeValue[] arguments)
    {
        if (handler is null or NullValue || Module is not { } owner)
            return;

        try
        {
            owner.Runtime.Interpreter.Call(handler, arguments, owner.Runtime.UserScope);
        }
        catch (Exception e)
        {
            Log.Error($"[UIX] A {GetType().Name} handler failed: {e.Message}");
        }
    }
}
