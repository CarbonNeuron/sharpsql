namespace SharpSql.Cli;

public static class CliArgumentRouter
{
    private static readonly HashSet<string> RootArguments = new(StringComparer.OrdinalIgnoreCase)
    {
        "transpile",
        "conformance",
        "init",
        "run",
        "verify",
        "--help",
        "-h",
        "--version",
        "-v"
    };

    public static string[] Route(IReadOnlyList<string> arguments)
    {
        if (arguments.Count > 0 && RootArguments.Contains(arguments[0]))
            return arguments.ToArray();

        var routed = new string[arguments.Count + 1];
        routed[0] = "transpile";
        for (var index = 0; index < arguments.Count; index++)
            routed[index + 1] = arguments[index];
        return routed;
    }
}
