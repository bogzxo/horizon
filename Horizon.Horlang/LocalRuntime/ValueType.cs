namespace Horizon.HIDL.Runtime;

public enum ValueType : byte
{
    Null,
    Number,
    Boolean,
    Object,
    NativeFunction,
    NativeValue,
    Function,
    AnonymousFunction,
    String,
    Vector2,
    Vector3,
    Vector4
}