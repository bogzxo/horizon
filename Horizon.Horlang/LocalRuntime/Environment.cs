namespace Horizon.Horlang.Runtime;

public class Environment(in Environment? parent = null)
{
    public Environment? Parent { get; init; } = parent;
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
    /// Assigns an existing variable to a specified runtime computed value.
    /// </summary>
    /// <param name="name">The identifier/name of the variable.</param>
    /// <param name="value">The new value of the variable.</param>
    /// <returns>Returns the new value.</returns>
    /// <exception cref="Exception"></exception>
    public IRuntimeValue Assign(in string name, in IRuntimeValue value)
    {
        if (!constants.Contains(name))
        {
            var variable = Resolve(name).variables[name];
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
    /// Searches all parent scopes to locate a variable.
    /// </summary>
    /// <param name="name">The identifier/name of the variable.</param>
    /// <returns>The value of the found variable, null if not found.</returns>
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
    /// <param name="target">The identifier/name of the variable.</param>
    public void Delete(string target)
    {
        variables.Remove(target);
        constants.Remove(target);
    }
}