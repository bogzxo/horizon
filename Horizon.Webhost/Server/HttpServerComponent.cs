﻿using System.Net;

using Horizon.Core;
using Horizon.Core.Components;

namespace Horizon.Webhost.Server;

using Logger = Bogz.Logging.Loggers.ConcurrentLogger;

/// <summary>
/// Internal component integrating the HttpListener into the Horizon ECS, providing callbacks to WebHost.
/// </summary>
public class HttpServerComponent : IGameComponent, IDisposable
{
    protected HttpListener Listener { get; init; }

    public bool Enabled { get; set; } = true;
    private bool isRunning = true;
    public string Name { get; set; } = "HttpServerComponent";
    public Entity Parent { get; set; }

    protected WebHost Host { get; private set; }

    public HttpServerComponent()
    {
        Listener = new HttpListener();
        Listener.Prefixes.Add("http://127.0.0.1:8080/");
    }

    private async Task ListeningLoop()
    {
        // start server
        Logger.Instance.Log(Bogz.Logging.LogLevel.Info, $"[{Name}] Starting web listener.");
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

    public void Initialize()
    {
        Host = Parent as WebHost;

        // start the HTTP server task
        Task.Run(ListeningLoop);
    }

    public void Dispose()
    {
        Logger.Instance.Log(Bogz.Logging.LogLevel.Info, $"[{Name}] Ending web listener.");
        isRunning = false;
    }

    public void Render(float dt, object? obj = null)
    { }

    public void UpdateState(float dt)
    { }

    public void UpdatePhysics(float dt)
    { }
}