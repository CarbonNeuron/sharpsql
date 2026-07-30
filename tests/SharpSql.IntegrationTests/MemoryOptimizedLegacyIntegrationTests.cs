using System.Diagnostics;
using Microsoft.Data.SqlClient;
using SharpSql.SqlServer;
using Xunit;

namespace SharpSql.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public sealed class MemoryOptimizedLegacyIntegrationTests(SqlServerFixture sqlServer)
{
    private static readonly TranspileOptions MemoryOptimizedOptions = new()
    {
        RuntimeStorage = RuntimeStorageKind.MemoryOptimized
    };

    public static IEnumerable<object[]> SuccessCases() => ParityCases.Discover("success");

    public static IEnumerable<object[]> RuntimeExceptionCases() => ParityCases.Discover("runtime-exceptions");

    [Theory]
    [MemberData(nameof(SuccessCases))]
    public async Task SuccessfulProgramsRetainCSharpParity(string casePath)
    {
        var testCase = await ParityCase.LoadAsync(casePath, TestContext.Current.CancellationToken);
        var csharp = await ParityHarness.ExecuteCSharpAsync(testCase);
        var connectionString = await sqlServer.GetMemoryOptimizedConnectionStringAsync(
            TestContext.Current.CancellationToken);
        var sql = await ParityHarness.ExecuteSqlAsync(
            testCase,
            connectionString,
            TestContext.Current.CancellationToken,
            MemoryOptimizedOptions);

        Assert.True(
            csharp.Failure is null &&
            sql.Outcome.Failure is null &&
            string.Equals(csharp.StandardOutput, sql.Outcome.StandardOutput, StringComparison.Ordinal) &&
            string.Equals(csharp.ReturnValue, sql.Outcome.ReturnValue, StringComparison.Ordinal),
            ParityHarness.FormatComparisonFailure(testCase, csharp, sql));
    }

    [Theory]
    [MemberData(nameof(RuntimeExceptionCases))]
    public async Task RuntimeFailuresRetainCSharpParity(string casePath)
    {
        var testCase = await ParityCase.LoadAsync(casePath, TestContext.Current.CancellationToken);
        var expectedException = testCase.RequiredDirective("sharpsql-expect-exception");
        var csharp = await ParityHarness.ExecuteCSharpAsync(testCase);
        var connectionString = await sqlServer.GetMemoryOptimizedConnectionStringAsync(
            TestContext.Current.CancellationToken);
        var sql = await ParityHarness.ExecuteSqlAsync(
            testCase,
            connectionString,
            TestContext.Current.CancellationToken,
            MemoryOptimizedOptions);

        Assert.True(
            csharp.Failure is { Category: FailureCategory.Runtime } csharpFailure &&
            sql.Outcome.Failure is { Category: FailureCategory.Runtime } sqlFailure &&
            csharpFailure.Type == expectedException &&
            sqlFailure.Type == expectedException &&
            string.Equals(csharp.StandardOutput, sql.Outcome.StandardOutput, StringComparison.Ordinal),
            ParityHarness.FormatComparisonFailure(testCase, csharp, sql, expectedException));
    }

    [Fact]
    [Trait("Category", "Performance")]
    public async Task MeasuresRecursiveVmAgainstTempTables()
    {
        const string source = """
            int Fibonacci(int value)
            {
                if (value < 2)
                    return value;
                return Fibonacci(value - 1) + Fibonacci(value - 2);
            }

            Console.WriteLine(Fibonacci(12));
            """;
        const int iterations = 20;

        var ephemeral = Compile(source, RuntimeStorageKind.Ephemeral);
        var memoryOptimized = Compile(source, RuntimeStorageKind.MemoryOptimized);
        var connectionString = await sqlServer.GetMemoryOptimizedConnectionStringAsync(
            TestContext.Current.CancellationToken);
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);

        var ephemeralValidation = await ExecuteAsync(connection, ephemeral);
        var memoryValidation = await ExecuteAsync(connection, memoryOptimized);
        Assert.True(ephemeralValidation.Success, ephemeralValidation.ErrorMessage);
        Assert.True(memoryValidation.Success, memoryValidation.ErrorMessage);
        Assert.Contains("144", ephemeralValidation.Messages);
        Assert.Equal(ephemeralValidation.Messages, memoryValidation.Messages);

