namespace SharpSql;

public sealed partial class SharpSqlCompiler
{
    private void AnalyzeMethodBehaviors()
    {
        var summaries = _methods.Values.ToDictionary(
            method => method.Id,
            _ => MethodBehaviorSummary.Empty);

        // Effects and aliases only grow. Recomputing from the preceding fixed-point
        // state handles recursive SCCs without coupling this pass to the SQL backend.
        var maximumPasses = Math.Max(2, _methods.Count * 4);
        for (var pass = 0; pass < maximumPasses; pass++)
        {
            var next = _methods.Values.ToDictionary(
                method => method.Id,
                method => AnalyzeMethodBehavior(method, summaries));
            if (next.All(item => BehaviorEquals(item.Value, summaries[item.Key])))
            {
                summaries = next;
                break;
            }
            summaries = next;
        }

        foreach (var method in _methods.Values.ToArray())
            _methods.Replace(method with { Behavior = summaries[method.Id] });
    }

    private MethodBehaviorSummary AnalyzeMethodBehavior(
        MethodDefinition method,
        IReadOnlyDictionary<IrMethodId, MethodBehaviorSummary> summaries)
    {
        var analysis = new MethodBehaviorAnalysis(method, summaries, _methods);
        analysis.Analyze();
        return analysis.ToSummary();
    }

    private static bool BehaviorEquals(MethodBehaviorSummary left, MethodBehaviorSummary right) =>
        left.Effects == right.Effects &&
        left.ReturnsFreshReference == right.ReturnsFreshReference &&
        left.ReturnsUnknownReference == right.ReturnsUnknownReference &&
        left.MutatedParameters.SetEquals(right.MutatedParameters) &&
        left.EscapingParameters.SetEquals(right.EscapingParameters) &&
        left.ReturnedParameters.SetEquals(right.ReturnedParameters);

