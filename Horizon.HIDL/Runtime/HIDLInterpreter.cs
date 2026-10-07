using System.Numerics;

using Horizon.HIDL.Library;
using Horizon.HIDL.Parsing;

namespace Horizon.HIDL.Runtime;

/// <summary>
/// Runs a syntax tree. Every statement is evaluated in a scope (see <see cref="Environment"/>) and hands a value back,
/// so an if or a block is as much an expression as a sum is.
/// Return, break and continue don't throw their way out. The interpreter notes which of them is on and every block
/// stops at the next statement, see <see cref="Flow"/>. A loop that never ends is stopped after a great many turns
/// with an error rather than hanging the game.
/// </summary>
public class HIDLInterpreter
{
    // How many turns a loop gets before it is taken to have run away
    private const int MAX_LOOP_TURNS = 1_000_000;

    private enum Flow
    {
        None,
        Return,
        Break,
        Continue
    }

    // Which of return, break and continue is on its way out, and what the return carries
    private Flow flow;
    private IRuntimeValue returned = Values.Null;

    public IRuntimeValue Evaluate(IStatement statement, Environment env)
    {
        return statement.Type switch
        {
            NodeType.Program => EvaluateProgram((ProgramStatement)statement, env),
            NodeType.NumericLiteral => new NumberValue(((NumericLiteralExpression)statement).Value),
            NodeType.BooleanLiteral => Values.Bool(((BooleanLiteralExpression)statement).Value),
            NodeType.StringLiteral => new StringValue(((StringLiteralExpression)statement).Value),
            NodeType.NullLiteral => Values.Null,
            NodeType.Identifier => EvaluateIdentifier((IdentifierExpression)statement, env),
            NodeType.BinaryExpression => EvaluateBinaryExpression((BinaryExpression)statement, env),
            NodeType.ObjectLiteral => EvaluateObjectLiteral((ObjectLiteralExpression)statement, env),
            NodeType.ListLiteral => EvaluateListLiteral((ListLiteralExpression)statement, env),
            NodeType.Spread => throw new HidlRuntimeException("A spread (...) only goes inside of an object or a list."),
            NodeType.Assignment => EvaluateAssignment((AssignmentExpression)statement, env),
            NodeType.CallExpression => EvaluateCall((CallExpression)statement, env),
            NodeType.VariableDeclaration => EvaluateVariableDeclaration((VariableDeclarationExpression)statement, env),
            NodeType.FunctionDeclaration => EvaluateFunctionDeclaration((FunctionDeclarationExpression)statement, env),
            NodeType.AnonymousFunctionDeclaration => EvaluateAnonymousFunction((AnonymousFunctionDeclarationExpression)statement, env),
            NodeType.MemberExpression => EvaluateMember((MemberExpression)statement, env),
            NodeType.WhileExpression => EvaluateWhile((WhileDeclarationExpression)statement, env),
            NodeType.DoWhileExpression => EvaluateDoWhile((DoWhileDeclarationExpression)statement, env),
            NodeType.ForInStatement => EvaluateForIn((ForInStatement)statement, env),
            NodeType.IfExpression => EvaluateIf((IfDeclarationExpression)statement, env),
            NodeType.Conditional => EvaluateConditional((ConditionalExpression)statement, env),
            NodeType.DeleteStatement => EvaluateDelete((DeleteStatement)statement, env),
            NodeType.VectorDeclaration => EvaluateVector((VectorDeclarationExpression)statement, env),
            NodeType.ReturnStatement => EvaluateReturn((ReturnStatement)statement, env),
            NodeType.BreakStatement => Signal(Flow.Break),
            NodeType.ContinueStatement => Signal(Flow.Continue),
            _ => throw new HidlRuntimeException($"The interpreter doesn't know what to do with '{statement.Type}'."),
        };
    }

    private IRuntimeValue Signal(Flow signal)
    {
        flow = signal;
        return Values.Null;
    }

    /// <summary>
    /// Helper method to run the statements of a block one after the other, stopping at a return, break or continue.
    /// </summary>
    private IRuntimeValue EvaluateBlock(IStatement[] body, Environment env)
    {
        IRuntimeValue result = Values.Null;
        foreach (IStatement statement in body)
        {
            result = Evaluate(statement, env);
            if (flow != Flow.None) break;
        }

        return result;
    }

