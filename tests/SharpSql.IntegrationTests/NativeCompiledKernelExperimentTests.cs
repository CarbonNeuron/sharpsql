using System.Diagnostics;
using Microsoft.Data.SqlClient;
using Xunit;

namespace SharpSql.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public sealed class NativeCompiledKernelExperimentTests(SqlServerFixture sqlServer)
{
    private const string ProcedureName = "[SharpSql].[NativeAccumulatorKernelV1]";

    [Fact]
    public async Task CompilerExtractsSupportedPureLoopMethodIntoNativeKernel()
    {
        const string source = """
            long Accumulate(int iterations, long seed)
            {
                long result = seed;
                int index = 0;
                while (index < iterations)
                {
                    result = (result + index) % 2147483647;
                    index++;
                }
                return result;
            }

            long value = Accumulate(100000, 7);
            Console.WriteLine(value);
            """;
        var compilation = new SharpSqlCompiler().Transpile(
            source,
            new TranspileOptions
            {
                RuntimeStorage = RuntimeStorageKind.MemoryOptimized,
                EnableNativeKernels = true
            });

        Assert.True(compilation.Success, string.Join(Environment.NewLine, compilation.Diagnostics));
        Assert.Contains("WITH NATIVE_COMPILATION, SCHEMABINDING", compilation.Sql, StringComparison.Ordinal);
        Assert.Contains("@__result =", compilation.Sql, StringComparison.Ordinal);
        Assert.DoesNotContain("WHILE @index", compilation.Sql, StringComparison.Ordinal);

        var connectionString = await sqlServer.GetMemoryOptimizedConnectionStringAsync(
            TestContext.Current.CancellationToken);
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = 60;
        command.CommandText = compilation.Sql;
        var messages = new List<string>();
        connection.InfoMessage += (_, args) =>
        {
            foreach (SqlError error in args.Errors)
                if (error.Class == 0)
                    messages.Add(error.Message);
        };
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);

