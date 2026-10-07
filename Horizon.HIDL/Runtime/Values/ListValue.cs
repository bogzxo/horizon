using System.Text;

namespace Horizon.HIDL.Runtime;

/// <summary>
/// A list of values, written as [a, b, c]. It is the one value that changes in place, two variables holding the same
/// list see each other's pushes, which is what you want for a list of fighters or a queue of inputs.
/// </summary>
public sealed class ListValue(List<IRuntimeValue> items) : IRuntimeValue
{
    public ListValue() : this([]) { }

    public ValueType Type { get; init; } = ValueType.List;

    public List<IRuntimeValue> Items { get; } = items;

    public int Count => Items.Count;

    public IRuntimeValue this[int index]
    {
        get => Items[index];
        set => Items[index] = value;
    }

    public override string ToString()
    {
        StringBuilder sb = new("[");
        for (int i = 0; i < Items.Count; i++)
        {
            if (i > 0) sb.Append(", ");
            sb.Append(Values.Text(Items[i]));
        }

        return sb.Append(']').ToString();
    }
}
