using System.Text;

namespace Horizon.Horlang.Runtime;

public readonly struct ObjectValue(in Dictionary<string, IRuntimeValue> properties) : IRuntimeValue
{
    public ValueType Type { get; init; } = ValueType.Object;
    public Dictionary<string, IRuntimeValue> Properties { get; init; } = properties;

    public override string ToString()
    {
        StringBuilder sb = new();
        foreach (var item in Properties)
            sb.AppendLine(item.Value.ToString());

        return sb.ToString();
    }
}