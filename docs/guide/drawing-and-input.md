# Drawing, input and the world

The rest of the first steps, `shapes`, `sprites`, `camera`, `input` and `gamepads`, each of them a scene in `Examples/FirstSteps/` with an "In your own game" block at the top of the file, which is where the snippets on this page come from.

## Shapes

Flat coloured shapes with no texture anywhere, lines, boxes and discs, are drawn in one draw call by the `PrimitiveRenderer`, a `Scene2D` has one ready as `Shapes`, and what it draws is written down by a callback, `Describe`, which is called at the end of every tick on the simulation thread with the list cleared, so it always draws whatever things look like right now.

```csharp
Shapes.Describe = shapes =>
{
    shapes.FillRectangle(new Vector2(-384.0f, -256.0f), new Vector2(384.0f, -224.0f), wall);
    shapes.FillCircle(ball, 14.0f, ballColour);
};
```

The `shapes` example goes further, debug overlays over bouncing balls, a bar graph, and a `Mesh2D` of your own drawn with a `Technique` of your own, the two shaders it uses are in `shaders/testing/`.

## Sprites

A `SpriteBatch` draws a pile of sprites in a call or two, a `Sprite` has a `Transform` for its position, size, origin and rotation, and a `SpriteSheet` cut from a texture gives it named animations.

```csharp
// Initialize, render thread: the sheet is a texture, so it can't be made any earlier than this
var sheet = SpriteSheet.FromTexture(texture, new Vector2(16));     // 16 by 16 pixels a frame
var batch = AddEntity(new SpriteBatch());

var blob = batch.AddEntity(new Sprite(new Vector2(96)));           // AddEntity so it gets updated...
blob.ConfigureSpriteSheet(sheet, "idle");
blob.AddAnimation("idle", new Vector2(0, 0), 4, 0.15f);            // first frame at column 0, row 0, 4 frames long
blob.AddAnimation("hop", new Vector2(0, 1), 6);
batch.Add(blob);                                                   // ...and Add so it gets drawn

// UpdateState, simulation thread
blob.SetAnimation("hop");
blob.Flipped = true;
blob.Punch();
```

A sprite is added twice on purpose, `AddEntity` so it gets its turns and `Add` so the batch draws it. Scenery that comes out of an atlas is described by a `SpriteSheetDefinition`, a `.hor` file next to the picture that says what is where, and packed into a `TextureAtlas`, which only goes near the GPU when a batch first draws out of it. `Flipped`, `Tint`, `Smooth` and the ready made `SpriteTweens` (`Punch` among them) are the tricks the example goes through one at a time.

## Cameras

A `Camera2D` is told how much of the world it sees, and there is no GPU in a camera, so it can be made whenever.

```csharp
// Constructor
_camera = AddEntity(new Camera2D(new Vector2(400, 225)));  // sees 400 x 225 of the world, 4 pixels a unit at 1600 x 900
ActiveCamera = _camera;
_camera.PixelSnap = 1.0f;                                  // pixel art: only ever shown on whole pixels

// UpdateState, simulation thread
_eye = Vector2.Lerp(_eye, player.Position, 1.0f - MathF.Exp(-4.0f * dt));
_camera.Position = new Vector3(_eye, 0.0f);
_camera.PixelSnapAnchor = player.Position;                 // the player holds still, the world steps under them
Vector2 aim = _camera.ScreenToWorld(Engine.Input.Mouse.Position);

// Respawned on the other side of the map? Cut, don't swoop
_eye = player.Position;
_camera.Position = new Vector3(_eye, 0.0f);
_camera.Snap();
```

`PixelSnapAnchor` is what stops the thing being followed from wobbling a pixel back and forth as the world snaps, `Bounds` says what is on screen, and the `camera` example's minimap is a `SpriteBatch` drawn through a camera of its own with `SpriteBatch.CustomCamera`. The zoom levels it steps through are all a whole number of screen pixels per art pixel, anything in between leaves some art pixels a screen pixel fatter than the rest.

## Keyboard and mouse

Input is read in `UpdateState`, on the simulation thread, through `Engine.Input`, by the time an update runs input has already moved on for it.

```csharp
var keyboard = Engine.Input.Keyboard;
if (keyboard.WasPressed(Key.Space)) Jump();                 // true for exactly one update per press
float steer = keyboard.Axis(Key.A, Key.D);                  // -1, 0 or 1

var mouse = Engine.Input.Mouse;
Vector2 aim = camera.ScreenToWorld(mouse.Position);         // window pixels -> the world
if (mouse.WasPressed(MouseButton.Left)) Shoot(aim);
camera.Zoom -= mouse.Scroll * 0.1f;                         // how far the wheel went THIS update
```

`IsDown`, `WasPressed` and `WasReleased` work the same for keys and mouse buttons, the mouse also has `Delta`, and `AnyPressed` says whether any key went down this update. Left alone for a few seconds the `input` example has a ghost have a go on it, and `touch-input` later reads fingers, taps and holds the same way.

## Gamepads

`Engine.Input.Gamepads` holds every pad that is plugged in, `LastUsed` is the one last touched, and a pad answers `IsDown` and `WasPressed` with a `GamepadInput`, the same as the keyboard does with a `Key`. Actions are bound once and hold for every pad, the ones plugged in later included.

```csharp
_gamepads = AddEntity(new GamepadInputManager());
_gamepads.DefaultBindings
    .Bind("jump", GamepadInput.A)
    .Bind("fire", GamepadInput.RightTrigger)
    .AddCombination("fire", GamepadInput.B)
    .AddCombination("special", GamepadInput.A, GamepadInput.B);
```

The `gamepads` example also has a pad that is no device at all and is fed by hand from the keyboard, and writes its bindings out as HIDL and reads them back, `HORIZON_INPUT_SCRIPT` plays a scripted pad into any example, which is how the examples are driven with nobody at the controls.
