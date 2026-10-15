using Horizon.HIDL;
using Horizon.HIDL.Interop;

namespace Horizon.Tests;

// HIDL's bridge to .NET, types, objects, members, overloads, out parameters and what goes across
public class HidlInteropTests
{
    private static HIDLRuntime Bridged()
    {
        var runtime = new HIDLRuntime { Output = null };
        HIDLInterop.Declare(runtime);
        return runtime;
    }

    [Fact]
    public void A_static_method_is_called_with_the_overload_that_fits_and_a_constant_read()
    {
        var (success, result) = Bridged().Evaluate("let math = dotnet.type(\"System.Math\");\ntext(math.Max(3, 7)) + \" \" + text(math.Round(math.PI))");

        Assert.True(success, result);
        Assert.Equal("7 3", result);
    }

    [Fact]
    public void An_object_is_made_and_its_members_called_set_and_read()
    {
        var (success, result) = Bridged().Evaluate(
            "let builder = dotnet.new(\"System.Text.StringBuilder\");\n" +
            "builder.Append(\"hi \");\n" +
            "builder.Append(3);\n" +
            "builder.Capacity = 64;\n" +
            "builder.ToString() + \" \" + text(builder.Length) + \" \" + text(builder.Capacity >= 64)");

        Assert.True(success, result);
        Assert.Equal("hi 3 4 true", result);
    }

    [Fact]
    public void Out_parameters_come_back_by_their_names_and_lists_go_in_as_arrays()
    {
        var (success, result) = Bridged().Evaluate(
            "let parsed = dotnet.type(\"System.Int32\").TryParse(\"42\");\n" +
            "let joined = dotnet.type(\"System.String\").Join(\"-\", [\"a\", \"b\", \"c\"]);\n" +
            "text(parsed.returned) + \" \" + text(parsed.result) + \" \" + joined");

        Assert.True(success, result);
        Assert.Equal("true 42 a-b-c", result);
    }

    [Fact]
    public void An_assembly_already_loaded_is_the_one_handed_back_and_its_types_found()
    {
        string horizon = typeof(HIDLRuntime).Assembly.Location;
        var (success, result) = Bridged().Evaluate($"let hidl = dotnet.load(\"{horizon.Replace("\\", "/")}\");\nhidl.type(\"Horizon.HIDL.HIDLRuntime\").VERSION");

        Assert.True(success, result);
        Assert.Equal(HIDLRuntime.VERSION, result);
    }

    [Fact]
    public void A_member_that_isnt_there_or_doesnt_fit_says_so()
    {
        var (success, result) = Bridged().Evaluate("dotnet.type(\"System.Math\").Max(\"a\", \"b\")");

        Assert.False(success);
        Assert.Contains("has nothing that takes", result);
    }
}
