using System.Net;
using System.Net.WebSockets;
using System.Text;

using Horizon.Webhost;
using Horizon.Webhost.Providers;

using Newtonsoft.Json;

namespace Horizon.Engine.WebHost;

internal readonly struct TelemetryData : IWebSocketPacket
{
    public TelemetryData()
    {
    }

    public uint PacketID { get; init; } = 0;
    public readonly double LogicRate { get; init; }
    public readonly double RenderRate { get; init; }
    public readonly double PhysicsRate { get; init; }
}