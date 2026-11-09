# Horizon.Platform.iOS

Work in progress. The project is deliberately outside the desktop solutions.

The initial adapters provide a Metal-layer Vulkan surface and immutable bundled content. The host owns the layer and must await surface detach before calling `IosSurface.Release` or disposing the view. Content resources retain `Assets/...` and `shaders/...` paths and casing.

These files have not yet been built or run on iOS. The UIKit host, bootstrap, input, audio, lifecycle, and Fighter2D head are still required. A separate native capability probe is under workspace `artifacts/ios-platform-baseline`, with its build and runtime evidence kept there. A managed build on Windows does not prove an installable iOS app.

IosStorage maps settings, data and state to Application Support and regenerable files to Caches, using DiskStorage transactional writes after path validation. It remains uncompiled under the shared build hold.

