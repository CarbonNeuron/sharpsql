namespace SharpSql.Cli;

/// <summary>Identifies the stage that produced a parity failure.</summary>
public enum ParityFailureCategory
{
    Compilation,
    Transpilation,
    Runtime
}

/// <summary>Identifies a stage in a parity verification run.</summary>
public enum ParityStage
{
    Parsing,
    SqlGenerated,
    EvaluatingCSharp,
    StartingSqlServer,
    EvaluatingSqlServer
}

/// <summary>Reports progress through a parity verification stage.</summary>
public sealed record ParityStageUpdate(ParityStage Stage, int? SqlLineCount = null);

/// <summary>Describes a request to compare C# and generated SQL execution.</summary>
public sealed record ParityRunRequest(
    string InputPath,
    string? Source,
    string? EntryPoint,
    string Configuration,
    string? TargetFramework,
    string SqlServerImage,
    int CommandTimeoutSeconds,
    bool KeepContainer,
    bool Debug = false,
    bool Profile = false)
{
    public bool IsProject => Source is null;
}

/// <summary>Describes a failure observed during parity verification.</summary>
public sealed record ParityFailure(ParityFailureCategory Category, string Type, string Message, int? Code = null);

/// <summary>Captures output and an optional failure from one side of parity verification.</summary>
public sealed record ParityOutcome(string StandardOutput, ParityFailure? Failure);

/// <summary>Contains SQL plan and SharpSql runtime diagnostics.</summary>
public sealed record ParityDebugInfo(
    int PlanStatementCount,
    int PlanOperatorCount,
    int MaximumPlanDepth,
    double EstimatedSubtreeCost,
    long CompileTimeMilliseconds,
    long CompileMemoryKilobytes,
    long HeapObjectsAllocated,
    long IndexedItemsAllocated,
    long DictionaryEntriesAllocated);

/// <summary>Contains timing samples collected during parity verification.</summary>
public sealed record ParityProfile(
    int WarmupRuns,
    IReadOnlyList<TimeSpan> CSharpSamples,
    IReadOnlyList<TimeSpan> SqlServerSamples);

/// <summary>Contains the C# and SQL Server outcomes of parity verification.</summary>
public sealed record ParityRunResult(
    ParityOutcome CSharp,
    ParityOutcome SqlServer,
    string GeneratedSql,
    ParityDebugInfo? DebugInfo = null,
    ParityProfile? Profile = null)
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

    /// <summary>Counts the lines in a generated SQL string.</summary>
    public static int CountLines(string value)
    {
        if (value.Length == 0)
            return 0;
        var lineCount = value.Count(character => character == '\n');
        return value[^1] == '\n' ? lineCount : lineCount + 1;
    }
}

/// <summary>Runs C# and generated SQL to compare their observable outcomes.</summary>
public interface IParityRunner
{
    /// <summary>Runs parity verification and reports optional stage updates.</summary>
    Task<ParityRunResult> RunAsync(
        ParityRunRequest request,
        Action<ParityStageUpdate>? reportStage,
        CancellationToken cancellationToken);
}
