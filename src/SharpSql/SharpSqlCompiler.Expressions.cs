using System.Globalization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace SharpSql;

public sealed partial class SharpSqlCompiler
{
    private string EmitScalar(
        ExpressionSyntax expression,
        VariableScope scope,
        IReadOnlyDictionary<string, Substitution>? substitutions = null) =>
        EmitScalarExpression(expression, scope, substitutions).Sql;

    private SqlScalarExpression EmitScalarExpression(
        ExpressionSyntax expression,
        VariableScope scope,
        IReadOnlyDictionary<string, Substitution>? substitutions = null)
    {
        EmitExpressionComments(expression);
        expression = StripParentheses(expression);
        var analysis = AnalyzeExpression(expression, scope, substitutions);
        if (analysis.Type.IsBoolean && IsPredicateShape(expression))
            return SqlScalarExpression.Primary(
                $"CASE WHEN {EmitPredicate(expression, scope, substitutions)} THEN CAST(1 AS BIT) ELSE CAST(0 AS BIT) END",
                IrType.Bool,
                ScalarNullability.NonNull);

        var result = expression switch
        {
            LiteralExpressionSyntax literal => SqlScalarExpression.Primary(EmitLiteral(literal)),
            IdentifierNameSyntax identifier => EmitIdentifierExpression(identifier, scope, substitutions),
            ThisExpressionSyntax or BaseExpressionSyntax => EmitThisExpression(scope, substitutions),
            BinaryExpressionSyntax binary => EmitBinaryScalar(binary, scope, substitutions),
            PrefixUnaryExpressionSyntax prefix => EmitPrefixScalar(prefix, scope, substitutions),
            CastExpressionSyntax cast => EmitScalarExpression(
                cast.Expression,
                scope,
                substitutions).CastTo(CSharpTypeFactory.From(cast.Type)),
            ConditionalExpressionSyntax conditional => SqlScalarExpression.Primary(
                $"CASE WHEN {EmitPredicate(conditional.Condition, scope, substitutions)} THEN {EmitScalar(conditional.WhenTrue, scope, substitutions)} ELSE {EmitScalar(conditional.WhenFalse, scope, substitutions)} END"),
            InvocationExpressionSyntax invocation => EmitInvocation(invocation, scope, substitutions),
            MemberAccessExpressionSyntax member => EmitHeapMemberScalar(member, scope, substitutions),
            ElementAccessExpressionSyntax element => EmitHeapElementScalar(element, scope),
            ObjectCreationExpressionSyntax creation => EmitIntrinsicObjectCreation(creation, scope, substitutions),
            InterpolatedStringExpressionSyntax interpolated => EmitInterpolatedString(interpolated, scope, substitutions),
            CheckedExpressionSyntax checkedExpression => EmitScalarExpression(checkedExpression.Expression, scope, substitutions),
            _ => SqlScalarExpression.Primary(UnsupportedExpression(expression))
        };
        return result.WithAnalysis(analysis.Type, analysis.Nullability);
    }

