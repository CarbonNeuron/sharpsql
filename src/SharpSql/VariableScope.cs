namespace SharpSql;

internal abstract record VariableBinding(IrType Type)
{
    public IrSymbolId SymbolId { get; init; }
}

internal sealed record ScalarVariableBinding(string SqlName, IrType Type) : VariableBinding(Type)
{
    public SqlScalarExpression Scalar => SqlScalarExpression.Primary(SqlName, Type);
}

internal sealed record QueryVariableBinding(IrType Type, SqlLinqQueryPlan Query) : VariableBinding(Type);

internal sealed record LambdaVariableBinding(IrType Type, SqlLinqLambdaPlan Lambda) : VariableBinding(Type);

internal sealed record UnavailableVariableBinding(IrType Type) : VariableBinding(Type);

internal sealed class VariableScope(VariableScope? parent = null)
{
    private readonly Dictionary<string, VariableBinding> _bindings = new(StringComparer.Ordinal);
    private readonly Dictionary<IrSymbolId, VariableBinding> _symbolBindings = [];
    private VariableScope? _parent = parent;

    public VariableScope Child() => new(this);

    public void SetParent(VariableScope parentScope) => _parent = parentScope;

    public void Add(string sourceName, VariableBinding binding)
    {
        _bindings[sourceName] = binding;
        if (binding.SymbolId != IrSymbolId.None)
            _symbolBindings[binding.SymbolId] = binding;
    }

    public void Add(IrSymbol symbol, VariableBinding binding) =>
        Add(symbol.Name, binding with { SymbolId = symbol.Id });

    public VariableBinding? Find(string sourceName) =>
        _bindings.TryGetValue(sourceName, out var binding) ? binding : _parent?.Find(sourceName);

    public VariableBinding? Find(IrSymbol symbol) =>
        _symbolBindings.TryGetValue(symbol.Id, out var binding) ? binding :
        _parent?.Find(symbol) ?? Find(symbol.Name);
}
