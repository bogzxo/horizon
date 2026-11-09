# Horizon.Testing.Android

The whole of Horizon.Testing on a real phone or tablet, the selector, every example and every check, plus device checks
only a phone has. Application id `dev.horizon.testing`, activity `dev.horizon.testing/dev.horizon.testing.TestingActivity`.
Every source is linked from `../Horizon.Testing`, nothing is copied. Nothing desktop is referenced. Not in any solution,
a desktop build never needs the Android workload.

What's the desktop's alone and left out of the link is `Program.cs` and `Host/TestPlatform.Desktop.cs` (its Android half
is `Host/TestPlatform.Android.cs` here). The terminal runs /system/bin/sh over the engine's pty from API 34, below that
it's marked "(not here)" and starting it shows why. The app starts in its files folder. Results so far are in workspace
`artifacts/android-platform-baseline/android-full-testing-results.md`.

Keyboard and mouse work the way they do on a desktop, arrows and Enter in the selector, Esc for the menu, Page Up and
Page Down between examples, F1, F3, F6, F7. Touch taps the same buttons, Back is Esc. The selector has a Run every check
button under the checks.

## Build

The shader pack has to exist first (`Horizon/shaders/packed/*.hzshaders`), the build stops with an error otherwise.

```
dotnet build horizon/Horizon.Testing.Android/Horizon.Testing.Android.csproj -c Debug -r android-arm64 ^
  -p:AndroidSdkDirectory=C:/Users/bogdan/AppData/Local/Android/Sdk ^
  -p:JavaSdkDirectory=D:/Microsoft/VSShared/Android/openjdk/jdk-21.0.8
```

The signed APK is `bin/Debug/net11.0-android/android-arm64/dev.horizon.testing-Signed.apk`. Check its layout before
installing, every content path keeps its case and sits under `assets/Assets/` or `assets/shaders/`.

```
unzip -l dev.horizon.testing-Signed.apk | grep -E "assets/(Assets|shaders)/" | wc -l
unzip -l dev.horizon.testing-Signed.apk | grep "assets/shaders/packed/"
```

## Install and run (a real device, never the emulator for results)

```
adb -s R52M416M2TH install -r dev.horizon.testing-Signed.apk
adb -s R52M416M2TH shell am force-stop dev.horizon.testing
adb -s R52M416M2TH shell am start -n dev.horizon.testing/dev.horizon.testing.TestingActivity ^
  --es HORIZON_TESTING_SCENE checks --es HORIZON_SCREENSHOT shot.png@5
adb -s R52M416M2TH logcat -s Horizon AndroidRuntime DOTNET
```

Settings are string extras (`--es`) named `HORIZON_*`, or lines `NAME=value` in `files/horizon.env`. Extras win. They
go into the process's own environment only. A process takes its settings once, so `am force-stop` before a run with
new ones.

| Setting | What |
|---|---|
| `HORIZON_TESTING_SCENE` | `selector` (default), `all-checks` (every catalog check, the desktop's `--checks`, device checks last), `checks` (the device checks alone), or any catalog id or alias, `sprites`, `village`, `check-ui-skins` |
| `HORIZON_TESTING_QUIT=on` | With `all-checks` or `checks`, quits once the report is written. Without it `all-checks` goes back to the selector |
| `HORIZON_TESTING_CHECK_TIMEOUT` | Seconds one check scene gets in `all-checks` before it counts as hung and failed, 300 by default |
| `HORIZON_SCREENSHOT` | `file.png@3` or `file.png@3+8x0.5` as on desktop, relative to the screenshots folder below |
| `HORIZON_INPUT_SCRIPT` | A script relative to the external files folder (adb push) or the files folder (run-as) |
| `HORIZON_AUDIO=off` | No AAudio stream at all, the audio check then says skipped |
| `HORIZON_SHADERS` | Always `packed` on a phone, anything else is replaced and logged |
| `HORIZON_LOG_LOOPS`, `HORIZON_VULKAN_VALIDATION`, the rest | As on desktop |

## Results

```
adb pull /sdcard/Android/data/dev.horizon.testing/files/screenshots/shot.png
adb pull /sdcard/Android/data/dev.horizon.testing/files/checks/report.json
adb pull /sdcard/Android/data/dev.horizon.testing/files/checks/report.txt
adb pull /sdcard/Android/data/dev.horizon.testing/files/checks/full-report.txt
adb pull /sdcard/Android/data/dev.horizon.testing/files/checks/full-report.json
adb exec-out run-as dev.horizon.testing cat files/debug.log > debug.log
adb exec-out run-as dev.horizon.testing cat files/debug-previous.log > debug-previous.log
```

## Trying input by hand

`--es HORIZON_TESTING_SCENE touch-input` is the page for touch, a mouse and a keyboard. Every event it hears goes into
`debug.log` with `[TouchInput]` in front, Back is Escape, and a tap on the performance pill in the corner steps it like
F3. Results of the input work are in workspace `artifacts/android-platform-baseline/touch-input-results.md`.

## Lifecycle by hand

```
adb shell input keyevent KEYCODE_HOME          (surfaceDestroyed, detach, release in the log)
adb shell am start -n dev.horizon.testing/dev.horizon.testing.TestingActivity   (same engine, generation +1)
adb shell input keyevent KEYCODE_BACK          (Escape to the scene, the app stays)
```

Rotation with auto rotate off is `settings put system user_rotation 1`. Read `accelerometer_rotation` and
`user_rotation` first and put them back after.

## Validation layer

Per app, following https://developer.android.com/ndk/guides/graphics/validation-layer and the probe's run in
`artifacts/android-platform-baseline/validation-layer-setup.md`. Save the four settings first and restore them after.

```
adb shell run-as dev.horizon.testing cp /data/local/tmp/horizon-libVkLayer_khronos_validation.so ./libVkLayer_khronos_validation.so
adb shell settings put global enable_gpu_debug_layers 1
adb shell settings put global gpu_debug_app dev.horizon.testing
adb shell settings put global gpu_debug_layers VK_LAYER_KHRONOS_validation
adb shell settings put global gpu_debug_layer_app dev.horizon.testing
adb shell am start -n dev.horizon.testing/dev.horizon.testing.TestingActivity --es HORIZON_VULKAN_VALIDATION on --es HORIZON_TESTING_SCENE checks
```
