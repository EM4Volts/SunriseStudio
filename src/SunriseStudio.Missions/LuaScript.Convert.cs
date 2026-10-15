using Loretta.CodeAnalysis;
using Loretta.CodeAnalysis.Lua.Syntax;
using LorettaKind = Loretta.CodeAnalysis.Lua.SyntaxKind;

namespace SunriseStudio.Missions;

/** Converts Loretta's tree into the records the mission graph reads. */
public sealed partial class LuaScript
{
    private static readonly HashSet<string> BinaryOperators = new(StringComparer.Ordinal)
    { "+", "-", "*", "/", "//", "%", "^", "..", "==", "~=", "<", "<=", ">", ">=", "and", "or", "&", "|", "~", "<<", ">>" };
    private static readonly HashSet<string> UnaryOperators = new(StringComparer.Ordinal) { "-", "not", "#", "~" };
    private int nodeDepth, bracketDepth;

    private LuaSpan Span(SyntaxNode node) => Shift(node.Span);
    private LuaSpan Span(SyntaxToken token) => Shift(token.Span);
    private int Start(SyntaxToken token) => token.SpanStart - offset;
    private int End(SyntaxToken token) => token.Span.End - offset;

    /** Builds a block's statements; a comment on the header line stays with the header. */
    private LuaBlock Block(string label, LuaExpression? condition, StatementListSyntax list, int afterHeader, int closing, int opening)
    {
        var statements = new List<LuaStatement>();
        int previous = afterHeader;
        bool returned = false;
        foreach (var statement in list.Statements)
        {
            if (returned) Error(statement.SpanStart, "A return must be the last statement in its Lua block.");
            if (statement is EmptyStatementSyntax) { previous = statement.Span.End - offset; continue; }
            // A missing token has no width but may sit after trailing comments; the code ends at the last real token.
            int first = statement.SpanStart - offset;
            var code = LuaSpan.FromBounds(first, Math.Max(first, End(statement.GetLastToken())));
            int start = LeadingComments(previous, code.Start);
            statements.Add(Statement(statement, LuaSpan.FromBounds(start, code.End), code));
            previous = code.End;
            returned |= statement is ReturnStatementSyntax;
        }
        return new(label, condition, statements, afterHeader, closing, LuaSpan.FromBounds(opening, closing));
    }

    /** A body that runs from the end of its header to its closing keyword. */
    private LuaBlock Body(StatementListSyntax list, SyntaxToken opening, SyntaxToken headerEnd, SyntaxToken closing) =>
        Block("body", null, list, End(headerEnd), Start(closing), Start(opening));

    /** One statement; past the depth limit, or in a form Lua 5.4 lacks, it is a LuaInvalidStatement. */
    private LuaStatement Statement(StatementSyntax syntax, LuaSpan span, LuaSpan code)
    {
        string keyword = syntax.GetFirstToken().Text;
        if (!Enter(syntax.SpanStart)) return new LuaInvalidStatement(span, code, keyword);
        try
        {
            switch (syntax)
            {
                case LocalVariableDeclarationStatementSyntax local:
                    Separators(local.Names);
                    if (local.EqualsValues is { } localValues) Separators(localValues.Values);
                    return new LuaLocalStatement(span, code, keyword,
                        local.Names.Select(name => new LuaLocalName(Span(name), name.Name, name.Attribute?.Name)).ToArray(),
                        local.EqualsValues is { } equals ? Span(equals.EqualsToken) : null,
                        local.EqualsValues?.Values.Select(Expression).ToArray() ?? []);
                case AssignmentStatementSyntax assignment:
                    Separators(assignment.Variables); Separators(assignment.EqualsValues.Values);
                    return new LuaAssignmentStatement(span, code, keyword, assignment.Variables.Select(Expression).ToArray(),
                        Span(assignment.EqualsValues.EqualsToken), assignment.EqualsValues.Values.Select(Expression).ToArray());
                case ExpressionStatementSyntax expression:
                    return new LuaCallStatement(span, code, keyword, Expression(expression.Expression));
                case LocalFunctionDeclarationStatementSyntax local:
                    return new LuaFunctionStatement(span, code, keyword, local.Name.Name, Span(local.Name), true,
                        Function(local.FunctionKeyword, local.Parameters, local.Body, local.EndKeyword));
                case FunctionDeclarationStatementSyntax function:
                    return new LuaFunctionStatement(span, code, keyword, FunctionName(function.Name), Span(function.Name), false,
                        Function(function.FunctionKeyword, function.Parameters, function.Body, function.EndKeyword));
                case ReturnStatementSyntax value:
                    Separators(value.Expressions);
                    return new LuaReturnStatement(span, code, keyword,
                        value.Expressions.Where(item => !item.IsMissing).Select(Expression).ToArray());
                case BreakStatementSyntax: return new LuaBreakStatement(span, code, keyword);
                case GotoStatementSyntax jump: return new LuaGotoStatement(span, code, keyword, jump.LabelName.Text);
                case GotoLabelStatementSyntax label: return new LuaLabelStatement(span, code, keyword, label.Identifier.Text);
                case IfStatementSyntax or WhileStatementSyntax or NumericForStatementSyntax or GenericForStatementSyntax
                    or RepeatUntilStatementSyntax or DoStatementSyntax:
                    return new LuaControlStatement(span, code, keyword, Control(syntax));
                default:
                    Error(syntax.SpanStart, "This statement is not Lua 5.4.");
                    return new LuaInvalidStatement(span, code, keyword);
            }
        }
        finally { nodeDepth--; }
    }

