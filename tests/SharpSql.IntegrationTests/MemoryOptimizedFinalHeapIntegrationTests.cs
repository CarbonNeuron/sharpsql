using Microsoft.Data.SqlClient;
using SharpSql.SqlServer;
using Xunit;

namespace SharpSql.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public sealed class MemoryOptimizedFinalHeapIntegrationTests(SqlServerFixture sqlServer)
{
    private const string FieldsTable =
        "[SharpSql].[__sharpsql_memory_heap_fields_ephemeral_v1]";
    private const string DictionaryEntriesTable =
        "[SharpSql].[__sharpsql_memory_heap_dictionary_entries_ephemeral_v1]";

    private static readonly TranspileOptions MemoryOptimizedOptions = new()
    {
        Execution = RuntimeExecutionKind.Inline,
        Durability = RuntimeDurabilityKind.Ephemeral,
        UseMemoryOptimizedTables = true
    };

    [Fact]
    public async Task ClassesRecordsInheritanceAndAliasMutationRetainExpectedParity()
    {
        const string source = """
            var item = new Derived(3);
            Base alias = item;
            item.Advance(2);
            alias.AddBase(1);
            Console.WriteLine($"class={item.BaseValue}:{item.DerivedValue}:{item.Shared}:{alias.Shared}");

            var student = new Student("Ada", 3);
            Person recordAlias = student;
            var promoted = student with { Name = "Grace", Grade = 4 };
            Person promotedAlias = promoted;
            Console.WriteLine($"record={student.Name}:{student.Grade}:{recordAlias.Name}");
            Console.WriteLine($"clone={promoted.Name}:{promoted.Grade}:{promotedAlias.Name}");

            class Base
            {
                public int BaseValue = 2;
                public int Shared = 10;

                public Base()
                {
                    BaseValue++;
                }

                public Base(int value) : this()
                {
                    BaseValue += value;
                }

                public void AddBase(int value)
                {
                    BaseValue += value;
                }
            }

            class Derived : Base
            {
                public new int Shared = 20;
                public int DerivedValue = 4;

                public Derived(int value) : base(value + 1)
                {
                    BaseValue++;
                    DerivedValue += BaseValue;
                    Shared++;
                    base.Shared++;
                }

                public void Advance(int value)
                {
                    BaseValue += value;
                    DerivedValue += value;
                    base.Shared += value;
                }
            }

            record Person(string Name);
            record Student(string Name, int Grade) : Person(Name);
            """;
        var sql = Compile(source, expectsFields: true, expectsDictionaries: false);
        await using var connection = await OpenConnectionAsync();

        var execution = await ExecuteAsync(connection, sql);

        Assert.True(execution.Success, execution.ErrorMessage);
        Assert.Equal(
            ["class=11:14:21:13", "record=Ada:3:Ada", "clone=Grace:4:Grace"],
            execution.Messages);
        await AssertFinalHeapEmptyAsync(connection);
    }

    [Fact]
    public async Task DictionariesRetainSupportedScalarStringAndReferenceKeyValueParity()
    {
        const string source = """
            var ada = new Person("Ada", 36);

            var scalarKeys = new Dictionary<int, string>();
            scalarKeys.Add(7, "seven");
            scalarKeys[7] = "SEVEN";

            var stringKeys = new Dictionary<string, int>();
            stringKeys.Add("answer", 42);

            var referenceValues = new Dictionary<string, Person>();
            referenceValues.Add("ada", ada);
            referenceValues["ada"].Age++;

            var referenceKeys = new Dictionary<Person, string>();
            referenceKeys.Add(ada, "owner");

            Console.WriteLine($"dict={scalarKeys[7]}:{stringKeys["answer"]}:{referenceValues["ada"].Age}:{referenceKeys[ada]}");

            class Person
            {
                public Person(string name, int age)
                {
                    Name = name;
                    Age = age;
                }

                public string Name { get; set; }
                public int Age { get; set; }
            }
            """;
        var sql = Compile(source, expectsFields: true, expectsDictionaries: true);
        await using var connection = await OpenConnectionAsync();

        var execution = await ExecuteAsync(connection, sql);

        Assert.True(execution.Success, execution.ErrorMessage);
        Assert.Equal(["dict=SEVEN:42:37:owner"], execution.Messages);
        await AssertFinalHeapEmptyAsync(connection);
    }

