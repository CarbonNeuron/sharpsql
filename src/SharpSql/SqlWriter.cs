using System.Text;

namespace SharpSql;

internal sealed class SqlWriter
{
    private readonly StringBuilder _builder = new();
    private int _indent;

    public void Line(string text = "")
    {
        if (text.Length > 0)
            _builder.Append(' ', _indent * 4).Append(text);
        _builder.AppendLine();
    }

    public IDisposable Indent()
    {
        _indent++;
        return new Indentation(this);
    }

    public override string ToString() => _builder.ToString().TrimEnd() + Environment.NewLine;

    private sealed class Indentation(SqlWriter writer) : IDisposable
    {
        public void Dispose() => writer._indent--;
    }
}