        Assert.Equal([Expected(100_000, 7).ToString(System.Globalization.CultureInfo.InvariantCulture)], messages);
    }

    [Fact]
    public async Task CatalogsPreviewsAndSafelyRemovesUnusedCompiledKernels()
    {
        const string source = """
            int Sum(int count)
            {
                int value = 0;
                int index = 0;
                while (index < count)
                {
                    value += index;
                    index++;
                }
                return value;
            }

            int result = Sum(10);
            Console.WriteLine(result);
            """;
        var compilation = new SharpSqlCompiler().Transpile(
            source,
            new TranspileOptions
            {
                RuntimeStorage = RuntimeStorageKind.MemoryOptimized,
                EnableNativeKernels = true
            });
        Assert.True(compilation.Success, string.Join(Environment.NewLine, compilation.Diagnostics));

        var connectionString = await sqlServer.GetMemoryOptimizedConnectionStringAsync(
            TestContext.Current.CancellationToken);
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = 120;
        command.CommandText = compilation.Sql;
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);

        var kernelName = System.Text.RegularExpressions.Regex.Match(
            compilation.Sql,
            @"NativeKernel_[0-9a-f]{32}",
            System.Text.RegularExpressions.RegexOptions.CultureInvariant).Value;
        Assert.NotEmpty(kernelName);
        command.CommandText = SharpSqlNativeKernelRuntime.GenerateStatusSql("SharpSql");
        await using (var status = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken))
        {
            var found = false;
            while (await status.ReadAsync(TestContext.Current.CancellationToken))
            {
                if (!string.Equals(status.GetString(0), kernelName, StringComparison.Ordinal))
                    continue;
                Assert.True(status.GetBoolean(3));
                found = true;
            }
            Assert.True(found, $"Kernel {kernelName} was not returned by the status query.");
        }

        command.CommandText = "UPDATE [SharpSql].[NativeKernelCatalog] SET [LastUsedAtUtc] = DATEADD(DAY, -2, SYSUTCDATETIME()) WHERE [KernelName] = @kernelName;";
        command.Parameters.AddWithValue("@kernelName", kernelName);
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        command.Parameters.Clear();

        command.CommandText = SharpSqlNativeKernelRuntime.GenerateCleanupSql(
            "SharpSql",
            TimeSpan.FromMinutes(1),
            dryRun: true);
        await using (var preview = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken))
        {
            Assert.True(await preview.ReadAsync(TestContext.Current.CancellationToken));
            Assert.Equal(kernelName, preview.GetString(0));
            Assert.False(preview.GetBoolean(1));
        }

        command.CommandText = SharpSqlNativeKernelRuntime.GenerateCleanupSql(
            "SharpSql",
            TimeSpan.FromMinutes(1));
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        command.CommandText = "SELECT COUNT_BIG(*) FROM [SharpSql].[NativeKernelCatalog] WHERE [KernelName] = @kernelName;";
        command.Parameters.AddWithValue("@kernelName", kernelName);
        Assert.Equal(0L, (long)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!);
        command.CommandText = "SELECT OBJECT_ID(N'[SharpSql].' + QUOTENAME(@kernelName), N'P');";
        Assert.Equal(DBNull.Value, await command.ExecuteScalarAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    [Trait("Category", "Performance")]
    public async Task MeasuresNativeScalarKernelCalledByInterpretedWrapper()
    {
        const int iterations = 100_000;
        const int samples = 30;
        var connectionString = await sqlServer.GetMemoryOptimizedConnectionStringAsync(
            TestContext.Current.CancellationToken);
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await ProvisionKernelAsync(connection);

        var interpretedSql = WrapperSql(InterpretedLoop(iterations));
        var nativeSql = WrapperSql(NativeCall(iterations));
        var expected = Expected(iterations, seed: 7);
        Assert.Equal(expected, await ExecuteScalarAsync(connection, interpretedSql));
        Assert.Equal(expected, await ExecuteScalarAsync(connection, nativeSql));

        await ExecuteScalarAsync(connection, interpretedSql);
        await ExecuteScalarAsync(connection, nativeSql);
        var interpretedSamples = new List<TimeSpan>(samples);
        var nativeSamples = new List<TimeSpan>(samples);
        for (var sample = 0; sample < samples; sample++)
        {
            if (sample % 2 == 0)
            {
                interpretedSamples.Add(await TimeAsync(connection, interpretedSql));
                nativeSamples.Add(await TimeAsync(connection, nativeSql));
            }
            else
            {
                nativeSamples.Add(await TimeAsync(connection, nativeSql));
                interpretedSamples.Add(await TimeAsync(connection, interpretedSql));
            }
        }

        var interpretedMedian = Median(interpretedSamples);
        var nativeMedian = Median(nativeSamples);
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"100,000-step interpreted loop: {interpretedMedian.TotalMilliseconds:F3} ms median");
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"100,000-step native kernel: {nativeMedian.TotalMilliseconds:F3} ms median");
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"native/interpreted ratio: {nativeMedian.TotalMilliseconds / interpretedMedian.TotalMilliseconds:F3}x");

        Assert.All(interpretedSamples, value => Assert.True(value > TimeSpan.Zero));
        Assert.All(nativeSamples, value => Assert.True(value > TimeSpan.Zero));
    }

    private static async Task ProvisionKernelAsync(SqlConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandTimeout = 120;
        command.CommandText = $"""
            DROP PROCEDURE IF EXISTS {ProcedureName};
            EXEC(N'
            CREATE PROCEDURE {ProcedureName}
                @iterations INT,
                @seed BIGINT,
                @result BIGINT OUTPUT
            WITH NATIVE_COMPILATION, SCHEMABINDING, EXECUTE AS OWNER
            AS
            BEGIN ATOMIC WITH
            (
                TRANSACTION ISOLATION LEVEL = SNAPSHOT,
                LANGUAGE = N''us_english''
            )
                DECLARE @index INT = 0;
                SET @result = @seed;
                WHILE @index < @iterations
                BEGIN
                    SET @result = (@result + @index) % 2147483647;
                    SET @index = @index + 1;
                END;
                RETURN 0;
            END;');
            """;
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    private static string WrapperSql(string body) => $$"""
        SET NOCOUNT ON;
        CREATE TABLE #state ([value] BIGINT NOT NULL);
        INSERT INTO #state ([value]) VALUES (7);
        DECLARE @value BIGINT = (SELECT [value] FROM #state);
        {{body}}
        UPDATE #state SET [value] = @value;
        SELECT [value] FROM #state;
        DROP TABLE #state;
        """;

    private static string InterpretedLoop(int iterations) => $$"""
        DECLARE @index INT = 0;
        WHILE @index < {{iterations}}
        BEGIN
            SET @value = (@value + @index) % 2147483647;
            SET @index = @index + 1;
        END;
        """;

    private static string NativeCall(int iterations) => $$"""
        DECLARE @status INT;
        EXEC @status = {{ProcedureName}}
            @iterations = {{iterations}},
            @seed = @value,
            @result = @value OUTPUT;
        IF @status <> 0 THROW 51930, 'Native SharpSql kernel returned a failure status.', 1;
        """;

    private static long Expected(int iterations, long seed)
    {
        var value = seed;
        for (var index = 0; index < iterations; index++)
            value = (value + index) % 2_147_483_647;
        return value;
    }

    private static async Task<long> ExecuteScalarAsync(SqlConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandTimeout = 60;
        command.CommandText = sql;
        return Convert.ToInt64(
            await command.ExecuteScalarAsync(TestContext.Current.CancellationToken),
            System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task<TimeSpan> TimeAsync(SqlConnection connection, string sql)
    {
        var started = Stopwatch.GetTimestamp();
        await ExecuteScalarAsync(connection, sql);
        return Stopwatch.GetElapsedTime(started);
    }

    private static TimeSpan Median(IEnumerable<TimeSpan> samples)
    {
        var ordered = samples.Order().ToArray();
        var middle = ordered.Length / 2;
        return ordered.Length % 2 == 0
            ? (ordered[middle - 1] + ordered[middle]) / 2
            : ordered[middle];
    }
}
