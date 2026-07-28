using Xunit;

namespace SharpSql.Tests;

public sealed class CompilerTests
{
    [Fact]
    public void TranslatesConsoleWriteLine()
    {
        var result = Compile("Console.WriteLine(\"Hello World\");");

        Assert.True(result.Success);
        Assert.Contains("PRINT N'Hello World';", result.Sql);
    }

    [Fact]
    public void TranslatesVariablesAndLoops()
    {
        const string source = """
            int total = 0;
            for (int i = 0; i < 3; i++)
            {
                total += i;
            }
            Console.WriteLine(total);
            """;

        var result = Compile(source);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        Assert.Contains("DECLARE @total INT = 0;", result.Sql);
        Assert.Contains("__sharpsql_for_condition:;", result.Sql);
        Assert.Contains("IF NOT (@i < 3) GOTO __sharpsql_for_break;", result.Sql);
        Assert.Contains("SET @total = @total + @i;", result.Sql);
        Assert.Contains("GOTO __sharpsql_for_condition;", result.Sql);
    }

    [Fact]
    public void InlinesExpressionMethod()
    {
        const string source = """
            int Square(int x) { return x * x; }
            int result = Square(5);
            """;

        var result = Compile(source);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        Assert.Contains("DECLARE @result INT = 5 * 5;", result.Sql);
        Assert.DoesNotContain("#__sharpsql_stack", result.Sql);
        Assert.DoesNotContain("CREATE FUNCTION", result.Sql);
    }

    [Fact]
    public void RemovesOnlyParenthesesThatOperatorPrecedenceMakesRedundant()
    {
        const string source = """
            int Square(int x) => x * x;
            int Add(int x, int y) => x + y;
            int a = 2;
            int b = 3;
            int c = 4;
            int simple = Square(5);
            int protectedValue = Square(a + b);
            int nested = a - (b - c);
            int multiplied = a * (b + c);
            int composed = Add(a * b, c);
            """;

        var result = Compile(source);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        Assert.Contains("@simple INT = 5 * 5;", result.Sql);
        Assert.Contains("@protectedValue INT = (@a + @b) * (@a + @b);", result.Sql);
        Assert.Contains("@nested INT = @a - (@b - @c);", result.Sql);
        Assert.Contains("@multiplied INT = @a * (@b + @c);", result.Sql);
        Assert.Contains("@composed INT = @a * @b + @c;", result.Sql);
    }

    [Fact]
    public void InlinesBranchingMethodWithoutSchemaObjects()
    {
        const string source = """
            int Clamp(int val, int lo, int hi)
            {
                if (val < lo) return lo;
                if (val > hi) return hi;
                return val;
            }
            int x = 125;
            int y = Clamp(x, 0, 100);
            """;

        var result = Compile(source);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        Assert.Contains("DECLARE @_clamp_1_val INT = @x;", result.Sql);
        Assert.Contains("DECLARE @y INT;", result.Sql);
        Assert.Contains("IF @_clamp_1_val < @_clamp_1_lo", result.Sql);
        Assert.Contains("GOTO __sharpsql__clamp_1_end;", result.Sql);
        Assert.DoesNotContain("_returned", result.Sql);
        Assert.DoesNotContain("CREATE ", result.Sql);
    }

    [Fact]
    public void LowersRecursiveMethodToOneStackAndDispatcher()
    {
        const string source = """
            int Fibonacci(int n)
            {
                if (n < 2) return n;
                return Fibonacci(n - 1) + Fibonacci(n - 2);
            }
            int value = Fibonacci(8);
            """;

        var result = Compile(source);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        Assert.Contains("CREATE TABLE #__sharpsql_stack", result.Sql);
        Assert.Contains("GOTO __sharpsql_vm_Fibonacci_entry;", result.Sql);
        Assert.Contains("__sharpsql_dispatch:;", result.Sql);
        Assert.Contains("IF @__sharpsql_jump = 1 GOTO", result.Sql);
        Assert.Contains("IF @__sharpsql_jump = 2 GOTO", result.Sql);
        Assert.Contains("DROP TABLE IF EXISTS #__sharpsql_stack;", result.Sql);
        Assert.Equal(1, Count(result.Sql, "__sharpsql_dispatch:;"));
        Assert.Equal(3, Count(result.Sql, "IF @__sharpsql_jump ="));
        Assert.DoesNotContain("CREATE PROCEDURE", result.Sql);
        Assert.DoesNotContain("CREATE FUNCTION", result.Sql);
    }

