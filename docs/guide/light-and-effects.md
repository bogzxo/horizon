# Light and effects

The third level of the examples, lamps, shadows, bounced light and the glass over the picture, `lighting`, `pathtraced` and `post`, the first two being one scene, a brick cellar out of a map, started with the path tracing off and on.

## Deferred lighting

A `DeferredRenderer2D` lights everything added to it, its ambient light is the colour of the dark, and its lights are `Light2D`s, point lights, spots and directional light like a moon or a sun (`LightType`), moved by setting where they are.

```csharp
// Initialize, render thread
lighting = AddEntity(new DeferredRenderer2D(width, height) { Ambient = new Vector3(0.18f, 0.2f, 0.28f) });
map = lighting.AddEntity(TileMap.Load("Assets/maps/cellar.tmx"));
lighting.Occlusion = map.CreateOcclusion();              // what blocks light, by the shape of the tiles
lighting.AmbientOcclusion.Enabled = true;

lamp = lighting.AddLight(new Light2D { Position = where, Radius = 200, Color = warm, Flicker = 0.1f });

// UpdateState, simulation thread
lamp.Position = player.Position;
lighting.AddFlash(new Light2D { Position = hit, Radius = 300, Intensity = 3 }, 0.4f);
```

`CreateOcclusion` makes what blocks light out of the map layers that say they cast shadows, a sprite throws a shadow of its own with `CastsShadows`, sharp to its pixels, and `AddFlash` puts a light in for a moment, a hit or a shot. `LightingPixelSize` lights pixel art one pixel of the art at a time, so the light steps with the art rather than smoothing over it.

A tile set's normal, specular and ambient occlusion maps are the files next to its picture with `_normal`, `_specular` and `_ao` on the end of the name, nothing has to be said for them to be found, the cellar's bricks have all three, with some of them glazed by the specular map. `Scene2D` sets all of this up by itself when it is made `lit`, see the [quickstart](getting-started.md#the-first-example).

## Path traced lighting

Set to `LightingMode.PathTraced`, through `DeferredRenderer2D.Lighting` or `Scene2D.Fancy`, the same renderer traces the light rather than adding it up, with radiance cascades, so it bounces off the bricks and spills round the pillars, and anything emissive is a lamp in its own right, lava, mushrooms, a crystal carried about, without a `Light2D` anywhere near it. `Sprite.FlashLights` has a sprite light its surroundings for a moment, the way the sparks in the `dungeon` showcase do, and the `pathtraced` example has the tracer's own buffer a key away for seeing what it sees.

The `village` showcase runs its whole day on the tracer, a directional sun that goes round, windows that come on at dusk, and an `ExposureEffect` that gets used to the dark like an eye does.

## Post processing

A renderer's `PostProcessing` is a list of effects run over what it drew, in the order they were added, and a renderer can hold another renderer, so the effects of the outer one go over the inner one and anything else in it alike.

```csharp
screen = AddEntity(new Renderer2D(width, height));
blur = screen.PostProcessing.Add(new BlurEffect { Enabled = false });
screen.PostProcessing.Add(new CrtEffect { PixelSize = 3 });      // last, on top of what the others did

world = screen.AddEntity(new DeferredRenderer2D(width, height)); // lit, and behind the glass
screen.AddComponent(new UICompositor(camera));                  // so is the HUD

blur.Enabled = paused;                                          // any thread, takes at the next frame
```

`CrtEffect` wants to be last, it is the glass, `Enabled` switches an effect off without taking it out, and a `UICompositor` added to the renderer rather than the scene goes through the effects with the rest, which is how the `post` example's HUD ends up behind the tube. `BloomEffect` gives what is bright a glow, the `town` and `dungeon` showcases have it, and an effect has `Tweens` of its own for fading it in and out.

## Render scale and upscaling

The scene can be drawn smaller than the window and blown back up, F4 steps through Auto, Native, 75% and 50% in any example and F5 opens the picker, upscaler and all. `WindowManagerConfiguration.RenderScale` sets it at start, `WindowManager.Scaling.Settings` changes it while running, and the `HORIZON_RENDER_SCALE`, `HORIZON_UPSCALER`, `HORIZON_UPSCALER_SHARPNESS` and `HORIZON_RENDER_COMPOSITION` settings win over both. The upscalers are CAS, GSR 1, FSR 1, bilinear and nearest, the UI can stay at full size over a scene drawn small or be drawn small with the whole frame, and the `check-render-scale` and `check-render-scale-quality` checks hold them to their references and log how close each comes to native.