    private string EmitPredicate(
        ExpressionSyntax expression,
        VariableScope scope,
        IReadOnlyDictionary<string, Substitution>? substitutions = null)
    {
        expression = StripParentheses(expression);
        var analysis = AnalyzeExpression(expression, scope, substitutions);
        if (analysis.HasConstantValue && analysis.ConstantValue is bool constant)
            return constant ? "1 = 1" : "1 = 0";

        switch (expression)
        {
            case LiteralExpressionSyntax literal when literal.IsKind(SyntaxKind.TrueLiteralExpression):
                return "1 = 1";
            case LiteralExpressionSyntax literal when literal.IsKind(SyntaxKind.FalseLiteralExpression):
                return "1 = 0";
            case IdentifierNameSyntax identifier:
                return $"{EmitIdentifier(identifier, scope, substitutions)} = 1";
            case PrefixUnaryExpressionSyntax prefix when prefix.IsKind(SyntaxKind.LogicalNotExpression):
                return $"NOT ({EmitPredicate(prefix.Operand, scope, substitutions)})";
            case BinaryExpressionSyntax binary when binary.IsKind(SyntaxKind.LogicalAndExpression):
                return $"({EmitPredicate(binary.Left, scope, substitutions)}) AND ({EmitPredicate(binary.Right, scope, substitutions)})";
            case BinaryExpressionSyntax binary when binary.IsKind(SyntaxKind.LogicalOrExpression):
                return $"({EmitPredicate(binary.Left, scope, substitutions)}) OR ({EmitPredicate(binary.Right, scope, substitutions)})";
            case BinaryExpressionSyntax binary when
                binary.Kind() is SyntaxKind.EqualsExpression or SyntaxKind.NotEqualsExpression &&
                (binary.Left.IsKind(SyntaxKind.NullLiteralExpression) || binary.Right.IsKind(SyntaxKind.NullLiteralExpression)):
                {
                    var operand = binary.Left.IsKind(SyntaxKind.NullLiteralExpression) ? binary.Right : binary.Left;
                    var op = binary.IsKind(SyntaxKind.EqualsExpression) ? "IS NULL" : "IS NOT NULL";
                    return $"{EmitScalar(operand, scope, substitutions)} {op}";
                }
            case BinaryExpressionSyntax binary when IsComparison(binary.Kind()):
                return $"{EmitScalar(binary.Left, scope, substitutions)} {SqlOperator(binary.Kind())} {EmitScalar(binary.Right, scope, substitutions)}";
            default:
                return $"{EmitScalar(expression, scope, substitutions)} = 1";
        }
    }

    private SqlScalarExpression EmitBinaryScalar(
        BinaryExpressionSyntax binary,
        VariableScope scope,
        IReadOnlyDictionary<string, Substitution>? substitutions)
    {
        if (binary.IsKind(SyntaxKind.CoalesceExpression))
            return SqlScalarExpression.Primary(
                $"COALESCE({EmitScalar(binary.Left, scope, substitutions)}, {EmitScalar(binary.Right, scope, substitutions)})");

        if (binary.IsKind(SyntaxKind.AddExpression) &&
            (InferType(binary.Left, scope, substitutions).IsString || InferType(binary.Right, scope, substitutions).IsString))
            return SqlScalarExpression.Primary(
                $"CONCAT({EmitScalar(binary.Left, scope, substitutions)}, {EmitScalar(binary.Right, scope, substitutions)})");
        if (binary.Kind() is SyntaxKind.LeftShiftExpression or SyntaxKind.RightShiftExpression)
        {
            var type = InferType(binary, scope, substitutions);
            var shiftLeft = EmitScalar(binary.Left, scope, substitutions);
            var shiftRight = EmitScalar(binary.Right, scope, substitutions);
            return SqlScalarExpression.Primary(binary.IsKind(SyntaxKind.LeftShiftExpression)
                ? LeftShiftSql(type, shiftLeft, shiftRight)
                : RightShiftSql(type, shiftLeft, shiftRight));
        }

        var op = SqlOperator(binary.Kind());
        if (op.Length == 0)
            return SqlScalarExpression.Primary(UnsupportedExpression(binary));

        var precedence = BinaryPrecedence(binary.Kind());
        var left = EmitScalarExpression(binary.Left, scope, substitutions).Render(precedence);
        var right = EmitScalarExpression(binary.Right, scope, substitutions).Render(precedence + 1);
        return new SqlScalarExpression($"{left} {op} {right}", IrType.Unknown, precedence);
    }

