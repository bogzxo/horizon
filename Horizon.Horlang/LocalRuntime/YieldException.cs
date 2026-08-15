using System;

namespace Horizon.HIDL.Runtime;

public class YieldException : Exception
{
    public int Value { get; init; }

    public YieldException(int value)
    {
        Value = value;
    }
}