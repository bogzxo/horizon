# Android loader probe

Stage 0 checks whether .NET for Android and Silk.NET.Vulkan 2.23.0 can load the system Vulkan driver,
create an instance, and enumerate GPUs. It deliberately does not reference the engine or ship desktop native
libraries. It stays outside `Horizon.sln` so desktop builds do not require the Android workload.

The probe reports success or the exception on screen, in logcat under `HorizonProbe`, and in its private
`files/probe.txt`. Success is only a loader check. It does not establish Horizon feature support, surface
creation, rendering, or lifecycle correctness. Use a physical device for those later gates.

## Build

Run from the workspace or engine directory so `global.json` selects the intended SDK.

```powershell
dotnet workload restore horizon/Horizon.Android.Probe/Horizon.Android.Probe.csproj
dotnet build horizon/Horizon.Android.Probe/Horizon.Android.Probe.csproj -c Debug
```

Paths above assume the workspace root. Omit `horizon/` from the engine checkout.
If the Java or Android SDK is missing, use the documented
[InstallAndroidDependencies target](https://learn.microsoft.com/en-us/dotnet/android/getting-started/installation/dependencies)
with explicit `AndroidSdkDirectory` and `JavaSdkDirectory` paths. Pass those same paths to the build.
Debug embeds assemblies into the APK, so installation through adb does not depend on IDE fast deployment.

## Run

Use `adb devices -l` to select a device. Replace `DEVICE` with its serial in every command.
Install the signed debug APK from `bin/Debug/net11.0-android/` (the SDK may append an API level to that directory).

```powershell
adb -s DEVICE install -r PATH_TO_SIGNED_APK
adb -s DEVICE shell am force-stop dev.horizon.androidprobe
adb -s DEVICE shell run-as dev.horizon.androidprobe rm -f files/probe.txt
adb -s DEVICE shell am start -W -n dev.horizon.androidprobe/.MainActivity
adb -s DEVICE shell run-as dev.horizon.androidprobe cat files/probe.txt
```

The new report must exist and contain `PROBE PASSED`. A missing report is a failed run, even if an earlier
logcat entry says it passed. For crash diagnosis, capture logcat from the launch time and filter for the app.

Record the device model, Android version, process architecture, GPU, Vulkan version, and complete report.
An emulator result proves its own driver path only. ARM64 devices and x64 emulators are packaged by default.
