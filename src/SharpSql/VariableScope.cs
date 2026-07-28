namespace SharpSql;

internal sealed record VariableBinding(
    string SqlName,
    CSharpType Type,
    LinqQueryPlan? Query = null,
    LinqLambdaPlan? Lambda = null)
{
    public SqlScalarExpression Scalar => SqlScalarExpression.Primary(SqlName, Type);
}

internal sealed class VariableScope(VariableScope? parent = null)
{
    private readonly Dictionary<string, VariableBinding> _bindings = new(StringComparer.Ordinal);
    private VariableScope? _parent = parent;

    public VariableScope Child() => new(this);

    public void SetParent(VariableScope parentScope) => _parent = parentScope;

    public void Add(string sourceName, VariableBinding binding) => _bindings[sourceName] = binding;

    public VariableBinding? Find(string sourceName) =>
        _bindings.TryGetValue(sourceName, out var binding) ? binding : _parent?.Find(sourceName);
}
