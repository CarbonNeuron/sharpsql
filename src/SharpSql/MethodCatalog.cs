namespace SharpSql;

internal sealed class MethodCatalog
{
    private readonly Dictionary<IrMethodId, MethodDefinition> _byId = [];
    private readonly Dictionary<string, List<IrMethodId>> _idsByName = new(StringComparer.Ordinal);
    private int _nextSyntheticId;

    public int Count => _byId.Count;
    public IEnumerable<MethodDefinition> Values => _byId.Values;
    public IReadOnlyDictionary<IrMethodId, MethodDefinition> ById => _byId;

    public bool TryAdd(MethodDefinition method, out MethodDefinition stored)
    {
        stored = method.Id.IsNone
            ? method with
            {
                Id = new IrMethodId($"ir:{method.ContainingType ?? "<global>"}.{method.Name}#{++_nextSyntheticId}")
            }
            : method;
        if (!_byId.TryAdd(stored.Id, stored))
            return false;
        if (!_idsByName.TryGetValue(stored.Name, out var ids))
        {
            ids = [];
            _idsByName.Add(stored.Name, ids);
        }
        ids.Add(stored.Id);
        return true;
    }

    public void Replace(MethodDefinition method)
    {
        if (method.Id.IsNone || !_byId.ContainsKey(method.Id))
            throw new InvalidOperationException($"Method '{method.Name}' is not registered.");
        _byId[method.Id] = method;
    }

    public bool TryGetValue(IrMethodId id, out MethodDefinition method)
    {
        if (!id.IsNone && _byId.TryGetValue(id, out method!))
            return true;
        method = null!;
        return false;
    }

    public bool TryGetValue(string name, out MethodDefinition method)
    {
        if (_idsByName.TryGetValue(name, out var ids) && ids.Count == 1)
        {
            method = _byId[ids[0]];
            return true;
        }
        method = null!;
        return false;
    }

    public bool TryResolve(IrInvocationExpression invocation, out MethodDefinition method)
    {
        if (!invocation.TargetMethodId.IsNone)
            return TryGetValue(invocation.TargetMethodId, out method);
        if (invocation.MethodName is not { } name || !_idsByName.TryGetValue(name, out var ids))
        {
            method = null!;
            return false;
        }
        var candidates = ids
            .Select(id => _byId[id])
            .Where(candidate => InvocationArgumentCount(candidate, invocation) == candidate.Parameters.Count)
            .ToArray();
        if (candidates.Length == 1)
        {
            method = candidates[0];
            return true;
        }
        method = null!;
        return false;
    }

    public IReadOnlyList<MethodDefinition> FindByName(string name) =>
        _idsByName.TryGetValue(name, out var ids)
            ? ids.Select(id => _byId[id]).ToArray()
            : [];

    private static int InvocationArgumentCount(MethodDefinition method, IrInvocationExpression invocation) =>
        invocation.Arguments.Count +
        (method.IsInstance && invocation.Target is IrMemberExpression ? 1 : 0);
}