    [Fact]
    public void PreservesIntermediateResultsAcrossTwoRecursiveCalls()
    {
        const string source = """
            int Fibonacci(int n)
            {
                if (n < 2) return n;
                return Fibonacci(n - 1) + Fibonacci(n - 2);
            }
            int value = Fibonacci(8);
            """;

        var result = Compile(source);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        Assert.Contains("__slot_id = 3", result.Sql);
        Assert.Contains("SET @_vm_Fibonacci_return =", result.Sql);
        Assert.Contains("CONVERT(INT, @__sharpsql_result)", result.Sql);
    }

    [Fact]
    public void LowersExpressionBodiedRecursionAndOverBudgetMethods()
    {
        const string recursive = """
            int Factorial(int n) => n <= 1 ? 1 : n * Factorial(n - 1);
            int value = Factorial(5);
            """;
        var recursiveResult = Compile(recursive);

        Assert.True(recursiveResult.Success, string.Join(Environment.NewLine, recursiveResult.Diagnostics));
        Assert.Contains("stack-machine body: Factorial", recursiveResult.Sql);
        Assert.Contains("vm_conditional_false", recursiveResult.Sql);

        const string overBudget = """
            int Clamp(int value, int low, int high)
            {
                if (value < low) return low;
                if (value > high) return high;
                return value;
            }
            int result = Clamp(10, 0, 5);
            """;
        var budgetResult = new SharpSqlCompiler().Transpile(
            overBudget,
            new TranspileOptions { MaxInlineStatements = 1 });

        Assert.True(budgetResult.Success, string.Join(Environment.NewLine, budgetResult.Diagnostics));
        Assert.Contains("stack-machine body: Clamp", budgetResult.Sql);
        Assert.DoesNotContain("SS3003", string.Join(Environment.NewLine, budgetResult.Diagnostics));
    }

    [Fact]
    public void StackMachineSupportsLocalsLoopsAndRecursiveTailCalls()
    {
        const string source = """
            int SumDown(int n)
            {
                if (n < 0) return SumDown(-n);
                int total = 0;
                while (n > 0)
                {
                    total += n;
                    n--;
                }
                return total;
            }
            int sum = SumDown(5);
            """;

        var result = Compile(source);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        Assert.Contains("DECLARE @_vm_SumDown_total INT;", result.Sql);
        Assert.Contains("vm_while_condition", result.Sql);
        Assert.Contains("SET @_vm_SumDown_total = @_vm_SumDown_total + (@_vm_SumDown_n);", result.Sql);
        Assert.Contains("GOTO __sharpsql_dispatch;", result.Sql);
    }

    [Fact]
    public void StackMachineCallsWorkInsideTopLevelConditions()
    {
        const string source = """
            int Factorial(int n) => n <= 1 ? 1 : n * Factorial(n - 1);
            if (Factorial(5) == 120)
                Console.WriteLine("correct");
            """;

        var result = Compile(source);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        Assert.Contains("IF CASE WHEN", result.Sql);
        Assert.Contains("PRINT N'correct';", result.Sql);
    }

    [Fact]
    public void StackMachineSupportsMutualRecursion()
    {
        const string source = """
            bool IsEven(int n)
            {
                if (n == 0) return true;
                return IsOdd(n - 1);
            }
            bool IsOdd(int n)
            {
                if (n == 0) return false;
                return IsEven(n - 1);
            }
            bool result = IsEven(10);
            """;

        var result = Compile(source);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        Assert.Contains("stack-machine body: IsEven", result.Sql);
        Assert.Contains("stack-machine body: IsOdd", result.Sql);
        Assert.Equal(1, Count(result.Sql, "__sharpsql_dispatch:;"));
    }

    [Fact]
    public void StackMachinePreservesShortCircuitingAndCompoundAssignment()
    {
        const string source = """
            bool NeverReturns(int n) => NeverReturns(n + 1);
            int Factorial(int n) => n <= 1 ? 1 : n * Factorial(n - 1);
            bool safe = true || NeverReturns(0);
            int value = 1;
            value += Factorial(5);
            """;

        var result = Compile(source);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        Assert.Contains("vm_short_circuit", result.Sql);
        Assert.Contains("SET @value = @value + (CONVERT(INT, @__sharpsql_result));", result.Sql);
    }

