using Microsoft.Data.SqlClient;
using Xunit;

namespace SharpSql.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public sealed class RegisterBytecodeIntegrationTests(SqlServerFixture sqlServer)
{
    [Fact]
    public async Task DurableImagesAreInstalledOnceWhileConcurrentExecutionsStayIsolatedAndCleanUp()
    {
        const string marker = "durable-register-image-20260804:";
        const string sourceTemplate = """
            string Decorate(string value)
            {
                string prefix = "durable-register-image-20260804:";
                return prefix + value;
            }

            Console.WriteLine(Decorate("VALUE"));
            """;
        var firstId = Guid.Parse("d94b1096-d1d7-4e0b-a46f-9af14e23a101");
        var secondId = Guid.Parse("d94b1096-d1d7-4e0b-a46f-9af14e23a102");
        var thirdId = Guid.Parse("d94b1096-d1d7-4e0b-a46f-9af14e23a103");
        var first = CompileDurableBytecode(sourceTemplate.Replace("VALUE", "alpha", StringComparison.Ordinal));
        var second = CompileDurableBytecode(sourceTemplate.Replace("VALUE", "beta", StringComparison.Ordinal));

        await using var firstConnection = await OpenConnectionAsync();
        await using var secondConnection = await OpenConnectionAsync();
        var outputs = await Task.WhenAll(
            ExecuteAsync(firstConnection, WithExecutionId(first.Sql, firstId)),
            ExecuteAsync(secondConnection, WithExecutionId(second.Sql, secondId)));

        Assert.Equal([marker + "alpha", marker + "beta"], outputs);
        Assert.Equal(0L, await CountExecutionRowsAsync(firstConnection, "BytecodeFramesV1", firstId));
        Assert.Equal(0L, await CountExecutionRowsAsync(firstConnection, "BytecodeRegistersV1", firstId));
        Assert.Equal(0L, await CountExecutionRowsAsync(firstConnection, "BytecodeFramesV1", secondId));
        Assert.Equal(0L, await CountExecutionRowsAsync(firstConnection, "BytecodeRegistersV1", secondId));

        var imageCount = await ScalarAsync(
            firstConnection,
            """
            SELECT COUNT_BIG(*)
            FROM (
                SELECT DISTINCT [__image_id]
                FROM [SharpSql].[BytecodeInstructionsV1]
                WHERE [__constant_text] = @marker
            ) AS [image];
            """,
            new SqlParameter("@marker", marker));
        Assert.Equal(1L, imageCount);
        Assert.Equal(1L, await ScalarAsync(
            firstConnection,
            """
            SELECT COUNT_BIG(*)
            FROM [SharpSql].[BytecodeImages] AS [image]
            WHERE [image].[__image_id] IN (
                SELECT [__image_id]
                FROM [SharpSql].[BytecodeInstructionsV1]
                WHERE [__constant_text] = @marker
            )
            AND [image].[__instruction_count] = (
                SELECT COUNT_BIG(*) FROM [SharpSql].[BytecodeInstructionsV1] AS [instruction]
                WHERE [instruction].[__image_id] = [image].[__image_id]
            )
            AND [image].[__argument_count] = (
                SELECT COUNT_BIG(*) FROM [SharpSql].[BytecodeArgumentsV1] AS [argument]
                WHERE [argument].[__image_id] = [image].[__image_id]
            )
            AND [image].[__parameter_count] = (
                SELECT COUNT_BIG(*) FROM [SharpSql].[BytecodeParametersV1] AS [parameter]
                WHERE [parameter].[__image_id] = [image].[__image_id]
            );
            """,
            new SqlParameter("@marker", marker)));

        var installedAt = await DateTimeScalarAsync(firstConnection, marker);
        var thirdOutput = await ExecuteAsync(firstConnection, WithExecutionId(first.Sql, thirdId));
        Assert.Equal(marker + "alpha", thirdOutput);
        Assert.Equal(installedAt, await DateTimeScalarAsync(firstConnection, marker));
        Assert.Equal(0L, await CountExecutionRowsAsync(firstConnection, "BytecodeFramesV1", thirdId));
        Assert.Equal(0L, await CountExecutionRowsAsync(firstConnection, "BytecodeRegistersV1", thirdId));
    }

