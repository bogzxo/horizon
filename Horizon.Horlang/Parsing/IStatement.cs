namespace Horizon.HIDL.Parsing;

/* Statement do not return a value
 * let x = 69; // a statement, returns nothing
 * x = 69; // return 69
*/

public interface IStatement
{
    NodeType Type { get; init; }
}