    private IRuntimeValue EvaluateProgram(ProgramStatement program, Environment env)
    {
        IRuntimeValue last = Values.Null;
        foreach (var statement in program.Body)
        {
            last = Evaluate(statement, env);

            // A return at the top of a file ends the file, a stray break or continue means nothing out here
            if (flow == Flow.Return)
            {
                last = returned;
                flow = Flow.None;
                break;
            }

            flow = Flow.None;
        }

        return last;
    }

    /* Values */

    private IRuntimeValue EvaluateVector(VectorDeclarationExpression statement, Environment env)
    {
        Span<float> components = stackalloc float[4];
        int count = 0;

        foreach (IExpression expression in statement.Expressions)
        {
            IRuntimeValue value = Evaluate(expression, env);

            // vec(other, w) and the like, a vector inside of a vector hands its numbers over
            if (Values.TryGetComponents(value, components[count..], out int inner))
            {
                count += inner;
                continue;
            }

            if (value is not NumberValue number)
                throw new HidlRuntimeException($"vec takes numbers, not {Values.TypeName(value)}.");

            if (count == 4)
                throw new HidlRuntimeException("vec takes 4 numbers at the most.");

            components[count++] = number.Value;
        }

        // One number is a square vector, vec(8) is vec(8, 8)
        if (count == 1)
        {
            components[1] = components[0];
            count = 2;
        }

        return Values.Vector(components[..count]);
    }

    private IRuntimeValue EvaluateObjectLiteral(ObjectLiteralExpression statement, Environment env)
    {
        Dictionary<string, IRuntimeValue> properties = [];

        foreach (PropertyExpression property in statement.Properties)
        {
            // ...other puts everything of the other object in, later properties win over it
            if (property.Key is null)
            {
                IRuntimeValue spread = Evaluate(((SpreadExpression)property.Value!).Source, env);
                if (spread is not ObjectValue source)
                    throw new HidlRuntimeException($"Only an object can be spread into an object, not {Values.TypeName(spread)}.");

                foreach (var (key, value) in source.Properties)
                    properties[key] = value is NativeValue native ? native.AccessorCallback() : value;

                continue;
            }

            properties[property.Key] = property.Value is null ? env.Lookup(property.Key) : Evaluate(property.Value, env);
        }

        return new ObjectValue(properties);
    }

    private IRuntimeValue EvaluateListLiteral(ListLiteralExpression statement, Environment env)
    {
        List<IRuntimeValue> items = new(statement.Items.Length);

        foreach (IExpression item in statement.Items)
        {
            if (item is SpreadExpression spread)
            {
                IRuntimeValue source = Evaluate(spread.Source, env);
                if (source is not ListValue list)
                    throw new HidlRuntimeException($"Only a list can be spread into a list, not {Values.TypeName(source)}.");

                items.AddRange(list.Items);
                continue;
            }

            items.Add(Evaluate(item, env));
        }

        return new ListValue(items);
    }

    private IRuntimeValue EvaluateIdentifier(IdentifierExpression statement, Environment env)
    {
        IRuntimeValue variable = env.Lookup(statement.Symbol);
        return variable is NativeValue native ? native.AccessorCallback() : variable;
    }

    /* Declarations and assignments */

    private IRuntimeValue EvaluateVariableDeclaration(VariableDeclarationExpression statement, Environment env) =>
        env.Declare(statement.Identifier, statement.Value is null ? null : Evaluate(statement.Value, env), statement.ReadOnly);

    private IRuntimeValue EvaluateFunctionDeclaration(FunctionDeclarationExpression statement, Environment env)
    {
        var function = new FunctionValue(statement.Name, statement.Parameters, env, statement.Body);

        // Declaring a function again replaces it, which is how a file can fix up another one
        return env.Resolve(statement.Name) == env ? env.Assign(statement.Name, function) : env.Declare(statement.Name, function);
    }

    private IRuntimeValue EvaluateAnonymousFunction(AnonymousFunctionDeclarationExpression statement, Environment env) =>
        new AnonymousFunctionValue(statement.Parameters, env, statement.Body);

