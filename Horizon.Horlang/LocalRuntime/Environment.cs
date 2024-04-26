using Microsoft.VisualBasic;

namespace Horizon.Horlang.Runtime;

public class Environment(in Environment? parent = null)
{
    public Environment? Parent { get; init; } = parent;
    private Dictionary<string, IRuntimeValue> variables = [];
    private List<string> constants = [];

    public IRuntimeValue Declare(in string identifier, in IRuntimeValue value, in bool isConst = false)
    {
        if (variables.ContainsKey(identifier))
            throw new Exception($"Variable '{identifier}' already exists!");
        if (constants.Contains(identifier))
            throw new Exception($"Constant '{identifier}' already exists and cannot be changed!");
        variables.Add(identifier, value);
        if (isConst) constants.Add(identifier);
        return value;
    }

    public IRuntimeValue Assign(in string name, in IRuntimeValue value)
    {
        if (!constants.Contains(name))
            Resolve(name).variables[name] = value;
        else throw new Exception($"Constant '{name}' is immutable!");

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

    /// <summary>
    /// Deletes a variable or a constant.
    /// </summary>
    /// <param name="target"></param>
    public void Delete(string target)
    {
        variables.Remove(target);
        constants.Remove(target);
    }
}