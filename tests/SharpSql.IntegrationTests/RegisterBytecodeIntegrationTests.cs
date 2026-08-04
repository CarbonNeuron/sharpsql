using Xunit;

namespace SharpSql.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public sealed class RegisterBytecodeIntegrationTests(SqlServerFixture sqlServer)
{
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

            Console.WriteLine(SumTo(9));
            Console.WriteLine(Factorial(6));
            Console.WriteLine(Twice(40));
            Console.WriteLine(IsEven(10));
            Console.WriteLine(IsOdd(10));
            Console.WriteLine(Announce(7));
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
        Assert.Contains("compact register-bytecode runtime ABI 1.1", sql.GeneratedSql, StringComparison.Ordinal);
        Assert.Contains("#__sharpsql_bc_arguments", sql.GeneratedSql, StringComparison.Ordinal);
        Assert.DoesNotContain("SharpSql stack-machine runtime", sql.GeneratedSql, StringComparison.Ordinal);
    }
}