    private IRuntimeValue EvaluateDelete(DeleteStatement statement, Environment env)
    {
        Environment owner = env.Resolve(statement.Target) ?? throw new HidlRuntimeException($"There is no '{statement.Target}' to delete.");
        owner.Delete(statement.Target);
        return Values.Null;
    }

    private IRuntimeValue EvaluateAssignment(AssignmentExpression statement, Environment env)
    {
        IRuntimeValue value = Evaluate(statement.Value, env);

        // x += 1 is x = x + 1
        if (statement.Operator is { } op)
            value = Operate(Evaluate(statement.Assignee, env), value, op);

        switch (statement.Assignee)
        {
            case IdentifierExpression identifier:
                return env.Assign(identifier.Symbol, value);

            case MemberExpression member:
                return AssignMember(member, value, env);

            default:
                throw new HidlRuntimeException("Only a variable, a property or an item of a list can be assigned to.");
        }
    }

    /// <summary>
    /// Helper method for obj.key = value, list[i] = value and v.x = value.
    /// </summary>
    private IRuntimeValue AssignMember(MemberExpression member, IRuntimeValue value, Environment env)
    {
        IRuntimeValue target = Evaluate(member.Object, env);

        switch (target)
        {
            case ObjectValue obj:
            {
                string key = KeyOf(member, env);

                if (obj.Properties.TryGetValue(key, out var existing) && existing is NativeValue native)
                    native.MutatorCallback(value);
                else
                    obj.Properties[key] = value;

                return value;
            }

            case ListValue list:
            {
                int index = IndexOf(member, env, list.Count);
                list[index] = value;
                return value;
            }

            case Vector2Value or Vector3Value or Vector4Value:
            {
                // Vectors are values, not things. The whole vector with one number changed goes back where it came from
                if (value is not NumberValue number)
                    throw new HidlRuntimeException($"A component of a vector has to be a number, not {Values.TypeName(value)}.");

                Span<float> components = stackalloc float[4];
                Values.TryGetComponents(target, components, out int count);

                int slot = ComponentIndex(KeyOf(member, env), count);
                components[slot] = number.Value;

                IRuntimeValue changed = Values.Vector(components[..count]);
                return member.Object switch
                {
                    IdentifierExpression identifier => env.Assign(identifier.Symbol, changed),
                    MemberExpression owner => AssignMember(owner, changed, env),
                    _ => throw new HidlRuntimeException("A component can only be changed on a vector that is kept somewhere.")
                };
            }

            default:
                throw new HidlRuntimeException($"Nothing can be assigned into {Values.TypeName(target)}.");
        }
    }

    private string KeyOf(MemberExpression member, Environment env)
    {
        if (!member.Computed)
            return ((IdentifierExpression)member.Property).Symbol;

        IRuntimeValue key = Evaluate(member.Property, env);
        return key is StringValue text ? text.Value : Values.Text(key);
    }

    private int IndexOf(MemberExpression member, Environment env, int count)
    {
        IRuntimeValue key = Evaluate(member.Property, env);
        if (key is not NumberValue number)
            throw new HidlRuntimeException($"A list is indexed with a number, not {Values.TypeName(key)}.");

        // Counting from the end with a negative number, the way everybody expects
        int index = (int)number.Value;
        if (index < 0) index += count;

        if (index < 0 || index >= count)
            throw new HidlRuntimeException($"There is no item {Values.Text(number.Value)} in a list of {count}.");

        return index;
    }

    private static int ComponentIndex(string name, int count)
    {
        int slot = name switch
        {
            "x" or "r" => 0,
            "y" or "g" => 1,
            "z" or "b" => 2,
            "w" or "a" => 3,
            _ => -1
        };

        if (slot < 0 || slot >= count)
            throw new HidlRuntimeException($"A vector of {count} has no '{name}'.");

        return slot;
    }

    /* Members and calls */

