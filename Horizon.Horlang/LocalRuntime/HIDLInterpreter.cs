using Horizon.HIDL.Parsing;

namespace Horizon.HIDL.Runtime;

public class HIDLInterpreter
{
    private readonly NullValue NULL = new();

    public IRuntimeValue Evaluate(IStatement statement, Environment env)
    {
        return statement.Type switch
        {
            NodeType.Program => EvaluateProgram((ProgramStatement)statement, env),
            NodeType.NumericLiteral => new NumberValue(((NumericLiteralExpression)statement).Value),
            NodeType.BooleanLiteral => new BooleanValue(((BooleanLiteralExpression)statement).Value),
            NodeType.StringLiteral => new StringValue(((StringLiteralExpression)statement).Value),
            NodeType.NullLiteral => new NullValue(),
            NodeType.Identifier => EvaluateIdentifier((IdentifierExpression)statement, env),
            NodeType.BinaryExpression => EvaluateBinaryExpression((BinaryExpression)statement, env),
            NodeType.ObjectLiteral => EvaluateObjectLiteralExpression((ObjectLiteralExpression)statement, env),
            NodeType.Assignment => EvaluateAssignment((AssignmentExpression)statement, env),
            NodeType.CallExpression => EvaluateFunctionCallExpression((CallExpression)statement, env),
            NodeType.VariableDeclaration => EvaluateVariableDeclaration((VariableDeclarationExpression)statement, env),
            NodeType.FunctionDeclaration => EvaluateFunctionDeclaration((FunctionDeclarationExpression)statement, env),
            NodeType.AnonymousFunctionDeclaration => EvaluateAnonymousFunctionDeclaration((AnonymousFunctionDeclarationExpression)statement, env),
            NodeType.MemberExpression => EvaluateMemberExpression((MemberExpression)statement, env),
            NodeType.WhileExpression => EvaluateWhileExpression((WhileDeclarationExpression)statement, env),
            NodeType.DoWhileExpression => EvaluateDoWhileExpression((DoWhileDeclarationExpression)statement, env),
            NodeType.IfExpression => EvaluateIfExpression((IfDeclarationExpression)statement, env),
            NodeType.DeleteStatement => EvaluateDeleteStatement((DeleteStatement)statement, env),
            NodeType.VectorDeclaration => EvaluateVecDeclaration((VectorDeclarationExpression)statement, env),
            _ => throw new Exception($"This node cannot be interpreted: '{statement}'"),
        };
    }

    private IRuntimeValue EvaluateVecDeclaration(VectorDeclarationExpression statement, Environment env)
    {
        float evalVectorDimension(in int dim)
        {
            return ((NumberValue)Evaluate(statement.Expressions[dim], env)).Value;
        }

        return (statement.Expressions.Length) switch
        {
            2 => new Vector2Value(new System.Numerics.Vector2(evalVectorDimension(0), evalVectorDimension(1))),
            3 => new Vector3Value(new System.Numerics.Vector3(evalVectorDimension(0), evalVectorDimension(1), evalVectorDimension(2))),
            4 => new Vector4Value(new System.Numerics.Vector4(evalVectorDimension(0), evalVectorDimension(1), evalVectorDimension(2), evalVectorDimension(3))),
            _ => NULL,
        };
    }

    private IRuntimeValue EvaluateDeleteStatement(DeleteStatement statement, Environment env)
    {
        var runtime = env.Resolve(statement.Target) ?? throw new Exception($"Cannot find scope of variable '{statement.Target}'!");
        runtime.Delete(statement.Target);
        return NULL;
    }

    private IRuntimeValue EvaluateIfExpression(IfDeclarationExpression statement, Environment env)
    {
        var compResult = Evaluate(statement.Condition, env);
        if (compResult.Type == ValueType.Boolean)
        {
            if (((BooleanValue)compResult).Value)
            {
                IRuntimeValue result = new NullValue();
                for (int i = 0; i < statement.Body.Length; i++)
                {
                    result = Evaluate(statement.Body[i], env);
                }
                return result;
            }
        }
        return NULL;
    }

