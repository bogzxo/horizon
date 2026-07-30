using System.Buffers.Text;
using System.Net;
using System.Net.WebSockets;
using System.Text;

using Horizon.Webhost;
using Horizon.Webhost.Providers;

using Newtonsoft.Json;

using SixLabors.ImageSharp.Metadata.Profiles.Exif;

namespace Horizon.Engine.Webhost;

using Logger = Bogz.Logging.Loggers.ConcurrentLogger;

/// <summary>
/// Global engine dashboard accessible at localhost:8080/dashboard, providing a backend interface to the executing application.
/// </summary>
public class DashboardContentProvider : IWebHostContentProvider
{
    private readonly struct InternalPayloadPacket : IWebSocketPacket
    {
        public readonly uint PacketID { get; init; }
        public readonly string Payload {  get; init; }
    }

    private static readonly Dictionary<string, string> fileContentPairs = new() {
        { "html", "text/html" },
        { "", "text/html" },
        { "css", "text/css" },
        { "js", "text/javascript" },
        { "map", "text/plain" }
    };

    private readonly Dictionary<uint, Action<string>> packetcallbacks = [];

    public void RegisterPacketCallback(in uint id, Action<string> action)
    {
        if (!packetcallbacks.TryAdd(id, action))
            Logger.Instance.Log(Bogz.Logging.LogLevel.Info, $"[DashboardContentProvider] Packet[{id}] already has a handler.");
    }

    private static readonly string filePrefix = "web_host/dashboard/";

    public DashboardContentProvider()
    {
        GameEngine.Instance.Debugger.Console.CommandProcessed += ProcessCommand;
        
        RegisterPacketCallback(2, GameEngine.Instance.Debugger.Console.EvaluateCallback);
    }

    ~DashboardContentProvider()
    {
        GameEngine.Instance.Debugger.Console.CommandProcessed -= ProcessCommand;
    }

    private HttpListenerWebSocketContext context;
    public async Task HandleRequest(string url, HttpListenerRequest request, HttpListenerResponse response)
    {
        // TODO: improve handling by proper parsing of the URI

        string val = url;
        if (url.StartsWith("dash/"))
            val = url[5..];

        if (val.CompareTo("/") == 0)
        {
            await ServeFile(response, Path.Combine(filePrefix, "dash.html"));
        }
        else
        {
            string filePath = filePrefix + val;
            if (File.Exists(filePath))
            {
                await ServeFile(response, filePath);
                Logger.Instance.Log(Bogz.Logging.LogLevel.Info, $"[DashboardContentProvider] Serving {filePath}.");
            }
            else
            {
                // Redirect if the file does not exist
                string redirectUrl = "/notfound.html"; // Redirect to a not found page or any other URL
                if (IsRedirectRequest(request))
                {
                    Redirect(response, redirectUrl);
                    Logger.Instance.Log(Bogz.Logging.LogLevel.Info, $"[DashboardContentProvider] Redirecting to {redirectUrl}.");
                }
                else
                {
                    // Serve a 404 page
                    await ServeFile(response, filePrefix + redirectUrl);
                    Logger.Instance.Log(Bogz.Logging.LogLevel.Info, $"[DashboardContentProvider] Can't locate resource '{filePath}', serving 404 page.");
                }
            }
        }
    }

    private static void Redirect(in HttpListenerResponse response, in string redirectUrl)
    {
        response.StatusCode = (int)HttpStatusCode.Redirect;
        response.AddHeader("Location", redirectUrl);
    }


    private static async Task ServeFile(HttpListenerResponse response, string filePath)
    {
        Stream output = response.OutputStream;
        response.AddHeader("Access-Control-Allow-Origin", "*");

        // Convert the file contents to bytes
        if (!File.Exists(filePath)) { output.Close(); return; }

        byte[] buffer = System.Text.Encoding.UTF8.GetBytes(File.ReadAllText(filePath));

        // Set the content length and write the file contents to the response stream
        response.ContentLength64 = buffer.Length;
        response.ContentType = fileContentPairs[filePath.Split('.').Last()];
        await output.WriteAsync(buffer, 0, buffer.Length);

        output.Close();
    }

    private static bool IsRedirectRequest(in HttpListenerRequest request)
    {
        return request.Url.Query.Contains("redirect=true");
    }

    public async Task HandleSocket(string url, HttpListenerContext context, HttpListenerWebSocketContext socketContext)
    {
        this.context = socketContext;
        try
        {
            var cancellationTokenSource = new CancellationTokenSource();

            // Launch two tasks to receive and send data simultaneously
            Task receive = ReceiveData(cancellationTokenSource.Token);
            Task transmit = TransmitData(cancellationTokenSource.Token);

            await Task.WhenAny(receive, transmit);

            cancellationTokenSource.Cancel(); // Cancel tasks if any one of them completes
            await Task.WhenAll(receive, transmit);

            Logger.Instance.Log(Bogz.Logging.LogLevel.Info, $"[DashboardContentProvider] Closed WS.");
        }
        catch (Exception ex)
        {
            Logger.Instance.Log(Bogz.Logging.LogLevel.Error, $"[DashboardContentProvider] Error handling WebSocket: {ex.Message}");
        }
        finally
        {
            // Close the WebSocket
            await socketContext.WebSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "WebSocket closed", CancellationToken.None);
        }
    }

    public Queue<IWebSocketPacket> PacketQueue { get; init; } = new();

    public void ProcessCommand(IWebSocketPacket result) => PacketQueue.Enqueue(result);

    private async Task TransmitData(CancellationToken cancellationToken)
    {
        try
        {
            byte[] bytes;
            while (context.WebSocket.State == WebSocketState.Open)
            {
                if (cancellationToken.IsCancellationRequested)
                    break;

                if (PacketQueue.TryDequeue(out var command))
                {
                    bytes = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(command));
                    await context.WebSocket.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken);
                }

                bytes = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(GameEngine.Instance.CollectTelemetry()));
                await context.WebSocket.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken);

                if (PacketQueue.Count > 0) await Task.Delay(10, cancellationToken);
                else await Task.Delay(100, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Task was canceled
        }
        catch (Exception ex)
        {
            Logger.Instance.Log(Bogz.Logging.LogLevel.Error, $"[DashboardContentProvider] Error transmitting data: {ex.Message}");
        }
    }

    private async Task ReceiveData(CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[1024];
        try
        {
            while (context.WebSocket.State == WebSocketState.Open)
            {
                if (cancellationToken.IsCancellationRequested)
                    break;

                var result = await context.WebSocket.ReceiveAsync(buffer, cancellationToken);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    break; // Exit the loop when WebSocket is closed
                }
                else
                {
                    InternalPayloadPacket packet = JsonConvert.DeserializeObject<InternalPayloadPacket>(Encoding.UTF8.GetString(buffer, 0, result.Count));

                    if (packetcallbacks.TryGetValue(packet.PacketID, out Action<string>? callback))
                    {
                        byte[] data = Convert.FromBase64String(packet.Payload);
                        callback?.Invoke(Encoding.UTF8.GetString(data));
                    }
                    else
                    {
                        Logger.Instance.Log(Bogz.Logging.LogLevel.Error, $"[DashboardContentProvider] No callback for packet [{packet.PacketID}].");
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Task was canceled
        }
        catch (Exception ex)
        {
            Logger.Instance.Log(Bogz.Logging.LogLevel.Error, $"[DashboardContentProvider] Error receiving data: {ex.Message}");
        }
    }
}