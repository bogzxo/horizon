using Horizon.Core.Primitives;
using Horizon.OpenGL.Managers;

using Silk.NET.OpenGL;

namespace Horizon.OpenGL;

internal class TechniqueResourceIndexManager : IndexManager
{
    public TechniqueResourceIndexManager(IGLObject obj)
        : base(obj) { }

    protected override uint GetIndex(string name) =>
        ObjectManager.GL.GetProgramResourceIndex(glObject.Handle, ProgramInterface.ShaderStorageBlock, name);
}
