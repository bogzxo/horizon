using Horizon.Content;
using Horizon.OpenGL;
using Horizon.OpenGL.Assets;

using ImGuiNET;

using System.Numerics;

namespace Horizon.Engine.Debugging.Debuggers;

public class LoadedContentDebugger : DebuggerComponent
{
    private SkylineDebugger Debugger { get; set; }

    public override void Initialize()
    {
        Debugger = (SkylineDebugger)Parent!;

        Name = "Content Manager";
    }

    public override void Render(float dt, object? obj = null)
    {
        if (!Visible)
            return;

        if (ImGui.Begin(Name))
        {
            DrawTextureSection();
            //DrawShaderSection();

            ImGui.End();
        }
    }

    public override void Dispose() { }

    private void DrawTextureSection()
    {
        var columnWidth = ImGui.GetContentRegionAvail().X;
        var itemSpacing = ImGui.GetStyle().ItemSpacing.X;

        if (ImGui.TreeNode("Textures"))
        {
            var imageSideLength = 100;
            var imagesPerRow = Math.Max(
                1,
                (int)(columnWidth / (imageSideLength + itemSpacing))
            );

            ImGui.Columns(imagesPerRow, "TextureColumns", false);


            int collectionSize = GameEngine.Instance.ObjectManager.Textures.OwnedAssets.Count;

            for (int i = 0; i < collectionSize; i++)
            {
                if (GameEngine.Instance.ObjectManager.Textures.OwnedAssets.Count != collectionSize)
                    break; // detect collection modification

                var texture = GameEngine.Instance.ObjectManager.Textures.OwnedAssets[i];

                ImGui.BeginGroup();

                ImGui.Image(
                    (IntPtr)texture.Handle,
                    new Vector2(imageSideLength, imageSideLength)
                );
                ImGui.TextWrapped($"Texture({texture.Handle})");

                ImGui.EndGroup();

                DrawTextureContextMenu(texture);

                ImGui.NextColumn();
            }

            ImGui.Columns(1);
            ImGui.TreePop(); // Moved here
        }
    }

    private void DrawTextureContextMenu(Texture texture)
    {
        if (ImGui.BeginPopupContextItem($"TextureContextMenu_{texture.Handle}"))
        {
            if (ImGui.MenuItem("Delete"))
            {
                GameEngine.Instance.ObjectManager.Textures.Remove(texture.Handle);
            }
            ImGui.EndPopup();
        }
    }

    //private void DrawShaderSection()
    //{
    //    if (ImGui.TreeNode("Shaders"))
    //    {
    //        foreach (var shader in ContentManager.GetShaders())
    //        {
    //            ImGui.Text($"Shader: {shader.ToString()}");
    //            // Add more shader preview UI elements here
    //        }
    //        ImGui.TreePop();
    //    }
    //}

    public override void UpdatePhysics(float dt) { }

    public override void UpdateState(float dt) { }
}
