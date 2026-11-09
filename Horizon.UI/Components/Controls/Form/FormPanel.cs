using Horizon.HIDL.Library;
using Horizon.HIDL.Runtime;
using Horizon.UI.Scripting;

namespace Horizon.UI.Components;

/// <summary>
/// A form, a row a field made out of a description of the fields, a name and what sets it, in sections by their group.
/// A field that isn't set shows what it is anyway (its default, greyed, or what it says in words when whoever reads it
/// works it out) and one that is has an x that takes it out again. What an options screen, a debug panel or an editor of
/// files wants, without a row of a layout written for every one of a few dozen fields.
/// <code>
/// let form = compositor.form({ size: vec(320, 0), fields: [
///     { key: "damage", kind: "whole", default: 0, help: "how much a hit takes", group: "hit" },
///     { key: "level", kind: "choice", choices: ["mid", "low", "overhead"], default: "mid", group: "hit" }
/// ] });
/// form.values = { damage: 6 };
/// form.on_changed = (key, value) => print(key, value);
/// form.on_cleared = key => print("no more", key);
/// </code>
/// The rows are in FormPanel.Rows.
/// </summary>
public partial class FormPanel : StackPanel
{
    private readonly List<UIFormField> fields = [];
    private readonly Dictionary<string, IRuntimeValue> values = new(StringComparer.Ordinal);
    private IRuntimeValue? changedHandler, clearedHandler;
    private bool rowsDirty = true;

    public FormPanel()
    {
        Spacing = 4.0f;
    }

    /// <summary>The fields, in order. Setting them makes the rows again.</summary>
    public IReadOnlyList<UIFormField> Fields
    {
        get => fields;
        set
        {
            fields.Clear();
            fields.AddRange(value);
            rowsDirty = true;
        }
    }

    /// <summary>How wide the name of a field is, the rest of the row being what sets it. The rows are made again for another.</summary>
    public float CaptionWidth
    {
        get => captionWidth;
        set => Remake(ref captionWidth, value);
    }

    /// <summary>How wide a row is, all of it, 0 for 320.</summary>
    public float RowWidth
    {
        get => rowWidth;
        set => Remake(ref rowWidth, value);
    }

    /// <summary>How big its text is, 0 for the skin's.</summary>
    public float TextScale
    {
        get => textScale;
        set => Remake(ref textScale, value);
    }

    private float captionWidth = 120.0f, rowWidth, textScale;

    // A size changed, the rows are made again at it (what is set is set again on them, nothing is told)
    private void Remake(ref float size, float value)
    {
        if (size == value)
            return;

        size = value;
        rowsDirty = true;
    }

    /// <summary>A field set to a value by whoever is using it.</summary>
    public Action<string, IRuntimeValue>? OnChanged { get; set; }

    /// <summary>A field taken out by its x, back to what it is when it isn't set.</summary>
    public Action<string>? OnCleared { get; set; }

    /// <summary>What a field is set to, null when it isn't.</summary>
    public IRuntimeValue? ValueOf(string key) => values.GetValueOrDefault(key);

    /// <summary>
    /// Sets every field at once, the ones that aren't in it unset, without telling anybody. What a program does when what
    /// the form shows changed somewhere else.
    /// </summary>
    public void SetValues(IReadOnlyDictionary<string, IRuntimeValue> set)
    {
        values.Clear();
        foreach (var (key, value) in set)
            values[key] = value;

        ShowAll();
    }

    /// <summary>A field set as if it had been typed into, told about the same way. False for a key it hasn't got.</summary>
    public bool Enter(string key, IRuntimeValue value)
    {
        if (fields.Find(field => field.Key == key) is not { } field)
            return false;

        Changed(field, value);
        return true;
    }

    /// <summary>A field unset as if its x had been pressed, told about the same way.</summary>
    public bool Unset(string key)
    {
        if (fields.Find(field => field.Key == key) is not { } field)
            return false;

        Cleared(field);
        return true;
    }

    // A field set or cleared from its row, kept and told about
    private void Changed(UIFormField field, IRuntimeValue value)
    {
        values[field.Key] = value;
        ShowRow(field.Key);
        OnChanged?.Invoke(field.Key, value);
        InvokeScript(changedHandler, new StringValue(field.Key), value);
    }

    private void Cleared(UIFormField field)
    {
        values.Remove(field.Key);
        ShowRow(field.Key);
        OnCleared?.Invoke(field.Key);
        InvokeScript(clearedHandler, new StringValue(field.Key));
    }

    protected override void Update(float dt)
    {
        if (rowsDirty)
        {
            rowsDirty = false;
            BuildRows();
        }

        SendTyped(dt);
        base.Update(dt);
    }

    protected override void DefineScript()
    {
        base.DefineScript();

        Expose("fields", () => new ListValue([]), value => Fields = value is ListValue list
            ? [.. list.Items.OfType<ObjectValue>().Select(UIFormField.FromScript)]
            : throw new HidlRuntimeException("the fields of a form are a list of objects."));
        Expose("values", () => new ObjectValue(new Dictionary<string, IRuntimeValue>(values)), value => SetValues(value is ObjectValue of
            ? of.Properties
            : throw new HidlRuntimeException("the values of a form are an object, a key a field.")));
        Expose("caption_width", () => CaptionWidth, value => CaptionWidth = MathF.Max(0.0f, value));
        Expose("row_width", () => RowWidth, value => RowWidth = MathF.Max(0.0f, value));
        Expose("text_scale", () => TextScale, value => TextScale = value);
        Expose("on_changed", () => changedHandler ?? new NullValue(), value => changedHandler = value);
        Expose("on_cleared", () => clearedHandler ?? new NullValue(), value => clearedHandler = value);
        Expose("get", () => Natives.Function("get", args => ValueOf(args.Text(0)) ?? Values.Null), _ => { });
    }
}