    private IRuntimeValue EvaluateMember(MemberExpression expression, Environment env)
    {
        IRuntimeValue target = Evaluate(expression.Object, env);

        switch (target)
        {
            case ObjectValue obj:
            {
                string key = KeyOf(expression, env);
                if (!obj.Properties.TryGetValue(key, out var value))
                    return Values.Null;

                return value is NativeValue native ? native.AccessorCallback() : value;
            }

            case ListValue list:
                if (expression.Computed)
                    return list[IndexOf(expression, env, list.Count)];

                return ListLibrary.Member(list, ((IdentifierExpression)expression.Property).Symbol);

            case StringValue text:
                if (expression.Computed)
                {
                    int index = IndexOf(expression, env, text.Value.Length);
                    return new StringValue(text.Value[index].ToString());
                }

                return TextLibrary.Member(text.Value, ((IdentifierExpression)expression.Property).Symbol);

            case Vector2Value or Vector3Value or Vector4Value:
            {
                Span<float> components = stackalloc float[4];
                Values.TryGetComponents(target, components, out int count);

                string name = KeyOf(expression, env);
                if (name == "length")
                    return new NumberValue(MathF.Sqrt(Dot(components[..count], components[..count])));

                return new NumberValue(components[ComponentIndex(name, count)]);
            }

            case NullValue:
                throw new HidlRuntimeException($"There is nothing to get '{KeyOf(expression, env)}' from, the value is null.");

            default:
                return Values.Null;
        }
    }

    private static float Dot(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        float sum = 0.0f;
        for (int i = 0; i < a.Length; i++) sum += a[i] * b[i];
        return sum;
    }

    private IRuntimeValue EvaluateCall(CallExpression statement, Environment env)
    {
        var arguments = new IRuntimeValue[statement.Arguments.Length];
        for (int i = 0; i < arguments.Length; i++)
            arguments[i] = Evaluate(statement.Arguments[i], env);

        IRuntimeValue function = Evaluate(statement.Caller, env);
        if (!Values.IsFunction(function))
            throw new HidlRuntimeException($"{Describe(statement.Caller)} isn't a function, it is {Values.TypeName(function)}.");

        return Call(function, arguments, env);
    }

    private static string Describe(IExpression expression) => expression switch
    {
        IdentifierExpression identifier => $"'{identifier.Symbol}'",
        MemberExpression { Computed: false } member => $"'{Describe(member.Object).Trim('\'')}.{((IdentifierExpression)member.Property).Symbol}'",
        _ => "That"
    };

    /// <summary>
    /// Calls a function with arguments that have already been evaluated. This is how native code runs a function a
    /// script handed to it, such as the handler of a button. Missing arguments are null, extra ones are ignored.
    /// </summary>
    /// <param name="env">The scope a native function is called from. Script functions run in the scope they were declared in.</param>
    public IRuntimeValue Call(IRuntimeValue func, IRuntimeValue[] args, Environment env)
    {
        switch (func)
        {
            case NativeFunctionValue native:
                return native.Callback(args, env);

            case FunctionValue function:
                return Run(function.Parameters, function.Environment, function.Body, args);

            case AnonymousFunctionValue anonymous:
                return Run(anonymous.Parameters, anonymous.Environment, anonymous.Body, args);

            default:
                throw new HidlRuntimeException($"That isn't a function, it is {Values.TypeName(func)}.");
        }
    }

    private IRuntimeValue Run(string[] parameters, Environment closure, IStatement[] body, IRuntimeValue[] args)
    {
        Environment scope = new(closure);
        for (int i = 0; i < parameters.Length; i++)
            scope.Declare(parameters[i], i < args.Length ? args[i] : Values.Null);

        // Whatever was on its way out of the caller waits, this function has flow of its own
        Flow outerFlow = flow;
        IRuntimeValue outerReturned = returned;
        flow = Flow.None;

        IRuntimeValue result = EvaluateBlock(body, scope);
        if (flow == Flow.Return)
            result = returned;

        flow = outerFlow;
        returned = outerReturned;
        return result;
    }

    private IRuntimeValue EvaluateReturn(ReturnStatement statement, Environment env)
    {
        returned = statement.Value is null ? Values.Null : Evaluate(statement.Value, env);
        flow = Flow.Return;
        return returned;
    }

    /* Branching and loops */

