namespace Horizon.HIDL.Runtime;

/// <summary>
/// A value the host owns. Reading it asks the host, writing it tells the host. This is how a UI component shows its
/// properties to a script without the script holding a copy that goes stale.
/// </summary>
public readonly struct NativeValue(in Func<IRuntimeValue> accessorCallback, in Action<IRuntimeValue> mutatorCallback) : IRuntimeValue
{
    public readonly ValueType Type { get; init; } = ValueType.NativeValue;
    public readonly Func<IRuntimeValue> AccessorCallback { get; init; } = accessorCallback;
    public readonly Action<IRuntimeValue> MutatorCallback { get; init; } = mutatorCallback;
}