    /** The bodies, header span and header expressions of an if, while, for, repeat or do statement. */
    private LuaControlBlock Control(StatementSyntax syntax)
    {
        switch (syntax)
        {
            case IfStatementSyntax branch:
            {
                var bodies = new List<LuaBlock>();
                var clauses = branch.ElseIfClauses;
                int Next(int index) => index < clauses.Count ? Start(clauses[index].ElseIfKeyword)
                    : branch.ElseClause is { } otherwise ? Start(otherwise.ElseKeyword) : Start(branch.EndKeyword);
                var condition = Expression(branch.Condition);
                bodies.Add(Block("if", condition, branch.Body, End(branch.ThenKeyword), Next(0), Start(branch.IfKeyword)));
                for (int i = 0; i < clauses.Count; i++)
                    bodies.Add(Block("elseif", Expression(clauses[i].Condition), clauses[i].Body, End(clauses[i].ThenKeyword), Next(i + 1),
                        Start(clauses[i].ElseIfKeyword)));
                if (branch.ElseClause is { } last)
                    bodies.Add(Block("else", null, last.ElseBody, End(last.ElseKeyword), Start(branch.EndKeyword), Start(last.ElseKeyword)));
                return new("if", bodies, condition.Span, [], []);
            }
            case WhileStatementSyntax loop:
            {
                var condition = Expression(loop.Condition);
                return new("while", [Body(loop.Body, loop.WhileKeyword, loop.DoKeyword, loop.EndKeyword)], condition.Span, [], [condition]);
            }
            case NumericForStatementSyntax loop:
            {
                var header = new List<LuaExpression> { Expression(loop.InitialValue), Expression(loop.FinalValue) };
                if (loop.StepValue is { } step) header.Add(Expression(step));
                return new("for", [Body(loop.Body, loop.ForKeyword, loop.DoKeyword, loop.EndKeyword)],
                    LuaSpan.FromBounds(Span(loop.Identifier).Start, header[^1].Span.End), [loop.Identifier.Name], header);
            }
            case GenericForStatementSyntax loop:
            {
                Separators(loop.Identifiers); Separators(loop.Expressions);
                var header = loop.Expressions.Select(Expression).ToArray();
                int start = loop.Identifiers.Count > 0 ? Span(loop.Identifiers[0]).Start : End(loop.ForKeyword);
                int end = header.Length > 0 ? header[^1].Span.End : Start(loop.DoKeyword);
                return new("for", [Body(loop.Body, loop.ForKeyword, loop.DoKeyword, loop.EndKeyword)],
                    LuaSpan.FromBounds(start, Math.Max(start, end)), loop.Identifiers.Select(name => name.Name).ToArray(), header);
            }
            case RepeatUntilStatementSyntax loop:
            {
                var body = Body(loop.Body, loop.RepeatKeyword, loop.RepeatKeyword, loop.UntilKeyword);
                var condition = Expression(loop.Condition);
                return new("repeat", [body], condition.Span, [], [condition]);
            }
            case DoStatementSyntax block:
                return new("do", [Body(block.Body, block.DoKeyword, block.DoKeyword, block.EndKeyword)], null, [], []);
            default: throw new InvalidOperationException("Not a Lua control statement.");
        }
    }

