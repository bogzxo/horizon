namespace Horizon.Horlang.Runtime;

public readonly struct NativeValue(in Func<IRuntimeValue> accessorCallback, in Action<IRuntimeValue> mutatorCallback) : IRuntimeValue
{
    public readonly ValueType Type { get; init; } = ValueType.NativeValue;
    public readonly Func<IRuntimeValue> AccessorCallback { get; init; } = accessorCallback;
    public readonly Action<IRuntimeValue> MutatorCallback { get; init; } = mutatorCallback;
}