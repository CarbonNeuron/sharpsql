using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace SharpSql;

public sealed partial class SharpSqlCompiler
{
    private readonly Dictionary<IrSource, SyntaxNode> _csharpSourceNodes = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<SyntaxTree, int> _sourceOffsets = [];
    private readonly Dictionary<ISymbol, IrSymbol> _irSymbols = new(SymbolEqualityComparer.Default);
    private int _nextSourceOffset;
    private int _nextIrSymbolId;

    private ProceduralDeclaration BindProceduralDeclaration(
        VariableDeclarationSyntax declaration,
        VariableScope scope)
    {
        var declaredType = CSharpTypeFactory.From(declaration.Type);
        return new ProceduralDeclaration(
            ToIrSource(declaration),
            declaration.Variables.Select(variable =>
            {
                var initializer = variable.Initializer is null
                    ? null
                    : BindIrExpression(variable.Initializer.Value, scope);
                var type = declaredType == IrType.Unknown && initializer is not null
                    ? initializer.Type
                    : declaredType;
                return new ProceduralVariable(
                    ToIrSource(variable),
                    GetOrCreateIrSymbol(
                        SemanticModelFor(variable)?.GetDeclaredSymbol(variable),
                        variable.Identifier.ValueText,
                        type),
                    initializer);
            }).ToArray());
    }

    private ProceduralStatement BindProceduralStatement(
        StatementSyntax statement,
        VariableScope scope) => statement switch
        {
            BlockSyntax block => new ProceduralBlock(
                ToIrSource(block),
                block.Statements.Select(item => BindProceduralStatement(item, scope)).ToArray()),
            LocalFunctionStatementSyntax localFunction => new ProceduralLocalFunction(
                ToIrSource(localFunction),
                localFunction.Identifier.ValueText),
            LocalDeclarationStatementSyntax declaration => new ProceduralDeclarationStatement(
                ToIrSource(declaration),
                BindProceduralDeclaration(declaration.Declaration, scope)),
            ExpressionStatementSyntax expression => new ProceduralExpressionStatement(
                ToIrSource(expression),
                BindIrExpression(expression.Expression, scope)),
            IfStatementSyntax @if => new ProceduralIf(
                ToIrSource(@if),
                BindIrExpression(@if.Condition, scope),
                BindProceduralStatement(@if.Statement, scope),
                @if.Else is null ? null : BindProceduralStatement(@if.Else.Statement, scope)),
            WhileStatementSyntax @while => new ProceduralWhile(
                ToIrSource(@while),
                BindIrExpression(@while.Condition, scope),
                BindProceduralStatement(@while.Statement, scope)),
            DoStatementSyntax @do => new ProceduralDo(
                ToIrSource(@do),
                BindIrExpression(@do.Condition, scope),
                BindProceduralStatement(@do.Statement, scope)),
            ForStatementSyntax @for => new ProceduralFor(
                ToIrSource(@for),
                @for.Declaration is null ? null : BindProceduralDeclaration(@for.Declaration, scope),
                @for.Initializers.Select(item => BindIrExpression(item, scope)).ToArray(),
                @for.Condition is null ? null : BindIrExpression(@for.Condition, scope),
                @for.Incrementors.Select(item => BindIrExpression(item, scope)).ToArray(),
                BindProceduralStatement(@for.Statement, scope)),
            ForEachStatementSyntax forEach => new ProceduralForEach(
                ToIrSource(forEach),
                GetOrCreateIrSymbol(
                    SemanticModelFor(forEach)?.GetDeclaredSymbol(forEach),
                    forEach.Identifier.ValueText,
                    InferProceduralForEachElementType(forEach, scope)),
                BindIrExpression(forEach.Expression, scope),
                BindProceduralStatement(forEach.Statement, scope)),
            BreakStatementSyntax @break => new ProceduralBreak(ToIrSource(@break)),
            ContinueStatementSyntax @continue => new ProceduralContinue(ToIrSource(@continue)),
            ReturnStatementSyntax @return => new ProceduralReturn(
                ToIrSource(@return),
                @return.Expression is null ? null : BindIrExpression(@return.Expression, scope)),
            EmptyStatementSyntax empty => new ProceduralEmpty(ToIrSource(empty)),
            _ => new ProceduralUnsupported(ToIrSource(statement), statement.Kind().ToString())
        };

