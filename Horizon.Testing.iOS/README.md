# Horizon.Testing.iOS

The whole of Horizon.Testing in the iOS simulator or on an iPhone, the selector, every example and every check. Bundle id
`dev.horizon.testing`. Every source is linked from `../Horizon.Testing` the way Horizon.Testing.Android does it, only
`Program.cs` and `Host/TestPlatform.Desktop.cs` are left out (`Host/TestPlatform.iOS.cs` takes the second's place). An
iOS app can't start another process, so the terminal example says why it isn't here. Not in any solution.

## Build

On a Mac with Xcode 27 and the .NET 11 iOS workload. The shader pack has to exist first
(`Horizon/shaders/packed/*.hzshaders`), the build stops with an error otherwise.

```
git submodule update --init
dotnet build Horizon.Testing.iOS/Horizon.Testing.iOS.csproj -c Debug
```

No `-r` on the command line. The project picks iossimulator-arm64 itself (`-p:RuntimeIdentifier=ios-arm64` in the
project for a phone), and a `-r` would reach every referenced project too, Horizon.HIDL publishes AOT and stops the
build with NETSDK1203 for a simulator RID.

The app is `bin/Debug/net11.0-ios/iossimulator-arm64/Horizon.Testing.app`. On Windows the sources compile with
`dotnet msbuild Horizon.Testing.iOS/Horizon.Testing.iOS.csproj -restore -t:Compile`, which is a quick check before
going to the Mac, but linking the app needs the Mac.

## Run in the simulator

```
xcrun simctl boot "iPhone 17 Pro"; open -a Simulator
xcrun simctl install booted Horizon.Testing.iOS/bin/Debug/net11.0-ios/iossimulator-arm64/Horizon.Testing.app
SIMCTL_CHILD_HORIZON_TESTING_SCENE=selector xcrun simctl launch --console-pty --terminate-running-process booted dev.horizon.testing
```

Any HORIZON_* setting goes in the same way, `SIMCTL_CHILD_HORIZON_AUDIO=off`, `SIMCTL_CHILD_HORIZON_SCREENSHOT=shot.png@3`.
`HORIZON_TESTING_SCENE` is `selector` (the default), `all-checks`, or any example's id. Everything the app writes is in
its Documents folder, `xcrun simctl get_app_container booted dev.horizon.testing data` says where, debug.log,
screenshots/ and checks/full-report.txt.