    [Fact]
    public void RecordsUseTypedHeapRowsAndReferenceIdentity()
    {
        const string source = """
            Person person = new Person("Ada", 36);
            Person alias = person;
            person.Name = "Grace";
            Console.WriteLine(alias.Name);
            record Person(string Name, int Age);
            """;

        var result = Compile(source);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        Assert.Contains("CREATE TABLE #__sharpsql_objects", result.Sql);
        Assert.Contains("[Name] NVARCHAR(MAX)", result.Sql);
        Assert.Contains("DECLARE @alias BIGINT = @person;", result.Sql);
        Assert.Contains("UPDATE #__sharpsql_type_1 SET [Name] = N'Grace'", result.Sql);
        Assert.Contains("WHERE __object_id = @alias", result.Sql);
    }

    [Fact]
    public void ListsAndDictionariesStoreScalarAndReferenceValues()
    {
        const string source = """
            List<Person> people = new List<Person>();
            people.Add(new Person("Ada", 36));
            people[0].Age = 37;
            Dictionary<string, Person> byName = new Dictionary<string, Person>();
            byName.Add("ada", people[0]);
            byName["grace"] = new Person("Grace", 30);
            Console.WriteLine($"{people.Count}:{byName.Count}:{byName.ContainsKey("ada")}:{byName["grace"].Name}");
            record Person(string Name, int Age);
            """;

        var result = Compile(source);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        Assert.Contains("CREATE TABLE #__sharpsql_list_items", result.Sql);
        Assert.Contains("__reference_value BIGINT", result.Sql);
        Assert.Contains("CREATE TABLE #__sharpsql_dictionary_entries", result.Sql);
        Assert.Contains("__key_text COLLATE Latin1_General_100_BIN2", result.Sql);
        Assert.Contains("CASE WHEN EXISTS", result.Sql);
    }

    [Fact]
    public void FormatsBooleansLikeCSharpInsideInterpolatedStrings()
    {
        const string source = """
            bool yes = true;
            bool no = false;
            Dictionary<string, int> values = new Dictionary<string, int>();
            values.Add("answer", 42);
            Console.WriteLine($"{yes}:{no}:{1 < 2}:{values.ContainsKey("answer")}");
            """;

        var result = Compile(source);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        Assert.Contains("CASE @yes WHEN CAST(1 AS BIT) THEN N'True' WHEN CAST(0 AS BIT) THEN N'False' ELSE N'' END", result.Sql);
        Assert.Contains("CASE @no WHEN CAST(1 AS BIT) THEN N'True' WHEN CAST(0 AS BIT) THEN N'False' ELSE N'' END", result.Sql);
        Assert.Equal(4, Count(result.Sql, "THEN N'True' WHEN CAST(0 AS BIT) THEN N'False' ELSE N'' END"));
        Assert.Contains("CASE WHEN EXISTS", result.Sql);
    }

    [Fact]
    public void HeapReferencesSurviveRecursiveVmFrames()
    {
        const string source = """
            Person person = new Person("Ada", 36);
            Person result = AddYears(person, 2);
            Console.WriteLine(result.Age);
            Person AddYears(Person value, int years)
            {
                if (years == 0) return value;
                value.Age = value.Age + 1;
                return AddYears(value, years - 1);
            }
            record Person(string Name, int Age);
            """;

        var result = Compile(source);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        Assert.Contains("stack-machine body: AddYears", result.Sql);
        Assert.Contains("DECLARE @_vm_AddYears_value BIGINT;", result.Sql);
        Assert.Contains("UPDATE #__sharpsql_type_1 SET [Age]", result.Sql);
        Assert.Contains("DROP TABLE IF EXISTS #__sharpsql_objects;", result.Sql);
    }

    [Fact]
    public void ArraysAndForeachUseIndexedHeapStorage()
    {
        const string source = """
            int[] values = new int[] { 2, 4, 6 };
            values[1] = 5;
            int total = 0;
            foreach (int value in values) total += value;
            Console.WriteLine($"{values.Length}:{total}");
            """;

        var result = Compile(source);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        Assert.Contains("VALUES (1003)", result.Sql);
        Assert.Contains("foreach_condition", result.Sql);
        Assert.Contains("SET @value = (SELECT CONVERT(INT, __value)", result.Sql);
        Assert.Contains("SET @total = @total + @value;", result.Sql);
    }

