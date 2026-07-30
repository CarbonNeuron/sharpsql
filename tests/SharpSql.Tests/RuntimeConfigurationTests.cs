using Xunit;

namespace SharpSql.Tests;

public sealed class RuntimeConfigurationTests
{
    private const string AsyncSource = """
        var values = new List<int> { 1 };
        var tasks = values.Select(Work).ToList();
        await Task.WhenAll(tasks);

        async Task<int> Work(int value)
        {
            await Task.Delay(value);
            return value;
        }
        """;

    [Fact]
    public void AutoSelectsInlineForSynchronousPrograms()
    {
        var result = new SharpSqlCompiler().Transpile("Console.WriteLine(42);");

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        Assert.Equal(
            new RuntimeConfiguration(
                RuntimeExecutionKind.Inline,
                RuntimeDurabilityKind.Ephemeral,
                UseMemoryOptimizedTables: false),
            result.EffectiveRuntime);
    }

    [Fact]
    public void AutoSelectsEphemeralServiceBrokerForReachableAsyncCode()
    {
        var result = new SharpSqlCompiler().Transpile(AsyncSource);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        Assert.Equal(
            new RuntimeConfiguration(
                RuntimeExecutionKind.ServiceBroker,
                RuntimeDurabilityKind.Ephemeral,
                UseMemoryOptimizedTables: false),
            result.EffectiveRuntime);
        Assert.Contains("SharpSql Service Broker program worker", result.Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void ExplicitServiceBrokerRetainsEphemeralDurability()
    {
        var result = new SharpSqlCompiler().Transpile(
            AsyncSource,
            new TranspileOptions
            {
                Execution = RuntimeExecutionKind.ServiceBroker,
                Durability = RuntimeDurabilityKind.Ephemeral
            });

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        Assert.Equal(
            new RuntimeConfiguration(
                RuntimeExecutionKind.ServiceBroker,
                RuntimeDurabilityKind.Ephemeral,
                UseMemoryOptimizedTables: false),
            result.EffectiveRuntime);
    }

    [Fact]
    public void AutoCombinesServiceBrokerWithEphemeralMemoryOptimizedTables()
    {
        var result = new SharpSqlCompiler().Transpile(
            AsyncSource,
            new TranspileOptions { UseMemoryOptimizedTables = true });

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        Assert.Equal(
            new RuntimeConfiguration(
                RuntimeExecutionKind.ServiceBroker,
                RuntimeDurabilityKind.Ephemeral,
                UseMemoryOptimizedTables: true),
            result.EffectiveRuntime);
        Assert.Contains("SharpSql Service Broker program worker", result.Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void AutoIgnoresUnreachableAsyncMethods()
    {
        const string source = """
            Console.WriteLine("sync");
            async Task Unused()
            {
                await Task.Delay(1);
            }
            """;

        var result = new SharpSqlCompiler().Transpile(source);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        Assert.Equal(RuntimeExecutionKind.Inline, result.EffectiveRuntime.Execution);
        Assert.DoesNotContain("Service Broker program worker", result.Sql, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(RuntimeStorageKind.Ephemeral, RuntimeExecutionKind.Inline, RuntimeDurabilityKind.Ephemeral, false)]
    [InlineData(RuntimeStorageKind.Durable, RuntimeExecutionKind.Inline, RuntimeDurabilityKind.Durable, false)]
    [InlineData(RuntimeStorageKind.ServiceBroker, RuntimeExecutionKind.ServiceBroker, RuntimeDurabilityKind.Durable, false)]
    [InlineData(RuntimeStorageKind.MemoryOptimized, RuntimeExecutionKind.Inline, RuntimeDurabilityKind.Ephemeral, true)]
    public void LegacyRuntimeStorageMapsToIndependentConfiguration(
        RuntimeStorageKind storage,
        RuntimeExecutionKind execution,
        RuntimeDurabilityKind durability,
        bool useMemoryOptimizedTables)
    {
        var result = new SharpSqlCompiler().Transpile(
            "Console.WriteLine(42);",
            new TranspileOptions { RuntimeStorage = storage });

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        Assert.Equal(
            new RuntimeConfiguration(execution, durability, useMemoryOptimizedTables),
            result.EffectiveRuntime);
    }

    [Fact]
    public void IndependentRuntimeAxesAreExposedOnTheResult()
    {
        var result = new SharpSqlCompiler().Transpile(
            "Console.WriteLine(42);",
            new TranspileOptions
            {
                Execution = RuntimeExecutionKind.Inline,
                Durability = RuntimeDurabilityKind.Durable,
                UseMemoryOptimizedTables = true
            });

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        Assert.Equal(
            new RuntimeConfiguration(
                RuntimeExecutionKind.Inline,
                RuntimeDurabilityKind.Durable,
                UseMemoryOptimizedTables: true),
            result.EffectiveRuntime);
        Assert.Contains("SharpSql durable shared runtime", result.Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void ExplicitInlineAsyncProducesOnlyTheConfigurationDiagnostic()
    {
        var result = new SharpSqlCompiler().Transpile(
            AsyncSource,
            new TranspileOptions { Execution = RuntimeExecutionKind.Inline });

        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal("SS7006", diagnostic.Code);
        Assert.Empty(result.Sql);
        Assert.Equal(RuntimeExecutionKind.Inline, result.EffectiveRuntime.Execution);
    }
}
