# Horizon.Platform.iOS

The engine on an iPhone or an iPad, the iOS twin of Horizon.Platform.Android. Not in any solution on purpose, a desktop
build never needs the iOS workload, and an app on it only builds on a Mac with Xcode (the .NET 11 iOS workload wants
Xcode 27).

A game's app delegate derives from `HorizonAppDelegate` and says which scene to start in. That sets the process up
(`IosBootstrap`, the console and Documents/debug.log, the HORIZON_* settings, the bundle as content, Application Support
and Caches as storage), puts up a `HorizonViewController` with a fullscreen `HorizonMetalView`, and the first time the
view is laid out with a size the engine is made on `IosPlatform` and started. MoltenVK makes the Vulkan surface on the
view's CAMetalLayer (`IosSurface`, VK_EXT_metal_surface), and the app links MoltenVK statically through
Silk.NET.MoltenVK.Native.

- Lifecycle. An app in the background mustn't touch the GPU, Metal kills it. `IosSession` takes the swapchain and the
  surface off the layer in applicationDidEnterBackground (it waits there for the render thread, the same detach the
  Android host does in surfaceDestroyed) and puts them back as the app comes to the front. Resigning active (a call,
  the notification centre) pauses sound and lets go of every touch and key.
- Input. Fingers, the Pencil and an iPad's trackpad or mouse (`IosPointers`), a hardware keyboard's presses by HID
  usage (`IosKeys`), and the soft keyboard through UIKeyInput while the UI asks for text. Controllers aren't read yet.
- Sound. `IosAudioOutput`, an AVAudioEngine with one source node, ambient session, the hardware's own rate.
- Settings. Documents/horizon.env and then the process's environment, which the simulator fills from
  `SIMCTL_CHILD_HORIZON_*`. Shaders only ever come out of the pack, HORIZON_SHADERS is always packed.

Still to do, a UIScene lifecycle (iOS 26 warns about apps without one), GameController pads, audio interruptions and
route changes, a release build with AOT, signing for a real phone. Horizon.Testing.iOS is the app that runs it all.