    private IRuntimeValue EvaluateWhileExpression(WhileDeclarationExpression statement, Environment env)
    {
        var compResult = Evaluate(statement.Condition, env);
        IRuntimeValue result = new NullValue();
        if (compResult.Type == ValueType.Boolean)
        {
            for (int j = 0; j < 500; j++)
            {
                compResult = Evaluate(statement.Condition, env);
                if (((BooleanValue)compResult).Value)
                {
                    for (int i = 0; i < statement.Body.Length; i++)
                    {
                        result = Evaluate(statement.Body[i], env);
                    }
                }
                else break;
            }
        }
        return result;
    }

    private IRuntimeValue EvaluateDoWhileExpression(DoWhileDeclarationExpression statement, Environment env)
    {
        IRuntimeValue result = new NullValue();
        for (int j = 0; j < 500; j++)
        {
            for (int i = 0; i < statement.Body.Length; i++)
            {
                result = Evaluate(statement.Body[i], env);
            }

            if (!((BooleanValue)Evaluate(statement.Condition, env)).Value) break;
        }
        return result;
    }

    private IRuntimeValue EvaluateAssignment(AssignmentExpression statement, Environment env)
    {
        if (statement.Assignee.Type != NodeType.Identifier)
            throw new Exception($"Invalid LHS assignee '{statement.Assignee.ToString()}'");

        return env.Assign(((IdentifierExpression)statement.Assignee).Symbol, Evaluate(statement.Value, env));
    }

    private IRuntimeValue EvaluateVariableDeclaration(VariableDeclarationExpression statement, Environment env)
    {
        if (statement.Value is null)
            return env.Declare(statement.Identifier, null, statement.ReadOnly);

        return env.Declare(statement.Identifier, Evaluate(statement.Value, env), statement.ReadOnly);
    }

    private IRuntimeValue EvaluateFunctionDeclaration(FunctionDeclarationExpression statement, Environment env)
    {
        var val = new FunctionValue(statement.Name, statement.Parameters, env, statement.Body);

        if (env.Lookup(statement.Name) is null)
            return env.Declare(statement.Name, val);

        return env.Assign(statement.Name, val);
    }

    private IRuntimeValue EvaluateAnonymousFunctionDeclaration(AnonymousFunctionDeclarationExpression statement, Environment env)
    {
        return new AnonymousFunctionValue(statement.Parameters, env, statement.Body);
    }

    private IRuntimeValue EvaluateIdentifier(IdentifierExpression statement, Environment env)
    {
        var variable = env.Lookup(statement.Symbol) ?? new NullValue();

        if (variable is NativeValue nativeValue)
        {
            return nativeValue.AccessorCallback?.Invoke();
        }

        return variable;
    }

    private IRuntimeValue EvaluateObjectLiteralExpression(ObjectLiteralExpression statement, Environment env)
    {
        Dictionary<string, IRuntimeValue> properties = [];

        foreach (var prop in statement.Properties)
        {
            properties.Add(prop.Key, prop.Value is null ? env.Lookup(prop.Key) : Evaluate(prop.Value, env));
        }

        return new ObjectValue(properties);
    }

    private IRuntimeValue EvaluateFunctionCallExpression(CallExpression statement, Environment env)
    {
        IRuntimeValue[] args = statement.Arguments.Select<IExpression, IRuntimeValue>((arg) => Evaluate(arg, env)).ToArray();
        var func = Evaluate(statement.Caller, env);

        if (func.Type == ValueType.NativeFunction)
            return ((NativeFunctionValue)func).Callback.Invoke(args, env);
        else if (func.Type == ValueType.Function)
        {
            var fun = ((FunctionValue)func);
            Environment scope = new(fun.Environment);

            // construct variables from parameters
            for (int i = 0; i < fun.Parameters.Length; i++)
                scope.Declare(fun.Parameters[i], args[i]);
            IRuntimeValue result = new NullValue();
            for (int i = 0; i < fun.Body.Length; i++)
            {
                result = Evaluate(fun.Body[i], scope);
            }
            return result;
        }
        else if (func.Type == ValueType.AnonymousFunction)
        {
            var fun = ((AnonymousFunctionValue )func);
            Environment scope = new(fun.Environment);

            // construct variables from parameters
            for (int i = 0; i < fun.Parameters.Length; i++)
                scope.Declare(fun.Parameters[i], args[i]);
            IRuntimeValue result = new NullValue();
            for (int i = 0; i < fun.Body.Length; i++)
            {
                result = Evaluate(fun.Body[i], scope);
            }
            return result;
        }
        return func;
        throw new Exception("Cannot call that of whom which is not a function!");
    }

