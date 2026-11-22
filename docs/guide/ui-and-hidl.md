# UI and HIDL

Horizon's UI is UIX, which lives in Horizon.UI, and its layouts are written in HIDL, the engine's scripting language, the examples for this page are `ui` (`Examples/Game/UIBasicsExample.cs`) and `ui-layouts` (`Examples/Game/UILayoutsExample.cs`), followed by `ui-controls`, `ui-skin` and `touch-input` for everything else UIX has.

## A UI is a component

A `UICompositor` is a UI, added to a scene as a component and drawn through a camera, and what that camera sees is the screen the UI is laid out against. `UICompositor.ForScreen()` makes one the size of the window by itself, and the UI is made of `UIModule`s, pieces that are laid out, moved and scaled as one.

UI components own nothing on the GPU, so the whole lot can be made in the scene's constructor, and the UI is updated on the simulation thread, so a button's handler can touch the game directly.

```csharp
// Constructor
ui = AddComponent(UICompositor.ForScreen());
var hud = ui.CreateModule();
health = hud.AddComponent(new ProgressBar { Anchor = Origin.TopLeft, Position = new Vector2(24, -48) });

var menu = ui.CreateModule().LoadLayout("Assets/ui/menu.hor");
menu.Get<Button>("play").OnPressed = StartGame;       // simulation thread, like everything about the UI

// UpdateState
if (mouse.WasPressed(MouseButton.Left) && !ui.IsPointerOverUI) Shoot();
```

`Anchor` says which corner or edge of the screen a thing is measured from and `Position` how far from it, Y goes up, so down from the top is a minus, and resizing the window keeps everything in its corner. `IsPointerOverUI` is what a game checks before it treats a click as a shot.

## The same panel twice

The `ui` example puts the same little panel on screen twice, once built in C# and once written as a script, so the two can be read next to each other.

```csharp
var native = _ui.CreateModule();
native.Position = new Vector2(300, 0);

var panel = native.AddComponent(new StackPanel
{
    Color = PanelColour,
    Radius = 10.0f,
    Padding = new UIEdges(24),
    Spacing = 16,
    Stretch = true      // everything in it as wide as the widest thing in it
});

panel.Add(new Label("Built in C#") { TextScale = 0.45f });
panel.Add(new Button("Hurt") { OnPressed = () => _health.Progress -= 0.25f });
panel.Add(new Button("Heal") { OnPressed = () => _health.Progress += 0.25f });
panel.Add(new Button("Locked") { Enabled = false });
```

The script is `Assets/examples/ui/scripted_panel.hor`, a layout is a script in which everything it makes is a variable, and the game finds them by those names with `layout.Get<Button>("more")`, what a button does can be said right in the layout, for anything that only touches the UI, or left for the game to say.

```
#define layout

let panel = compositor.stack({
    color: vec(0.1, 0.12, 0.17, 0.92),
    radius: 10,
    padding: 24,
    spacing: 16
});

let heading = compositor.label({ parent: panel, text: "Written as a script", text_scale: 0.45 });
let charge = compositor.progress_bar({ parent: panel, size: vec(360, 30), progress: 0.5 });

let row = compositor.stack({ parent: panel, direction: "horizontal" });

let less = compositor.button({
    parent: row,
    label: "Less",
    on_pressed: func() {
        charge.progress = charge.progress - 0.1;
    }
});

let level = compositor.slider({
    parent: panel,
    size: vec(360, 28),
    value: 0.5,
    on_changed: func(value) {
        charge.progress = value;
    }
});
```

A module loaded from a layout is still a module, so it moves and scales as one, and a scaled module is still pressed where it is drawn, Space in the example grows the scripted panel to show it.

## Menus out of layout files

`ui-layouts` is a whole settings screen that lives in `Assets/examples/ui/settings.hor`, with the game only saying what things do.

- `compositor.design` says what screen a layout was made for, it is laid out on exactly that and scaled to fit whatever it is shown on, `"contain"` keeps its shape in the middle so an ultrawide gets room either side, and `"stretch"` lays it out over the whole screen instead.
- `compositor.tabs` makes pages to flip between, every child is a page.
- `compositor.group` is a bunch of things that move, hide and animate as one.
- Anything with an `OnContextMenu` can show a `ContextMenu`, a right click menu.
- `UILayout.Bind` fills a container from a list that keeps changing, each item made from a template file, only what is new gets made and the rest is kept and filled in again.

Hex opens the same `.hor` files and does all of it with a mouse, for whoever would rather click than type.

## Everything else

| Example | What it adds |
|---|---|
| `ui-controls` | one of everything UIX has, dropdowns, sliders, the colour picker, lists and grids, text boxes, on screen keyboards, dialogs and tooltips, all walkable with the arrow keys or a pad through the `UINavigator` |
| `ui-skin` | the Dead Revolver pack as a skin in every colour it comes in, and icons in the middle of text |
| `touch-input` | taps, double clicks, holding for a menu and scrolling by a finger, with every pointer read out |

The skins are under `Assets/uix`, F6 and F7 step every example's UI through them, and the `check-ui-*` checks click through all of the above by themselves, see [Checks](checks.md).
