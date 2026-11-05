using System.Reflection;
using Horizon.Engine;

namespace Horizon.Tests.Platform;

public class BoundaryTests
{
    [Fact]
    public void Public_input_and_ui_signatures_do_not_expose_silk_input()
    {
        var types = typeof(GameEngine).Assembly.GetExportedTypes().Where(type => InNamespace(type, "Horizon.Input"))
            .Concat(typeof(Horizon.UI.UIKeyboard).Assembly.GetExportedTypes());
        var leaks = types.SelectMany(type => SignatureTypes(type, publicOnly: true)
            .Where(UsesSilkInput).Select(reference => $"{type.FullName} uses {reference}"));
        Assert.Empty(leaks);
    }

    private static bool UsesSilkInput(Type type) => InNamespace(type, "Silk.NET.Input") ||
        (type.HasElementType && UsesSilkInput(type.GetElementType()!)) ||
        (type.IsGenericType && type.GetGenericArguments().Any(UsesSilkInput));

    [Fact]
    public void Vulkan_types_stay_in_graphics_signatures()
    {
        var leaks = typeof(GameEngine).Assembly.GetTypes()
            .Where(type => !InNamespace(type, "Horizon.Graphics"))
            .SelectMany(type => SignatureTypes(type)
                .Where(UsesVulkan)
                .Select(reference => $"{type.FullName} uses {reference}"))
            .Distinct()
            .Order()
            .ToArray();

        Assert.Empty(leaks);
    }

    [Fact]
    public void Platform_contracts_name_no_silk_types()
    {
        var leaks = typeof(GameEngine).Assembly.GetExportedTypes()
            .Where(type => type.Namespace == "Horizon.Platform")
            .SelectMany(type => SignatureTypes(type, publicOnly: true)
                .Where(UsesSilk)
                .Select(reference => $"{type.FullName} uses {reference}"))
            .ToArray();

        Assert.Empty(leaks);
    }

    [Fact]
    public void The_gpu_is_made_from_the_platform_surface_and_not_from_silk_windows()
    {
        Type[] gpu = [typeof(Horizon.Graphics.GraphicsDevice), typeof(Horizon.Graphics.Vulkan.VulkanContext), typeof(Horizon.Graphics.Vulkan.Swapchain)];
        var leaks = gpu
            .SelectMany(type => SignatureTypes(type)
                .Where(reference => InNamespace(reference, "Silk.NET.Core.Contexts") || InNamespace(reference, "Silk.NET.Windowing"))
                .Select(reference => $"{type.FullName} uses {reference}"))
            .ToArray();

        Assert.Empty(leaks);
    }

    [Fact]
    public void The_engine_and_the_ui_reference_no_desktop_windowing_input_or_sdl()
    {
        string[] desktop = ["Silk.NET.Windowing", "Silk.NET.Input", "Silk.NET.SDL", "Silk.NET.GLFW", "Silk.NET.MoltenVK", "Horizon.Platform.Desktop"];
        var leaks = new[] { typeof(GameEngine).Assembly, typeof(Horizon.UI.UIKeyboard).Assembly }
            .SelectMany(assembly => assembly.GetReferencedAssemblies()
                .Where(reference => desktop.Any(name => reference.Name == name || reference.Name!.StartsWith(name + ".", StringComparison.Ordinal)))
                .Select(reference => $"{assembly.GetName().Name} references {reference.Name}"))
            .ToArray();

        Assert.Empty(leaks);
    }

    [Fact]
    public void Nothing_desktop_is_left_in_the_engine()
    {
        var left = typeof(GameEngine).Assembly.GetTypes()
            .Where(type => InNamespace(type, "Horizon.Platform.Desktop") || type.Name.StartsWith("Glfw", StringComparison.Ordinal) || type.Name.StartsWith("Silk", StringComparison.Ordinal) || type.Name.StartsWith("Sdl", StringComparison.Ordinal))
            .Select(type => type.FullName)
            .ToArray();

        Assert.Empty(left);
    }

    [Fact]
    public void The_engine_and_the_ui_name_no_silk_types_outside_of_the_gpu()
    {
        var types = typeof(GameEngine).Assembly.GetExportedTypes().Where(type => !InNamespace(type, "Horizon.Graphics"))
            .Concat(typeof(Horizon.UI.UIKeyboard).Assembly.GetExportedTypes());
        var leaks = types.SelectMany(type => SignatureTypes(type, publicOnly: true)
                .Where(UsesSilk)
                .Select(reference => $"{type.FullName} uses {reference}"))
            .Distinct()
            .Order()
            .ToArray();

        Assert.Empty(leaks);
    }

    [Fact]
    public void The_window_manager_hands_out_no_native_window_input_or_options()
    {
        string[] gone = ["Window", "Input", "WindowOptions", "ReadsGamepads", "UsesSdl", "CanPlaceWindow"];
        var members = typeof(Horizon.Core.WindowManager).GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
            .Select(member => member.Name)
            .Intersect(gone)
            .ToArray();

        Assert.Empty(members);
    }

    private static bool UsesSilk(Type type) => InNamespace(type, "Silk") ||
        (type.HasElementType && UsesSilk(type.GetElementType()!)) ||
        (type.IsGenericType && type.GetGenericArguments().Any(UsesSilk));

    private static bool InNamespace(Type type, string prefix) =>
        type.Namespace == prefix || type.Namespace?.StartsWith(prefix + ".", StringComparison.Ordinal) == true;

    private static bool UsesVulkan(Type type) =>
        InNamespace(type, "Silk.NET.Vulkan") ||
        (type.HasElementType && UsesVulkan(type.GetElementType()!)) ||
        (type.IsGenericType && type.GetGenericArguments().Any(UsesVulkan));

    private static IEnumerable<Type> SignatureTypes(Type type, bool publicOnly = false)
    {
        BindingFlags flags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        if (!publicOnly) flags |= BindingFlags.NonPublic;
        if (type.BaseType is { } parent) yield return parent;
        foreach (var contract in type.GetInterfaces()) yield return contract;
        foreach (var field in type.GetFields(flags)) yield return field.FieldType;
        foreach (var property in type.GetProperties(flags)) yield return property.PropertyType;
        foreach (var method in type.GetMethods(flags))
        {
            yield return method.ReturnType;
            foreach (var parameter in method.GetParameters()) yield return parameter.ParameterType;
        }
        foreach (var constructor in type.GetConstructors(flags))
            foreach (var parameter in constructor.GetParameters()) yield return parameter.ParameterType;
    }
}