    private SqlScalarExpression EmitPrefixScalar(
        PrefixUnaryExpressionSyntax prefix,
        VariableScope scope,
        IReadOnlyDictionary<string, Substitution>? substitutions) => prefix.Kind() switch
        {
            SyntaxKind.UnaryMinusExpression => new SqlScalarExpression(
                $"-{EmitScalarExpression(prefix.Operand, scope, substitutions).Render(PrecedenceUnary + 1)}",
                IrType.Unknown,
                PrecedenceUnary),
            SyntaxKind.UnaryPlusExpression => new SqlScalarExpression(
                $"+{EmitScalarExpression(prefix.Operand, scope, substitutions).Render(PrecedenceUnary + 1)}",
                IrType.Unknown,
                PrecedenceUnary),
            SyntaxKind.BitwiseNotExpression => new SqlScalarExpression(
                $"~{EmitScalarExpression(prefix.Operand, scope, substitutions).Render(PrecedenceUnary + 1)}",
                IrType.Unknown,
                PrecedenceUnary),
            SyntaxKind.LogicalNotExpression => SqlScalarExpression.Primary(
                $"CASE WHEN {EmitPredicate(prefix.Operand, scope, substitutions)} THEN CAST(0 AS BIT) ELSE CAST(1 AS BIT) END"),
            _ => SqlScalarExpression.Primary(UnsupportedExpression(prefix))
        };

    private SqlScalarExpression EmitInvocation(
        InvocationExpressionSyntax invocation,
        VariableScope scope,
        IReadOnlyDictionary<string, Substitution>? substitutions)
    {
        if (TryEmitLinqInvocation(invocation, scope, substitutions, out var linqExpression))
            return linqExpression;

        if (substitutions is null && TryEmitHeapInvocationScalar(invocation, scope, out var heapExpression))
            return heapExpression;

        if (!TryGetMethod(invocation, out var method))
            return SqlScalarExpression.Primary(
                UnsupportedExpression(invocation, "Only user-defined methods and Console.WriteLine are supported."));
        if (method.PureExpression is null)
            return SqlScalarExpression.Primary(
                UnsupportedExpression(invocation, "A branching method call must be the complete variable initializer."));
        EmitLeadingComments(method.Source);
        if (method.Body?.Statements.OfType<ProceduralReturn>().FirstOrDefault() is { } returnStatement)
            EmitLeadingComments(returnStatement.Source);
        var arguments = InvocationArgumentExpressions(invocation, method);
        if (!CanInline(method, arguments.Count))
            return SqlScalarExpression.Primary("NULL");

        var replacements = new Dictionary<string, Substitution>(StringComparer.Ordinal);
        var planReplacements = new Dictionary<string, SqlLinqQueryPlan>(StringComparer.Ordinal);
        var lambdaReplacements = new Dictionary<string, SqlLinqLambdaPlan>(StringComparer.Ordinal);
        for (var index = 0; index < method.Parameters.Count; index++)
        {
            var parameter = method.Parameters[index];
            var argument = arguments[index];
            if (TryBuildLinqLambda(argument, scope, substitutions, out var lambdaArgument))
            {
                lambdaReplacements[parameter.Name] = lambdaArgument;
                continue;
            }
            if ((IsSequenceType(parameter.Type.Name) || IsLinqSequenceType(parameter.Type.Name)) &&
                TryBuildLinqQuery(argument, scope, substitutions, out var argumentQuery))
            {
                planReplacements[parameter.Name] = argumentQuery;
                continue;
            }
            replacements[parameter.Name] = new Substitution(
                EmitScalarExpression(argument, scope, substitutions));
        }

        _linqPlanSubstitutions.Push(planReplacements);
        _linqLambdaSubstitutions.Push(lambdaReplacements);
        try
        {
            return EmitScalarExpression(method.PureExpression, scope, replacements);
        }
        finally
        {
            _linqLambdaSubstitutions.Pop();
            _linqPlanSubstitutions.Pop();
        }
    }

    private static IReadOnlyList<ExpressionSyntax> InvocationArgumentExpressions(
        InvocationExpressionSyntax invocation,
        MethodDefinition method)
    {
        var arguments = new List<ExpressionSyntax>();
        if (method.IsInstance)
        {
            arguments.Add(invocation.Expression is MemberAccessExpressionSyntax member
                ? member.Expression
                : SyntaxFactory.ThisExpression());
        }
        arguments.AddRange(invocation.ArgumentList.Arguments.Select(argument => argument.Expression));
        return arguments;
    }

