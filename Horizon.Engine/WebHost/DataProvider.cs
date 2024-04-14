using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Numerics;
using System.Text;
using System.Threading.Tasks;

using Horizon.Webhost.Providers;

using Newtonsoft.Json;

namespace Horizon.Engine.WebHost;

file readonly struct TelemetryData
{
    public Vector2 Test { get; init; }
}

internal class DataProvider : IWebHostContentProvider
{
    public void HandleRequest(in string url, ref HttpListenerRequest request, ref HttpListenerResponse response)
    {
        if (url.CompareTo("favicon.ico") == 0) return;

        TelemetryData data = new()
        {
            Test = GameEngine.Instance.InputManager.GetVirtualController().MovementAxis
        };

        Stream stream = response.OutputStream;

        byte[] bytes = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(data));
        response.AddHeader("Access-Control-Allow-Origin", "*");
        response.ContentLength64 = bytes.Length;
        response.ContentType = "application/json";

        stream.Write(bytes, 0, bytes.Length);
        stream.Flush();
        stream.Close();
    }
}
