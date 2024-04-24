using System.Net;
using System.Net.WebSockets;
using System.Text;

using Horizon.Core;
using Horizon.Engine.Debugging.Debuggers;
using Horizon.Engine.WebHost;
using Horizon.Webhost;
using Horizon.Webhost.Providers;

using Newtonsoft.Json;

using Silk.NET.Core.Native;

namespace Horizon.Engine.Webhost;

using static System.Runtime.InteropServices.JavaScript.JSType;

using Logger = Bogz.Logging.Loggers.ConcurrentLogger;

public class DashboardContentProvider : IWebHostContentProvider
{
    private static Dictionary<string, string> fileContentPairs = new() {
        { "html", "text/html" },
        { "", "text/html" },
        { "css", "text/css" },
        { "js", "text/javascript" },
        { "map", "text/plain" }
    };

    private static readonly string filePrefix = "web_host/dashboard/";

    private HttpListenerWebSocketContext context;
    private bool isSocketClosed = false;

    public async Task HandleRequest(string url, HttpListenerRequest request, HttpListenerResponse response)
    {
        // TODO: fuck

        string val = url;
        if (url.StartsWith("index/")) val = url[6..];

        if (val.CompareTo(string.Empty) == 0)
        {
            ServeFile(ref response, Path.Combine(filePrefix, "index.html"));
        }
        else ServeFile(ref response, filePrefix + val);

        Logger.Instance.Log(Bogz.Logging.LogLevel.Info, $"[DashboardContentProvider] Serving {filePrefix + (val.CompareTo(string.Empty) == 0 ? "index.html" : val)}.");
    }

    private static void ServeFile(ref HttpListenerResponse response, string filePath)
    {
        Stream output = response.OutputStream;
        response.AddHeader("Access-Control-Allow-Origin", "*");


        // Convert the file contents to bytes
        if (!File.Exists(filePath)) goto DIE;

        byte[] buffer = System.Text.Encoding.UTF8.GetBytes(File.ReadAllText(filePath));

        // Set the content length and write the file contents to the response stream
        response.ContentLength64 = buffer.Length;
        response.ContentType = fileContentPairs[filePath.Split('.').Last()];
        output.Write(buffer, 0, buffer.Length);
    DIE:
        output.Close();
    }

    public async Task HandleSocket(string url, HttpListenerContext context, HttpListenerWebSocketContext socketContext)
    {
        this.context = socketContext;
        GameEngine.Instance.Debugger.Console.CommandProcessed += ProcessCommand;
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
        GameEngine.Instance.Debugger.Console.CommandProcessed -= ProcessCommand;
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
        byte[] receiveBuffer = new byte[1024];

        try
        {
            while (context.WebSocket.State == WebSocketState.Open)
            {
                if (cancellationToken.IsCancellationRequested)
                    break;

                var result = await context.WebSocket.ReceiveAsync(receiveBuffer, cancellationToken);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    isSocketClosed = true;
                    break; // Exit the loop when WebSocket is closed
                }
                else
                {
                    GameEngine.Instance.Debugger.Console.ExecuteCommand(Encoding.UTF8.GetString(receiveBuffer, 0, result.Count));
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
