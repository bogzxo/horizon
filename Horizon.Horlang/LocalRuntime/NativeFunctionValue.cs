namespace Horizon.Horlang.Runtime;

public readonly struct NativeFunctionValue(in Func<IRuntimeValue[], Environment, IRuntimeValue> callback) : IRuntimeValue
{
    public ValueType Type { get; init; } = ValueType.NativeFunction;
    public Func<IRuntimeValue[], Environment, IRuntimeValue> Callback { get; init; } = callback;
}