    private IRuntimeValue EvaluateIf(IfDeclarationExpression statement, Environment env)
    {
        if (Values.IsTruthy(Evaluate(statement.Condition, env)))
            return EvaluateBlock(statement.Body, env);

        return statement.Else is { } otherwise ? EvaluateBlock(otherwise, env) : Values.Null;
    }

    private IRuntimeValue EvaluateConditional(ConditionalExpression statement, Environment env) =>
        Evaluate(Values.IsTruthy(Evaluate(statement.Condition, env)) ? statement.Then : statement.Otherwise, env);

    private IRuntimeValue EvaluateWhile(WhileDeclarationExpression statement, Environment env)
    {
        IRuntimeValue result = Values.Null;

        for (int turn = 0; Values.IsTruthy(Evaluate(statement.Condition, env)); turn++)
        {
            if (turn >= MAX_LOOP_TURNS)
                throw new HidlRuntimeException($"A while loop ran {MAX_LOOP_TURNS} times and showed no sign of stopping.");

            result = EvaluateBlock(statement.Body, env);
            if (LeaveLoop()) break;
        }

        return result;
    }

    private IRuntimeValue EvaluateDoWhile(DoWhileDeclarationExpression statement, Environment env)
    {
        IRuntimeValue result = Values.Null;

        for (int turn = 0; ; turn++)
        {
            if (turn >= MAX_LOOP_TURNS)
                throw new HidlRuntimeException($"A do while loop ran {MAX_LOOP_TURNS} times and showed no sign of stopping.");

            result = EvaluateBlock(statement.Body, env);
            if (LeaveLoop() || !Values.IsTruthy(Evaluate(statement.Condition, env))) break;
        }

        return result;
    }

    private IRuntimeValue EvaluateForIn(ForInStatement statement, Environment env)
    {
        IRuntimeValue source = Evaluate(statement.Source, env);
        IRuntimeValue result = Values.Null;

        // Every turn gets a scope of its own, so a function made in the loop keeps the value of its turn
        foreach (IRuntimeValue item in ListLibrary.Walk(source))
        {
            Environment turn = new(env);
            turn.Declare(statement.Variable, item);

            result = EvaluateBlock(statement.Body, turn);
            if (LeaveLoop()) break;
        }

        return result;
    }

    /// <summary>
    /// Helper method for the end of a turn of a loop. A continue is used up here, a break ends the loop and is used
    /// up too, a return is left for the function to find.
    /// </summary>
    private bool LeaveLoop()
    {
        switch (flow)
        {
            case Flow.Continue:
                flow = Flow.None;
                return false;
            case Flow.Break:
                flow = Flow.None;
                return true;
            case Flow.Return:
                return true;
            default:
                return false;
        }
    }

    /* Operators */

    private IRuntimeValue EvaluateBinaryExpression(BinaryExpression expression, Environment env)
    {
        // The unary ones only have a left side
        if (expression.Right is null)
        {
            IRuntimeValue operand = Evaluate(expression.Left, env);
            return expression.Operator switch
            {
                "!" => Values.Bool(!Values.IsTruthy(operand)),
                "-" => Negate(operand),
                "+" => operand,
                _ => throw new HidlRuntimeException($"'{expression.Operator}' can't be put in front of a value.")
            };
        }

        // && and || only look at the right side when they have to
        if (expression.Operator == "&&")
        {
            IRuntimeValue left = Evaluate(expression.Left, env);
            return Values.IsTruthy(left) ? Evaluate(expression.Right, env) : left;
        }

        if (expression.Operator == "||")
        {
            IRuntimeValue left = Evaluate(expression.Left, env);
            return Values.IsTruthy(left) ? left : Evaluate(expression.Right, env);
        }

        return Operate(Evaluate(expression.Left, env), Evaluate(expression.Right, env), expression.Operator);
    }

    private static IRuntimeValue Negate(IRuntimeValue operand)
    {
        if (operand is NumberValue number)
            return new NumberValue(-number.Value);

        Span<float> components = stackalloc float[4];
        if (Values.TryGetComponents(operand, components, out int count))
        {
            for (int i = 0; i < count; i++) components[i] = -components[i];
            return Values.Vector(components[..count]);
        }

        throw new HidlRuntimeException($"{Values.TypeName(operand)} can't be negated.");
    }