    private static IReadOnlyList<IrExpression> InvocationArgumentExpressions(
        IrInvocationExpression invocation,
        MethodDefinition method)
    {
        var arguments = new List<IrExpression>();
        if (method.IsInstance)
        {
            arguments.Add(invocation.Target is IrMemberExpression member
                ? member.Receiver
                : new IrThisExpression(
                    invocation.Source,
                    new ExpressionFacts(
                        method.Parameters[0].Type,
                        ScalarNullability.MaybeNull,
                        HasConstantValue: false,
                        ConstantValue: null),
                    method.Parameters[0].Symbol));
        }
        arguments.AddRange(invocation.Arguments);
        return arguments;
    }

    private SqlScalarExpression EmitInterpolatedString(
        InterpolatedStringExpressionSyntax interpolated,
        VariableScope scope,
        IReadOnlyDictionary<string, Substitution>? substitutions)
    {
        var parts = interpolated.Contents.Select(content => content switch
        {
            InterpolatedStringTextSyntax text => "N'" + EscapeSqlString(text.TextToken.ValueText) + "'",
            InterpolationSyntax interpolation => EmitInterpolation(interpolation, scope, substitutions),
            _ => "N''"
        }).ToArray();
        return parts.Length switch
        {
            0 => SqlScalarExpression.Primary("N''"),
            1 when interpolated.Contents[0] is InterpolatedStringTextSyntax => SqlScalarExpression.Primary(parts[0]),
            1 => SqlScalarExpression.Primary($"CONCAT(N'', {parts[0]})"),
            _ => SqlScalarExpression.Primary($"CONCAT({string.Join(", ", parts)})")
        };
    }

    private SqlScalarExpression EmitIntrinsicObjectCreation(
        ObjectCreationExpressionSyntax creation,
        VariableScope scope,
        IReadOnlyDictionary<string, Substitution>? substitutions)
    {
        if (IsStringCharacterArrayCreation(creation, scope, substitutions) &&
            creation.ArgumentList!.Arguments is { Count: 1 } arguments)
        {
            var characters = EmitScalar(arguments[0].Expression, scope, substitutions);
            return SqlScalarExpression.Primary(StringFromCharacterArraySql(characters));
        }

        return SqlScalarExpression.Primary(UnsupportedExpression(
            creation,
            "Only the string(char[]) scalar constructor is supported."));
    }

    private bool IsStringCharacterArrayCreation(
        ObjectCreationExpressionSyntax creation,
        VariableScope scope,
        IReadOnlyDictionary<string, Substitution>? substitutions = null) =>
        CSharpTypeFactory.From(creation.Type).IsString &&
        creation.ArgumentList?.Arguments is { Count: 1 } arguments &&
        InferType(arguments[0].Expression, scope, substitutions).Name == "char[]";

    private string StringFromCharacterArraySql(string characters)
    {
        var character = IndexedItemReadValue(new IrType("char"));
        return $"COALESCE((SELECT STRING_AGG(CONVERT(NVARCHAR(MAX), {character}), N'') " +
            $"WITHIN GROUP (ORDER BY __index) FROM {HeapIndexedItems} WHERE {IndexedItemExecutionFilter()}__owner_id = {characters}), N'')";
    }

    private string EmitInterpolation(
        InterpolationSyntax interpolation,
        VariableScope scope,
        IReadOnlyDictionary<string, Substitution>? substitutions)
    {
        var value = EmitScalar(interpolation.Expression, scope, substitutions);
        return FormatTextValue(InferType(interpolation.Expression, scope, substitutions), value);
    }

    private string EmitIdentifier(
        IdentifierNameSyntax identifier,
        VariableScope scope,
        IReadOnlyDictionary<string, Substitution>? substitutions) =>
        EmitIdentifierExpression(identifier, scope, substitutions).Sql;

