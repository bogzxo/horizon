using System.Text.Json;
using System.Text.Json.Nodes;

namespace Horizon.UI.Scripting;

/// <summary>
/// The numbers of a server's messages read the way they come, whatever size or kind they are. GetValue of an int threw
/// on one past an int's room (serve-d says a column of 4294967295 for the end of a line), the throw took the reader
/// thread down with it, and the server was gone for the rest of the run.
/// </summary>
internal static class JsonNumbers
{
    /// <summary>A number as an int, held to an int's room, the fallback for none or for something that isn't one.</summary>
    public static int Int(JsonNode? node, int fallback = 0)
    {
        if (node is not JsonValue value || value.GetValueKind() != JsonValueKind.Number)
            return fallback;
        if (value.TryGetValue(out int small))
            return small;
        if (value.TryGetValue(out long big))
            return (int)Math.Clamp(big, int.MinValue, int.MaxValue);
        if (value.TryGetValue(out ulong huge))
            return huge > int.MaxValue ? int.MaxValue : (int)huge;
        if (value.TryGetValue(out double real) && !double.IsNaN(real))
            return (int)Math.Clamp(real, int.MinValue, int.MaxValue);

        return fallback;
    }
}
