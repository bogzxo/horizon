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

    internal void ContentRequest(ref HttpListenerRequest request, ref HttpListenerResponse response)
    {

        // TODO: error checking
        var orgl = request.Url.LocalPath.Trim('/').Split('/').FirstOrDefault().Split('.').FirstOrDefault() ?? string.Empty;
        var path = orgl;
        if (path.CompareTo(string.Empty) == 0) path = "index";

        if (ContentProviders.TryGetValue(path, out var provider))
        {
            // extract url
            string url = request.Url.LocalPath[(orgl.Length + 1)..];

            provider.HandleRequest(url, ref request, ref response);
        }
    }
}
