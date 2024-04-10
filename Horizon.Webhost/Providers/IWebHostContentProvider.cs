using System.Net;

namespace Horizon.Webhost.Providers;

public interface IWebHostContentProvider
{
    void HandleRequest(in string url, ref HttpListenerRequest request, ref HttpListenerResponse response);
}