    [Fact]
    public async Task ConcurrentObjectAndDictionaryExecutionsRemainIsolatedAndCleanUp()
    {
        const string source = """
            var value = new Counter(10);
            Counter alias = value;
            alias.Add(5);

            var lookup = new Dictionary<int, Counter>();
            lookup.Add(1, value);
            lookup[1].Add(2);
            Console.WriteLine($"value={value.Value}:alias={alias.Value}:lookup={lookup[1].Value}");

            class Counter
            {
                public Counter(int value)
                {
                    Value = value;
                }

                public int Value { get; set; }

                public void Add(int value)
                {
                    Value += value;
                }
            }
            """;
        var sql = Compile(source, expectsFields: true, expectsDictionaries: true);
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
            Assert.Equal(["value=17:alias=17:lookup=17"], result.Messages);
        });
        await using var verification = new SqlConnection(connectionString);
        await verification.OpenAsync(TestContext.Current.CancellationToken);
        await AssertFinalHeapEmptyAsync(verification);
    }

    [Fact]
    public async Task RuntimeFailureCleansUpFieldsAndDictionaryEntriesBeforeRethrowing()
    {
        const string source = """
            var person = new Person("Ada", 36);
            Person alias = person;
            alias.Age++;

            var lookup = new Dictionary<string, Person>();
            lookup.Add("present", person);
            Console.WriteLine(lookup["missing"].Name);

            class Person
            {
                public Person(string name, int age)
                {
                    Name = name;
                    Age = age;
                }

                public string Name { get; set; }
                public int Age { get; set; }
            }
            """;
        var sql = Compile(source, expectsFields: true, expectsDictionaries: true);
        await using var connection = await OpenConnectionAsync();

        var execution = await ExecuteAsync(connection, sql);

        Assert.False(execution.Success);
        Assert.Equal(51010, execution.ErrorNumber);
        await AssertFinalHeapEmptyAsync(connection);
    }

    private static string Compile(string source, bool expectsFields, bool expectsDictionaries)
    {
        var result = new SharpSqlCompiler().Transpile(source, MemoryOptimizedOptions);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        if (expectsFields)
        {
            Assert.Contains(FieldsTable, result.Sql, StringComparison.Ordinal);
            Assert.DoesNotContain("#__sharpsql_type_", result.Sql, StringComparison.Ordinal);
        }
        if (expectsDictionaries)
        {
            Assert.Contains(DictionaryEntriesTable, result.Sql, StringComparison.Ordinal);
            Assert.DoesNotContain("#__sharpsql_dictionary_entries", result.Sql, StringComparison.Ordinal);
        }
        return result.Sql;
    }

    private async Task<SqlConnection> OpenConnectionAsync()
    {
        var connectionString = await sqlServer.GetMemoryOptimizedConnectionStringAsync(
            TestContext.Current.CancellationToken);
        var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        return connection;
    }

    private static Task<SqlBatchExecutionResult> ExecuteAsync(SqlConnection connection, string sql) =>
        SqlBatchExecutor.ExecuteAsync(
            connection,
            sql,
            120,
            TestContext.Current.CancellationToken);

    private static async Task AssertFinalHeapEmptyAsync(SqlConnection connection)
    {
        Assert.Equal(0L, await CountAsync(connection, FieldsTable));
        Assert.Equal(0L, await CountAsync(connection, DictionaryEntriesTable));
    }

    private static async Task<long> CountAsync(SqlConnection connection, string table)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT_BIG(*) FROM {table};";
        return Convert.ToInt64(
            await command.ExecuteScalarAsync(TestContext.Current.CancellationToken),
            System.Globalization.CultureInfo.InvariantCulture);
    }
}