    private SqlScalarExpression EmitIdentifierExpression(
        IdentifierNameSyntax identifier,
        VariableScope scope,
        IReadOnlyDictionary<string, Substitution>? substitutions)
    {
        var name = identifier.Identifier.ValueText;
        if (substitutions is not null && substitutions.TryGetValue(name, out var replacement))
            return replacement.Expression;
        if (scope.Find(name) is ScalarVariableBinding binding)
            return binding.Scalar;
        if (TryEmitImplicitHeapField(identifier, scope, substitutions, out var heapField))
            return heapField;
        AddDiagnostic("SS4001", $"Unknown identifier '{name}'.", identifier);
        return SqlScalarExpression.Primary("NULL");
    }

    private static SqlScalarExpression EmitThisExpression(
        VariableScope scope,
        IReadOnlyDictionary<string, Substitution>? substitutions)
    {
        if (substitutions is not null && substitutions.TryGetValue("this", out var replacement))
            return replacement.Expression;
        return scope.Find("this") is ScalarVariableBinding binding
            ? binding.Scalar
            : SqlScalarExpression.Primary("NULL");
    }

    private IrType InferType(
        ExpressionSyntax expression,
        VariableScope scope,
        IReadOnlyDictionary<string, Substitution>? substitutions = null) =>
        AnalyzeExpression(expression, scope, substitutions).Type;

    private ExpressionFacts AnalyzeExpression(
        ExpressionSyntax expression,
        VariableScope scope,
        IReadOnlyDictionary<string, Substitution>? substitutions = null)
    {
        expression = StripParentheses(expression);
        if (expression is IdentifierNameSyntax substitutedIdentifier &&
            substitutions is not null &&
            substitutions.TryGetValue(substitutedIdentifier.Identifier.ValueText, out var substitution))
        {
            return new ExpressionFacts(
                substitution.Expression.Type,
                substitution.Expression.Nullability,
                HasConstantValue: false,
                ConstantValue: null);
        }

        var semanticModel = SemanticModelFor(expression);
        if (semanticModel is not null)
        {
            var typeInfo = semanticModel.GetTypeInfo(expression);
            if (typeInfo.Type is { TypeKind: not TypeKind.Error } semanticType)
            {
                var constant = semanticModel.GetConstantValue(expression);
                return new ExpressionFacts(
                    CSharpTypeFactory.From(semanticType),
                    ToScalarNullability(typeInfo.Nullability.FlowState, expression),
                    constant.HasValue,
                    constant.HasValue ? constant.Value : null);
            }
        }

        var fallbackType = InferTypeFallback(expression, scope, substitutions);
        return new ExpressionFacts(
            fallbackType,
            expression.IsKind(SyntaxKind.NullLiteralExpression)
                ? ScalarNullability.Null
                : fallbackType.IsReference
                    ? ScalarNullability.MaybeNull
                    : ScalarNullability.NonNull,
            HasConstantValue: false,
            ConstantValue: null);
    }

    private static ScalarNullability ToScalarNullability(
        NullableFlowState flowState,
        ExpressionSyntax expression)
    {
        if (expression.IsKind(SyntaxKind.NullLiteralExpression))
            return ScalarNullability.Null;
        return flowState switch
        {
            NullableFlowState.NotNull => ScalarNullability.NonNull,
            NullableFlowState.MaybeNull => ScalarNullability.MaybeNull,
            _ => ScalarNullability.Unknown
        };
    }

