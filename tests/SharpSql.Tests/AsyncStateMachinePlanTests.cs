using Xunit;

namespace SharpSql.Tests;

public sealed class AsyncStateMachinePlanTests
{
    private static string ServiceBrokerProjectPath => Path.Combine(
        AppContext.BaseDirectory,
        "fixtures",
        "ServiceBrokerProject",
        "ServiceBrokerProject.csproj");

    [Fact]
    public void PlansDelayAndWhenAllSuspensionsWithConservativeLiveState()
    {
        const string source = """
            async System.Threading.Tasks.Task<int> Work(int value)
            {
                await System.Threading.Tasks.Task.Delay(value);
                return value + 1;
            }

            var tasks = new List<System.Threading.Tasks.Task<int>>();
            await System.Threading.Tasks.Task.WhenAll(tasks);
            Console.WriteLine(tasks.Count);
            """;
        var compiler = new SharpSqlCompiler();

        compiler.Transpile(source);

        var program = Assert.IsType<IrProgram>(compiler.BoundProgram);
        var work = Assert.Single(program.Methods, method => method.Name == "Work");
        var methodPlan = AsyncStateMachinePlan.Create("Work", work);
        var methodSuspension = Assert.Single(methodPlan.SuspensionPoints);
        Assert.Equal(AsyncAwaitOperationKind.Delay, methodSuspension.Operation);
        Assert.Equal(1, methodSuspension.ResumeState);
        Assert.Contains(methodSuspension.LiveSymbols, symbol => symbol.Name == "value");

        var rootPlan = AsyncStateMachinePlan.Create("__entry", program.EntryPoint);
        var rootSuspension = Assert.Single(rootPlan.SuspensionPoints);
        Assert.Equal(AsyncAwaitOperationKind.WhenAll, rootSuspension.Operation);
        Assert.Contains(rootSuspension.LiveSymbols, symbol => symbol.Name == "tasks");
        Assert.Equal(2, rootPlan.StateCount);
    }

    [Fact]
    public void ServiceBrokerStorageModeIsExplicitAndRetainsDurableStorage()
    {
        Assert.NotEqual(RuntimeStorageKind.Durable, RuntimeStorageKind.ServiceBroker);

        var result = new SharpSqlCompiler().Transpile(
            "var values = new List<int> { 1, 2 };",
            new TranspileOptions { RuntimeStorage = RuntimeStorageKind.ServiceBroker });

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        Assert.Contains("[SharpSql].[__sharpsql_objects]", result.Sql);
        Assert.Contains("@__sharpsql_execution_id", result.Sql);
    }

    [Fact]
    public void DoesNotClassifyUserMethodsNamedDelayAsTaskDelay()
    {
        const string source = """
            async Task<int> Work(int value)
            {
                await Delay(value);
                return value;
            }

            async Task Delay(int value)
            {
                await Task.Delay(value);
            }
            """;
        var compiler = new SharpSqlCompiler();

        compiler.Transpile(source);

        var program = Assert.IsType<IrProgram>(compiler.BoundProgram);
        var work = Assert.Single(program.Methods, method => method.Name == "Work");
        var suspension = Assert.Single(AsyncStateMachinePlan.Create("Work", work).SuspensionPoints);
        Assert.Equal(AsyncAwaitOperationKind.Task, suspension.Operation);
    }

    [Fact]
    public void RejectsUnsupportedTaskDelayOverloadsWithoutThrowingDuringTranspilation()
    {
        const string source = """
            var values = new List<int> { 1 };
            var tasks = values.Select(Work).ToList();
            await Task.WhenAll(tasks);

            async Task<int> Work(int value)
            {
                await Task.Delay(value, System.Threading.CancellationToken.None);
                return value;
            }
            """;

        var result = new SharpSqlCompiler().Transpile(
            source,
            new TranspileOptions { RuntimeStorage = RuntimeStorageKind.ServiceBroker });

        var diagnostic = Assert.Single(result.Diagnostics, item => item.Code == "SS7002");
        Assert.Contains("Task.Delay(int)", diagnostic.Message);
    }

