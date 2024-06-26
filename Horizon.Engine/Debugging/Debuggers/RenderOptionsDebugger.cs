﻿//using Horizon.Rendering;
//using ImGuiNET;

//namespace Horizon.Debugging.Debuggers
//{
//    public class RenderOptionsDebugger : DebuggerComponent
//    {
//        private SkylineDebugger Debugger { get; set; }

//        private float gamma,
//            ambientStrength;

//        private string[] renderModes;
//        private int renderModeIndex;

//        private bool isPostProcessingEnabled,
//            isWireframeEnabled,
//            isBox2DDebugDrawEnabled;

//        public override void Initialize()
//        {
//            RenderOptions = RenderOptions.Default;

//            // Initialize the properties
//            gamma = RenderOptions.Default.Gamma;
//            ambientStrength = RenderOptions.Default.AmbientLightingStrength;

//            // automatically generate the render modes from the DefferedRenderLayer enum
//            renderModes = Enum.GetNames(typeof(DefferedRenderLayer));

//            isPostProcessingEnabled = RenderOptions.Default.IsPostProcessingEnabled;
//            isWireframeEnabled = RenderOptions.Default.IsWireframeEnabled;

//            Debugger = (SkylineDebugger)Parent;

//            Name = "Render Options";
//        }

//        public RenderOptions RenderOptions;

//        public override void Render(float dt, ref RenderOptions options)
//        {
//            if (!Visible)
//                return;

//            // Collapsible header for Render Options window
//            if (ImGui.Begin(Name))
//            {
//                ImGui.DragFloat("Gamma", ref gamma, 0.01f, 0.1f, 10.0f);
//                ImGui.DragFloat("Ambient", ref ambientStrength, 0.01f, 0.0f, 10.0f);

//                // Listbox to select the render mode.
//                ImGui.Combo("Render Mode", ref renderModeIndex, renderModes, renderModes.Length);

//                ImGui.Checkbox("Post Processing", ref isPostProcessingEnabled);
//                ImGui.Checkbox("Wireframe Mode", ref isWireframeEnabled);
//                ImGui.Checkbox("Box2D Debugger", ref isBox2DDebugDrawEnabled);

//                ImGui.End();
//            }
//        }

//        public override void Dispose() { }

//        public override void UpdateState(float dt)
//        {
//            RenderOptions = RenderOptions.Default with
//            {
//                AmbientLightingStrength = ambientStrength,
//                Gamma = gamma,
//                DebugOptions = DebugRenderOptions.Default with
//                {
//                    DefferedLayer = (DefferedRenderLayer)renderModeIndex
//                },
//                IsPostProcessingEnabled = isPostProcessingEnabled,
//                IsWireframeEnabled = isWireframeEnabled,
//                IsBox2DDebugDrawEnabled = isBox2DDebugDrawEnabled
//            };
//        }

//        public override void UpdatePhysics(float dt) { }
//    }
//}
