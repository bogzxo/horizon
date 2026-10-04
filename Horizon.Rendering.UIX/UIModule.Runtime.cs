using Horizon.HIDL;
using Horizon.HIDL.Runtime;
using Horizon.Rendering.UIX.Components;

namespace Horizon.Rendering.UIX;

public partial class UIModule
{
    private readonly Dictionary<string, IRuntimeValue> factories = [];

    public HIDLRuntime Runtime { get; init; } = new();

    protected void SetupRuntime()
    {
        Runtime.UserScope.DeclareSystem("compositor", new ObjectValue(factories));

        Register<Button>("button");
        Register<ToggleButton>("toggle");
        Register<ProgressBar>("progress_bar");
        Register<Slider>("slider");
        Register<TextBox>("textbox");
        Register<Label>("label");
        Register<Image>("image");
        Register<Panel>("panel");
        Register<StackPanel>("stack");
    }

    /// <summary>
    /// Lets scripts create a kind of component as compositor.name({ ... }). The object is optional and
    /// can set any property the component exposes, including parent to put it inside another component;
    /// a component without a parent is added to the module itself.
    /// </summary>
    public void Register<T>(string name) where T : UIComponent, new()
    {
        factories[name] = new NativeFunctionValue((args, _) =>
        {
            if (args.Length > 1 || (args.Length == 1 && args[0] is not ObjectValue))
                throw new Exception($"compositor.{name} expects nothing, or one object with the properties to set.");

            var component = new T();
            try
            {
                if (args.Length == 1)
                    component.ApplyScript((ObjectValue)args[0]);
            }
            catch
            {
                // Don't leave a half configured component behind in whatever parent it was given.
                component.Parent?.Remove(component);
                throw;
            }

            if (component.Parent is null)
                AddComponent(component);

            return component.Object;
        });
    }

    /// <summary>
    /// Attempts to construct a UI using HIDL.
    /// </summary>
    /// <param name="definitionPath">The path to the definition file.</param>
    /// <exception cref="FileNotFoundException">If the defintion could not be found</exception>
    public (bool success, string result) Load(string definitionPath) => !File.Exists(definitionPath) ? throw new FileNotFoundException($"The file '{definitionPath}' could not be found!") : Runtime.Evaluate(File.ReadAllText(definitionPath));
}