    /** One expression; past the depth limit, or in a form Lua 5.4 lacks, it is a LuaInvalidExpression. */
    private LuaExpression Expression(ExpressionSyntax syntax)
    {
        var span = Span(syntax);
        if (!Enter(syntax.SpanStart)) return new LuaInvalidExpression(span);
        try
        {
            switch (syntax)
            {
                case IdentifierNameSyntax name: return new LuaName(span, name.Name);
                case VarArgExpressionSyntax: return new LuaLiteral(span, LuaLiteralKind.VarArgs, "...");
                case LiteralExpressionSyntax literal:
                    LuaLiteralKind? kind = literal.Kind() switch
                    {
                        LorettaKind.NilLiteralExpression => LuaLiteralKind.Nil, LorettaKind.TrueLiteralExpression => LuaLiteralKind.True,
                        LorettaKind.FalseLiteralExpression => LuaLiteralKind.False,
                        LorettaKind.NumericalLiteralExpression => LuaLiteralKind.Number,
                        LorettaKind.StringLiteralExpression => LuaLiteralKind.String, _ => null
                    };
                    if (kind is { } known) return new LuaLiteral(span, known, literal.Token.Text);
                    break;
                case MemberAccessExpressionSyntax member:
                    return new LuaMember(span, Expression(member.Expression), member.MemberName.Text, Span(member.MemberName));
                case ElementAccessExpressionSyntax element:
                {
                    var target = Expression(element.Expression);
                    return new LuaIndex(span, target, Bracketed(element.OpenBracketToken, () => Expression(element.KeyExpression)));
                }
                case FunctionCallExpressionSyntax call:
                {
                    var target = Expression(call.Expression);
                    return new LuaCall(span, target, null, null, Arguments(call.Argument));
                }
                case MethodCallExpressionSyntax call:
                {
                    var target = Expression(call.Expression);
                    return new LuaCall(span, target, call.Identifier.Text, Span(call.Identifier), Arguments(call.Argument));
                }
                case ParenthesizedExpressionSyntax parenthesized:
                    return new LuaParenthesized(span, Bracketed(parenthesized.OpenParenthesisToken, () => Expression(parenthesized.Expression)));
                case TableConstructorExpressionSyntax table: return Table(table);
                case AnonymousFunctionExpressionSyntax function:
                    return Function(function.FunctionKeyword, function.Parameters, function.Body, function.EndKeyword);
                case BinaryExpressionSyntax binary:
                    if (!BinaryOperators.Contains(binary.OperatorToken.Text))
                        Error(binary.OperatorToken.SpanStart, $"'{binary.OperatorToken.Text}' is not a Lua 5.4 operator.");
                    return new LuaBinary(span, binary.OperatorToken.Text, Expression(binary.Left), Expression(binary.Right));
                case UnaryExpressionSyntax unary:
                    if (!UnaryOperators.Contains(unary.OperatorToken.Text))
                        Error(unary.OperatorToken.SpanStart, $"'{unary.OperatorToken.Text}' is not a Lua 5.4 operator.");
                    return new LuaUnary(span, unary.OperatorToken.Text, Expression(unary.Operand));
            }
            Error(syntax.SpanStart, "This expression is not Lua 5.4.");
            return new LuaInvalidExpression(span);
        }
        finally { nodeDepth--; }
    }

