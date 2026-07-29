namespace SharpSql;

internal sealed class MethodGraph
{
    private readonly IReadOnlyDictionary<IrMethodId, IReadOnlySet<IrMethodId>> _callees;
    private readonly IReadOnlyDictionary<IrMethodId, IReadOnlySet<IrMethodId>> _callers;
    private readonly IReadOnlyDictionary<IrMethodId, int> _callSiteCounts;
    private readonly IReadOnlyDictionary<IrMethodId, string> _names;
    private readonly IReadOnlyDictionary<string, IReadOnlyList<IrMethodId>> _idsByName;

    private MethodGraph(
        IReadOnlyDictionary<IrMethodId, IReadOnlySet<IrMethodId>> callees,
        IReadOnlyDictionary<IrMethodId, IReadOnlySet<IrMethodId>> callers,
        IReadOnlyDictionary<IrMethodId, int> callSiteCounts,
        IReadOnlySet<IrMethodId> recursiveMethods,
        IReadOnlyDictionary<IrMethodId, string> names,
        IReadOnlyDictionary<string, IReadOnlyList<IrMethodId>> idsByName)
    {
        _callees = callees;
        _callers = callers;
        _callSiteCounts = callSiteCounts;
        RecursiveMethodIds = recursiveMethods;
        _names = names;
        _idsByName = idsByName;
    }

    public IReadOnlySet<IrMethodId> RecursiveMethodIds { get; }
    public IReadOnlySet<string> RecursiveMethods => RecursiveMethodIds
        .Select(id => _names[id])
        .ToHashSet(StringComparer.Ordinal);

    public int CallSiteCount(IrMethodId methodId) =>
        _callSiteCounts.GetValueOrDefault(methodId);

    public int CallSiteCount(string methodName) =>
        Ids(methodName).Sum(CallSiteCount);

    public IReadOnlySet<IrMethodId> Callees(IrMethodId methodId) =>
        _callees.GetValueOrDefault(methodId, EmptyIds);

    public IReadOnlySet<IrMethodId> Callers(IrMethodId methodId) =>
        _callers.GetValueOrDefault(methodId, EmptyIds);

    public IReadOnlySet<string> Callees(string methodName) =>
        Ids(methodName).SelectMany(Callees).Select(id => _names[id]).ToHashSet(StringComparer.Ordinal);

    public IReadOnlySet<string> Callers(string methodName) =>
        Ids(methodName).SelectMany(Callers).Select(id => _names[id]).ToHashSet(StringComparer.Ordinal);

    public IReadOnlySet<IrMethodId> ConnectedClosure(IEnumerable<IrMethodId> roots)
    {
        var selected = roots.ToHashSet();
        var pending = new Queue<IrMethodId>(selected);
        while (pending.TryDequeue(out var method))
        {
            foreach (var adjacent in Callees(method).Concat(Callers(method)))
            {
                if (selected.Add(adjacent))
                    pending.Enqueue(adjacent);
            }
        }
        return selected;
    }

    public IReadOnlySet<string> ConnectedClosure(IEnumerable<string> roots)
    {
        return ConnectedClosure(roots.SelectMany(Ids))
            .Select(id => _names[id])
            .ToHashSet(StringComparer.Ordinal);
    }

    private IReadOnlyList<IrMethodId> Ids(string name) =>
        _idsByName.GetValueOrDefault(name, []);

    public static MethodGraph Create(
        IReadOnlyCollection<MethodDefinition> methods,
        ProceduralBlock entryPoint)
    {
        var definitions = methods.Select((method, index) => new
        {
            Method = method,
            Id = method.Id.IsNone
                ? new IrMethodId($"graph:{index}:{method.ContainingType ?? "<global>"}.{method.Name}")
                : method.Id
        }).ToArray();
        var names = definitions.ToDictionary(item => item.Id, item => item.Method.Name);
        var idsByName = definitions
            .GroupBy(item => item.Method.Name, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<IrMethodId>)group.Select(item => item.Id).ToArray(),
                StringComparer.Ordinal);
        var methodsById = definitions.ToDictionary(item => item.Id, item => item.Method);
        var callees = definitions.ToDictionary(item => item.Id, _ => new HashSet<IrMethodId>());
        var callers = definitions.ToDictionary(item => item.Id, _ => new HashSet<IrMethodId>());
        var callSiteCounts = new Dictionary<IrMethodId, int>();

