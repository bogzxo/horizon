using Horizon.Horlang.Lexxing;
using Horizon.Horlang.Parsing;
using Horizon.Horlang.Runtime;


namespace Horizon.Horlang;

using Environment = Horizon.Horlang.Runtime.Environment;

public class HorlangRuntime
{
    public Environment Environment { get; init; }
    public HorlangInterpreter Interpreter { get; init; }

    public HorlangRuntime()
    {
        Interpreter = new HorlangInterpreter();
        Environment = new();
        Environment.Declare("true", new BooleanValue(true));
        Environment.Declare("false", new BooleanValue(false));
        Environment.Declare("version", new StringValue("0.0.2"));
        Environment.Declare("null", new NullValue());
        Evaluate("func not(input) { !input }");
    }
    public string Evaluate(in string input)
    {
        Token[] tokens = Lexer.Tokenize(input);
        var parser = new Parser();

        ProgramStatement ast = parser.ProduceSyntaxTree(tokens);
        return Interpreter.Evaluate(ast, Environment).ToString() ?? string.Empty;

        try
        {
        }
        catch (Exception e)
        {
            return e.Message;
        }
    }
}