    [Fact]
    public void AsyncByteArrayResultsUseManagedReferenceStorage()
    {
        const string source = """
            var values = new List<int> { 1 };
            var tasks = values.Select(Work).ToList();
            await Task.WhenAll(tasks);

            async Task<byte[]> Work(int value)
            {
                await Task.Delay(0);
                return new byte[] { (byte)value };
            }
            """;

        var result = new SharpSqlCompiler().Transpile(
            source,
            new TranspileOptions { RuntimeStorage = RuntimeStorageKind.ServiceBroker });

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        Assert.Contains("@ResultKind = 4", result.Sql, StringComparison.Ordinal);
        Assert.Contains("@ResultReferenceId", result.Sql, StringComparison.Ordinal);
        Assert.Contains("__owner_id", result.Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void ReportsPreAwaitLocalsThatTheCurrentBackendCannotSpill()
    {
        const string source = """
            var values = new List<int> { 1 };
            var tasks = values.Select(Work).ToList();
            await Task.WhenAll(tasks);

            async Task<int> Work(int value)
            {
                int preserved = value + 1;
                await Task.Delay(value);
                return preserved;
            }
            """;

        var result = new SharpSqlCompiler().Transpile(
            source,
            new TranspileOptions { RuntimeStorage = RuntimeStorageKind.ServiceBroker });

        var diagnostic = Assert.Single(result.Diagnostics, item => item.Code == "SS7004");
        Assert.Contains("preserved", diagnostic.Message);
    }

    [Fact]
    public void RejectsNestedWorkerReturnsBeforeSqlEmission()
    {
        const string source = """
            var values = new List<int> { 1 };
            if (values.Count == 0)
                return;
            var tasks = values.Select(Work).ToList();
            await Task.WhenAll(tasks);

            async Task<int> Work(int value)
            {
                await Task.Delay(value);
                return value;
            }
            """;

        var result = new SharpSqlCompiler().Transpile(
            source,
            new TranspileOptions { RuntimeStorage = RuntimeStorageKind.ServiceBroker });

        var diagnostic = Assert.Single(result.Diagnostics, item => item.Code == "SS7001");
        Assert.Contains("top-level return", diagnostic.Message);
        Assert.DoesNotContain("GOTO __sharpsql_execution_cleanup", result.Sql);
    }

    [Fact]
    public void RejectsVmFallbackCallsFromWorkerContinuations()
    {
        const string source = """
            var values = new List<int> { 5 };
            var tasks = values.Select(Work).ToList();
            await Task.WhenAll(tasks);

            async Task<int> Work(int value)
            {
                await Task.Delay(value);
                return Fib(value);
            }

            int Fib(int value) => value <= 1 ? value : Fib(value - 1) + Fib(value - 2);
            """;

        var result = new SharpSqlCompiler().Transpile(
            source,
            new TranspileOptions
            {
                RuntimeStorage = RuntimeStorageKind.ServiceBroker,
                ManagedFallback = ManagedFallbackKind.Legacy
            });

        var diagnostic = Assert.Single(result.Diagnostics, item => item.Code == "SS7005");
        Assert.Contains("legacy stack-VM fallback", diagnostic.Message);
        Assert.DoesNotContain("CREATE OR ALTER PROCEDURE [SharpSql].[Program_", result.Sql);
    }

    [Fact]
    public void EmbedsRunToCompletionRegisterBytecodeInsideBrokerWorkers()
    {
        const string source = """
            var values = new List<int> { 6 };
            var tasks = values.Select(Work).ToList();
            await Task.WhenAll(tasks);
            var results = tasks.Select(task => task.Result);
            foreach (int result in results)
                Console.WriteLine("result:" + result);

            async Task<int> Work(int value)
            {
                await Task.Delay(25);
                Emit(Decorate("worker"));
                return Fib(value);
            }

            string Echo(string value)
            {
                string copy = value;
                return copy;
            }

            string Decorate(string value)
            {
                string decorated = "[" + Echo(value);
                return decorated + "]";
            }

            void Emit(string value)
            {
                string output = value;
                Console.WriteLine(output);
            }

            int Fib(int value) => value <= 1 ? value : Fib(value - 1) + Fib(value - 2);
            """;

        var result = new SharpSqlCompiler().Transpile(
            source,
            new TranspileOptions
            {
                RuntimeStorage = RuntimeStorageKind.ServiceBroker,
                ManagedFallback = ManagedFallbackKind.Bytecode,
                MaxInlineStatements = 1
            });

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        Assert.True(result.UsesRegisterBytecode);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == "SS7005");
        Assert.Contains("CREATE TABLE #__sharpsql_bc_program", result.Sql, StringComparison.Ordinal);
        Assert.Contains("__sharpsql_bc_dispatch:;", result.Sql, StringComparison.Ordinal);
        Assert.Contains("EXEC [SharpSql].[AppendOutput]", result.Sql, StringComparison.Ordinal);
        Assert.DoesNotContain("PRINT CASE WHEN @__sharpsql_bc", result.Sql, StringComparison.Ordinal);
        Assert.DoesNotContain("#__sharpsql_stack", result.Sql, StringComparison.Ordinal);
        Assert.DoesNotContain("[SharpSql].[BytecodeFramesV1]", result.Sql, StringComparison.Ordinal);
        Assert.Equal(1, Count(result.Sql, "CREATE TABLE #__sharpsql_bc_program"));
    }

    [Fact]
    public void RejectsAssignmentsToCapturedEntryLocalsUntilClosureCellsAreDurable()
    {
        const string source = """
            int shared = 0;
            var values = new List<int> { 1 };
            var tasks = values.Select(Work).ToList();
            await Task.WhenAll(tasks);

            async Task<int> Work(int value)
            {
                await Task.Delay(value);
                shared++;
                return shared;
            }
            """;

        var result = new SharpSqlCompiler().Transpile(
            source,
            new TranspileOptions { RuntimeStorage = RuntimeStorageKind.ServiceBroker });

        var diagnostic = Assert.Single(result.Diagnostics, item => item.Code == "SS7004");
        Assert.Contains("shared closure cells", diagnostic.Message);
    }

    [Fact]
    public void LowersForkJoinDelayResultsOutputAndCatchIntoBrokerContinuations()
    {
        const string source = """
            var people = new List<Person>
            {
                new("Bob", 12),
                new("John", 20),
                new("Jane", 30)
            };
            var random = new Random(4);
            PrintPeople(people);

            var tasks = people.Select(AgeUp).ToList();
            await Task.WhenAll(tasks);
            var results = tasks.Select(task => task.Result);
            PrintPeople(results);
            return;

            async Task<Person> AgeUp(Person person)
            {
                try
                {
                    await Task.Delay(random.Next(0, 1000));
                    if (random.Next(0, 5) > 2)
                        throw new ApplicationException();
                }
                catch (ApplicationException)
                {
                    Console.WriteLine($"Don't worry, {person.Name}");
                }
                Console.WriteLine($"[{System.Threading.Thread.GetCurrentProcessorId()}] Aging up {person.Name}");
                return person with { Age = person.Age + random.Next(0, 50) };
            }

            void PrintPeople(IEnumerable<Person> values)
            {
                foreach (var person in values.OrderByDescending(value => value.Age))
                    Console.WriteLine(person);
            }

            record Person(string Name, int Age);
            """;

        var result = new SharpSqlCompiler().Transpile(
            source,
            new TranspileOptions { RuntimeStorage = RuntimeStorageKind.ServiceBroker });

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        Assert.Contains("CREATE OR ALTER PROCEDURE [SharpSql].[Program_", result.Sql);
        Assert.Contains("EXEC [SharpSql].[ScheduleTask]", result.Sql);
        Assert.Contains("EXEC [SharpSql].[SuspendTaskForDelay]", result.Sql);
        Assert.Contains("EXEC [SharpSql].[SuspendTaskForDependencies]", result.Sql);
        Assert.Contains("EXEC [SharpSql].[RegisterTaskDependency]", result.Sql);
        Assert.Contains("INNER JOIN [SharpSql].[Tasks]", result.Sql);
        Assert.Contains("EXEC [SharpSql].[AppendOutput]", result.Sql);
        Assert.Contains("CONCAT(N''['', CONVERT(INT, @@SPID), N''] Aging up '',", result.Sql);
        Assert.Contains("IF DATALENGTH(@__sharpsql_output_text) <= 4000", result.Sql);
        Assert.Contains("RAISERROR(N'%s', 0, 1, @__sharpsql_output_text) WITH NOWAIT;", result.Sql);
        Assert.Contains("PRINT @__sharpsql_output_text;", result.Sql);
        Assert.Contains("ERROR_NUMBER()", result.Sql);
        Assert.Contains("IN (1205, 51929) THROW;", result.Sql);
        Assert.Contains("= -3 THROW 51929", result.Sql);
        Assert.Contains("< 0 THROW 51930", result.Sql);
        Assert.Contains("= 51012", result.Sql);
        Assert.Contains("IF XACT_STATE() <> 1 THROW;", result.Sql);
        Assert.Contains("@LockOwner = ''Transaction''", result.Sql);
        Assert.DoesNotContain("sp_releaseapplock", result.Sql);
        Assert.DoesNotContain("Await expressions require async scheduling", result.Sql);

        var childAllocation = result.Sql.IndexOf("@StartSuspended = 1", StringComparison.Ordinal);
        Assert.True(childAllocation >= 0);
        var childSuspension = result.Sql.IndexOf(
            "EXEC [SharpSql].[SuspendTaskForDelay]",
            childAllocation,
            StringComparison.Ordinal);
        var synchronousRandom = result.Sql.IndexOf(
            "sys.sp_getapplock",
            childAllocation,
            StringComparison.Ordinal);
        var childContinuation = result.Sql.IndexOf(
            "@ContinuationState = 1",
            childAllocation,
            StringComparison.Ordinal);
        Assert.True(synchronousRandom > childAllocation && synchronousRandom < childSuspension);
        Assert.True(childContinuation > childSuspension);

        Assert.DoesNotContain(
            "SET [DueAtUtc] = DATEADD(MILLISECOND, [timer].[DelayMilliseconds]",
            result.Sql);
    }

    [Fact]
    public void ServiceBrokerProgramIdentityIncludesCompilerOptions()
    {
        const string source = """
            var values = new List<int> { 1 };
            var tasks = values.Select(Work).ToList();
            await Task.WhenAll(tasks);
            var results = tasks.Select(task => task.Result);
            foreach (int result in results)
                Console.WriteLine(result);

            async Task<int> Work(int value)
            {
                await Task.Delay(value);
                return value + 1;
            }
            """;

        var first = new SharpSqlCompiler().Transpile(
            source,
            new TranspileOptions
            {
                RuntimeStorage = RuntimeStorageKind.ServiceBroker,
                MaxInlineStatements = 40
            });
        var second = new SharpSqlCompiler().Transpile(
            source,
            new TranspileOptions
            {
                RuntimeStorage = RuntimeStorageKind.ServiceBroker,
                MaxInlineStatements = 41
            });
        var differentFallback = new SharpSqlCompiler().Transpile(
            source,
            new TranspileOptions
            {
                RuntimeStorage = RuntimeStorageKind.ServiceBroker,
                ManagedFallback = ManagedFallbackKind.Bytecode,
                MaxInlineStatements = 40
            });

        Assert.True(first.Success, string.Join(Environment.NewLine, first.Diagnostics));
        Assert.True(second.Success, string.Join(Environment.NewLine, second.Diagnostics));
        Assert.True(differentFallback.Success, string.Join(Environment.NewLine, differentFallback.Diagnostics));
        Assert.NotEqual(ProgramId(first.Sql), ProgramId(second.Sql));
        Assert.NotEqual(ProgramId(first.Sql), ProgramId(differentFallback.Sql));

        static string ProgramId(string sql)
        {
            const string marker = "CREATE OR ALTER PROCEDURE [SharpSql].[Program_";
            var start = sql.IndexOf(marker, StringComparison.Ordinal);
            Assert.True(start >= 0);
            start += marker.Length;
            return sql.Substring(start, 32);
        }
    }

    [Fact]
    public async Task ServiceBrokerProgramIdentityIncludesTheSelectedEntryPoint()
    {
        var loaded = await new SharpSqlProjectCompiler().LoadCompilationAsync(
            ServiceBrokerProjectPath,
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(loaded.Success, string.Join(Environment.NewLine, loaded.Diagnostics));
        var options = new TranspileOptions { RuntimeStorage = RuntimeStorageKind.ServiceBroker };

        var main = new SharpSqlCompiler().Transpile(
            loaded.Compilation!,
            "ServiceBrokerProject.SqlJob::Main",
            options);
        var alternate = new SharpSqlCompiler().Transpile(
            loaded.Compilation!,
            "ServiceBrokerProject.SqlJob::Alternate",
            options);

        Assert.True(main.Success, string.Join(Environment.NewLine, main.Diagnostics));
        Assert.True(alternate.Success, string.Join(Environment.NewLine, alternate.Diagnostics));
        Assert.NotEqual(ProgramId(main.Sql), ProgramId(alternate.Sql));

        static string ProgramId(string sql)
        {
            const string marker = "CREATE OR ALTER PROCEDURE [SharpSql].[Program_";
            var start = sql.IndexOf(marker, StringComparison.Ordinal);
            Assert.True(start >= 0);
            start += marker.Length;
            return sql.Substring(start, 32);
        }
    }

    private static int Count(string value, string fragment)
    {
        var count = 0;
        var offset = 0;
        while ((offset = value.IndexOf(fragment, offset, StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += fragment.Length;
        }
        return count;
    }
}