        VisitStatement(entryPoint, owner: null);
        foreach (var definition in definitions)
        {
            var method = definition.Method;
            if (method.Body is not null)
                VisitStatement(method.Body, definition.Id);
            if (method.ExpressionBody is not null)
                VisitExpression(method.ExpressionBody, definition.Id);
        }

        foreach (var (caller, targets) in callees)
            foreach (var target in targets)
                callers[target].Add(caller);

        var readonlyCallees = callees.ToDictionary(
            item => item.Key,
            item => (IReadOnlySet<IrMethodId>)item.Value);
        var readonlyCallers = callers.ToDictionary(
            item => item.Key,
            item => (IReadOnlySet<IrMethodId>)item.Value);
        return new MethodGraph(
            readonlyCallees,
            readonlyCallers,
            callSiteCounts,
            FindRecursiveMethods(readonlyCallees),
            names,
            idsByName);

        void VisitStatement(ProceduralStatement statement, IrMethodId? owner)
        {
            switch (statement)
            {
                case ProceduralBlock block:
                    foreach (var child in block.Statements)
                        VisitStatement(child, owner);
                    break;
                case ProceduralDeclarationStatement declaration:
                    foreach (var variable in declaration.Declaration.Variables)
                        if (variable.Initializer is not null)
                            VisitExpression(variable.Initializer, owner);
                    break;
                case ProceduralExpressionStatement expression:
                    VisitExpression(expression.Expression, owner);
                    break;
                case ProceduralIf @if:
                    VisitExpression(@if.Condition, owner);
                    VisitStatement(@if.Then, owner);
                    if (@if.Else is not null)
                        VisitStatement(@if.Else, owner);
                    break;
                case ProceduralWhile @while:
                    VisitExpression(@while.Condition, owner);
                    VisitStatement(@while.Body, owner);
                    break;
                case ProceduralDo @do:
                    VisitStatement(@do.Body, owner);
                    VisitExpression(@do.Condition, owner);
                    break;
                case ProceduralFor @for:
                    if (@for.Declaration is not null)
                        foreach (var variable in @for.Declaration.Variables)
                            if (variable.Initializer is not null)
                                VisitExpression(variable.Initializer, owner);
                    foreach (var initializer in @for.Initializers)
                        VisitExpression(initializer, owner);
                    if (@for.Condition is not null)
                        VisitExpression(@for.Condition, owner);
                    foreach (var incrementor in @for.Incrementors)
                        VisitExpression(incrementor, owner);
                    VisitStatement(@for.Body, owner);
                    break;
                case ProceduralForEach forEach:
                    VisitExpression(forEach.SourceExpression, owner);
                    VisitStatement(forEach.Body, owner);
                    break;
                case ProceduralReturn { Expression: not null } @return:
                    VisitExpression(@return.Expression, owner);
                    break;
            }
        }