    private IrExpression BindIrExpression(ExpressionSyntax expression, VariableScope scope)
    {
        expression = StripParentheses(expression);
        var source = ToIrSource(expression);
        var facts = AnalyzeExpression(expression, scope);
        return expression switch
        {
            LiteralExpressionSyntax literal => new IrConstantExpression(
                source,
                facts,
                literal.Token.Value,
                literal.Token.Text),
            IdentifierNameSyntax identifier => new IrVariableExpression(
                source,
                facts,
                GetOrCreateIrSymbol(
                    SemanticModelFor(identifier)?.GetSymbolInfo(identifier).Symbol,
                    identifier.Identifier.ValueText,
                    facts.Type)),
            ThisExpressionSyntax => new IrThisExpression(
                source,
                facts,
                GetOrCreateIrSymbol(null, "this", facts.Type)),
            BinaryExpressionSyntax binary when TryGetIrBinaryOperator(binary.Kind(), out var binaryOperator) =>
                new IrBinaryExpression(
                    source,
                    facts,
                    binaryOperator,
                    BindIrExpression(binary.Left, scope),
                    BindIrExpression(binary.Right, scope)),
            PrefixUnaryExpressionSyntax prefix when TryGetIrUnaryOperator(prefix.Kind(), out var prefixOperator) =>
                new IrUnaryExpression(source, facts, prefixOperator, BindIrExpression(prefix.Operand, scope)),
            PostfixUnaryExpressionSyntax postfix when TryGetIrUnaryOperator(postfix.Kind(), out var postfixOperator) =>
                new IrUnaryExpression(source, facts, postfixOperator, BindIrExpression(postfix.Operand, scope)),
            CastExpressionSyntax cast => new IrConversionExpression(
                source,
                facts,
                CSharpTypeFactory.From(cast.Type),
                BindIrExpression(cast.Expression, scope)),
            ConditionalExpressionSyntax conditional => new IrConditionalExpression(
                source,
                facts,
                BindIrExpression(conditional.Condition, scope),
                BindIrExpression(conditional.WhenTrue, scope),
                BindIrExpression(conditional.WhenFalse, scope)),
            MemberAccessExpressionSyntax member => new IrMemberExpression(
                source,
                facts,
                BindIrExpression(member.Expression, scope),
                member.Name.Identifier.ValueText),
            ElementAccessExpressionSyntax element => new IrElementExpression(
                source,
                facts,
                BindIrExpression(element.Expression, scope),
                element.ArgumentList.Arguments.Select(argument => BindIrExpression(argument.Expression, scope)).ToArray()),
            InvocationExpressionSyntax invocation => new IrInvocationExpression(
                source,
                facts,
                BindIrExpression(invocation.Expression, scope),
                invocation.ArgumentList.Arguments.Select(argument => BindIrExpression(argument.Expression, scope)).ToArray()),
            ObjectCreationExpressionSyntax creation => new IrObjectCreationExpression(
                source,
                facts,
                CSharpTypeFactory.From(creation.Type),
                creation.ArgumentList?.Arguments.Select(argument => BindIrExpression(argument.Expression, scope)).ToArray() ?? [],
                creation.Initializer?.Expressions.Select(item => BindIrExpression(item, scope)).ToArray() ?? []),
            ArrayCreationExpressionSyntax array => BindArrayCreation(array, source, facts, scope),
            ImplicitArrayCreationExpressionSyntax array => new IrArrayCreationExpression(
                source,
                facts,
                SequenceElementType(facts.Type.Name),
                null,
                array.Initializer.Expressions.Select(item => BindIrExpression(item, scope)).ToArray()),
            CollectionExpressionSyntax collection => new IrArrayCreationExpression(
                source,
                facts,
                SequenceElementType(facts.Type.Name),
                null,
                collection.Elements.OfType<ExpressionElementSyntax>()
                    .Select(item => BindIrExpression(item.Expression, scope)).ToArray()),
            InterpolatedStringExpressionSyntax interpolated => new IrInterpolatedStringExpression(
                source,
                facts,
                interpolated.Contents.Select(content => (object)(content switch
                {
                    InterpolatedStringTextSyntax text => new IrInterpolatedText(text.TextToken.ValueText),
                    InterpolationSyntax interpolation => new IrInterpolation(BindIrExpression(interpolation.Expression, scope)),
                    _ => new IrInterpolatedText(string.Empty)
                })).ToArray()),
            AssignmentExpressionSyntax assignment when TryGetIrAssignmentOperator(assignment.Kind(), out var assignmentOperator) =>
                new IrAssignmentExpression(
                    source,
                    facts,
                    assignmentOperator,
                    BindIrExpression(assignment.Left, scope),
                    BindIrExpression(assignment.Right, scope)),
            CheckedExpressionSyntax checkedExpression => BindIrExpression(checkedExpression.Expression, scope),
            LambdaExpressionSyntax lambda => BindLambda(lambda, source, facts, scope),
            QueryExpressionSyntax query => BindQuery(query, source, facts, scope),
            _ => new IrUnsupportedExpression(source, facts, expression.Kind().ToString())
        };
    }

