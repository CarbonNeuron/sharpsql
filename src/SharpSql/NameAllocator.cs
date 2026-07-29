using System.Text.RegularExpressions;

namespace SharpSql;

internal sealed class NameAllocator
{
    private static readonly Regex UnsafeNamePattern = new("[^A-Za-z0-9_]", RegexOptions.Compiled);
    private readonly HashSet<string> _used = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _usedLabels = new(StringComparer.OrdinalIgnoreCase);

    public string Allocate(string preferred)
    {
        var safe = UnsafeNamePattern.Replace(preferred, "_");
        if (safe.Length == 0 || char.IsDigit(safe[0]))
            safe = "_" + safe;

        var candidate = "@" + safe;
        for (var suffix = 2; !_used.Add(candidate); suffix++)
            candidate = $"@{safe}_{suffix}";
        return candidate;
    }

    public string AllocateLabel(string preferred)
    {
        var safe = UnsafeNamePattern.Replace(preferred, "_");
        if (safe.Length == 0 || char.IsDigit(safe[0]))
            safe = "_" + safe;

        var candidate = "__sharpsql_" + safe;
        for (var suffix = 2; !_usedLabels.Add(candidate); suffix++)
            candidate = $"__sharpsql_{safe}_{suffix}";
        return candidate;
    }
}
