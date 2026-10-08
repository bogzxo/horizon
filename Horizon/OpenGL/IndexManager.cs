using System.Runtime.InteropServices;

using Horizon.Core.Primitives;

namespace Horizon.OpenGL;

/// <summary>
/// Internal class for managing GL indexes associated with strings
/// </summary>
internal abstract class IndexManager
{
    protected readonly Dictionary<string, uint> namedIndices;
    protected readonly IGLObject glObject;

    public IndexManager(IGLObject obj)
    {
        glObject = obj;
        namedIndices = [];
    }

    /// <summary>
    /// Where a name lives in the shader. Only the first time is the GPU asked, uniforms are set many times a frame and this has to be quick.
    /// </summary>
    public uint GetLocation(string name)
    {
        ref uint index = ref CollectionsMarshal.GetValueRefOrAddDefault(namedIndices, name, out bool known);
        if (!known) index = GetIndex(name);

        return index;
    }

    protected abstract uint GetIndex(string name);
}
