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

    static string GetProvider(in Uri url)
    {
        string value = url.AbsolutePath.Trim('/');

        if (value.Contains('/')) value = value.Split('/')[0];
        if (value.Contains('.')) value = value.Split('.')[0];

        return value;
    }

    internal void ContentRequest(ref HttpListenerRequest request, ref HttpListenerResponse response)
    {
        // TODO: something
        string key = GetProvider(request.Url ?? new Uri("http://127.0.0.1:8080/index.html"));

        string url = (request.Url.AbsolutePath.StartsWith('/') && request.Url.AbsolutePath.Length > 1) ? request.Url.AbsolutePath.TrimStart('/') : request.Url.AbsolutePath;

        if (ContentProviders.TryGetValue(key, out var provider))
            provider.HandleRequest(url, ref request, ref response);
    }
}