    [Fact]
    public void InstanceMethodsReceiveThisAndCanMutateFields()
    {
        const string source = """
            Person person = new Person("Ada", 36);
            person.Birthday();
            Console.WriteLine(person.Greet());
            record Person(string Name, int Age)
            {
                public string Greet() => Name + ":" + Age;
                public void Birthday() { Age = Age + 1; }
            }
            """;

        var result = Compile(source);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        Assert.Contains("DECLARE @_birthday_1_this BIGINT = @person;", result.Sql);
        Assert.Contains("UPDATE #__sharpsql_type_1 SET [Age]", result.Sql);
        Assert.Contains("WHERE __object_id = @person", result.Sql);
    }

    [Fact]
    public void RecursiveVmMethodsCanEnumerateLists()
    {
        const string source = """
            List<int> values = new List<int> { 1, 2, 3 };
            int total = SumRepeated(values, 2);
            int SumRepeated(List<int> items, int times)
            {
                if (times == 0) return 0;
                int subtotal = 0;
                foreach (int item in items) subtotal += item;
                return subtotal + SumRepeated(items, times - 1);
            }
            """;

        var result = Compile(source);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        Assert.Contains("vm_foreach_condition", result.Sql);
        Assert.Contains("UPDATE #__sharpsql_slots SET __value", result.Sql);
        Assert.Contains("stack-machine body: SumRepeated", result.Sql);
    }

    [Fact]
    public void UsesSqlNullPredicatesAndCanAssignAComplexCall()
    {
        const string source = """
            int Pick(int value)
            {
                if (value > 0) return value;
                return 0;
            }
            string? text = null;
            bool missing = text == null;
            int result = 0;
            result = Pick(4);
            """;

        var result = Compile(source);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        Assert.Contains("WHEN @text IS NULL", result.Sql);
        Assert.Equal(1, Count(result.Sql, "DECLARE @result INT"));
        Assert.Contains("SET @result = @_pick_1_value;", result.Sql);
    }

    [Fact]
    public void CompilerInstancesCanBeReused()
    {
        var compiler = new SharpSqlCompiler();

        Assert.Contains("PRINT N'one';", compiler.Transpile("Console.WriteLine(\"one\");").Sql);
        Assert.Contains("PRINT N'two';", compiler.Transpile("Console.WriteLine(\"two\");").Sql);
    }

    [Fact]
    public void MapsCoreValueTypesWithoutNarrowingUnsignedValues()
    {
        const string source = """
            bool boolean;
            byte unsignedByte;
            sbyte signedByte;
            short signedShort;
            ushort unsignedShort;
            int signedInt;
            uint unsignedInt;
            long signedLong;
            ulong unsignedLong;
            float single;
            double doubleValue;
            decimal decimalValue;
            char character;
            string text;
            object boxed;
            DateTime timestamp;
            DateOnly date;
            TimeOnly time;
            Guid id;
            byte[] bytes;
            int? nullableInt;
            """;

        var sql = Compile(source).Sql;

        Assert.Contains("@boolean BIT", sql);
        Assert.Contains("@unsignedByte TINYINT", sql);
        Assert.Contains("@signedByte SMALLINT", sql);
        Assert.Contains("@unsignedShort INT", sql);
        Assert.Contains("@unsignedInt BIGINT", sql);
        Assert.Contains("@unsignedLong DECIMAL(20,0)", sql);
        Assert.Contains("@decimalValue DECIMAL(38,18)", sql);
        Assert.Contains("@text NVARCHAR(MAX)", sql);
        Assert.Contains("@timestamp DATETIME2", sql);
        Assert.Contains("@id UNIQUEIDENTIFIER", sql);
        Assert.Contains("@bytes VARBINARY(MAX)", sql);
        Assert.Contains("@nullableInt INT", sql);
    }

