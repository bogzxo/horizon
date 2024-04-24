using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Horizon.Webhost;

/// <summary>
/// Standard Packet Interface for WebSocket
/// </summary>
public interface IWebSocketPacket
{
    public uint PacketID { get; init; }
}
