namespace SharpSql;

internal static class SqlIdentifier
{
    internal const int MaximumLength = 128;

    internal static string Validate(string value, string parameterName)
    {
        if (value is null)
            throw new ArgumentNullException(parameterName);
        if (value.Length == 0 || string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("SQL identifiers cannot be empty or whitespace.", parameterName);
        if (value.Length > MaximumLength)
            throw new ArgumentException($"SQL identifiers cannot exceed {MaximumLength} characters.", parameterName);
        if (!string.Equals(value, value.Trim(), StringComparison.Ordinal))
            throw new ArgumentException("SQL identifiers cannot start or end with whitespace.", parameterName);
        if (value.Any(character => char.IsControl(character) || char.IsSurrogate(character)))
            throw new ArgumentException("SQL identifiers cannot contain control or UTF-16 surrogate characters.", parameterName);
        return value;
    }

    internal static string Quote(string value, string parameterName) =>
        $"[{Validate(value, parameterName).Replace("]", "]]", StringComparison.Ordinal)}]";

    internal static string UnicodeLiteral(string value)
    {
        if (value is null)
            throw new ArgumentNullException(nameof(value));
        return $"N'{value.Replace("'", "''", StringComparison.Ordinal)}'";
    }

    internal static string Qualified(string schemaName, string objectName) =>
        $"{Quote(schemaName, nameof(schemaName))}.{Quote(objectName, nameof(objectName))}";
}
