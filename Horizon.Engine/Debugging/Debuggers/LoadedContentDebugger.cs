#if DEBUG
using System.Numerics;

using Horizon.OpenGL.Assets;

using Egui;
using Egui.Containers;
using Egui.Widgets;

namespace Horizon.Engine.Debugging.Debuggers;

public class LoadedContentDebugger : DebuggerComponent
{
    private SkylineDebugger Debugger { get; set; }

    public override void Initialize()
    {
        Debugger = (SkylineDebugger)Parent!;

        Name = "Content Manager";
    }

    public override void RenderUi(Ui root)
    {
        if (!Visible)
            return;

        new Window(Name)
            .Show(root.Ctx, ui =>
            {
                DrawTextureSection(ui);
                //DrawShaderSection(ui);
            });
    }

    public override void Dispose()
    { }

    public override void UpdatePhysics(float dt)
    { }

    public override void UpdateState(float dt)
    { }

    private void DrawTextureSection(Ui ui)
    {
        ui.Collapsing("Textures", innerUi =>
        {
            int collectionSize = GameEngine.Instance.ObjectManager.Textures.OwnedAssets.Count;

            for (int i = 0; i < collectionSize; i++)
            {
                if (GameEngine.Instance.ObjectManager.Textures.OwnedAssets.Count != collectionSize)
                    break; // detect collection modification

                var texture = GameEngine.Instance.ObjectManager.Textures.OwnedAssets[i];

                innerUi.Horizontal(row => 
                {
                    row.Label($"Texture({texture.Handle})");
                    if (row.Button("Delete").Clicked)
                    {
                        GameEngine.Instance.ObjectManager.Textures.Remove(texture.Handle);
                    }
                });
            }
        });
    }
}

#endif