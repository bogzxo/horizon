using Horizon.Horlang.Parsing;

namespace Horizon.Horlang.Runtime;

public readonly struct FunctionValue(in string name, in string[] parameters, in Environment env, in IStatement[] body) : IRuntimeValue
{
    public ValueType Type { get; init; } = ValueType.Function;
    public string Name { get; init; } = name;
    public string[] Parameters { get; init; } = parameters;
    public Environment Environment { get; init; } = env;
    public IStatement[] Body { get; init; } = body;
}