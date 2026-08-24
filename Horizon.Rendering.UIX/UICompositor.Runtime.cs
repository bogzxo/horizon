using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text;

using Horizon.HIDL;
using Horizon.HIDL.Runtime;
using Horizon.Rendering.UIX.Components;

namespace Horizon.Rendering.UIX;

public partial class UICompositor
{
    protected void SetupRuntime()
    {
        _runtime.UserScope.DeclareSystem("compositor", new ObjectValue()
        {
            Properties = new Dictionary<string, IRuntimeValue>()
            {
                {"button", new NativeFunctionValue((args, env) =>
                {
                    if (args.Length != 1) throw new Exception("Button constructor expects one parameter");

                    string label = string.Empty;
                    float sprScale = 1.0f;
                    float lblScale = 1.0f;
                    Vector2 pos = Vector2.One;

                    if (args[0] is ObjectValue obj)
                    {
                        if (obj.Properties["label"] is StringValue labelVal)
                            label = labelVal.Value;
                        if (obj.Properties["spr_scale"] is NumberValue sprScaleVal)
                            sprScale = sprScaleVal.Value;
                        if (obj.Properties["lbl_scale"] is NumberValue lblScaleVal)
                            lblScale = lblScaleVal.Value;
                        if (obj.Properties["position"] is Vector2Value posVal)
                            pos = posVal.Value;
                    }
                    else throw new Exception("Buttons constructor required object scheme");

                    return AddComponent(new Button(label, pos, sprScale, lblScale)).Object;
                })},


                {"progress_bar", new NativeFunctionValue((args, env) =>
                {
                    return AddComponent(new ProgressBar()).Object;
                })}
            }
        });

    }
    
    /// <summary>
    /// Attempts to construct a UI using HIDL.
    /// </summary>
    /// <param name="definitionPath">The path to the definition file.</param>
    /// <exception cref="FileNotFoundException">If the defintion could not be found</exception>
    public (bool success, string result) Load(string definitionPath) => !File.Exists(definitionPath) ? throw new FileNotFoundException($"The file '{definitionPath}' could not be found!") : _runtime.Evaluate(File.ReadAllText(definitionPath));
}
