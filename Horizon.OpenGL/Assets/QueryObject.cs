using Horizon.Core.Primitives;
using Horizon.OpenGL.Managers;

using Silk.NET.OpenGL;

namespace Horizon.OpenGL.Assets;

public class QueryObject : IGLObject
{
    public uint Handle { get; init; }
    public QueryTarget Target { get; init; }

    public void Begin() => ObjectManager.GL.BeginQuery(Target, Handle);
    public void End() => ObjectManager.GL.EndQuery(Target);
    public long GetParameter(in QueryObjectParameterName parameter=QueryObjectParameterName.Result) => ObjectManager.GL.GetQueryObject(Handle, parameter);
}
