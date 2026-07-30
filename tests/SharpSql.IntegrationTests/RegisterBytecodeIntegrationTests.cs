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

            Console.WriteLine(SumTo(9));
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
        Assert.Contains("compact register-bytecode runtime ABI 1.0", sql.GeneratedSql, StringComparison.Ordinal);
        Assert.DoesNotContain("SharpSql stack-machine runtime", sql.GeneratedSql, StringComparison.Ordinal);
    }
}
