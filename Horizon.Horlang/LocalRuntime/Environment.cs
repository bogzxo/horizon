namespace Horizon.Horlang.Runtime;

public class Environment(in Environment? parent = null)
{
    public Environment? Parent { get; init; } = parent;
    private Dictionary<string, IRuntimeValue> variables = [];

    public IRuntimeValue Declare(in string identifier, in IRuntimeValue value)
    {
        if (variables.ContainsKey(identifier))
            throw new Exception($"Variable '{identifier}' already exists!");

        variables.Add(identifier, value);
        return value;
    }

    public IRuntimeValue Assign(in string name, in IRuntimeValue value)
    {
        Resolve(name).variables[name] = value;

        return value;
    }

    public IRuntimeValue? Lookup(in string name)
    {
        return Resolve(name)?.variables[name];
    }

    /// <summary>
    /// Traverses the environment tree to find the context of a variable.
    /// </summary>
    public Environment? Resolve(in string name)
    {
        // check current scope
        if (variables.ContainsKey(name))
            return this;

        // check if a parent exists
        if (Parent is null)
            return null;

        return Parent.Resolve(name);
    }
}
