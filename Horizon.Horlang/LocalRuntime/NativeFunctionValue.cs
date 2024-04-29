namespace Horizon.HIDL.Runtime;

public readonly struct NativeFunctionValue(in Func<IRuntimeValue[], Environment, IRuntimeValue> callback) : IRuntimeValue
{
    public readonly ValueType Type { get; init; } = ValueType.NativeFunction;
    public readonly Func<IRuntimeValue[], Environment, IRuntimeValue> Callback { get; init; } = callback;
}
