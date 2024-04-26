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

internal class DataProvider : IWebHostContentProvider
{
    public async Task HandleRequest(string url, HttpListenerRequest request, HttpListenerResponse response)
    {
        if (url.CompareTo("favicon.ico") == 0) return;

        var data = GameEngine.Instance.CollectTelemetry();

        Stream stream = response.OutputStream;

        byte[] bytes = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(data));
        response.AddHeader("Access-Control-Allow-Origin", "*");
        response.ContentLength64 = bytes.Length;
        response.ContentType = "application/json";

        stream.Write(bytes, 0, bytes.Length);
        stream.Flush();
        stream.Close();
    }

    public async Task HandleSocket(string url, HttpListenerContext context, HttpListenerWebSocketContext socketContext)
    {
    }
}