    [Fact]
    public void LowersNestedLoopExitsToTheCorrectUniqueLabels()
    {
        const string source = """
            int count = 0;
            while (count < 4)
            {
                count++;
                if (count == 2) continue;
                do
                {
                    break;
                } while (count < 3);
            }
            """;

        var result = Compile(source);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        Assert.Contains("GOTO __sharpsql_while_continue;", result.Sql);
        Assert.Contains("GOTO __sharpsql_do_break;", result.Sql);
        Assert.Contains("__sharpsql_do_continue:;", result.Sql);
        Assert.Contains("__sharpsql_while_break:;", result.Sql);
        Assert.DoesNotContain("WHILE", result.Sql);
    }

    [Fact]
    public void PreservesCommentsBesideTheirStatementsAndExpressionsOnce()
    {
        const string source = """
            // File-level explanation.
            int value = 4; // Keep the input.
            /* Explain the calculation. */
            int doubled = value /* use the original */ * 2;
            Console.WriteLine(doubled);
            """;

        var result = Compile(source);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        Assert.Contains("-- File-level explanation.\nSET NOCOUNT ON;", result.Sql);
        Assert.Contains("DECLARE @value INT = 4;\n-- Keep the input.", result.Sql);
        Assert.Contains("/* Explain the calculation. */\n/* use the original */\nDECLARE @doubled INT = @value * 2;", result.Sql);
        Assert.Equal(1, Count(result.Sql, "File-level explanation"));
        Assert.Equal(1, Count(result.Sql, "Keep the input"));
        Assert.Equal(1, Count(result.Sql, "Explain the calculation"));
        Assert.Equal(1, Count(result.Sql, "use the original"));
    }

    [Fact]
    public void PreservesCommentsInInlinedAndStackMachineMethods()
    {
        const string inlineSource = """
            // Clamp values to the requested range.
            int Clamp(int value, int low, int high)
            {
                // Handle the lower bound.
                if (value < low) return low;
                /* Otherwise preserve the value. */
                return value;
            }
            int result = Clamp(3, 0, 5);
            """;

        var inlineResult = Compile(inlineSource);

        Assert.True(inlineResult.Success, string.Join(Environment.NewLine, inlineResult.Diagnostics));
        Assert.Contains("-- Clamp values to the requested range.", inlineResult.Sql);
        Assert.Contains("-- Handle the lower bound.\nIF", inlineResult.Sql);
        Assert.Contains("/* Otherwise preserve the value. */\nSET @result", inlineResult.Sql);

        const string recursiveSource = """
            int Factorial(int n)
            {
                // Stop recursive descent.
                if (n <= 1) return 1;
                // Multiply after the recursive call.
                return n * Factorial(n - 1);
            }
            int result = Factorial(5);
            """;

        var recursiveResult = Compile(recursiveSource);

        Assert.True(recursiveResult.Success, string.Join(Environment.NewLine, recursiveResult.Diagnostics));
        Assert.Contains("-- Stop recursive descent.\nIF", recursiveResult.Sql);
        Assert.Contains("-- Multiply after the recursive call.", recursiveResult.Sql);
        Assert.Equal(1, Count(recursiveResult.Sql, "Stop recursive descent"));
    }

    [Fact]
    public void PreservesTypeAndMemberCommentsOnGeneratedHeapTables()
    {
        const string source = """
            Person person = new Person { Name = "Ada" };
            Console.WriteLine(person.Name);

            /// A person stored in the managed heap.
            class Person
            {
                // The display name.
                public string Name { get; set; }
            }
            """;

        var result = Compile(source);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        Assert.Contains("-- A person stored in the managed heap.\nCREATE TABLE #__sharpsql_type_", result.Sql);
        Assert.Contains("-- The display name.\n    [Name] NVARCHAR(MAX) NULL", result.Sql);
        Assert.Equal(1, Count(result.Sql, "A person stored in the managed heap"));
        Assert.Equal(1, Count(result.Sql, "The display name"));
    }

    [Fact]
    public void DoesNotTreatCommentMarkersInsideStringsAsComments()
    {
        var result = Compile("Console.WriteLine(\"https://example.test/*literal*/\");");

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        Assert.Contains("PRINT N'https://example.test/*literal*/';", result.Sql);
        Assert.Equal(1, Count(result.Sql, "/*literal*/"));
    }

    private static TranspileResult Compile(string source) => new SharpSqlCompiler().Transpile(source);

    private static int Count(string value, string needle) =>
        (value.Length - value.Replace(needle, string.Empty, StringComparison.Ordinal).Length) / needle.Length;
}
