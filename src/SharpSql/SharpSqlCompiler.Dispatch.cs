namespace SharpSql;

public sealed partial class SharpSqlCompiler
{
    private readonly Dictionary<(IrMethodId MethodId, IrCallDispatch Dispatch), RuntimeDispatchSlot>
        _runtimeDispatchSlots = [];
    private readonly HashSet<(IrMethodId MethodId, IrCallDispatch Dispatch)> _runtimeDispatchRequests = [];

    private void PrepareRuntimeDispatch()
    {
        foreach (var request in _runtimeDispatchRequests)
            if (_methods.TryGetValue(request.MethodId, out var method))
                AddRuntimeDispatchSlot(method, request.Dispatch);
    }

    private void AddRuntimeDispatchSlot(MethodDefinition slotMethod, IrCallDispatch dispatch)
    {
        var key = (slotMethod.Id, dispatch);
        if (_runtimeDispatchSlots.ContainsKey(key))
            return;

        var candidates = _methods.Values
            .Where(candidate => candidate.IsInstance && !candidate.IsAbstract &&
                (candidate.Body is not null || candidate.ExpressionBody is not null))
            .Where(candidate => dispatch == IrCallDispatch.Interface
                ? ImplementsInterfaceSlot(candidate, slotMethod.Id)
                : OverridesVirtualSlot(candidate, slotMethod.Id))
            .ToArray();
        var targets = new List<RuntimeDispatchTarget>();
        foreach (var runtimeType in UsedHeapTypes())
        {
            var hierarchy = HeapHierarchyBaseFirst(runtimeType);
            MethodDefinition? target = null;
            for (var index = hierarchy.Count - 1; index >= 0 && target is null; index--)
            {
                var declaringType = hierarchy[index].Name;
                target = candidates.FirstOrDefault(candidate =>
                    string.Equals(candidate.ContainingType, declaringType, StringComparison.Ordinal));
            }
            if (target is not null)
                targets.Add(new RuntimeDispatchTarget(runtimeType.Id, target));
        }

        if (targets.Count > 0)
            _runtimeDispatchSlots.Add(key, new RuntimeDispatchSlot(slotMethod, dispatch, targets));
    }

    private bool ImplementsInterfaceSlot(MethodDefinition method, IrMethodId interfaceMethodId)
    {
        for (MethodDefinition? current = method; current is not null; current = OverriddenMethod(current))
            if (current.ImplementedInterfaceMethodIds.Contains(interfaceMethodId))
                return true;
        return false;
    }

    private bool OverridesVirtualSlot(MethodDefinition method, IrMethodId virtualMethodId)
    {
        for (MethodDefinition? current = method; current is not null; current = OverriddenMethod(current))
            if (current.Id == virtualMethodId)
                return true;
        return false;
    }

    private MethodDefinition? OverriddenMethod(MethodDefinition method) =>
        !method.OverriddenMethodId.IsNone && _methods.TryGetValue(method.OverriddenMethodId, out var overridden)
            ? overridden
            : null;

    private bool TryGetRuntimeDispatch(
        IrInvocationExpression invocation,
        out RuntimeDispatchSlot slot)
    {
        if (invocation.Dispatch is not (IrCallDispatch.Virtual or IrCallDispatch.Interface))
        {
            slot = null!;
            return false;
        }

        var methodId = invocation.TargetMethodId;
        if (methodId.IsNone && _methods.TryResolve(invocation, out var method))
            methodId = method.Id;
        return _runtimeDispatchSlots.TryGetValue((methodId, invocation.Dispatch), out slot!);
    }

    private bool TryEmitRuntimeDispatchScalar(
        IrInvocationExpression invocation,
        VariableScope scope,
        IReadOnlyDictionary<string, Substitution>? substitutions,
        out SqlScalarExpression expression)
    {
        if (!TryGetRuntimeDispatch(invocation, out var slot))
        {
            expression = null!;
            return false;
        }

        var arguments = InvocationArgumentExpressions(invocation, slot.Method);
        if (arguments.Count != slot.Method.Parameters.Count || !CanEmitRuntimeDispatchScalar(slot))
        {
            expression = SqlScalarExpression.Primary(UnsupportedExpression(
                invocation.Source,
                "Set-based virtual dispatch requires expression-bodied implementations."));
            return true;
        }

        var argumentValues = arguments
            .Select(argument => EmitScalarExpression(argument, scope, substitutions))
            .ToArray();
        var receiver = argumentValues[0].Sql;
        var branches = new List<string>();
        foreach (var target in slot.Targets)
        {
            var replacements = new Dictionary<string, Substitution>(StringComparer.Ordinal);
            for (var index = 0; index < target.Method.Parameters.Count; index++)
            {
                var parameter = target.Method.Parameters[index];
                replacements[parameter.Name] = new Substitution(
                    SqlScalarExpression.Primary(argumentValues[index].Sql, parameter.Type));
            }
            var value = EmitScalarExpression(target.Method.PureExpression!, scope, replacements);
            branches.Add($"WHEN {target.RuntimeTypeId} THEN {value.Sql}");
        }

        expression = SqlScalarExpression.Primary(
            $"CASE (SELECT __type_id FROM {HeapObjects} WHERE {HeapObjectExecutionFilter()}__id = {receiver}) {string.Join(" ", branches)} END",
            invocation.Type);
        return true;
    }

    private bool CanEmitRuntimeDispatchScalar(IrInvocationExpression invocation) =>
        TryGetRuntimeDispatch(invocation, out var slot) && CanEmitRuntimeDispatchScalar(slot);

    private static bool CanEmitRuntimeDispatchScalar(RuntimeDispatchSlot slot) =>
        slot.Targets.All(target => target.Method.PureExpression is not null &&
            target.Method.Behavior.IsSideEffectFree);

    private sealed record RuntimeDispatchSlot(
        MethodDefinition Method,
        IrCallDispatch Dispatch,
        IReadOnlyList<RuntimeDispatchTarget> Targets);

    private sealed record RuntimeDispatchTarget(int RuntimeTypeId, MethodDefinition Method);
}