    private IrArrayCreationExpression BindArrayCreation(
        ArrayCreationExpressionSyntax array,
        IrSource source,
        ExpressionFacts facts,
        VariableScope scope)
    {
        var rank = array.Type.RankSpecifiers.FirstOrDefault();
        var lengthSyntax = rank?.Sizes.FirstOrDefault() as ExpressionSyntax;
        return new IrArrayCreationExpression(
            source,
            facts,
            CSharpTypeFactory.From(array.Type.ElementType),
            lengthSyntax is null ? null : BindIrExpression(lengthSyntax, scope),
            array.Initializer?.Expressions.Select(item => BindIrExpression(item, scope)).ToArray() ?? []);
    }

    private IrLambdaExpression BindLambda(
        LambdaExpressionSyntax lambda,
        IrSource source,
        ExpressionFacts facts,
        VariableScope scope)
    {
        var parameters = lambda switch
        {
            SimpleLambdaExpressionSyntax simple => new[] { simple.Parameter },
            ParenthesizedLambdaExpressionSyntax parenthesized => parenthesized.ParameterList.Parameters.ToArray(),
            _ => Array.Empty<ParameterSyntax>()
        };
        var irParameters = parameters.Select(parameter =>
        {
            var type = parameter.Type is null ? IrType.Unknown : CSharpTypeFactory.From(parameter.Type);
            return GetOrCreateIrSymbol(
                SemanticModelFor(parameter)?.GetDeclaredSymbol(parameter),
                parameter.Identifier.ValueText,
                type);
        }).ToArray();
        return new IrLambdaExpression(
            source,
            facts,
            irParameters,
            lambda.Body is ExpressionSyntax body ? BindIrExpression(body, scope) : null,
            lambda.Body is BlockSyntax block
                ? (ProceduralBlock)BindProceduralStatement(block, scope)
                : null);
    }