    /// <summary>
    /// Helper method for every operator with two sides. Numbers do arithmetic and compare, texts join with + and
    /// compare, booleans do | and &amp;, vectors add, subtract and scale, and anything compares with == and !=.
    /// </summary>
    private static IRuntimeValue Operate(IRuntimeValue lhs, IRuntimeValue rhs, string op)
    {
        switch (op)
        {
            case "==":
                return Values.Bool(Values.AreEqual(lhs, rhs));
            case "!=":
                return Values.Bool(!Values.AreEqual(lhs, rhs));
        }

        // A text on either side of a + joins the two
        if (op == "+" && (lhs is StringValue || rhs is StringValue))
            return new StringValue(Values.Text(lhs) + Values.Text(rhs));

        if (lhs is NumberValue a && rhs is NumberValue b)
            return OperateNumbers(a.Value, b.Value, op);

        if (lhs is StringValue x && rhs is StringValue y)
        {
            int order = string.CompareOrdinal(x.Value, y.Value);
            return op switch
            {
                "<" => Values.Bool(order < 0),
                ">" => Values.Bool(order > 0),
                "<=" => Values.Bool(order <= 0),
                ">=" => Values.Bool(order >= 0),
                _ => throw new HidlRuntimeException($"Two texts can't be put through '{op}'.")
            };
        }

        if (lhs is BooleanValue p && rhs is BooleanValue q)
        {
            return op switch
            {
                "|" => Values.Bool(p.Value | q.Value),
                "&" => Values.Bool(p.Value & q.Value),
                _ => throw new HidlRuntimeException($"Two booleans can't be put through '{op}'.")
            };
        }

        if (op == "+" && lhs is ListValue first && rhs is ListValue second)
            return new ListValue([.. first.Items, .. second.Items]);

        return OperateVectors(lhs, rhs, op);
    }

    private static IRuntimeValue OperateNumbers(float a, float b, string op) => op switch
    {
        "+" => new NumberValue(a + b),
        "-" => new NumberValue(a - b),
        "*" => new NumberValue(a * b),
        "/" => new NumberValue(a / b),
        "%" => new NumberValue(a % b),
        "<" => Values.Bool(a < b),
        ">" => Values.Bool(a > b),
        "<=" => Values.Bool(a <= b),
        ">=" => Values.Bool(a >= b),
        "&" => Values.Bool(a != 0.0f && b != 0.0f),
        "|" => Values.Bool(a != 0.0f || b != 0.0f),
        _ => throw new HidlRuntimeException($"Two numbers can't be put through '{op}'.")
    };

    private static IRuntimeValue OperateVectors(IRuntimeValue lhs, IRuntimeValue rhs, string op)
    {
        Span<float> a = stackalloc float[4], b = stackalloc float[4];
        bool leftVector = Values.TryGetComponents(lhs, a, out int countA);
        bool rightVector = Values.TryGetComponents(rhs, b, out int countB);

        // A number on one side scales (or shifts) every component of the vector on the other
        if (leftVector && rhs is NumberValue scalar)
        {
            b[..countA].Fill(scalar.Value);
            countB = countA;
            rightVector = true;
        }
        else if (rightVector && lhs is NumberValue scalarLeft)
        {
            a[..countB].Fill(scalarLeft.Value);
            countA = countB;
            leftVector = true;
        }

        if (!leftVector || !rightVector)
            throw new HidlRuntimeException($"{Values.TypeName(lhs)} and {Values.TypeName(rhs)} can't be put through '{op}'.");

        if (countA != countB)
            throw new HidlRuntimeException($"A vector of {countA} and a vector of {countB} don't go together.");

        for (int i = 0; i < countA; i++)
        {
            a[i] = op switch
            {
                "+" => a[i] + b[i],
                "-" => a[i] - b[i],
                "*" => a[i] * b[i],
                "/" => a[i] / b[i],
                _ => throw new HidlRuntimeException($"Two vectors can't be put through '{op}'.")
            };
        }

        return Values.Vector(a[..countA]);
    }
}
