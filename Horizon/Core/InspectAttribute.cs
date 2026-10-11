namespace Horizon.Core;

/// <summary>
/// Puts a field or a property into the inspector of the Skyline debugger that wouldn't be there by itself (anything
/// that isn't public), or says more about one that would, the ends of the slider its number gets, say.
/// <code>
/// [Inspect(0.0f, 400.0f)] private float walkSpeed = 120.0f;
/// [Inspect] private Vector2 knockback;
/// </code>
/// Whatever is public shows up without it, and is edited if it can be set. It is always there to be written, a
/// Release build has no inspector and the attribute sits on the field doing nothing.
/// </summary>
[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property)]
public sealed class InspectAttribute : Attribute
{
    /// <summary>The least and the most the number can be dragged to, a slider is shown between the two. No slider while they are the same.</summary>
    public float Min { get; }

    public float Max { get; }

    /// <summary>What the inspector calls it, the name of the field if this is left empty.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>Whether four (or three) numbers are a colour and get a picker, for one that isn't called a colour or a tint.</summary>
    public bool Color { get; init; }

    public bool HasRange => Max > Min;

    public InspectAttribute()
    { }

    public InspectAttribute(float min, float max)
    {
        Min = min;
        Max = max;
    }
}

/// <summary>
/// Keeps something public out of the inspector of the Skyline debugger. For what means nothing to whoever is
/// looking (plumbing), and for a property that does something when it is read.
/// </summary>
[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property)]
public sealed class HideInInspectorAttribute : Attribute;