    [Fact]
    public async Task DurableInterpreterFailureCleansMutableStateButRetainsTheImage()
    {
        const string marker = "durable-register-failure-20260804";
        const string source = """
            int Fail(int divisor)
            {
                Console.WriteLine("durable-register-failure-20260804");
                return 10 / divisor;
            }

            Console.WriteLine(Fail(0));
            """;
        var executionId = Guid.Parse("d94b1096-d1d7-4e0b-a46f-9af14e23a104");
        var result = CompileDurableBytecode(source);

        await using var connection = await OpenConnectionAsync();
        connection.FireInfoMessageEventOnUserErrors = false;
        var exception = await Assert.ThrowsAsync<SqlException>(() =>
            ExecuteAsync(connection, WithExecutionId(result.Sql, executionId)));

        Assert.Equal(8134, exception.Number);
        Assert.Equal(0L, await CountExecutionRowsAsync(connection, "BytecodeFramesV1", executionId));
        Assert.Equal(0L, await CountExecutionRowsAsync(connection, "BytecodeRegistersV1", executionId));
        Assert.Equal(1L, await ScalarAsync(
            connection,
            """
            SELECT COUNT_BIG(*)
            FROM [SharpSql].[BytecodeInstructionsV1]
            WHERE [__constant_text] = @marker;
            """,
            new SqlParameter("@marker", marker)));
    }

    [Fact]
    public async Task CompactFallbackExecutesScalarLoopsWithCSharpParity()
    {
        const string source = """
            int SumTo(int limit)
            {
                int sum = 0;
                while (limit > 0)
                {
                    int weight = limit switch
                    {
                        1 => 10,
                        2 => 20,
                        _ => limit
                    };
                    if ((limit & 1) == 0)
                        sum += weight << 1;
                    else
                        sum += weight;
                    limit--;
                }
                return sum;
            }

            int Factorial(int value) => value <= 1 ? 1 : value * Factorial(value - 1);

            int Step(int value) => value + 1;

            int Twice(int value)
            {
                value = Step(value);
                return Step(value);
            }

            bool IsEven(int value) => value == 0 ? true : IsOdd(value - 1);
            bool IsOdd(int value) => value == 0 ? false : IsEven(value - 1);

            int Announce(int value)
            {
                Console.WriteLine(value);
                return value + 1;
            }

            void Emit(int value) => Console.WriteLine(value);

            void Countdown(int value)
            {
                if (value == 0)
                    return;
                Emit(value);
                Countdown(value - 1);
            }

            string Echo(string value) => value;

            string Decorate(string value)
            {
                string missing = default;
                Console.WriteLine(value);
                return "[" + Echo(value) + "]";
            }

            string? EchoNullable(string? value)
            {
                string? copy = value;
                return copy;
            }

            bool IsNullRoundTrip(string? value)
            {
                string? copy = EchoNullable(value);
                return copy == null;
            }

            bool Same(string? left, string? right)
            {
                bool result = left == right;
                return result;
            }

            bool Different(string? left, string? right)
            {
                bool result = left != right;
                return result;
            }

            Console.WriteLine(SumTo(9));
            Console.WriteLine(Factorial(6));
            Console.WriteLine(Twice(40));
            Console.WriteLine(IsEven(10));
            Console.WriteLine(IsOdd(10));
            Console.WriteLine(Announce(7));
            Countdown(3);
            Console.WriteLine(Decorate("O'Brien Ω"));
            Console.WriteLine(Same(null, null));
            Console.WriteLine(Same(null, ""));
            Console.WriteLine(Same("A", "a"));
            Console.WriteLine(Same("tail ", "tail"));
            Console.WriteLine(Different("A", "a"));
            Console.WriteLine(IsNullRoundTrip(null));
            """;
        var testCase = new ParityCase("register-bytecode", source);
        var csharp = await ParityHarness.ExecuteCSharpAsync(testCase);
        var sql = await ParityHarness.ExecuteSqlAsync(
            testCase,
            sqlServer.ConnectionString,
            TestContext.Current.CancellationToken,
            new TranspileOptions
            {
                MaxInlineStatements = 1,
                ManagedFallback = ManagedFallbackKind.Bytecode
            });

        Assert.True(
            csharp.Failure is null &&
            sql.Outcome.Failure is null &&
            csharp.StandardOutput == sql.Outcome.StandardOutput,
            ParityHarness.FormatComparisonFailure(testCase, csharp, sql));
        Assert.Contains("compact register-bytecode runtime ABI 1.2", sql.GeneratedSql, StringComparison.Ordinal);
        Assert.Contains("#__sharpsql_bc_arguments", sql.GeneratedSql, StringComparison.Ordinal);
        Assert.Contains("__constant_text NVARCHAR(MAX)", sql.GeneratedSql, StringComparison.Ordinal);
        Assert.Contains("__text_value NVARCHAR(MAX)", sql.GeneratedSql, StringComparison.Ordinal);
        Assert.DoesNotContain("SharpSql stack-machine runtime", sql.GeneratedSql, StringComparison.Ordinal);
        Assert.DoesNotContain("#__sharpsql_stack", sql.GeneratedSql, StringComparison.Ordinal);
    }