        await ExecuteAsync(connection, ephemeral);
        await ExecuteAsync(connection, memoryOptimized);
        var ephemeralSamples = new List<TimeSpan>(iterations);
        var memorySamples = new List<TimeSpan>(iterations);
        for (var iteration = 0; iteration < iterations; iteration++)
        {
            if (iteration % 2 == 0)
            {
                ephemeralSamples.Add(await TimeAsync(connection, ephemeral));
                memorySamples.Add(await TimeAsync(connection, memoryOptimized));
            }
            else
            {
                memorySamples.Add(await TimeAsync(connection, memoryOptimized));
                ephemeralSamples.Add(await TimeAsync(connection, ephemeral));
            }
        }

        var ephemeralMedian = Median(ephemeralSamples);
        var memoryMedian = Median(memorySamples);
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"recursive Fibonacci(12), temp tables: {ephemeralMedian.TotalMilliseconds:F3} ms median");
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"recursive Fibonacci(12), memory optimized: {memoryMedian.TotalMilliseconds:F3} ms median");
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"memory/temp ratio: {memoryMedian.TotalMilliseconds / ephemeralMedian.TotalMilliseconds:F3}x");

        Assert.All(ephemeralSamples, sample => Assert.True(sample > TimeSpan.Zero));
        Assert.All(memorySamples, sample => Assert.True(sample > TimeSpan.Zero));
    }

    [Fact]
    public async Task ConcurrentExecutionsKeepVmStateIsolated()
    {
        const string source = """
            int Sum(int value)
            {
                if (value == 0)
                    return 0;
                return value + Sum(value - 1);
            }

            Console.WriteLine(Sum(20));
            """;
        var sql = Compile(source, RuntimeStorageKind.MemoryOptimized);
        var connectionString = await sqlServer.GetMemoryOptimizedConnectionStringAsync(
            TestContext.Current.CancellationToken);

        var executions = Enumerable.Range(0, 8).Select(async _ =>
        {
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            return await ExecuteAsync(connection, sql);
        });
        var results = await Task.WhenAll(executions);

        Assert.All(results, result =>
        {
            Assert.True(result.Success, result.ErrorMessage);
            Assert.Equal(["210"], result.Messages);
        });
    }

    [Fact]
    public async Task ConcurrentExecutionsKeepHeapObjectHeadersIsolatedAndReclaimThem()
    {
        const string source = """
            var values = new List<int> { 2, 3, 5 };
            values.Add(7);
            Console.WriteLine(values.Count);
            Console.WriteLine(values[0] + values[3]);
            """;
        var sql = Compile(source, RuntimeStorageKind.MemoryOptimized);
        var connectionString = await sqlServer.GetMemoryOptimizedConnectionStringAsync(
            TestContext.Current.CancellationToken);

        var executions = Enumerable.Range(0, 8).Select(async _ =>
        {
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            return await ExecuteAsync(connection, sql);
        });
        var results = await Task.WhenAll(executions);

        Assert.All(results, result =>
        {
            Assert.True(result.Success, result.ErrorMessage);
            Assert.Equal(["4", "9"], result.Messages);
        });

        await using var verification = new SqlConnection(connectionString);
        await verification.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = verification.CreateCommand();
        command.CommandText = "SELECT COUNT_BIG(*) FROM [SharpSql].[__sharpsql_memory_heap_objects_ephemeral_v1];";
        var remaining = Convert.ToInt64(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken));
        Assert.Equal(0, remaining);
    }

    private static string Compile(string source, RuntimeStorageKind runtimeStorage)
    {
        var result = new SharpSqlCompiler().Transpile(
            source,
            new TranspileOptions { RuntimeStorage = runtimeStorage });
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        return result.Sql;
    }

    private static async Task<TimeSpan> TimeAsync(SqlConnection connection, string sql)
    {
        var started = Stopwatch.GetTimestamp();
        var result = await ExecuteAsync(connection, sql);
        Assert.True(result.Success, result.ErrorMessage);
        return Stopwatch.GetElapsedTime(started);
    }

    private static Task<SqlBatchExecutionResult> ExecuteAsync(SqlConnection connection, string sql) =>
        SqlBatchExecutor.ExecuteAsync(
            connection,
            sql,
            60,
            TestContext.Current.CancellationToken);

    private static TimeSpan Median(IEnumerable<TimeSpan> samples)
    {
        var ordered = samples.Order().ToArray();
        var middle = ordered.Length / 2;
        return ordered.Length % 2 == 0
            ? (ordered[middle - 1] + ordered[middle]) / 2
            : ordered[middle];
    }
}
