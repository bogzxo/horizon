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
        { "js", "text/javascript" },
        { "map", "text/plain" }
    };

    private static readonly string filePrefix = "web_host/dashboard/";

    public void HandleRequest(in string url, ref HttpListenerRequest request, ref HttpListenerResponse response)
    {
        // TODO: fuck

        string val = url;
        if (url.StartsWith("index/")) val= url[6..];

        if (val.CompareTo(string.Empty) == 0)
        {
            ServeFile(ref response, Path.Combine(filePrefix, "index.html"));
        }
        else ServeFile(ref response, filePrefix + val);

        Logger.Instance.Log(Bogz.Logging.LogLevel.Info, $"[DashboardContentProvider] Serving {filePrefix + (val.CompareTo(string.Empty) == 0 ? "index.html" : val)}.");
    }

    private static void ServeFile(ref HttpListenerResponse response, string filePath)
    {
        Stream output = response.OutputStream;
        response.AddHeader("Access-Control-Allow-Origin", "*");


        // Convert the file contents to bytes
        if (!File.Exists(filePath)) goto DIE;

        byte[] buffer = System.Text.Encoding.UTF8.GetBytes(File.ReadAllText(filePath));

        // Set the content length and write the file contents to the response stream
        response.ContentLength64 = buffer.Length;
        response.ContentType = fileContentPairs[filePath.Split('.').Last()];
        output.Write(buffer, 0, buffer.Length);
    DIE:
        output.Close();
    }
}
