namespace SharpSql;

internal sealed record VariableBinding(string SqlName, CSharpType Type);

internal sealed class VariableScope(VariableScope? parent = null)
{
    private readonly Dictionary<string, VariableBinding> _bindings = new(StringComparer.Ordinal);

    public VariableScope Child() => new(this);

    public void Add(string sourceName, VariableBinding binding) => _bindings[sourceName] = binding;

    public VariableBinding? Find(string sourceName) =>
        _bindings.TryGetValue(sourceName, out var binding) ? binding : parent?.Find(sourceName);
}