    private IrType InferTypeFallback(
        ExpressionSyntax expression,
        VariableScope scope,
        IReadOnlyDictionary<string, Substitution>? substitutions = null)
    {
        expression = StripParentheses(expression);
        return expression switch
        {
            LiteralExpressionSyntax literal when literal.IsKind(SyntaxKind.StringLiteralExpression) => IrType.String,
            LiteralExpressionSyntax literal when literal.IsKind(SyntaxKind.CharacterLiteralExpression) => new("char"),
            LiteralExpressionSyntax literal when literal.IsKind(SyntaxKind.TrueLiteralExpression) || literal.IsKind(SyntaxKind.FalseLiteralExpression) => IrType.Bool,
            LiteralExpressionSyntax literal when literal.Token.Value is decimal => new("decimal"),
            LiteralExpressionSyntax literal when literal.Token.Value is double => new("double"),
            LiteralExpressionSyntax literal when literal.Token.Value is float => new("float"),
            LiteralExpressionSyntax literal when literal.Token.Value is long => new("long"),
            LiteralExpressionSyntax => IrType.Int,
            IdentifierNameSyntax identifier when substitutions is not null && substitutions.TryGetValue(identifier.Identifier.ValueText, out var value) => value.Type,
            IdentifierNameSyntax identifier => scope.Find(identifier.Identifier.ValueText)?.Type ?? IrType.Unknown,
            ThisExpressionSyntax or BaseExpressionSyntax => scope.Find("this")?.Type ?? IrType.Unknown,
            BinaryExpressionSyntax binary when IsPredicateShape(binary) => IrType.Bool,
            BinaryExpressionSyntax binary when binary.IsKind(SyntaxKind.AddExpression) &&
                (InferType(binary.Left, scope, substitutions).IsString || InferType(binary.Right, scope, substitutions).IsString) => IrType.String,
            BinaryExpressionSyntax binary => InferType(binary.Left, scope, substitutions),
            PrefixUnaryExpressionSyntax prefix when prefix.IsKind(SyntaxKind.LogicalNotExpression) => IrType.Bool,
            PrefixUnaryExpressionSyntax prefix => InferType(prefix.Operand, scope, substitutions),
            CastExpressionSyntax cast => CSharpTypeFactory.From(cast.Type),
            ConditionalExpressionSyntax conditional => InferType(conditional.WhenTrue, scope, substitutions),
            InterpolatedStringExpressionSyntax => IrType.String,
            ObjectCreationExpressionSyntax creation => CSharpTypeFactory.From(creation.Type),
            ArrayCreationExpressionSyntax creation => CSharpTypeFactory.From(creation.Type),
            MemberAccessExpressionSyntax member => InferHeapMemberType(member, scope),
            ElementAccessExpressionSyntax element => InferHeapElementType(element, scope),
            InvocationExpressionSyntax invocation when invocation.Expression is MemberAccessExpressionSyntax member &&
                ((IsDictionaryType(InferType(member.Expression, scope, substitutions).Name) &&
                  member.Name.Identifier.ValueText is "ContainsKey" or "ContainsValue") ||
                 (IsListType(InferType(member.Expression, scope, substitutions).Name) &&
                  member.Name.Identifier.ValueText == "Contains") ||
                 (InferType(member.Expression, scope, substitutions).Name == "byte[]" &&
                  member.Name.Identifier.ValueText == "SequenceEqual")) => IrType.Bool,
            InvocationExpressionSyntax invocation when TryGetMethod(invocation, out var method) => method.ReturnType,
            _ => IrType.Unknown
        };
    }

    private string UnsupportedExpression(SyntaxNode node, string? detail = null)
    {
        AddDiagnostic("SS4002", detail ?? $"Unsupported expression: {node.Kind()}.", node);
        return "NULL";
    }

    private string UnsupportedExpression(IrSource source, string? detail = null)
    {
        AddDiagnostic("SS4002", detail ?? "Unsupported IR expression.", source);
        return "NULL";
    }

    private void Unsupported(SyntaxNode node, string category) =>
        AddDiagnostic("SS4003", $"Unsupported {category}: {node.Kind()}.", node);

    private void Unsupported(IrSource source, string category) =>
        AddDiagnostic("SS4003", $"Unsupported {category}.", source);

    private void AddDiagnostic(string code, string message, SyntaxNode node)
    {
        var location = node.GetLocation().GetLineSpan().StartLinePosition;
        var diagnostic = new CompilerDiagnostic(
            code,
            message,
            location.Line + 1,
            location.Character + 1,
            node.SyntaxTree.FilePath);
        if (!_diagnostics.Contains(diagnostic))
            _diagnostics.Add(diagnostic);
    }

    private void AddDiagnostic(string code, string message, IrSource source)
    {
        var diagnostic = new CompilerDiagnostic(code, message, source.Span.Line, source.Span.Column, source.FilePath);
        if (!_diagnostics.Contains(diagnostic))
            _diagnostics.Add(diagnostic);
    }

