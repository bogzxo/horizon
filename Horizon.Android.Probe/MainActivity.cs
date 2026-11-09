using Android.App;
using Android.OS;
using Android.Widget;
using System.Runtime.InteropServices;

namespace Horizon.Android.Probe;

[Activity(Name = "dev.horizon.androidprobe.MainActivity", Label = "Horizon Android probe", MainLauncher = true, Exported = true)]
public sealed class MainActivity : Activity
{
    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        string report;
        try
        {
            report = $"{RuntimeInformation.FrameworkDescription}\n" +
                     $"Android {Build.VERSION.Release}, SDK {Build.VERSION.SdkInt}\n" +
                     $"Process {RuntimeInformation.ProcessArchitecture}\n\n" + VulkanProbe.Run();
        }
        catch (Exception error)
        {
            report = $"PROBE FAILED\n{error}";
        }

        global::Android.Util.Log.Info("HorizonProbe", report);
        try
        {
            File.WriteAllText(Path.Combine(FilesDir!.AbsolutePath, "probe.txt"), report);
        }
        catch (Exception error)
        {
            global::Android.Util.Log.Error("HorizonProbe", $"Could not save report\n{error}");
        }

        var text = new TextView(this) { Text = report, TextSize = 16 };
        text.SetPadding(24, 24, 24, 24);
        var scroll = new ScrollView(this);
        scroll.AddView(text);
        SetContentView(scroll);
    }
}
