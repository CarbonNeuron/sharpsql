namespace SharpSql.Cli;

public enum ParityFailureCategory
{
    Compilation,
    Transpilation,
    Runtime
}

public enum ParityStage
{
    Parsing,
    SqlGenerated,
    EvaluatingCSharp,
    StartingSqlServer,
    EvaluatingSqlServer
}

public sealed record ParityStageUpdate(ParityStage Stage, int? SqlLineCount = null);

public sealed record ParityRunRequest(
    string InputPath,
    string? Source,
    string? EntryPoint,
    string Configuration,
    string? TargetFramework,
    string SqlServerImage,
    int CommandTimeoutSeconds,
    bool KeepContainer)
{
    public bool IsProject => Source is null;
}

public sealed record ParityFailure(ParityFailureCategory Category, string Type, string Message, int? Code = null);

public sealed record ParityOutcome(string StandardOutput, ParityFailure? Failure);

public sealed record ParityRunResult(
    ParityOutcome CSharp,
    ParityOutcome SqlServer,
    string GeneratedSql)
{
    public int GeneratedSqlLineCount => CountLines(GeneratedSql);

    public bool Matches =>
        string.Equals(CSharp.StandardOutput, SqlServer.StandardOutput, StringComparison.Ordinal) &&
        (CSharp.Failure, SqlServer.Failure) switch
        {
            (null, null) => true,
            ({ Category: ParityFailureCategory.Runtime } left, { Category: ParityFailureCategory.Runtime } right) =>
                string.Equals(left.Type, right.Type, StringComparison.Ordinal),
            _ => false
        };

    public static int CountLines(string value)
    {
        if (value.Length == 0)
            return 0;
        var lineCount = value.Count(character => character == '\n');
        return value[^1] == '\n' ? lineCount : lineCount + 1;
    }
}

public interface IParityRunner
{
    Task<ParityRunResult> RunAsync(
        ParityRunRequest request,
        Action<ParityStageUpdate>? reportStage,
        CancellationToken cancellationToken);
}
