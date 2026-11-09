using Horizon.Content;

namespace Horizon.Graphics;

// Modules out of a shipped pack. No cache, no compiler, a module that isn't there is an error that says which and why
public static partial class ShaderCompiler
{
    private static ShaderModule FromPack(in ShaderUnit unit, string fileName, ShaderOptions options, IContentFiles content)
    {
        ShaderPackSet packs = options.Packs ?? ShaderPackSet.For(content);
        if (packs.TryFind(unit.Key, out var entry)) return entry.ToModule();

        uint capacity = options.BindlessCapacity ?? ShaderPreprocessor.CurrentBindlessCapacity;
        throw new ShaderUnavailableException(packs.Missing(unit.Label(fileName), unit.Key, capacity));
    }
}
