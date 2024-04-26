namespace Horizon.Horlang.Parsing;

/*  let x = 10 + (foo * bar)
 *  [Let][Assignee][Equals][Number][BinaryOp][BinaryOp]
 */

public enum NodeType
{
    // Body
    Program,

    // Expression
    NullLiteral,
    VariableDeclaration,
    FunctionDeclaration,
    Assignment,
    IfExpression,
    WhileExpression,
    DoWhileExpression,

    // Statements
    DeleteStatement,

    // Literals
    Property,
    ObjectLiteral,
    Identifier,
    NumericLiteral,
    BinaryExpression,
    MemberExpression,
    CallExpression,
    StringLiteral,
    BooleanLiteral,
}