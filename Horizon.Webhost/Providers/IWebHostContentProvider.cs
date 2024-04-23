using System.Net;
using System.Net.WebSockets;

namespace Horizon.Webhost.Providers;

public interface IWebHostContentProvider
{
    Task HandleRequest(string url, HttpListenerRequest request, HttpListenerResponse response);
    Task HandleSocket(string url, HttpListenerContext context, HttpListenerWebSocketContext socketContext);
}