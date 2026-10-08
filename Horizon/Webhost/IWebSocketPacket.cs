namespace Horizon.Webhost;

/// <summary>
/// Standard Packet Interface for WebSocket
/// </summary>
public interface IWebSocketPacket
{
    public uint PacketID { get; init; }
}