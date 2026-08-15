using System.Collections;
using System.Collections.Generic;
using Horizon.HIDL.Parsing;

namespace Horizon.HIDL.Runtime;

// A runtime object representing a generator that yields integer values.
// Implements IEnumerator<int> so it can be consumed from C# code as well.
public class GeneratorValue : IRuntimeValue, IEnumerator<int>
{
    public ValueType Type { get; init; } = ValueType.Generator;

    private readonly FunctionValue _function;
    private readonly Environment _env; // call-time environment (closure)
    private readonly IStatement[] _body;

    private int _index = 0; // next top-level statement index to execute
    private int _current = 0;
    private bool _finished = false;

    private readonly HIDLInterpreter _interpreter = new();

    public GeneratorValue(FunctionValue function, IRuntimeValue[] args)
    {
        _function = function;
        _body = function.Body;
        // create a fresh scope with the function's closure as parent
        _env = new Environment(function.Environment);

        // Defensive check: ensure there are no nested yields (we only support top-level yields currently)
        static bool ContainsYieldRec(IStatement s)
        {
            if (s == null) return false;
            if (s.Type == NodeType.YieldStatement) return true;
            if (s is IfDeclarationExpression ifExp) foreach (var st in ifExp.Body) if (ContainsYieldRec(st)) return true;
            if (s is WhileDeclarationExpression whileExp) foreach (var st in whileExp.Body) if (ContainsYieldRec(st)) return true;
            if (s is DoWhileDeclarationExpression doExp) foreach (var st in doExp.Body) if (ContainsYieldRec(st)) return true;
            return false;
        }

        int nestedYields = 0;
        foreach (var s in _body)
        {
            if (s == null) continue;
            if (s.Type == NodeType.YieldStatement) continue; // top-level yield is fine
            if (ContainsYieldRec(s)) nestedYields++;
        }

        if (nestedYields > 0)
            throw new Exception("Nested 'yield' statements are not supported by the current generator implementation; please move 'yield' to top-level statements in the function body.");

        // declare parameters
        for (int i = 0; i < function.Parameters.Length; i++)
        {
            var argVal = i < args.Length ? args[i] : new NullValue();
            _env.Declare(function.Parameters[i], argVal);
        }
    }

    // IEnumerator<int>
    public int Current => _current;
    object IEnumerator.Current => _current;

    // MoveNext executes top-level statements until it encounters a YieldStatement.
    // NOTE: This implementation only reliably supports yields that are top-level statements
    // inside the function body (not inside nested expressions or control-flow blocks).
    public bool MoveNext()
    {
        if (_finished) return false;

        while (_index < _body.Length)
        {
            var stmt = _body[_index];

            if (stmt.Type == NodeType.YieldStatement)
            {
                var yieldStmt = (YieldStatement)stmt;
                var val = _interpreter.Evaluate(yieldStmt.Expression, _env);
                if (val.Type != ValueType.Number)
                    throw new Exception("Yield expression must evaluate to a number.");

                _current = (int)((NumberValue)val).Value;
                _index++; // resume after this yield next time
                return true;
            }

            // For non-yield statements, just execute them and advance
            try
            {
                _interpreter.Evaluate(stmt, _env);
            }
            catch (YieldException ye)
            {
                // Nested yields should have been rejected at parse/construct time.
                throw new Exception("Nested 'yield' encountered at runtime - nested yields are unsupported; parsing should have rejected this.");
            }

            _index++;
        }

        _finished = true;
        return false;
    }

    public void Reset()
    {
        _index = 0;
        _current = 0;
        _finished = false;
    }

    public void Dispose()
    {
        // no unmanaged resources
    }
}