    private sealed class MethodBehaviorAnalysis(
        MethodDefinition method,
        IReadOnlyDictionary<IrMethodId, MethodBehaviorSummary> summaries,
        MethodCatalog methods)
    {
        private readonly Dictionary<IrSymbolId, AliasValue> _aliases = [];
        private readonly HashSet<int> _mutatedParameters = [];
        private readonly HashSet<int> _escapingParameters = [];
        private readonly HashSet<int> _returnedParameters = [];
        private MethodEffects _effects;
        private bool _returnsFreshReference;
        private bool _returnsUnknownReference;
        private bool _insideDeferredBody;

        public void Analyze()
        {
            for (var index = 0; index < method.Parameters.Count; index++)
            {
                var parameter = method.Parameters[index];
                if (parameter.Type.IsReference)
                    _aliases[parameter.Symbol.Id] = AliasValue.ForParameter(index);
            }

            if (method.ExpressionBody is not null)
            {
                var result = AnalyzeExpression(method.ExpressionBody);
                RecordReturn(result, method.ReturnType);
            }
            else if (method.Body is not null)
            {
                AnalyzeStatement(method.Body);
            }
        }

        public MethodBehaviorSummary ToSummary() => new(
            _effects,
            _mutatedParameters.ToHashSet(),
            _escapingParameters.ToHashSet(),
            _returnedParameters.ToHashSet(),
            _returnsFreshReference,
            _returnsUnknownReference);

        private void AnalyzeStatement(ProceduralStatement statement)
        {
            switch (statement)
            {
                case ProceduralBlock block:
                    foreach (var child in block.Statements)
                        AnalyzeStatement(child);
                    break;
                case ProceduralDeclarationStatement declaration:
                    foreach (var variable in declaration.Declaration.Variables)
                    {
                        var value = variable.Initializer is null
                            ? AliasValue.None
                            : AnalyzeExpression(variable.Initializer);
                        if (variable.Symbol.Type.IsReference)
                            _aliases[variable.Symbol.Id] = value;
                    }
                    break;
                case ProceduralExpressionStatement expression:
                    AnalyzeExpression(expression.Expression);
                    break;
                case ProceduralIf @if:
                    AnalyzeExpression(@if.Condition);
                    var beforeIf = SnapshotAliases();
                    AnalyzeStatement(@if.Then);
                    var afterThen = SnapshotAliases();
                    RestoreAliases(beforeIf);
                    if (@if.Else is not null)
                        AnalyzeStatement(@if.Else);
                    MergeAliases(afterThen);
                    break;
                case ProceduralWhile @while:
                    AnalyzeExpression(@while.Condition);
                    var beforeWhile = SnapshotAliases();
                    AnalyzeStatement(@while.Body);
                    MergeAliases(beforeWhile);
                    break;
                case ProceduralDo @do:
                    AnalyzeStatement(@do.Body);
                    AnalyzeExpression(@do.Condition);
                    break;
                case ProceduralFor @for:
                    if (@for.Declaration is not null)
                    {
                        foreach (var variable in @for.Declaration.Variables)
                        {
                            var value = variable.Initializer is null
                                ? AliasValue.None
                                : AnalyzeExpression(variable.Initializer);
                            if (variable.Symbol.Type.IsReference)
                                _aliases[variable.Symbol.Id] = value;
                        }
                    }
                    foreach (var initializer in @for.Initializers)
                        AnalyzeExpression(initializer);
                    if (@for.Condition is not null)
                        AnalyzeExpression(@for.Condition);
                    var beforeForBody = SnapshotAliases();
                    AnalyzeStatement(@for.Body);
                    foreach (var incrementor in @for.Incrementors)
                        AnalyzeExpression(incrementor);
                    MergeAliases(beforeForBody);
                    break;
                case ProceduralForEach forEach:
                    AnalyzeExpression(forEach.SourceExpression);
                    _effects |= MethodEffects.ReadsMutableState | MethodEffects.MayThrow;
                    AnalyzeStatement(forEach.Body);
                    break;
                case ProceduralReturn @return:
                    if (@return.Expression is not null)
                        RecordReturn(AnalyzeExpression(@return.Expression), method.ReturnType);
                    break;
                case ProceduralUnsupported:
                    _effects |= MethodEffects.InvokesUnknown | MethodEffects.MayThrow;
                    break;
            }
        }

        private AliasValue AnalyzeExpression(IrExpression expression)
        {
            switch (expression)
            {
                case IrConstantExpression:
                    return AliasValue.None;
                case IrVariableExpression variable:
                    var variableAlias = _aliases.GetValueOrDefault(variable.Symbol.Id, AliasValue.None);
                    if (_insideDeferredBody)
                        _escapingParameters.UnionWith(variableAlias.Parameters);
                    return variableAlias;
                case IrThisExpression @this:
                    var thisAlias = _aliases.GetValueOrDefault(@this.Symbol.Id, AliasValue.None);
                    if (_insideDeferredBody)
                        _escapingParameters.UnionWith(thisAlias.Parameters);
                    return thisAlias;
                case IrBinaryExpression binary:
                    AnalyzeExpression(binary.Left);
                    AnalyzeExpression(binary.Right);
                    if (binary.Operator is IrBinaryOperator.Divide or IrBinaryOperator.Remainder &&
                        binary.Type.Name is not ("float" or "double"))
                        _effects |= MethodEffects.MayThrow;
                    return AliasValue.None;
                case IrUnaryExpression unary:
                    AnalyzeExpression(unary.Operand);
                    if (unary.Operator is IrUnaryOperator.PreIncrement or IrUnaryOperator.PreDecrement or
                        IrUnaryOperator.PostIncrement or IrUnaryOperator.PostDecrement)
                        RecordMutation(unary.Operand);
                    return AliasValue.None;
                case IrConversionExpression conversion:
                    var operand = AnalyzeExpression(conversion.Operand);
                    return conversion.TargetType.IsReference ? operand : AliasValue.None;
                case IrConditionalExpression conditional:
                    AnalyzeExpression(conditional.Condition);
                    return AnalyzeExpression(conditional.WhenTrue)
                        .Union(AnalyzeExpression(conditional.WhenFalse));
                case IrMemberExpression member:
                    AnalyzeExpression(member.Receiver);
                    if (member.Receiver.Type.IsReference)
                        _effects |= MethodEffects.ReadsMutableState;
                    return member.Type.IsReference
                        ? AliasValue.Unknown
                        : AliasValue.None;
                case IrElementExpression element:
                    AnalyzeExpression(element.Receiver);
                    foreach (var argument in element.Arguments)
                        AnalyzeExpression(argument);
                    _effects |= MethodEffects.ReadsMutableState | MethodEffects.MayThrow;
                    return element.Type.IsReference
                        ? AliasValue.Unknown
                        : AliasValue.None;
                case IrInvocationExpression invocation:
                    return AnalyzeInvocation(invocation);
                case IrObjectCreationExpression creation:
                    foreach (var argument in creation.Arguments)
                        AnalyzeExpression(argument);
                    foreach (var initializer in creation.Initializers)
                        AnalyzeExpression(initializer);
                    _effects |= MethodEffects.Allocates | MethodEffects.MayThrow;
                    if (creation.CreatedType.Name is "Random" or "System.Random")
                    {
                        _effects |= MethodEffects.UsesRandom;
                        if (creation.Arguments.Count == 0)
                            _effects |= MethodEffects.Nondeterministic;
                    }
                    return creation.Type.IsReference ? AliasValue.Fresh : AliasValue.None;
                case IrWithExpression withExpression:
                    AnalyzeExpression(withExpression.Receiver);
                    foreach (var initializer in withExpression.Initializers)
                        AnalyzeExpression(initializer);
                    _effects |= MethodEffects.ReadsMutableState | MethodEffects.Allocates | MethodEffects.MayThrow;
                    return withExpression.Type.IsReference ? AliasValue.Fresh : AliasValue.None;
                case IrArrayCreationExpression array:
                    if (array.Length is not null)
                        AnalyzeExpression(array.Length);
                    foreach (var element in array.Elements)
                        AnalyzeExpression(element);
                    _effects |= MethodEffects.Allocates | MethodEffects.MayThrow;
                    return AliasValue.Fresh;
                case IrInterpolatedStringExpression interpolated:
                    foreach (var interpolation in interpolated.Parts.OfType<IrInterpolation>())
                        AnalyzeExpression(interpolation.Expression);
                    return AliasValue.None;
                case IrAssignmentExpression assignment:
                    return AnalyzeAssignment(assignment);
                case IrLambdaExpression lambda:
                    // SharpSql currently stores lambda plans at compile time. Captured
                    // aliases escape into that plan even though no SQL heap allocation occurs.
                    var wasInsideDeferredBody = _insideDeferredBody;
                    _insideDeferredBody = true;
                    if (lambda.ExpressionBody is not null)
                        AnalyzeExpression(lambda.ExpressionBody);
                    if (lambda.StatementBody is not null)
                        AnalyzeStatement(lambda.StatementBody);
                    _insideDeferredBody = wasInsideDeferredBody;
                    return AliasValue.Fresh;
                case IrQueryExpression query:
                    var source = AnalyzeExpression(query.SourceExpression);
                    _escapingParameters.UnionWith(source.Parameters);
                    var wasInsideQuery = _insideDeferredBody;
                    _insideDeferredBody = true;
                    foreach (var clause in query.Clauses)
                    {
                        switch (clause)
                        {
                            case IrWhereClause where:
                                AnalyzeExpression(where.Predicate);
                                break;
                            case IrOrderClause order:
                                AnalyzeExpression(order.Key);
                                break;
                            case IrSelectClause select:
                                AnalyzeExpression(select.Projection);
                                break;
                            case IrGroupClause group:
                                AnalyzeExpression(group.Element);
                                AnalyzeExpression(group.Key);
                                break;
                        }
                    }
                    _insideDeferredBody = wasInsideQuery;
                    return AliasValue.Unknown;
                case IrUnsupportedExpression:
                    _effects |= MethodEffects.InvokesUnknown | MethodEffects.MayThrow;
                    return expression.Type.IsReference ? AliasValue.Unknown : AliasValue.None;
                default:
                    _effects |= MethodEffects.InvokesUnknown;
                    return expression.Type.IsReference ? AliasValue.Unknown : AliasValue.None;
            }
        }

        private AliasValue AnalyzeAssignment(IrAssignmentExpression assignment)
        {
            var value = AnalyzeExpression(assignment.Value);
            if (assignment.Target is IrVariableExpression variable)
            {
                if (assignment.Operator != IrAssignmentOperator.Assign)
                    AnalyzeExpression(assignment.Target);
                if (variable.Symbol.Type.IsReference && assignment.Operator == IrAssignmentOperator.Assign)
                    _aliases[variable.Symbol.Id] = value;
                return value;
            }

            AnalyzeExpression(assignment.Target);
            _effects |= MethodEffects.WritesMutableState;
            _mutatedParameters.UnionWith(AliasOrigins(assignment.Target).Parameters);
            if (assignment.Value.Type.IsReference)
                _escapingParameters.UnionWith(value.Parameters);
            return value;
        }

        private AliasValue AnalyzeInvocation(IrInvocationExpression invocation)
        {
            if (methods.TryResolve(invocation, out var callee))
            {
                var argumentExpressions = new List<IrExpression>();
                if (callee.IsInstance && invocation.Target is IrMemberExpression member)
                    argumentExpressions.Add(member.Receiver);
                argumentExpressions.AddRange(invocation.Arguments);
                var arguments = argumentExpressions.Select(AnalyzeExpression).ToArray();
                var behavior = summaries[callee.Id];
                _effects |= behavior.Effects;
                MapParameters(behavior.MutatedParameters, arguments, _mutatedParameters);
                MapParameters(behavior.EscapingParameters, arguments, _escapingParameters);

                var result = behavior.ReturnsFreshReference ? AliasValue.Fresh : AliasValue.None;
                if (behavior.ReturnsUnknownReference)
                    result = result.Union(AliasValue.Unknown);
                foreach (var parameter in behavior.ReturnedParameters)
                {
                    if (parameter < arguments.Length)
                        result = result.Union(arguments[parameter]);
                }
                return invocation.Type.IsReference ? result : AliasValue.None;
            }

            var receiverValue = invocation.Target is IrMemberExpression targetMember
                ? AnalyzeExpression(targetMember.Receiver)
                : AliasValue.None;
            var argumentValues = invocation.Arguments.Select(AnalyzeExpression).ToArray();
            var intrinsic = IntrinsicCatalog.Describe(invocation);
            _effects |= intrinsic.Effects;
            if (intrinsic.MutatesReceiver)
                _mutatedParameters.UnionWith(receiverValue.Parameters);
            if (intrinsic.MutatesArguments)
                foreach (var argument in argumentValues)
                    _mutatedParameters.UnionWith(argument.Parameters);
            if (intrinsic.ArgumentsEscape)
            {
                _escapingParameters.UnionWith(receiverValue.Parameters);
                foreach (var argument in argumentValues)
                    _escapingParameters.UnionWith(argument.Parameters);
            }
            return invocation.Type.IsReference
                ? intrinsic.ReturnsFreshReference ? AliasValue.Fresh : AliasValue.Unknown
                : AliasValue.None;
        }

        private void RecordMutation(IrExpression target)
        {
            if (target is not (IrVariableExpression or IrThisExpression))
            {
                _effects |= MethodEffects.WritesMutableState;
                _mutatedParameters.UnionWith(AliasOrigins(target).Parameters);
            }
        }

        private AliasValue AliasOrigins(IrExpression expression) => expression switch
        {
            IrVariableExpression variable => _aliases.GetValueOrDefault(variable.Symbol.Id, AliasValue.None),
            IrThisExpression @this => _aliases.GetValueOrDefault(@this.Symbol.Id, AliasValue.None),
            IrMemberExpression member => AliasOrigins(member.Receiver),
            IrElementExpression element => AliasOrigins(element.Receiver),
            IrConversionExpression conversion => AliasOrigins(conversion.Operand),
            IrConditionalExpression conditional =>
                AliasOrigins(conditional.WhenTrue).Union(AliasOrigins(conditional.WhenFalse)),
            _ => AliasValue.None
        };

        private Dictionary<IrSymbolId, AliasValue> SnapshotAliases() =>
            _aliases.ToDictionary(item => item.Key, item => item.Value);

        private void RestoreAliases(IReadOnlyDictionary<IrSymbolId, AliasValue> snapshot)
        {
            _aliases.Clear();
            foreach (var item in snapshot)
                _aliases[item.Key] = item.Value;
        }

        private void MergeAliases(IReadOnlyDictionary<IrSymbolId, AliasValue> aliases)
        {
            foreach (var item in aliases)
                _aliases[item.Key] = _aliases.TryGetValue(item.Key, out var current)
                    ? current.Union(item.Value)
                    : item.Value;
        }

        private void RecordReturn(AliasValue value, IrType returnType)
        {
            if (!returnType.IsReference)
                return;
            _returnedParameters.UnionWith(value.Parameters);
            _escapingParameters.UnionWith(value.Parameters);
            _returnsFreshReference |= value.IsFresh;
            _returnsUnknownReference |= value.IsUnknown;
        }

        private static void MapParameters(
            IEnumerable<int> parameterIndices,
            IReadOnlyList<AliasValue> arguments,
            HashSet<int> destination)
        {
            foreach (var parameter in parameterIndices)
                if (parameter < arguments.Count)
                    destination.UnionWith(arguments[parameter].Parameters);
        }
    }

    private sealed record AliasValue(
        IReadOnlySet<int> Parameters,
        bool IsFresh,
        bool IsUnknown)
    {
        public static AliasValue None { get; } = new(new HashSet<int>(), false, false);
        public static AliasValue Fresh { get; } = new(new HashSet<int>(), true, false);
        public static AliasValue Unknown { get; } = new(new HashSet<int>(), false, true);

        public static AliasValue ForParameter(int index) => new(new HashSet<int> { index }, false, false);

        public AliasValue Union(AliasValue other) => new(
            Parameters.Concat(other.Parameters).ToHashSet(),
            IsFresh || other.IsFresh,
            IsUnknown || other.IsUnknown);
    }
}
