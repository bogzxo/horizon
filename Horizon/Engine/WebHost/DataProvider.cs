using System.Net;
using System.Net.WebSockets;
using System.Text;

using Horizon.Webhost;
using Horizon.Webhost.Providers;

using System.Text.Json.Serialization;

namespace Horizon.Engine.WebHost;

internal readonly struct TelemetryData : IWebSocketPacket
{
    public TelemetryData()
    {
    }

    public uint PacketID { get; init; } = 0;
    public double LogicRate { get; init; }
    public double RenderRate { get; init; }
    public double PhysicsRate { get; init; }
}

/// <summary>
/// Everything the dashboard sends and receives as JSON, serialized by code made at compile time: nothing is looked
/// up by reflection, so it survives trimming and compiling ahead of time.
/// </summary>
[JsonSerializable(typeof(TelemetryData))]
[JsonSerializable(typeof(DashboardContentProvider.InternalPayloadPacket))]
[JsonSerializable(typeof(Debugging.Debuggers.DeveloperConsole.CommandLinePacket))]
internal partial class WebHostJsonContext : JsonSerializerContext
{
}