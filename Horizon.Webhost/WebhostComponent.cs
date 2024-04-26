using System.Net;

using Horizon.Core;
using Horizon.Webhost.Providers;
using Horizon.Webhost.Server;

namespace Horizon.Webhost;

using Logger = Bogz.Logging.Loggers.ConcurrentLogger;

/// <summary>
/// An engine utility providing a seamlessly integrating webhost. The user is expected to provide a <see cref="IWebHostContentProvider"/>, which can serve content of any MIME type, however for simplicity, a web socket request handler is provided separately, allowing bidirectional data transfer, however for the aforementioned system to be able to integrate seemlessly with the users envisioned goal, no packet protocol is provided, and is up to the end user to implement, however a skeleton interface <see cref="IWebSocketPacket"/> is provided. The user should push a content provider key-value pair, where the key is the url directory that the server will respond to.
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

    private static string GetProviderKey(in Uri url)
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