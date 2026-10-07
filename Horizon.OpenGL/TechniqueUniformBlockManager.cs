using Horizon.Core.Primitives;
using Horizon.OpenGL.Managers;

namespace Horizon.OpenGL;

internal class TechniqueUniformBlockManager
{
    private readonly IGLObject shader;
    private readonly Dictionary<string, uint> blockIndices = new();

    public TechniqueUniformBlockManager(IGLObject obj)
    {
        this.shader = obj;
    }

    public void UniformBlockBinding(string name, uint index)
    {
        uint blockIndex = !blockIndices.TryGetValue(name, out uint value) ? GetBlockIndex(name) : value;
        ObjectManager.GL.UniformBlockBinding(shader.Handle, index, blockIndex);
    }

    private uint GetBlockIndex(string name)
    {
        uint handle = ObjectManager.GL.GetUniformBlockIndex(shader.Handle, name);
        blockIndices.TryAdd(name, handle);
        return handle;
    }
}