    private IrQueryExpression BindQuery(
        QueryExpressionSyntax query,
        IrSource source,
        ExpressionFacts facts,
        VariableScope scope)
    {
        var semanticModel = SemanticModelFor(query);
        var rangeType = semanticModel?.GetTypeInfo(query.FromClause.Expression).Type is { } sourceType
            ? SequenceElementType(CSharpTypeFactory.From(sourceType).Name)
            : IrType.Unknown;
        var range = GetOrCreateIrSymbol(
            semanticModel?.GetDeclaredSymbol(query.FromClause),
            query.FromClause.Identifier.ValueText,
            rangeType);
        var clauses = new List<IrQueryClause>();
        foreach (var clause in query.Body.Clauses)
        {
            switch (clause)
            {
                case WhereClauseSyntax where:
                    clauses.Add(new IrWhereClause(ToIrSource(where), BindIrExpression(where.Condition, scope)));
                    break;
                case OrderByClauseSyntax orderBy:
                    for (var index = 0; index < orderBy.Orderings.Count; index++)
                    {
                        var ordering = orderBy.Orderings[index];
                        clauses.Add(new IrOrderClause(
                            ToIrSource(ordering),
                            BindIrExpression(ordering.Expression, scope),
                            ordering.AscendingOrDescendingKeyword.IsKind(SyntaxKind.DescendingKeyword),
                            index > 0));
                    }
                    break;
            }
        }
        switch (query.Body.SelectOrGroup)
        {
            case SelectClauseSyntax select:
                clauses.Add(new IrSelectClause(ToIrSource(select), BindIrExpression(select.Expression, scope)));
                break;
            case GroupClauseSyntax group:
                clauses.Add(new IrGroupClause(
                    ToIrSource(group),
                    BindIrExpression(group.GroupExpression, scope),
                    BindIrExpression(group.ByExpression, scope)));
                break;
        }
        return new IrQueryExpression(
            source,
            facts,
            range,
            BindIrExpression(query.FromClause.Expression, scope),
            clauses);
    }

    private IrType InferProceduralForEachElementType(ForEachStatementSyntax statement, VariableScope scope)
    {
        if (!statement.Type.IsVar)
            return CSharpTypeFactory.From(statement.Type);
        var sourceType = InferType(statement.Expression, scope);
        return IsSequenceType(sourceType.Name) || IsLinqSequenceType(sourceType.Name)
            ? SequenceElementType(sourceType.Name)
            : IrType.Unknown;
    }

    private IrSymbol GetOrCreateIrSymbol(ISymbol? symbol, string name, IrType type)
    {
        if (symbol is not null && _irSymbols.TryGetValue(symbol, out var existing))
            return existing;
        var created = new IrSymbol(new IrSymbolId(++_nextIrSymbolId), name, type);
        if (symbol is not null)
            _irSymbols[symbol] = created;
        return created;
    }

    private IrSource ToIrSource(SyntaxNode node)
    {
        var position = node.SyntaxTree.GetLineSpan(node.Span).StartLinePosition;
        var source = new IrSource(
            new IrSourceSpan(GlobalSourcePosition(node.SyntaxTree, node.SpanStart), node.Span.Length, position.Line + 1, position.Character + 1),
            ToIrComments(node.GetLeadingTrivia(), node.SyntaxTree),
            ToIrComments(node.GetTrailingTrivia(), node.SyntaxTree),
            ToIrComments(node.DescendantTrivia(descendIntoTrivia: true), node.SyntaxTree));
        _csharpSourceNodes[source] = node;
        return source;
    }