    private static TranspileResult CompileDurableBytecode(string source)
    {
        var result = new SharpSqlCompiler().Transpile(
            source,
            new TranspileOptions
            {
                Execution = RuntimeExecutionKind.Inline,
                Durability = RuntimeDurabilityKind.Durable,
                UseMemoryOptimizedTables = false,
                ManagedFallback = ManagedFallbackKind.Bytecode,
                MaxInlineStatements = 1
            });
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        Assert.True(result.UsesRegisterBytecode);
        Assert.DoesNotContain("CREATE TABLE #__sharpsql_bc_", result.Sql, StringComparison.Ordinal);
        return result;
    }

    private static string WithExecutionId(string sql, Guid executionId)
    {
        const string generated = "DECLARE @__sharpsql_execution_id UNIQUEIDENTIFIER = NEWID();";
        Assert.Contains(generated, sql, StringComparison.Ordinal);
        return sql.Replace(
            generated,
            $"DECLARE @__sharpsql_execution_id UNIQUEIDENTIFIER = '{executionId:D}';",
            StringComparison.Ordinal);
    }

    private async Task<SqlConnection> OpenConnectionAsync()
    {
        var connection = new SqlConnection(sqlServer.ConnectionString)
        {
            FireInfoMessageEventOnUserErrors = true
        };
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        return connection;
    }

    private static async Task<string> ExecuteAsync(SqlConnection connection, string sql)
    {
        var messages = new List<string>();
        connection.InfoMessage += HandleMessage;
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.CommandTimeout = 60;
            await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
            return string.Join(Environment.NewLine, messages);
        }
        finally
        {
            connection.InfoMessage -= HandleMessage;
        }

        void HandleMessage(object sender, SqlInfoMessageEventArgs args)
        {
            foreach (SqlError error in args.Errors)
                if (error.Class == 0)
                    messages.Add(error.Message);
        }
    }

    private static async Task<long> CountExecutionRowsAsync(
        SqlConnection connection,
        string table,
        Guid executionId) => await ScalarAsync(
            connection,
            $"SELECT COUNT_BIG(*) FROM [SharpSql].[{table}] WHERE [__execution_id] = @executionId;",
            new SqlParameter("@executionId", executionId));

    private static async Task<long> ScalarAsync(
        SqlConnection connection,
        string sql,
        params SqlParameter[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddRange(parameters);
        return Convert.ToInt64(
            await command.ExecuteScalarAsync(TestContext.Current.CancellationToken),
            System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task<DateTime> DateTimeScalarAsync(SqlConnection connection, string marker)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT [image].[__installed_at_utc]
            FROM [SharpSql].[BytecodeImages] AS [image]
            WHERE [image].[__image_id] IN (
                SELECT [__image_id]
                FROM [SharpSql].[BytecodeInstructionsV1]
                WHERE [__constant_text] = @marker
            );
            """;
        command.Parameters.AddWithValue("@marker", marker);
        return (DateTime)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }
}
