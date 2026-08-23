using System;
using System.Collections.Generic;
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
                    if (args[0] is not StringValue label) throw new Exception("Button label text must be a string!");

                    return AddComponent(new Button(label.Value)).Object;
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