        void VisitExpression(IrExpression expression, IrMethodId? owner)
        {
            switch (expression)
            {
                case IrBinaryExpression binary:
                    VisitExpression(binary.Left, owner);
                    VisitExpression(binary.Right, owner);
                    break;
                case IrUnaryExpression unary:
                    VisitExpression(unary.Operand, owner);
                    break;
                case IrConversionExpression conversion:
                    VisitExpression(conversion.Operand, owner);
                    break;
                case IrConditionalExpression conditional:
                    VisitExpression(conditional.Condition, owner);
                    VisitExpression(conditional.WhenTrue, owner);
                    VisitExpression(conditional.WhenFalse, owner);
                    break;
                case IrMemberExpression member:
                    VisitExpression(member.Receiver, owner);
                    break;
                case IrElementExpression element:
                    VisitExpression(element.Receiver, owner);
                    foreach (var argument in element.Arguments)
                        VisitExpression(argument, owner);
                    break;
                case IrInvocationExpression invocation:
                    if (Resolve(invocation) is { } methodId)
                    {
                        callSiteCounts[methodId] = callSiteCounts.GetValueOrDefault(methodId) + 1;
                        if (owner is not null)
                            callees[owner.Value].Add(methodId);
                    }
                    VisitExpression(invocation.Target, owner);
                    foreach (var argument in invocation.Arguments)
                        VisitExpression(argument, owner);
                    break;
                case IrObjectCreationExpression creation:
                    foreach (var argument in creation.Arguments)
                        VisitExpression(argument, owner);
                    foreach (var initializer in creation.Initializers)
                        VisitExpression(initializer, owner);
                    break;
                case IrWithExpression withExpression:
                    VisitExpression(withExpression.Receiver, owner);
                    foreach (var initializer in withExpression.Initializers)
                        VisitExpression(initializer, owner);
                    break;
                case IrArrayCreationExpression array:
                    if (array.Length is not null)
                        VisitExpression(array.Length, owner);
                    foreach (var element in array.Elements)
                        VisitExpression(element, owner);
                    break;
                case IrInterpolatedStringExpression interpolated:
                    foreach (var interpolation in interpolated.Parts.OfType<IrInterpolation>())
                        VisitExpression(interpolation.Expression, owner);
                    break;
                case IrAssignmentExpression assignment:
                    VisitExpression(assignment.Target, owner);
                    VisitExpression(assignment.Value, owner);
                    break;
                case IrLambdaExpression lambda:
                    if (lambda.ExpressionBody is not null)
                        VisitExpression(lambda.ExpressionBody, owner);
                    if (lambda.StatementBody is not null)
                        VisitStatement(lambda.StatementBody, owner);
                    break;
                case IrQueryExpression query:
                    VisitExpression(query.SourceExpression, owner);
                    foreach (var clause in query.Clauses)
                    {
                        switch (clause)
                        {
                            case IrWhereClause where:
                                VisitExpression(where.Predicate, owner);
                                break;
                            case IrOrderClause order:
                                VisitExpression(order.Key, owner);
                                break;
                            case IrSelectClause select:
                                VisitExpression(select.Projection, owner);
                                break;
                            case IrGroupClause group:
                                VisitExpression(group.Element, owner);
                                VisitExpression(group.Key, owner);
                                break;
                        }
                    }
                    break;
            }
        }

        IrMethodId? Resolve(IrInvocationExpression invocation)
        {
            if (!invocation.TargetMethodId.IsNone)
                return methodsById.ContainsKey(invocation.TargetMethodId)
                    ? invocation.TargetMethodId
                    : null;
            if (invocation.MethodName is not { } name || !idsByName.TryGetValue(name, out var ids))
                return null;
            var matches = ids.Where(id =>
            {
                var method = methodsById[id];
                var argumentCount = invocation.Arguments.Count +
                    (method.IsInstance && invocation.Target is IrMemberExpression ? 1 : 0);
                return argumentCount == method.Parameters.Count;
            }).ToArray();
            return matches.Length == 1 ? matches[0] : null;
        }
    }

    private static IReadOnlySet<IrMethodId> FindRecursiveMethods(
        IReadOnlyDictionary<IrMethodId, IReadOnlySet<IrMethodId>> callees)
    {
        var recursive = new HashSet<IrMethodId>();
        foreach (var origin in callees.Keys)
            Visit(origin, origin, []);
        return recursive;

        void Visit(IrMethodId origin, IrMethodId current, HashSet<IrMethodId> path)
        {
            if (!path.Add(current))
            {
                if (current == origin)
                    recursive.UnionWith(path);
                return;
            }
            foreach (var next in callees[current])
                Visit(origin, next, new HashSet<IrMethodId>(path));
        }
    }

    private static IReadOnlySet<IrMethodId> EmptyIds { get; } = new HashSet<IrMethodId>();
}