    private IReadOnlyList<IrComment> ToIrComments(IEnumerable<SyntaxTrivia> trivia, SyntaxTree tree) =>
        trivia.Where(IsComment).Select(item => new IrComment(
            GlobalSourcePosition(tree, item.SpanStart),
            item.ToFullString().TrimEnd('\r', '\n'),
            item.IsKind(SyntaxKind.SingleLineCommentTrivia)
                ? IrCommentKind.Line
                : item.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia) ||
                  item.IsKind(SyntaxKind.MultiLineDocumentationCommentTrivia)
                    ? IrCommentKind.Documentation
                    : IrCommentKind.Block)).ToArray();

    private int GlobalSourcePosition(SyntaxTree tree, int position)
    {
        if (!_sourceOffsets.TryGetValue(tree, out var offset))
        {
            offset = _nextSourceOffset;
            _sourceOffsets.Add(tree, offset);
            _nextSourceOffset += tree.Length + 1;
        }
        return offset + position;
    }

    private T CSharpSyntax<T>(IrSource source) where T : SyntaxNode =>
        (T)_csharpSourceNodes[source];

    private ExpressionSyntax CSharpExpression(IrExpression expression) =>
        CSharpSyntax<ExpressionSyntax>(expression.Source);

    private bool HasCSharpSource(IrSource source) =>
        _csharpSourceNodes.ContainsKey(source);

    private static bool TryGetIrBinaryOperator(SyntaxKind kind, out IrBinaryOperator result)
    {
        var value = kind switch
        {
            SyntaxKind.AddExpression => IrBinaryOperator.Add,
            SyntaxKind.SubtractExpression => IrBinaryOperator.Subtract,
            SyntaxKind.MultiplyExpression => IrBinaryOperator.Multiply,
            SyntaxKind.DivideExpression => IrBinaryOperator.Divide,
            SyntaxKind.ModuloExpression => IrBinaryOperator.Remainder,
            SyntaxKind.BitwiseAndExpression => IrBinaryOperator.BitwiseAnd,
            SyntaxKind.BitwiseOrExpression => IrBinaryOperator.BitwiseOr,
            SyntaxKind.ExclusiveOrExpression => IrBinaryOperator.ExclusiveOr,
            SyntaxKind.LogicalAndExpression => IrBinaryOperator.LogicalAnd,
            SyntaxKind.LogicalOrExpression => IrBinaryOperator.LogicalOr,
            SyntaxKind.EqualsExpression => IrBinaryOperator.Equal,
            SyntaxKind.NotEqualsExpression => IrBinaryOperator.NotEqual,
            SyntaxKind.LessThanExpression => IrBinaryOperator.LessThan,
            SyntaxKind.LessThanOrEqualExpression => IrBinaryOperator.LessThanOrEqual,
            SyntaxKind.GreaterThanExpression => IrBinaryOperator.GreaterThan,
            SyntaxKind.GreaterThanOrEqualExpression => IrBinaryOperator.GreaterThanOrEqual,
            SyntaxKind.CoalesceExpression => IrBinaryOperator.Coalesce,
            _ => (IrBinaryOperator?)null
        };
        result = value.GetValueOrDefault();
        return value.HasValue;
    }

    private static bool TryGetIrUnaryOperator(SyntaxKind kind, out IrUnaryOperator result)
    {
        var value = kind switch
        {
            SyntaxKind.UnaryPlusExpression => IrUnaryOperator.Identity,
            SyntaxKind.UnaryMinusExpression => IrUnaryOperator.Negate,
            SyntaxKind.LogicalNotExpression => IrUnaryOperator.LogicalNot,
            SyntaxKind.BitwiseNotExpression => IrUnaryOperator.BitwiseNot,
            SyntaxKind.PreIncrementExpression => IrUnaryOperator.PreIncrement,
            SyntaxKind.PreDecrementExpression => IrUnaryOperator.PreDecrement,
            SyntaxKind.PostIncrementExpression => IrUnaryOperator.PostIncrement,
            SyntaxKind.PostDecrementExpression => IrUnaryOperator.PostDecrement,
            _ => (IrUnaryOperator?)null
        };
        result = value.GetValueOrDefault();
        return value.HasValue;
    }

    private static bool TryGetIrAssignmentOperator(SyntaxKind kind, out IrAssignmentOperator result)
    {
        var value = kind switch
        {
            SyntaxKind.SimpleAssignmentExpression => IrAssignmentOperator.Assign,
            SyntaxKind.AddAssignmentExpression => IrAssignmentOperator.Add,
            SyntaxKind.SubtractAssignmentExpression => IrAssignmentOperator.Subtract,
            SyntaxKind.MultiplyAssignmentExpression => IrAssignmentOperator.Multiply,
            SyntaxKind.DivideAssignmentExpression => IrAssignmentOperator.Divide,
            SyntaxKind.ModuloAssignmentExpression => IrAssignmentOperator.Remainder,
            SyntaxKind.AndAssignmentExpression => IrAssignmentOperator.BitwiseAnd,
            SyntaxKind.OrAssignmentExpression => IrAssignmentOperator.BitwiseOr,
            SyntaxKind.ExclusiveOrAssignmentExpression => IrAssignmentOperator.ExclusiveOr,
            _ => (IrAssignmentOperator?)null
        };
        result = value.GetValueOrDefault();
        return value.HasValue;
    }
}
