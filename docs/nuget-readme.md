# Horizon

Horizon is a game engine for .NET, drawn through Vulkan, with a UI toolkit and a scripting language of its own. It is the engine the Hex code editor is built on.

The engine comes as four packages of one version, to be used together:

| Package | What it is |
|---|---|
| `bogzxo.Horizon.Engine` | The runtime: renderer, content, input, audio, entities, physics. Brings its shaders, fonts and UI skins and copies them next to your program |
| `bogzxo.Horizon.Platform.Desktop` | Windows, macOS and Linux: the window, keyboard, mouse, controllers and audio |
| `bogzxo.Horizon.UI` | The compositor, components, skins, layouts, the code editor and `Scene2D` |
| `bogzxo.Horizon.HIDL` | The scripting language, usable on its own |

A game needs the first three. `bogzxo.Horizon.HIDL` comes with them.

```xml
<ItemGroup>
  <PackageReference Include="bogzxo.Horizon.Engine" Version="1.4.0" />
  <PackageReference Include="bogzxo.Horizon.Platform.Desktop" Version="1.4.0" />
  <PackageReference Include="bogzxo.Horizon.UI" Version="1.4.0" />
</ItemGroup>
```

They target .NET 11. `bogzxo.Horizon.UI` and `bogzxo.Horizon.Platform.Desktop` work only with the `bogzxo.Horizon.Engine` of the same version.

Source: https://github.com/bogzxo/horizon
