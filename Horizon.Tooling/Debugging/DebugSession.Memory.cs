using System.Text.Json.Nodes;

namespace Horizon.UI.Scripting;

/// <summary>A bounded native memory snapshot. Unreadable bytes follow the returned data.</summary>
public sealed record DebugMemory(string Address, byte[] Data, long UnreadableBytes);

public sealed partial class DebugSession
{
    public bool CanReadMemory => Can("supportsReadMemoryRequest");

    public async Task<DebugMemory?> ReadMemory(string reference, long offset = 0, int count = 256)
    {
        if (!CanReadMemory || State != DebugState.Stopped || string.IsNullOrWhiteSpace(reference)
            || count is < 1 or > 4096 || offset is < -9007199254740991 or > 9007199254740991) return null;
        long epoch = Volatile.Read(ref threadEpoch), selection = Volatile.Read(ref threadSelection);
        var body = await connection.Request("readMemory", new JsonObject
        {
            ["memoryReference"] = reference, ["offset"] = offset, ["count"] = count
        }).ConfigureAwait(false);
        if (State != DebugState.Stopped || epoch != Volatile.Read(ref threadEpoch) || selection != Volatile.Read(ref threadSelection)) return null;
        if (body is null) return null;
        string address = body["address"]?.GetValue<string>() ?? string.Empty;
        string data = body["data"]?.GetValue<string>() ?? string.Empty;
        long unreadable = body["unreadableBytes"]?.GetValue<long>() ?? 0;
        if (address.Length == 0 || data.Length > ((count + 2) / 3) * 4 || unreadable < 0)
            throw new InvalidDataException("The debugger returned an invalid memory block.");
        byte[] bytes;
        try { bytes = Convert.FromBase64String(data); }
        catch (FormatException error) { throw new InvalidDataException("The debugger returned invalid memory bytes.", error); }
        if (bytes.Length > count) throw new InvalidDataException("The debugger returned more memory than requested.");
        return new DebugMemory(address, bytes, unreadable);
    }
}
