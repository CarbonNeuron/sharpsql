using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace SharpSql;

public sealed partial class SharpSqlCompiler
{
    private readonly Dictionary<IrSource, SyntaxNode> _csharpSourceNodes = new(ReferenceComparer<IrSource>.Instance);
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
            TryStatementSyntax @try => BindProceduralTry(@try, scope),
            ThrowStatementSyntax @throw => new ProceduralThrow(
                ToIrSource(@throw),
                @throw.Expression is null ? null : BindIrExpression(@throw.Expression, scope),
                @throw.Expression is null ? null : BindExceptionType(@throw.Expression)),
            BreakStatementSyntax @break => new ProceduralBreak(ToIrSource(@break)),
            ContinueStatementSyntax @continue => new ProceduralContinue(ToIrSource(@continue)),
            ReturnStatementSyntax @return => new ProceduralReturn(
                ToIrSource(@return),
                @return.Expression is null ? null : BindIrExpression(@return.Expression, scope)),
            EmptyStatementSyntax empty => new ProceduralEmpty(ToIrSource(empty)),
            _ => new ProceduralUnsupported(ToIrSource(statement), statement.Kind().ToString())
        };

    private ProceduralStatement BindProceduralTry(TryStatementSyntax statement, VariableScope scope)
    {
        if (statement.Finally is not null)
            return new ProceduralUnsupported(ToIrSource(statement), "try/finally");

        return new ProceduralTry(
            ToIrSource(statement),
            (ProceduralBlock)BindProceduralStatement(statement.Block, scope),
            statement.Catches.Select(catchClause =>
            {
                var declaration = catchClause.Declaration;
                var exceptionType = declaration is null ? null : BindExceptionType(declaration.Type);
                var exceptionSymbol = declaration is null || declaration.Identifier.IsKind(SyntaxKind.None)
                    ? null
                    : GetOrCreateIrSymbol(
                        SemanticModelFor(declaration)?.GetDeclaredSymbol(declaration),
                        declaration.Identifier.ValueText,
                        CSharpTypeFactory.From(declaration.Type));
                return new ProceduralCatch(
                    ToIrSource(catchClause),
                    exceptionType,
                    exceptionSymbol,
                    catchClause.Filter is null ? null : BindIrExpression(catchClause.Filter.FilterExpression, scope),
                    (ProceduralBlock)BindProceduralStatement(catchClause.Block, scope));
            }).ToArray());
    }

    private IrExceptionType BindExceptionType(SyntaxNode syntax)
    {
        var symbol = syntax switch
        {
            TypeSyntax type => SemanticModelFor(type)?.GetTypeInfo(type).Type,
            ExpressionSyntax expression => SemanticModelFor(expression)?.GetTypeInfo(expression).Type,
            _ => null
        };
        if (symbol is not null && symbol.TypeKind != TypeKind.Error)
        {
            var baseTypes = new List<string>();
            for (var current = symbol.BaseType; current is not null; current = current.BaseType)
                baseTypes.Add(ExceptionMetadataName(current));
            return new IrExceptionType(ExceptionMetadataName(symbol), baseTypes);
        }

        var fallbackName = syntax switch
        {
            TypeSyntax type => type.ToString(),
            ObjectCreationExpressionSyntax creation => creation.Type.ToString(),
            ImplicitObjectCreationExpressionSyntax => "Exception",
            _ => "Exception"
        };
        return new IrExceptionType(
            NormalizeExceptionMetadataName(fallbackName),
            Array.Empty<string>());
    }

    private static string ExceptionMetadataName(ITypeSymbol symbol) =>
        symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)
            .Replace("global::", string.Empty, StringComparison.Ordinal);

    private static string NormalizeExceptionMetadataName(string name)
    {
        name = name.Replace("global::", string.Empty, StringComparison.Ordinal).Trim();
        if (name.Contains(".", StringComparison.Ordinal))
            return name;
        return name == "DatabaseException" ? "SharpSql.DatabaseException" : $"System.{name}";
    }

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
            ThisExpressionSyntax or BaseExpressionSyntax => new IrThisExpression(
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
            AwaitExpressionSyntax awaitExpression => new IrAwaitExpression(
                source,
                facts,
                BindIrExpression(awaitExpression.Expression, scope)),
            ConditionalExpressionSyntax conditional => new IrConditionalExpression(
                source,
                facts,
                BindIrExpression(conditional.Condition, scope),
                BindIrExpression(conditional.WhenTrue, scope),
                BindIrExpression(conditional.WhenFalse, scope)),
            MemberAccessExpressionSyntax member => BindMemberExpression(member, source, facts, scope),
            ElementAccessExpressionSyntax element => new IrElementExpression(
                source,
                facts,
                BindIrExpression(element.Expression, scope),
                element.ArgumentList.Arguments.Select(argument => BindIrExpression(argument.Expression, scope)).ToArray()),
            InvocationExpressionSyntax invocation => BindInvocation(invocation, source, facts, scope),
            ObjectCreationExpressionSyntax creation => BindObjectCreation(creation, source, facts, scope),
            ImplicitObjectCreationExpressionSyntax creation => BindObjectCreation(creation, source, facts, scope),
            WithExpressionSyntax withExpression => new IrWithExpression(
                source,
                facts,
                BindIrExpression(withExpression.Expression, scope),
                withExpression.Initializer.Expressions
                    .Select(item => BindIrExpression(item, scope))
                    .OfType<IrAssignmentExpression>()
                    .ToArray()),
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

    private IrMemberExpression BindMemberExpression(
        MemberAccessExpressionSyntax member,
        IrSource source,
        ExpressionFacts facts,
        VariableScope scope) =>
        new(
            source,
            facts,
            BindIrExpression(member.Expression, scope),
            member.Name.Identifier.ValueText)
        {
            MemberId = MemberIdentity(SemanticModelFor(member)?.GetSymbolInfo(member).Symbol)
        };

    private IrInvocationExpression BindInvocation(
        InvocationExpressionSyntax invocation,
        IrSource source,
        ExpressionFacts facts,
        VariableScope scope)
    {
        var method = SemanticModelFor(invocation)?.GetSymbolInfo(invocation).Symbol as IMethodSymbol;
        return new IrInvocationExpression(
            source,
            facts,
            BindIrExpression(invocation.Expression, scope),
            invocation.ArgumentList.Arguments
                .Select(argument => BindIrExpression(argument.Expression, scope))
                .ToArray())
        {
            TargetMethodId = MethodIdentity(method),
            Dispatch = invocation.Expression is MemberAccessExpressionSyntax { Expression: BaseExpressionSyntax }
                ? IrCallDispatch.Direct
                : CallDispatch(method)
        };
    }

    private IrObjectCreationExpression BindObjectCreation(
        ObjectCreationExpressionSyntax creation,
        IrSource source,
        ExpressionFacts facts,
        VariableScope scope) =>
        new(
            source,
            facts,
            CSharpTypeFactory.From(creation.Type),
            creation.ArgumentList?.Arguments
                .Select(argument => BindIrExpression(argument.Expression, scope))
                .ToArray() ?? [],
            creation.Initializer?.Expressions.Select(item => BindIrExpression(item, scope)).ToArray() ?? [])
        {
            ConstructorId = ConstructorIdentity(
                SemanticModelFor(creation)?.GetSymbolInfo(creation).Symbol as IMethodSymbol)
        };

    private IrObjectCreationExpression BindObjectCreation(
        ImplicitObjectCreationExpressionSyntax creation,
        IrSource source,
        ExpressionFacts facts,
        VariableScope scope) =>
        new(
            source,
            facts,
            facts.Type,
            creation.ArgumentList.Arguments
                .Select(argument => BindIrExpression(argument.Expression, scope))
                .ToArray(),
            creation.Initializer?.Expressions.Select(item => BindIrExpression(item, scope)).ToArray() ?? [])
        {
            ConstructorId = ConstructorIdentity(
                SemanticModelFor(creation)?.GetSymbolInfo(creation).Symbol as IMethodSymbol)
        };

    private IReadOnlyList<IrHeapTypeDefinition> BindHeapTypeDefinitions(
        IReadOnlyList<CompilationUnitSyntax> roots,
        IReadOnlyList<SyntaxNode>? compilationSources)
    {
        var usedTypeNames = compilationSources is null
            ? null
            : compilationSources
                .SelectMany(source => source.DescendantNodesAndSelf().OfType<BaseObjectCreationExpressionSyntax>())
                .Select(creation => SemanticModelFor(creation)?.GetTypeInfo(creation).Type?.Name)
                .Where(name => !string.IsNullOrEmpty(name))
                .ToHashSet(StringComparer.Ordinal);
        var declarations = roots
            .SelectMany(root => root.DescendantNodes().OfType<TypeDeclarationSyntax>())
            .Where(declaration => declaration is
                ClassDeclarationSyntax or RecordDeclarationSyntax or StructDeclarationSyntax)
            .ToArray();
        if (usedTypeNames is not null)
        {
            var changed = true;
            while (changed)
            {
                changed = false;
                foreach (var declaration in declarations.Where(candidate =>
                             usedTypeNames.Contains(candidate.Identifier.ValueText)))
                {
                    var baseName = SemanticModelFor(declaration)?
                        .GetDeclaredSymbol(declaration)?.BaseType?.Name;
                    if (baseName is not null && usedTypeNames.Add(baseName))
                        changed = true;
                }
            }
        }
        var definitions = new List<IrHeapTypeDefinition>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var declaration in declarations)
        {
            var name = declaration.Identifier.ValueText;
            if (usedTypeNames is not null && !usedTypeNames.Contains(name))
                continue;
            if (!names.Add(name))
            {
                AddDiagnostic("SS6001", $"Duplicate heap type '{name}' is not supported.", declaration);
                continue;
            }
            definitions.Add(BindHeapTypeDefinition(declaration));
        }
        return definitions;
    }

    private IrHeapTypeDefinition BindHeapTypeDefinition(TypeDeclarationSyntax declaration)
    {
        var semanticModel = SemanticModelFor(declaration);
        var typeSymbol = semanticModel?.GetDeclaredSymbol(declaration);
        var fields = new Dictionary<string, IrHeapFieldDefinition>(StringComparer.Ordinal);

        if (declaration is RecordDeclarationSyntax { ParameterList: not null } record)
        {
            foreach (var parameter in record.ParameterList.Parameters)
            {
                var name = parameter.Identifier.ValueText;
                var property = typeSymbol?.GetMembers(name).OfType<IPropertySymbol>().FirstOrDefault();
                if (property is null)
                    continue;
                fields.TryAdd(name, new IrHeapFieldDefinition(
                    name,
                    parameter.Type is null ? IrType.Unknown : CSharpTypeFactory.From(parameter.Type),
                    ToIrSource(parameter))
                {
                    Id = MemberIdentity(property),
                    Kind = IrMemberKind.Property,
                    IsReadOnly = property?.SetMethod is null
                });
            }
        }

        foreach (var property in declaration.Members.OfType<PropertyDeclarationSyntax>())
        {
            var propertySymbol = semanticModel?.GetDeclaredSymbol(property);
            fields.TryAdd(property.Identifier.ValueText, new IrHeapFieldDefinition(
                property.Identifier.ValueText,
                CSharpTypeFactory.From(property.Type),
                ToIrSource(property))
            {
                Id = MemberIdentity(propertySymbol),
                Kind = IrMemberKind.Property,
                IsStatic = propertySymbol?.IsStatic == true,
                IsReadOnly = propertySymbol?.SetMethod is null,
                Initializer = property.Initializer is null
                    ? null
                    : BindIrExpression(property.Initializer.Value, new VariableScope())
            });
        }

        foreach (var field in declaration.Members.OfType<FieldDeclarationSyntax>())
        {
            foreach (var variable in field.Declaration.Variables)
            {
                var fieldSymbol = semanticModel?.GetDeclaredSymbol(variable) as IFieldSymbol;
                fields.TryAdd(variable.Identifier.ValueText, new IrHeapFieldDefinition(
                    variable.Identifier.ValueText,
                    CSharpTypeFactory.From(field.Declaration.Type),
                    ToIrSource(variable))
                {
                    Id = MemberIdentity(fieldSymbol),
                    Kind = IrMemberKind.Field,
                    IsStatic = fieldSymbol?.IsStatic == true,
                    IsReadOnly = fieldSymbol?.IsReadOnly == true,
                    Initializer = variable.Initializer is null
                        ? null
                        : BindIrExpression(variable.Initializer.Value, new VariableScope())
                });
            }
        }

        var constructors = new List<IrHeapConstructorDefinition>();
        if (declaration is RecordDeclarationSyntax { ParameterList: not null } positionalRecord)
        {
            var parameters = positionalRecord.ParameterList.Parameters.Select(ToParameter).ToArray();
            var constructorSymbol = typeSymbol?.InstanceConstructors.FirstOrDefault(constructor =>
                constructor.Parameters.Length == parameters.Length && !constructor.IsImplicitlyDeclared);
            var primaryBase = positionalRecord.BaseList?.Types
                .OfType<PrimaryConstructorBaseTypeSyntax>()
                .FirstOrDefault();
            constructors.Add(new IrHeapConstructorDefinition(
                positionalRecord.ParameterList.Parameters
                    .Select(parameter => parameter.Identifier.ValueText)
                    .ToArray())
            {
                Id = ConstructorIdentity(constructorSymbol),
                Parameters = parameters,
                InitializerKind = primaryBase is null
                    ? IrConstructorInitializerKind.None
                    : IrConstructorInitializerKind.Base,
                InitializerConstructorId = ConstructorIdentity(
                    primaryBase is null
                        ? null
                        : semanticModel?.GetSymbolInfo(primaryBase).Symbol as IMethodSymbol),
                InitializerArguments = primaryBase?.ArgumentList.Arguments
                    .Select(argument => BindIrExpression(argument.Expression, new VariableScope()))
                    .ToArray() ?? []
            });
        }

        foreach (var constructor in declaration.Members.OfType<ConstructorDeclarationSyntax>())
            constructors.Add(BindHeapConstructor(constructor, fields));

        if (constructors.Count == 0)
        {
            constructors.Add(new IrHeapConstructorDefinition([])
            {
                Id = ConstructorIdentity(typeSymbol?.InstanceConstructors.FirstOrDefault(constructor =>
                    constructor.Parameters.Length == 0))
            });
        }

        var baseSymbol = typeSymbol?.BaseType;
        var meaningfulBase = baseSymbol is null || baseSymbol.SpecialType is
            SpecialType.System_Object or SpecialType.System_ValueType
            ? null
            : CSharpTypeFactory.From(baseSymbol);
        return new IrHeapTypeDefinition(
            declaration.Identifier.ValueText,
            declaration is StructDeclarationSyntax,
            declaration is RecordDeclarationSyntax,
            fields.Values.ToArray(),
            constructors,
            ToIrSource(declaration))
        {
            Id = TypeIdentity(typeSymbol),
            BaseType = meaningfulBase,
            Interfaces = typeSymbol?.Interfaces.Select(CSharpTypeFactory.From).ToArray() ?? [],
            IsAbstract = typeSymbol?.IsAbstract == true,
            IsSealed = typeSymbol?.IsSealed == true
        };
    }

    private IrHeapConstructorDefinition BindHeapConstructor(
        ConstructorDeclarationSyntax constructor,
        IReadOnlyDictionary<string, IrHeapFieldDefinition> fields)
    {
        var semanticModel = SemanticModelFor(constructor);
        var constructorSymbol = semanticModel?.GetDeclaredSymbol(constructor);
        var parameters = constructor.ParameterList.Parameters.Select(ToParameter).ToArray();
        var targets = constructor.ParameterList.Parameters.Select(parameter =>
        {
            var parameterName = parameter.Identifier.ValueText;
            var assignment = constructor.Body?.DescendantNodes()
                .OfType<AssignmentExpressionSyntax>()
                .FirstOrDefault(candidate =>
                    candidate.Right is IdentifierNameSyntax identifier &&
                    identifier.Identifier.ValueText == parameterName);
            var target = assignment is null ? null : ConstructorAssignmentTarget(assignment.Left);
            target ??= fields.Values.FirstOrDefault(field =>
                string.Equals(field.Name, parameterName, StringComparison.OrdinalIgnoreCase))?.Name;
            return target ?? string.Empty;
        }).ToArray();
        var fieldAssignmentOnly = constructor.ExpressionBody?.Expression is AssignmentExpressionSyntax ||
            constructor.ExpressionBody is null &&
            (constructor.Body?.Statements.All(statement =>
                statement is ExpressionStatementSyntax { Expression: AssignmentExpressionSyntax }) ?? true);
        var body = constructor.Body is not null
            ? (ProceduralBlock)BindProceduralStatement(constructor.Body, new VariableScope())
            : constructor.ExpressionBody is not null
                ? new ProceduralBlock(
                    ToIrSource(constructor.ExpressionBody),
                    [new ProceduralExpressionStatement(
                        ToIrSource(constructor.ExpressionBody.Expression),
                        BindIrExpression(constructor.ExpressionBody.Expression, new VariableScope()))])
                : null;
        var initializerKind = constructor.Initializer?.ThisOrBaseKeyword.Kind() switch
        {
            SyntaxKind.ThisKeyword => IrConstructorInitializerKind.This,
            SyntaxKind.BaseKeyword => IrConstructorInitializerKind.Base,
            _ => IrConstructorInitializerKind.None
        };
        return new IrHeapConstructorDefinition(targets)
        {
            Id = ConstructorIdentity(constructorSymbol),
            Parameters = parameters,
            Body = body,
            InitializerKind = initializerKind,
            InitializerConstructorId = ConstructorIdentity(
                constructor.Initializer is null
                    ? null
                    : semanticModel?.GetSymbolInfo(constructor.Initializer).Symbol as IMethodSymbol),
            InitializerArguments = constructor.Initializer?.ArgumentList.Arguments
                .Select(argument => BindIrExpression(argument.Expression, new VariableScope()))
                .ToArray() ?? [],
            IsFieldAssignmentOnly = fieldAssignmentOnly
        };
    }

    private static string? ConstructorAssignmentTarget(ExpressionSyntax expression) => expression switch
    {
        IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
        MemberAccessExpressionSyntax member => member.Name.Identifier.ValueText,
        _ => null
    };

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
            array.Initializer is not null || lengthSyntax is null or OmittedArraySizeExpressionSyntax
                ? null
                : BindIrExpression(lengthSyntax, scope),
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
        var created = new IrSymbol(new IrSymbolId(++_nextIrSymbolId), name, type)
        {
            ReferencedMemberId = symbol is IFieldSymbol or IPropertySymbol
                ? MemberIdentity(symbol)
                : IrMemberId.None
        };
        if (symbol is not null)
            _irSymbols[symbol] = created;
        return created;
    }

    private static IrTypeDefinitionId TypeIdentity(INamedTypeSymbol? type) =>
        type is null
            ? IrTypeDefinitionId.None
            : new IrTypeDefinitionId(SemanticIdentity(type));

    private static IrMemberId MemberIdentity(ISymbol? member) =>
        member is null
            ? IrMemberId.None
            : new IrMemberId(SemanticIdentity(member));

    private static IrMethodId MethodIdentity(IMethodSymbol? method) =>
        method is null
            ? IrMethodId.None
            : new IrMethodId(SemanticIdentity(
                method.ReducedFrom?.OriginalDefinition ?? method.OriginalDefinition));

    private static IrConstructorId ConstructorIdentity(IMethodSymbol? constructor) =>
        constructor is null
            ? IrConstructorId.None
            : new IrConstructorId(SemanticIdentity(constructor.OriginalDefinition));

    private static string SemanticIdentity(ISymbol symbol) =>
        symbol.GetDocumentationCommentId() ??
        $"{symbol.Kind}:{symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)}";

    private static IrCallDispatch CallDispatch(IMethodSymbol? method)
    {
        if (method is null)
            return IrCallDispatch.Unknown;
        if (method.MethodKind == MethodKind.DelegateInvoke)
            return IrCallDispatch.Delegate;
        if (method.IsStatic)
            return IrCallDispatch.Static;
        if (method.ContainingType.TypeKind == TypeKind.Interface)
            return IrCallDispatch.Interface;
        return method.IsVirtual || method.IsOverride || method.IsAbstract
            ? IrCallDispatch.Virtual
            : IrCallDispatch.Direct;
    }

    private static IReadOnlyList<IrMethodId> InterfaceMethodIdentities(IMethodSymbol? method)
    {
        if (method is null)
            return [];
        var identities = method.ExplicitInterfaceImplementations
            .Select(MethodIdentity)
            .ToHashSet();
        foreach (var @interface in method.ContainingType.AllInterfaces)
        {
            foreach (var member in @interface.GetMembers().OfType<IMethodSymbol>())
            {
                if (SymbolEqualityComparer.Default.Equals(
                        method.ContainingType.FindImplementationForInterfaceMember(member),
                        method))
                    identities.Add(MethodIdentity(member));
            }
        }
        return identities.Where(identity => !identity.IsNone).ToArray();
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
