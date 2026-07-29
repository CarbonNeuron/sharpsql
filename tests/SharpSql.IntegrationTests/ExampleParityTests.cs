using Xunit;

namespace SharpSql.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public sealed class ExampleParityTests(SqlServerFixture sqlServer)
{
    public static IEnumerable<object[]> SuccessCases() => ParityCases.Discover("success");

    public static IEnumerable<object[]> RuntimeExceptionCases() => ParityCases.Discover("runtime-exceptions");

    public static IEnumerable<object[]> DiagnosticCases() => ParityCases.Discover("diagnostics");

    [Theory]
    [MemberData(nameof(SuccessCases))]
    public async Task SuccessfulProgramsMatchCSharp(string casePath)
    {
        var testCase = await ParityCase.LoadAsync(casePath, TestContext.Current.CancellationToken);
        var csharp = await ParityHarness.ExecuteCSharpAsync(testCase);
        var sql = await ParityHarness.ExecuteSqlAsync(
            testCase,
            sqlServer.ConnectionString,
            TestContext.Current.CancellationToken);

        Assert.True(
            csharp.Failure is null &&
            sql.Outcome.Failure is null &&
            string.Equals(csharp.StandardOutput, sql.Outcome.StandardOutput, StringComparison.Ordinal) &&
            string.Equals(csharp.ReturnValue, sql.Outcome.ReturnValue, StringComparison.Ordinal),
            ParityHarness.FormatComparisonFailure(testCase, csharp, sql));
    }

    [Theory]
    [MemberData(nameof(RuntimeExceptionCases))]
    public async Task RuntimeExceptionsMatchCSharp(string casePath)
    {
        var testCase = await ParityCase.LoadAsync(casePath, TestContext.Current.CancellationToken);
        var expectedException = testCase.RequiredDirective("sharpsql-expect-exception");
        var csharp = await ParityHarness.ExecuteCSharpAsync(testCase);
        var sql = await ParityHarness.ExecuteSqlAsync(
            testCase,
            sqlServer.ConnectionString,
            TestContext.Current.CancellationToken);

        Assert.True(
            csharp.Failure is { Category: FailureCategory.Runtime } csharpFailure &&
            sql.Outcome.Failure is { Category: FailureCategory.Runtime } sqlFailure &&
            csharpFailure.Type == expectedException &&
            sqlFailure.Type == expectedException &&
            string.Equals(csharp.StandardOutput, sql.Outcome.StandardOutput, StringComparison.Ordinal),
            ParityHarness.FormatComparisonFailure(testCase, csharp, sql, expectedException));
    }

    [Theory]
    [MemberData(nameof(DiagnosticCases))]
    public async Task UnsupportedProgramsProduceExpectedDiagnostics(string casePath)
    {
        var testCase = await ParityCase.LoadAsync(casePath, TestContext.Current.CancellationToken);
        var expectedCodes = testCase.RequiredDirective("sharpsql-expect-diagnostics")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var csharpDiagnostics = ParityHarness.GetCSharpCompilationErrors(testCase);
        var result = new SharpSqlCompiler().Transpile(testCase.Source);
        var actualCodes = result.Diagnostics
            .Select(diagnostic => diagnostic.Code)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            csharpDiagnostics.Count == 0 &&
            !result.Success &&
            expectedCodes.SequenceEqual(actualCodes, StringComparer.Ordinal),
            ParityHarness.FormatDiagnosticFailure(testCase, expectedCodes, actualCodes, csharpDiagnostics, result.Diagnostics));
    }
}
