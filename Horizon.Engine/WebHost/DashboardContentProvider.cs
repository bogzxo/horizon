using System.Net;
using System.Text;

using Horizon.Webhost.Providers;

namespace Horizon.Engine.Webhost;
using Logger = Bogz.Logging.Loggers.ConcurrentLogger;

public class DashboardContentProvider : IWebHostContentProvider
{
    private static Dictionary<string, string> fileContentPairs = new() {
        { "html", "text/html" },
        { "", "text/html" },
        { "css", "text/css" },
        { "js", "text/javascript" }
    };

    private static readonly string filePrefix = "web_host/dashboard/";

    public void HandleRequest(in string url, ref HttpListenerRequest request, ref HttpListenerResponse response)
    {
        if (url.CompareTo(string.Empty) == 0)
        {
            ServeFile(ref response, Path.Combine(filePrefix, "index.html"));
        }
        else ServeFile(ref response, filePrefix + url);

        Logger.Instance.Log(Bogz.Logging.LogLevel.Info, $"[DashboardContentProvider] Serving {filePrefix + (url.CompareTo(string.Empty) == 0 ? "index.html" : url)}.");
    }

    private static void ServeFile(ref HttpListenerResponse response, string filePath)
    {
        // Convert the file contents to bytes
        byte[] buffer = System.Text.Encoding.UTF8.GetBytes(File.ReadAllText(filePath));

        // Set the content length and write the file contents to the response stream
        response.ContentLength64 = buffer.Length;
        response.ContentType = fileContentPairs[filePath.Split('.').Last()];
        Stream output = response.OutputStream;
        output.Write(buffer, 0, buffer.Length);
        output.Close();
    }
}
