namespace Horizon.HIDL.Parsing;

/*  let x = 10 + (foo * bar)
 *  [Let][Assignee][Equals][Number][BinaryOp][BinaryOp]
 */

public enum NodeType
{
    // Body
    Program,

    // Expression
    NullLiteral,
    VectorDeclaration,

    VariableDeclaration,
    FunctionDeclaration,
    AnonymousFunctionDeclaration,
    Assignment,
    IfExpression,
    WhileExpression,
    DoWhileExpression,

    // Statements
    DeleteStatement,
    ForInStatement,
    ReturnStatement,
    BreakStatement,
    ContinueStatement,

    // Literals
    Property,

    ObjectLiteral,
    ListLiteral,
    Spread,
    Conditional,
    Identifier,
    NumericLiteral,
    BinaryExpression,
    MemberExpression,
    CallExpression,
    StringLiteral,
    BooleanLiteral,
}