    private IRuntimeValue EvaluateMemberExpression(MemberExpression expression, Environment env)
    {
        var objNoType = Evaluate(expression.Object, env);
        if (objNoType.Type == ValueType.Object)
        {
            var obj = (ObjectValue)objNoType;
            if (expression.Property.Type == NodeType.Identifier)
            {
                var propName = ((IdentifierExpression)expression.Property).Symbol;

                if (obj.Properties.TryGetValue(propName, out var value))
                    return value;
            }
        }
        else if (objNoType.Type == ValueType.Vector2)
        {
            var obj = (Vector2Value)objNoType;
            if (expression.Property.Type == NodeType.Identifier)
            {
                var propName = ((IdentifierExpression)expression.Property).Symbol;

                switch (propName)
                {
                    case "x":
                        return new NumberValue(obj.Value.X);
                    case "y":
                        return new NumberValue(obj.Value.Y);
                }
            }
        }
        else if (objNoType.Type == ValueType.Vector3)
        {
            var obj = (Vector3Value)objNoType;
            if (expression.Property.Type == NodeType.Identifier)
            {
                var propName = ((IdentifierExpression)expression.Property).Symbol;

                switch (propName)
                {
                    case "x":
                        return new NumberValue(obj.Value.X);
                    case "y":
                        return new NumberValue(obj.Value.Y);
                    case "z":
                        return new NumberValue(obj.Value.Z);
                }
            }
        }
        else if (objNoType.Type == ValueType.Vector4)
        {
            var obj = (Vector4Value)objNoType;
            if (expression.Property.Type == NodeType.Identifier)
            {
                var propName = ((IdentifierExpression)expression.Property).Symbol;

                switch (propName)
                {
                    case "x":
                        return new NumberValue(obj.Value.X);
                    case "y":
                        return new NumberValue(obj.Value.Y);
                    case "z":
                        return new NumberValue(obj.Value.Z);
                    case "w":
                        return new NumberValue(obj.Value.W);
                }
            }
        }

        return NULL;
    }

    private IRuntimeValue EvaluateProgram(ProgramStatement program, Environment env)
    {
        IRuntimeValue lastEvaluated = new NullValue();

        foreach (var statement in program.Body)
            lastEvaluated = Evaluate(statement, env);

        return lastEvaluated;
    }

    public IRuntimeValue EvaluateBinaryExpression(BinaryExpression expression, Environment env)
    {
        if (expression.Operator == "!") // special case for inverting bool
            return new BooleanValue(!((BooleanValue)Evaluate(expression.Left, env)).Value);

        // evaluate both sides
        var lhs = Evaluate(expression.Left, env);
        var rhs = Evaluate(expression.Right, env);

        // handle number case

        if (rhs.Type == ValueType.Boolean && lhs.Type == ValueType.Boolean)
            return EvaluateBooleanExpression((BooleanValue)lhs, (BooleanValue)rhs, expression.Operator);
        else if (rhs.Type == ValueType.Number && lhs.Type == ValueType.Number)
            return EvaluateNumericExpression((NumberValue)lhs, (NumberValue)rhs, expression.Operator);
        else if (rhs.Type == ValueType.String && lhs.Type == ValueType.String)
            return EvaluateStringComparisonExpression((StringValue)lhs, (StringValue)rhs, expression.Operator);
        else if ((((byte)rhs.Type > 7 && (byte)rhs.Type < 11) || rhs.Type == ValueType.Number) && ((byte)lhs.Type > 7 && (byte)lhs.Type < 11))
            return EvaluateVectorComparisonExpression(lhs, rhs, expression.Operator);

        return NULL;
    }

    private IRuntimeValue EvaluateStringComparisonExpression(StringValue lhs, StringValue rhs, string op)
    {
        return op switch
        {
            "==" => new BooleanValue(lhs.Value == rhs.Value),
            "!=" => new BooleanValue(lhs.Value != rhs.Value),
            _ => NULL,
        };
    }

