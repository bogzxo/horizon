using System.Net;
using System.Net.WebSockets;

namespace Horizon.Webhost.Providers;

/// <summary>
/// An async interface for implementing a content provider, at minimal, even if no data is served, the implementation is expected to respond to all clients, even if returning no data.
/// </summary>
public interface IWebHostContentProvider
{
    Task HandleRequest(string url, HttpListenerRequest request, HttpListenerResponse response);

    Task HandleSocket(string url, HttpListenerContext context, HttpListenerWebSocketContext socketContext);
}