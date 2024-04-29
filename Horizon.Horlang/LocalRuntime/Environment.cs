using System.Numerics;

namespace Horizon.HIDL.Runtime;

/// <summary>
/// The environment is the scope that the interpreter uses to localize variables and soon states in functions.
/// </summary>
/// <param name="parent"></param>
public class Environment(in Environment? parent = null)
{
    private static readonly NullValue NULL = new();

    public Environment? Parent { get; init; } = parent;
    private Dictionary<string, IRuntimeValue> systemVariables = [];
    private Dictionary<string, IRuntimeValue> variables = [];
    private List<string> constants = [];

    /// <summary>
    /// Declare a variable in the global scope, functions are defined using the <see cref="NativeFunctionValue"/> struct.
    /// </summary>
    /// <param name="identifier">The identifier/name of the variable.</param>
    /// <param name="value">The runtime computed value of the variable.</param>
    /// <param name="isConst">Whether or not this variable can be overwritten.</param>
    /// <returns></returns>
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

    /// <summary>
    /// Declare a system variable in the global scope, these are not reset unlike user variables and are immutable by definition.
    /// </summary>
    /// <param name="identifier">The identifier/name of the variable.</param>
    /// <param name="value">The runtime computed value of the variable.</param>
    /// <returns></returns>
    public IRuntimeValue DeclareSystem(in string identifier, in IRuntimeValue value)
    {
        if (systemVariables.ContainsKey(identifier))
            throw new Exception($"System variable '{identifier}' already exists!");
        systemVariables.Add(identifier, value);
        return value;
    }

    /// <summary>
    /// Assigns an existing variable to a specified runtime computed value.
    /// </summary>
    /// <param name="name">The identifier/name of the variable.</param>
    /// <param name="value">The new value of the variable.</param>
    /// <returns>Returns the new value.</returns>
    /// <exception cref="Exception"></exception>
    public IRuntimeValue? Assign(in string name, in IRuntimeValue value)
    {
        if (!constants.Contains(name))
        {
            var variable = Resolve(name)?.variables[name];
            if (variable is NativeValue nativeValue)
            {
                nativeValue.MutatorCallback(value);
                return nativeValue.AccessorCallback.Invoke();
            }

            return variable;

        }
        else throw new Exception($"Constant '{name}' is immutable!");
    }

    /// <summary>
    /// Deletes all user set variables and constants, excluding system declared variables.
    /// </summary>
    public void Reset(in bool resetSystem = false)
    {
        variables.Clear();
        constants.Clear();
        if (resetSystem) systemVariables.Clear();
    }

    /// <summary>
    /// Searches all parent scopes to locate a variable.
    /// </summary>
    /// <param name="name">The identifier/name of the variable.</param>
    /// <returns>The value of the found variable, null if not found.</returns>
    public IRuntimeValue? Lookup(in string name)
    {
        var env = Resolve(name);

        if (env is null) return NULL;

        if (env.variables.TryGetValue(name, out IRuntimeValue? varValue))
            return varValue;

        if (env.systemVariables.TryGetValue(name, out IRuntimeValue? sysValue))
            return sysValue;

        return NULL;
    }

    /// <summary>
    /// Traverses the environment tree to find the context of a variable.
    /// </summary>
    public Environment? Resolve(in string name)
    {
        // check current scope
        if (variables.ContainsKey(name))
            return this;

        if (systemVariables.ContainsKey(name))
            return this;

        // check if a parent exists
        if (Parent is null)
            return null;

        return Parent.Resolve(name);
    }

    /// <summary>
    /// Deletes a variable or a constant.
    /// </summary>
    /// <param name="target">The identifier/name of the variable.</param>
    public void Delete(string target)
    {
        variables.Remove(target);
        constants.Remove(target);
    }

    internal void Copy(Environment other)
    {
        this.constants.Clear();
        this.constants.AddRange(other.constants);

        this.variables.Clear();
        foreach (var item in other.variables)
        {
            this.variables.Add(item.Key, item.Value);
        }
        this.systemVariables.Clear();
        foreach (var item in other.systemVariables)
        {
            this.systemVariables.Add(item.Key, item.Value);
        }
    }
}