    private IRuntimeValue EvaluateVectorComparisonExpression(IRuntimeValue lhs, IRuntimeValue rhs, string op)
    {
        if (lhs is Vector2Value lVal2 && rhs is Vector2Value rVal2)
        {
            return op switch
            {
                "==" => new BooleanValue(lVal2.Value == rVal2.Value),
                "!=" => new BooleanValue(lVal2.Value != rVal2.Value),
                "+" => new Vector2Value(lVal2.Value + rVal2.Value),
                "-" => new Vector2Value(lVal2.Value - rVal2.Value),
                "/" => new Vector2Value(lVal2.Value / rVal2.Value),
                "*" => new Vector2Value(lVal2.Value * rVal2.Value),
                _ => NULL,
            };
        }
        else if (lhs is Vector3Value lVal3 && rhs is Vector3Value rVal3)
        {
            return op switch
            {
                "==" => new BooleanValue(lVal3.Value == rVal3.Value),
                "!=" => new BooleanValue(lVal3.Value != rVal3.Value),
                "+" => new Vector3Value(lVal3.Value + rVal3.Value),
                "-" => new Vector3Value(lVal3.Value - rVal3.Value),
                "/" => new Vector3Value(lVal3.Value / rVal3.Value),
                "*" => new Vector3Value(lVal3.Value * rVal3.Value),
                _ => NULL,
            };
        }
        else if (lhs is Vector4Value lVal && rhs is Vector4Value rVal)
        {
            return op switch
            {
                "==" => new BooleanValue(lVal.Value == rVal.Value),
                "!=" => new BooleanValue(lVal.Value != rVal.Value),
                "+" => new Vector4Value(lVal.Value + rVal.Value),
                "-" => new Vector4Value(lVal.Value - rVal.Value),
                "/" => new Vector4Value(lVal.Value / rVal.Value),
                "*" => new Vector4Value(lVal.Value * rVal.Value),
                _ => NULL,
            };
        }
        else if (lhs is Vector2Value lVal2_0 && rhs is NumberValue rVal2_0)
        {
            return op switch
            {
                "/" => new Vector2Value(lVal2_0.Value / rVal2_0.Value),
                "*" => new Vector2Value(lVal2_0.Value * rVal2_0.Value),
                _ => NULL,
            };
        }
        else if (lhs is Vector3Value lVal3_0 && rhs is NumberValue rVal3_0)
        {
            return op switch
            {
                "/" => new Vector3Value(lVal3_0.Value / rVal3_0.Value),
                "*" => new Vector3Value(lVal3_0.Value * rVal3_0.Value),
                _ => NULL,
            };
        }
        else if (lhs is Vector4Value lVal_0 && rhs is NumberValue rVal_0)
        {
            return op switch
            {
                "/" => new Vector4Value(lVal_0.Value / rVal_0.Value),
                "*" => new Vector4Value(lVal_0.Value * rVal_0.Value),
                _ => NULL,
            };
        }

        return NULL;
    }
    private IRuntimeValue EvaluateNumericExpression(NumberValue lhs, NumberValue rhs, string op)
    {
        return op switch
        {
            ">" => new BooleanValue(lhs.Value > rhs.Value),
            "<" => new BooleanValue(lhs.Value < rhs.Value),
            "==" => new BooleanValue(lhs.Value == rhs.Value),
            "!=" => new BooleanValue(lhs.Value != rhs.Value),
            ">=" => new BooleanValue(lhs.Value >= rhs.Value),
            "<=" => new BooleanValue(lhs.Value <= rhs.Value),
            "+" => new NumberValue(lhs.Value + rhs.Value),
            "-" => new NumberValue(lhs.Value - rhs.Value),
            "/" => new NumberValue(lhs.Value / rhs.Value),
            "*" => new NumberValue(lhs.Value * rhs.Value),
            "%" => new NumberValue(lhs.Value % rhs.Value),
            _ => NULL,
        };
    }

    private IRuntimeValue EvaluateBooleanExpression(BooleanValue lhs, BooleanValue rhs, string op)
    {
        return op switch
        {
            "|" => new BooleanValue(lhs.Value | rhs.Value),
            "&" => new BooleanValue(lhs.Value & rhs.Value),
            "==" => new BooleanValue(lhs.Value == rhs.Value),
            "!=" => new BooleanValue(lhs.Value != rhs.Value),
            _ => NULL,
        };
    }
}