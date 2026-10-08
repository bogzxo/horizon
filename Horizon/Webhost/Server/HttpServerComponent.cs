using Horizon.Logging;
using System.Net;

using Horizon.Core;
using Horizon.Core.Components;

namespace Horizon.Webhost.Server;

/// <summary>
/// Internal component integrating the HttpListener into the Horizon ECS, providing delegating callbacks to from the WebHost to content providers.
/// </summary>
public class HttpServerComponent : GameComponent, IDisposable
{
    protected HttpListener Listener { get; init; }

    private bool isRunning = true;

    protected WebHost Host { get; private set; }

    public HttpServerComponent()
    {
        Listener = new HttpListener();
        Listener.Prefixes.Add("http://127.0.0.1:8080/");
    }

    private async Task ListeningLoop()
    {
        // start server
        Log.Info($"[{Name}] Starting web listener.");
        Listener.Start();

        // event loop
        while (isRunning)
        {
            HttpListenerContext context = await Listener.GetContextAsync();

            if (context.Request.IsWebSocketRequest)
                // Handle the WebSocket connection
                await Host.SocketRequest(context);
            else
                // TODO: allow external function injection
                await Host.ContentRequest(context);
        }

        // end server
        Listener.Stop();
    }

    public override void Initialize()
    {
        Host = Parent as WebHost ?? throw new InvalidOperationException("An HttpServerComponent belongs on a WebHost.");

        // start the HTTP server task
        Task.Run(ListeningLoop);
    }

    public void Dispose()
    {
        Log.Info($"[{Name}] Ending web listener.");
        isRunning = false;
    }
}