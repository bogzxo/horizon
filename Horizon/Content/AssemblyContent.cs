using System.Reflection;

namespace Horizon.Content;

/// <summary>Embedded files whose resource names preserve their content paths after a prefix.</summary>
public sealed class AssemblyContent : IndexedContent
{
    private readonly Assembly assembly;
    private readonly string prefix;

    public AssemblyContent(Assembly assembly, string prefix, string? diskRoot = null)
        : base(assembly.GetManifestResourceNames().Where(name => name.StartsWith(prefix, StringComparison.Ordinal)).Select(name => name[prefix.Length..]), diskRoot)
    {
        this.assembly = assembly;
        this.prefix = prefix;
    }

    protected override Stream OpenFile(string path) => assembly.GetManifestResourceStream(prefix + path)
        ?? throw new FileNotFoundException($"Embedded content '{path}' was not found.", path);
}
