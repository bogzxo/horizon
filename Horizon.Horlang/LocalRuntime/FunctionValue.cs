﻿using Horizon.Horlang.Parsing;

namespace Horizon.Horlang.Runtime;

public readonly struct FunctionValue(in string name, in string[] parameters, in Environment env, in IStatement[] body) : IRuntimeValue
{
    public readonly ValueType Type { get; init; } = ValueType.Function;
    public readonly string Name { get; init; } = name;
    public readonly string[] Parameters { get; init; } = parameters;
    public readonly Environment Environment { get; init; } = env;
    public readonly IStatement[] Body { get; init; } = body;
}