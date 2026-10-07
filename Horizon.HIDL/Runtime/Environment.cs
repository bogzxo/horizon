namespace Horizon.HIDL.Runtime;

/// <summary>
/// A scope. The variables declared in it, which of them are constants, and the scope it sits inside of, which is
/// asked for anything that isn't here. A function call makes one of these over the scope the function was declared in.
/// System variables are what the host puts in for scripts to use (the compositor of a UI, the fight of a game).
/// They stay through a <see cref="Reset"/> and can't be assigned to.
/// </summary>
public class Environment
{
    public Environment? Parent { get; init; }

    private readonly Dictionary<string, IRuntimeValue> systemVariables = [];
    private readonly Dictionary<string, IRuntimeValue> variables = [];
    private readonly HashSet<string> constants = [];

    /// <summary>
    /// The variables declared in this scope itself, without the ones of its parents or its system variables.
    /// </summary>
    public IReadOnlyDictionary<string, IRuntimeValue> Variables => variables;

    /// <summary>
    /// Whether a variable of this scope was declared as a constant.
    /// </summary>
    public bool IsConstant(in string name) => constants.Contains(name);

    /// <param name="parent">The scope this one sits inside of, null for the outermost.</param>
    /// <param name="copy">Whether to take a copy of everything the parent holds instead of looking things up in it.</param>
    public Environment(in Environment? parent = null, in bool copy = false)
    {
        if (copy && parent is not null)
        {
            Parent = parent.Parent;
            Copy(parent);
        }
        else
        {
            Parent = parent;
        }
    }

    /// <summary>
    /// Declares a variable in this scope. Declaring one that is here already is a mistake worth hearing about.
    /// </summary>
    public IRuntimeValue Declare(in string identifier, in IRuntimeValue? value, in bool isConst = false)
    {
        if (variables.ContainsKey(identifier) || systemVariables.ContainsKey(identifier))
            throw new HidlRuntimeException($"There is a '{identifier}' already, use another name or assign to it.");

        IRuntimeValue stored = value ?? Values.Null;
        variables.Add(identifier, stored);
        if (isConst) constants.Add(identifier);
        return stored;
    }

    /// <summary>
    /// Declares a variable the host owns. Scripts can read it and call into it but never assign to it or reset it away.
    /// </summary>
    public IRuntimeValue DeclareSystem(in string identifier, in IRuntimeValue value)
    {
        if (systemVariables.ContainsKey(identifier))
            throw new HidlRuntimeException($"The system variable '{identifier}' exists already.");

        systemVariables.Add(identifier, value);
        return value;
    }

    /// <summary>
    /// Gives an existing variable a new value, in whichever scope it was declared in. A variable nobody declared is a mistake.
    /// </summary>
    public IRuntimeValue Assign(in string name, in IRuntimeValue value)
    {
        Environment? owner = Resolve(name);
        if (owner is null)
            throw new HidlRuntimeException($"There is no '{name}' to assign to, declare it with let first.");

        if (owner.constants.Contains(name))
            throw new HidlRuntimeException($"'{name}' is a constant, it can't be changed.");

        if (owner.systemVariables.ContainsKey(name))
            throw new HidlRuntimeException($"'{name}' belongs to the program, it can't be assigned to.");

        // A value the host owns is handed to the host rather than replaced
        if (owner.variables[name] is NativeValue native)
        {
            native.MutatorCallback(value);
            return native.AccessorCallback();
        }

        owner.variables[name] = value;
        return value;
    }

    /// <summary>
    /// Throws away every variable and constant, keeping the system variables unless told to drop those as well.
    /// </summary>
    public void Reset(in bool resetSystem = false)
    {
        variables.Clear();
        constants.Clear();
        if (resetSystem) systemVariables.Clear();
    }

    /// <summary>
    /// Finds a variable here or in any scope around this one. Null (the value, not nothing) if there is none.
    /// </summary>
    public IRuntimeValue Lookup(in string name)
    {
        Environment? owner = Resolve(name);
        if (owner is null) return Values.Null;

        return owner.variables.TryGetValue(name, out IRuntimeValue? value) ? value : owner.systemVariables[name];
    }

    /// <summary>
    /// Whether a variable of that name exists, here or around this scope.
    /// </summary>
    public bool Has(in string name) => Resolve(name) is not null;

    /// <summary>
    /// Finds the scope a variable was declared in, null if it was never declared.
    /// </summary>
    public Environment? Resolve(in string name)
    {
        for (Environment? scope = this; scope is not null; scope = scope.Parent)
        {
            if (scope.variables.ContainsKey(name) || scope.systemVariables.ContainsKey(name))
                return scope;
        }

        return null;
    }

    /// <summary>
    /// Everything that can be seen from this scope, the nearest declaration of every name winning.
    /// </summary>
    public Dictionary<string, IRuntimeValue> GetAllDeclaredValues(bool includeParents = true)
    {
        Dictionary<string, IRuntimeValue> result = [];
        if (includeParents && Parent is not null)
        {
            foreach (var item in Parent.GetAllDeclaredValues(true))
                result[item.Key] = item.Value;
        }

        foreach (var item in systemVariables)
            result[item.Key] = item.Value;

        foreach (var item in variables)
            result[item.Key] = item.Value;

        return result;
    }

    /// <summary>
    /// Takes a variable or a constant out of this scope.
    /// </summary>
    public void Delete(string target)
    {
        variables.Remove(target);
        constants.Remove(target);
    }

    internal void Copy(Environment other)
    {
        constants.Clear();
        constants.UnionWith(other.constants);

        variables.Clear();
        foreach (var item in other.variables)
            variables.Add(item.Key, item.Value);

        systemVariables.Clear();
        foreach (var item in other.systemVariables)
            systemVariables.Add(item.Key, item.Value);
    }
}