    private static string? InvocationName(ExpressionSyntax expression) => expression switch
    {
        IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
        GenericNameSyntax generic => generic.Identifier.ValueText,
        MemberAccessExpressionSyntax member => member.Name.Identifier.ValueText,
        _ => null
    };

    private bool TryGetMethod(IrInvocationExpression invocation, out MethodDefinition method) =>
        _methods.TryResolve(invocation, out method);

    private bool TryGetMethod(InvocationExpressionSyntax invocation, out MethodDefinition method)
    {
        var symbol = SemanticModelFor(invocation)?.GetSymbolInfo(invocation).Symbol as IMethodSymbol;
        if (symbol is not null)
            return _methods.TryGetValue(MethodIdentity(symbol), out method);
        var name = InvocationName(invocation.Expression);
        if (name is not null && _methods.TryGetValue(name, out method))
            return true;
        method = null!;
        return false;
    }

    private bool InvocationInputsAreDiscardable(InvocationExpressionSyntax invocation)
    {
        if (invocation.Expression is MemberAccessExpressionSyntax member &&
            !ExpressionIsDiscardable(member.Expression))
            return false;
        return invocation.ArgumentList.Arguments.All(argument => ExpressionIsDiscardable(argument.Expression));
    }

    private bool InvocationInputsAreDiscardable(IrInvocationExpression invocation)
    {
        if (invocation.Target is IrMemberExpression member && !ExpressionIsDiscardable(member.Receiver))
            return false;
        return invocation.Arguments.All(ExpressionIsDiscardable);
    }

    private bool ExpressionIsDiscardable(IrExpression expression) => expression switch
    {
        IrConstantExpression or IrVariableExpression or IrThisExpression => true,
        IrConversionExpression conversion => ExpressionIsDiscardable(conversion.Operand),
        IrUnaryExpression unary when unary.Operator is not (
            IrUnaryOperator.PreIncrement or IrUnaryOperator.PreDecrement or
            IrUnaryOperator.PostIncrement or IrUnaryOperator.PostDecrement) =>
            ExpressionIsDiscardable(unary.Operand),
        IrBinaryExpression binary when binary.Operator is not (
            IrBinaryOperator.Divide or IrBinaryOperator.Remainder) =>
            ExpressionIsDiscardable(binary.Left) && ExpressionIsDiscardable(binary.Right),
        IrConditionalExpression conditional =>
            ExpressionIsDiscardable(conditional.Condition) &&
            ExpressionIsDiscardable(conditional.WhenTrue) &&
            ExpressionIsDiscardable(conditional.WhenFalse),
        IrInvocationExpression nested when TryGetMethod(nested, out var method) =>
            method.Behavior.IsSideEffectFree && InvocationInputsAreDiscardable(nested),
        _ => false
    };

    private bool ExpressionIsDiscardable(ExpressionSyntax expression)
    {
        expression = StripParentheses(expression);
        return expression switch
        {
            LiteralExpressionSyntax or IdentifierNameSyntax or ThisExpressionSyntax or BaseExpressionSyntax => true,
            CastExpressionSyntax cast => ExpressionIsDiscardable(cast.Expression),
            PrefixUnaryExpressionSyntax prefix when prefix.Kind() is not (
                SyntaxKind.PreIncrementExpression or SyntaxKind.PreDecrementExpression) =>
                ExpressionIsDiscardable(prefix.Operand),
            BinaryExpressionSyntax binary when binary.Kind() is not (
                SyntaxKind.DivideExpression or SyntaxKind.ModuloExpression) =>
                ExpressionIsDiscardable(binary.Left) && ExpressionIsDiscardable(binary.Right),
            ConditionalExpressionSyntax conditional =>
                ExpressionIsDiscardable(conditional.Condition) &&
                ExpressionIsDiscardable(conditional.WhenTrue) &&
                ExpressionIsDiscardable(conditional.WhenFalse),
            InvocationExpressionSyntax nested when TryGetMethod(nested, out var method) =>
                method.Behavior.IsSideEffectFree && InvocationInputsAreDiscardable(nested),
            _ => false
        };
    }

