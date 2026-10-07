using System;
using System.Collections.Generic;
using System.Drawing;
using System.Numerics;
using Horizon.Engine;
using Horizon.Rendering;
using Horizon.Rendering.UIX;
using Horizon.Rendering.UIX.Components;

namespace Horizon.Testing;

/// <summary>
/// The list of tests: a button for each, with what it is about written next to it. Pressing one
/// hands the test to the <see cref="TestHost"/> to run.
/// </summary>
internal class TestSelectorScene : Scene
{
    private const int TestsPerPage = 8;
    private const float RowHeight = 56.0f;
    private const float RowSpacing = 10.0f;

    public override Camera ActiveCamera { get; protected set; }

    // The host keeps the selector and shows it again as it was left
    public override bool Persistent => true;

    private readonly List<(Button Name, Label Description)> rows = [];
    private readonly Button previousPage, nextPage;
    private readonly Label pageNumber;
    private int page;

    private int PageCount => Math.Max(1, (rows.Count + TestsPerPage - 1) / TestsPerPage);

    public TestSelectorScene(TestHost host, IReadOnlyList<TestDefinition> tests)
    {
        // Create a 2d scene camera
        Camera2D cam = AddEntity(new Camera2D(Engine.WindowManager.ViewportSize));
        ActiveCamera = cam;

        // Components need nothing from the GPU, so the whole screen can be put together right here.
        var compositor = AddComponent(new UICompositor(cam));
        var panel = compositor.CreateModule().AddComponent(new StackPanel
        {
            Color = new Vector4(0.1f, 0.12f, 0.17f, 0.92f),
            Padding = new UIEdges(32),
            Spacing = 24,
            Background = "panel"
        });

        panel.Add(new Label("Horizon tests") { TextScale = 0.6f });

        // Two columns sharing a row height, so every description lines up with its button and the
        // buttons all come out as wide as the widest of them.
        var columns = panel.Add(new StackPanel { Direction = UIDirection.Horizontal, Spacing = 20 });
        var names = columns.Add(new StackPanel { Stretch = true, Spacing = RowSpacing });
        var descriptions = columns.Add(new StackPanel { Spacing = RowSpacing });

        // With several pages the list keeps the height of a full one, so a short last page doesn't
        // pull the page buttons out from under the pointer.
        if (tests.Count > TestsPerPage)
        {
            float fullPage = TestsPerPage * RowHeight + (TestsPerPage - 1) * RowSpacing;
            names.Size = descriptions.Size = new Vector2(0, fullPage);
        }

        foreach (var test in tests)
        {
            rows.Add((
                names.Add(new Button(test.Name)
                {
                    Size = new Vector2(0, RowHeight),
                    OnPressed = () => host.Start(test)
                }),
                descriptions.Add(new Label(test.Description)
                {
                    Anchor = Origin.Left,
                    Align = Origin.Left,
                    Size = new Vector2(0, RowHeight),
                    TextScale = 0.3f
                })));
        }

        // Only shown once there are more tests than fit on one page.
        var pager = panel.Add(new StackPanel { Direction = UIDirection.Horizontal, Visible = PageCount > 0 });
        previousPage = pager.Add(new Button("<") { LabelScale = 0.3f, Size = new Vector2(96, 44), OnPressed = () => ShowPage(page - 1) });
        pageNumber = pager.Add(new Label { TextScale = 0.3f });
        nextPage = pager.Add(new Button(">") { LabelScale = 0.3f, Size = new Vector2(96, 44), OnPressed = () => ShowPage(page + 1) });

        panel.Add(new Label("ESC or Back returns here from a test")
        {
            TextScale = 0.25f,
            Color = new Vector4(0.93f, 0.95f, 1.0f, 0.6f)
        });

        ShowPage(0);
    }

    private void ShowPage(int index)
    {
        page = Math.Clamp(index, 0, PageCount - 1);

        // Every row exists all along; a page is just the rows that are visible.
        for (int i = 0; i < rows.Count; i++)
            rows[i].Name.Visible = rows[i].Description.Visible = i / TestsPerPage == page;

        previousPage.Enabled = page > 0;
        nextPage.Enabled = page < PageCount - 1;
        pageNumber.Text = $"{page + 1} / {PageCount}";
    }

    public override void Render(float dt)
    {
        // Set every frame rather than once: the test that was just left will have changed it.
        Engine.GL.ClearColor(Color.CornflowerBlue);
        base.Render(dt);
    }
}
