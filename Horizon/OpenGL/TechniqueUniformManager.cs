using Horizon.Core.Primitives;
using Horizon.OpenGL.Managers;

namespace Horizon.OpenGL;

internal class TechniqueUniformManager : IndexManager
{
    public TechniqueUniformManager(IGLObject obj)
        : base(obj) { }

    protected override uint GetIndex(string name) =>
        (uint)ObjectManager.GL.GetUniformLocation(glObject.Handle, name);
}
