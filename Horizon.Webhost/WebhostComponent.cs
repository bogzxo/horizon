using System;
using System.Net;
using System.Net.Sockets;

using Horizon.Core;
using Horizon.Core.Components;
using Horizon.Webhost.Providers;
using Horizon.Webhost.Server;

namespace Horizon.Webhost;

using Logger = Bogz.Logging.Loggers.ConcurrentLogger;

/// <summary>
/// An engine utility providing a seamlessly integrating webhost.
/// </summary>
public class WebHost : Entity, IDisposable
{
    protected HttpServerComponent Server { get; init; }
    public Dictionary<string, IWebHostContentProvider> ContentProviders { get; init; }

    public WebHost()
    {
        Name = "WebHost";
        ContentProviders = [];


        Logger.Instance.Log(Bogz.Logging.LogLevel.Info, $"[{Name}] Initializing WebHost.");
        Server = AddComponent<HttpServerComponent>();
    }

    static string GetProviderKey(in Uri url)
    {
        string value = url.AbsolutePath.Trim('/');

        if (value.Contains('/')) value = value.Split('/')[0];
        if (value.Contains('.')) value = value.Split('.')[0];

        return value;
    }

    private (IWebHostContentProvider? provider, string url) GetProvider(in HttpListenerContext context)
    {
        // TODO: something
        string key = GetProviderKey(context.Request.Url ?? new Uri("http://127.0.0.1:8080/index.html"));

        string url = (context.Request.Url.AbsolutePath.StartsWith('/') && context.Request.Url.AbsolutePath.Length > 1) ? context.Request.Url.AbsolutePath.TrimStart('/') : context.Request.Url.AbsolutePath;

        if (ContentProviders.TryGetValue(key, out var provider)) return (provider, url);
        return (null, string.Empty); // TODO: FIX
    }

    internal async Task ContentRequest(HttpListenerContext context)
    {
        var (provider, url) = GetProvider(context);
        if (provider is null) return;

        await provider.HandleRequest(url, context.Request, context.Response);
    }

    internal async Task SocketRequest(HttpListenerContext context)
    {
        var (provider, url) = GetProvider(context);
        if (provider is null) return;

        var socketContext = await context.AcceptWebSocketAsync(null);


        if (socketContext.WebSocket.State == System.Net.WebSockets.WebSocketState.Open)
        {
            Logger.Instance.Log(Bogz.Logging.LogLevel.Info, $"[{Name}] Established WS connection.");
            await Task.Run(() => provider.HandleSocket(url, context, socketContext));
            Logger.Instance.Log(Bogz.Logging.LogLevel.Info, $"[{Name}] Closed WS connection.");
        }

    }
}