    private static ExpressionSyntax StripParentheses(ExpressionSyntax expression)
    {
        while (expression is ParenthesizedExpressionSyntax parenthesized)
            expression = parenthesized.Expression;
        return expression;
    }

    private void EmitLabel(string label) => _sql.Line($"{label}:;");

    private static bool IsPredicateShape(ExpressionSyntax expression) => expression switch
    {
        BinaryExpressionSyntax binary => IsComparison(binary.Kind()) ||
            binary.IsKind(SyntaxKind.LogicalAndExpression) ||
            binary.IsKind(SyntaxKind.LogicalOrExpression),
        PrefixUnaryExpressionSyntax prefix => prefix.IsKind(SyntaxKind.LogicalNotExpression),
        _ => false
    };

    private static bool IsComparison(SyntaxKind kind) => kind is
        SyntaxKind.EqualsExpression or SyntaxKind.NotEqualsExpression or
        SyntaxKind.LessThanExpression or SyntaxKind.LessThanOrEqualExpression or
        SyntaxKind.GreaterThanExpression or SyntaxKind.GreaterThanOrEqualExpression;

    private static string SqlOperator(SyntaxKind kind) => kind switch
    {
        SyntaxKind.AddExpression => "+",
        SyntaxKind.SubtractExpression => "-",
        SyntaxKind.MultiplyExpression => "*",
        SyntaxKind.DivideExpression => "/",
        SyntaxKind.ModuloExpression => "%",
        SyntaxKind.BitwiseAndExpression => "&",
        SyntaxKind.BitwiseOrExpression => "|",
        SyntaxKind.ExclusiveOrExpression => "^",
        SyntaxKind.LeftShiftExpression => "<<",
        SyntaxKind.RightShiftExpression => ">>",
        SyntaxKind.EqualsExpression => "=",
        SyntaxKind.NotEqualsExpression => "<>",
        SyntaxKind.LessThanExpression => "<",
        SyntaxKind.LessThanOrEqualExpression => "<=",
        SyntaxKind.GreaterThanExpression => ">",
        SyntaxKind.GreaterThanOrEqualExpression => ">=",
        _ => ""
    };

    private static int BinaryPrecedence(SyntaxKind kind) => kind switch
    {
        SyntaxKind.MultiplyExpression or SyntaxKind.DivideExpression or SyntaxKind.ModuloExpression =>
            PrecedenceMultiplicative,
        SyntaxKind.LeftShiftExpression or SyntaxKind.RightShiftExpression => PrecedenceShift,
        _ => PrecedenceAdditive
    };

    private static string EmitLiteral(LiteralExpressionSyntax literal)
    {
        if (literal.IsKind(SyntaxKind.StringLiteralExpression) || literal.IsKind(SyntaxKind.CharacterLiteralExpression))
            return "N'" + EscapeSqlString(literal.Token.ValueText) + "'";
        if (literal.IsKind(SyntaxKind.TrueLiteralExpression))
            return "CAST(1 AS BIT)";
        if (literal.IsKind(SyntaxKind.FalseLiteralExpression))
            return "CAST(0 AS BIT)";
        if (literal.IsKind(SyntaxKind.NullLiteralExpression))
            return "NULL";

        return literal.Token.Value switch
        {
            float value => $"CAST({value.ToString("R", CultureInfo.InvariantCulture)} AS REAL)",
            double value => $"CAST({value.ToString("R", CultureInfo.InvariantCulture)} AS FLOAT)",
            decimal value => $"CAST({value.ToString(CultureInfo.InvariantCulture)} AS DECIMAL(38,18))",
            IFormattable value => value.ToString(null, CultureInfo.InvariantCulture),
            _ => literal.Token.ValueText
        };
    }

    private static string EscapeSqlString(string value) => value.Replace("'", "''", StringComparison.Ordinal);

}
