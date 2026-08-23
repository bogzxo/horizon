using System.Text;

namespace Horizon.HIDL.Runtime;

public readonly struct ObjectValue(in Dictionary<string, IRuntimeValue> properties) : IRuntimeValue
{
    public ObjectValue() : this([]) { }

    public readonly ValueType Type { get; init; } = ValueType.Object;
    public readonly Dictionary<string, IRuntimeValue> Properties { get; init; } = properties;

    public override string ToString()
    {
        if (Properties is null) return "{}";

        StringBuilder sb = new();
        sb.AppendLine("{");
        foreach (var item in Properties)
            sb.AppendLine($"  {item.Key}: {item.Value},");
        sb.AppendLine("}");

        return sb.ToString();
    }
}