    /** Call arguments in list, table or string form. */
    private LuaArguments Arguments(FunctionArgumentSyntax syntax)
    {
        var span = Span(syntax);
        switch (syntax)
        {
            case ExpressionListFunctionArgumentSyntax list:
            {
                Separators(list.Expressions);
                var values = Bracketed(list.OpenParenthesisToken, () => list.Expressions.Select(Expression).ToArray());
                var inner = values.Length == 0 ? new LuaSpan(Start(list.CloseParenthesisToken), 0)
                    : LuaSpan.FromBounds(values[0].Span.Start, values[^1].Span.End);
                return new(span, LuaArgumentsForm.List, values, inner);
            }
            case TableConstructorFunctionArgumentSyntax table:
                return new(span, LuaArgumentsForm.Table, [Table(table.TableConstructor)], span);
            case StringFunctionArgumentSyntax text:
                return new(span, LuaArgumentsForm.String, [Expression(text.Expression)], span);
            default:
                Error(syntax.SpanStart, "These call arguments are not Lua 5.4.");
                return new(span, LuaArgumentsForm.List, [], span);
        }
    }

    /** A table constructor; Lua allows one separator after the last field, not more. */
    private LuaTable Table(TableConstructorExpressionSyntax syntax) => Bracketed(syntax.OpenBraceToken, () =>
    {
        var list = syntax.Fields;
        if (list.SeparatorCount > list.Count)
            Error(list.GetSeparator(list.SeparatorCount - 1).SpanStart, "Unexpected separator in a Lua table.");
        var fields = new List<LuaField>(list.Count);
        for (int i = 0; i < list.Count; i++)
        {
            LuaSpan? separator = i < list.SeparatorCount ? Span(list.GetSeparator(i)) : null;
            var span = Span(list[i]);
            fields.Add(Field(list[i], LuaSpan.FromBounds(span.Start, separator?.End ?? span.End), separator));
        }
        return new LuaTable(Span(syntax), fields);
    });

    private LuaField Field(TableFieldSyntax field, LuaSpan span, LuaSpan? separator) => field switch
    {
        IdentifierKeyedTableFieldSyntax named =>
            new LuaField(span, named.Identifier.Text, Span(named.Identifier), null, Expression(named.Value), separator),
        ExpressionKeyedTableFieldSyntax keyed => new LuaField(span, null, null, Expression(keyed.Key), Expression(keyed.Value), separator),
        UnkeyedTableFieldSyntax value => new LuaField(span, null, null, null, Expression(value.Value), separator),
        _ => new LuaField(span, null, null, null, new LuaInvalidExpression(span), separator)
    };

    /** A function body starts a new bracket count, as each body is read on its own. */
    private LuaFunction Function(SyntaxToken keyword, ParameterListSyntax parameters, StatementListSyntax body, SyntaxToken end)
    {
        Separators(parameters.Parameters);
        var names = parameters.Parameters.Select(parameter => parameter is NamedParameterSyntax named ? named.Name : "...").ToArray();
        int outer = bracketDepth;
        bracketDepth = 0;
        try
        {
            var block = Body(body, keyword, parameters.CloseParenthesisToken, end);
            return new(LuaSpan.FromBounds(Start(keyword), End(end)), names, block);
        }
        finally { bracketDepth = outer; }
    }

    private T Bracketed<T>(SyntaxToken opening, Func<T> read)
    {
        if (++bracketDepth == MaximumBracketDepth + 1) Error(opening.SpanStart, "Lua expression nesting exceeds the reader limit.");
        try { return read(); }
        finally { bracketDepth--; }
    }

    /** False past the depth limit; otherwise the caller owns one level and must leave it. */
    private bool Enter(int position)
    {
        if (nodeDepth >= MaximumNodeDepth)
        {
            Error(position, "Lua nesting exceeds the reader limit.");
            return false;
        }
        nodeDepth++;
        return true;
    }

    /** Loretta accepts a trailing comma in a list; Lua does not. */
    private void Separators<T>(SeparatedSyntaxList<T> list) where T : SyntaxNode
    {
        if (list.SeparatorCount > 0 && list.SeparatorCount >= list.Count)
            Error(list.GetSeparator(list.SeparatorCount - 1).SpanStart, "Unexpected ',' in a Lua list.");
    }

    private static string FunctionName(FunctionNameSyntax name) => name switch
    {
        SimpleFunctionNameSyntax simple => simple.Name.Text,
        MemberFunctionNameSyntax member => FunctionName(member.BaseName) + "." + member.Name.Text,
        MethodFunctionNameSyntax method => FunctionName(method.BaseName) + ":" + method.Name.Text,
        _ => name.ToString()
    };
}
