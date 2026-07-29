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
        Assert.Contains("__caller_id INT NULL", result.Sql);
        Assert.Contains("SET @__sharpsql_frame_id = @__sharpsql_caller_frame_id;", result.Sql);
        Assert.DoesNotContain("SELECT MAX(__id) FROM #__sharpsql_stack", result.Sql);
        Assert.Contains("UPDATE #__sharpsql_slots SET __value", result.Sql);
        Assert.DoesNotContain("DELETE FROM #__sharpsql_slots WHERE __frame_id = @__sharpsql_frame_id AND __slot_id", result.Sql);
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
        Assert.Contains("DECLARE @alias INT = @person;", result.Sql);
        Assert.Contains("UPDATE #__sharpsql_type_1 SET [Name] = N'Grace'", result.Sql);
        Assert.Contains("WHERE __object_id = @alias", result.Sql);
    }

    [Fact]
    public void FormatsRecordsWhenWritingOrInterpolatingThem()
    {
        const string source = """
            var item = new InvItem("Gold", 256);
            Console.WriteLine(item);
            Console.WriteLine($"item={item}");
            record InvItem(string Name, int Quantity);
            """;

        var result = Compile(source);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        Assert.Contains("N'InvItem { '", result.Sql);
        Assert.Contains("N'Name = '", result.Sql);
        Assert.Contains("N'Quantity = '", result.Sql);
        Assert.Contains("WHERE __object_id = @item", result.Sql);
        Assert.DoesNotContain("PRINT @item;", result.Sql);
    }

    [Fact]
    public void SupportsTargetTypedRecordCreationAndWithExpressionsContainingRuntimeCalls()
    {
        const string source = """
            var inventory = new List<InvItem>
            {
                new("Wood", 64),
                new("Iron", 64),
                new("Gold", 256)
            };
            var random = new Random(4);
            var item = inventory[random.Next(0, inventory.Count)];
            inventory.Add(item with
            {
                Quantity = random.Next(
                    inventory.Min(candidate => candidate.Quantity),
                    inventory.Max(candidate => candidate.Quantity))
            });
            record InvItem(string Name, int Quantity);
            """;

        var result = Compile(source);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        Assert.Contains("SELECT [Name] FROM #__sharpsql_type_1", result.Sql);
        Assert.Contains("SELECT MIN(", result.Sql);
        Assert.Contains("SELECT MAX(", result.Sql);
        Assert.Contains("INSERT INTO #__sharpsql_type_1 (__object_id, [Name], [Quantity])", result.Sql);
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
        Assert.Contains("CREATE TABLE #__sharpsql_indexed_items", result.Sql);
        Assert.Contains("__reference_value INT", result.Sql);
        Assert.Contains("CREATE TABLE #__sharpsql_dictionary_entries", result.Sql);
        Assert.Contains("PRIMARY KEY (__dictionary_id, __id)", result.Sql);
        Assert.Contains("CREATE INDEX __sharpsql_dictionary_hash_key", result.Sql);
        Assert.Contains("__key_hash = HASHBYTES('SHA2_256'", result.Sql);
        Assert.Contains("__key_text COLLATE Latin1_General_100_BIN2", result.Sql);
        Assert.Contains("CASE WHEN EXISTS", result.Sql);
    }

    [Fact]
    public void ScalarDictionaryKeysUseASeekableTypedIndexPredicate()
    {
        const string source = """
            var values = new Dictionary<int, string>();
            values.Add(42, "answer");
            Console.WriteLine(values.ContainsKey(42));
            """;

        var result = Compile(source);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        Assert.Contains("CREATE INDEX __sharpsql_dictionary_scalar_key", result.Sql);
        Assert.Contains("__key = CONVERT(SQL_VARIANT, 42)", result.Sql);
        Assert.DoesNotContain("CONVERT(INT, __key) = 42", result.Sql);
    }

    [Fact]
    public void BatchesCollectionAndArrayInitializersIntoMultiRowInserts()
    {
        const string source = """
            var people = new List<Person>
            {
                new Person("Bob", 40),
                new Person("Jane", 20),
                new Person("Saul", 55)
            };
            int[] values = new int[] { 3, 1, 4 };
            record Person(string Name, int Age);
            """;

        var result = Compile(source);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        Assert.Equal(1, Count(result.Sql, "(__owner_id, __index, __reference_value) VALUES"));
        Assert.Equal(1, Count(result.Sql, "(__owner_id, __index, __value) VALUES"));
        Assert.Contains("(@_object_4, 0, @_object),", result.Sql);
        Assert.Contains("(@_object_4, 2, @_object_3);", result.Sql);
    }

    [Fact]
    public void ChunksMultiRowInsertsAtSqlServersThousandRowLimit()
    {
        var items = string.Join(", ", Enumerable.Range(0, 1001));
        var result = Compile($"var values = new List<int> {{ {items} }};");

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        Assert.Equal(2, Count(result.Sql, "(__owner_id, __index, __value) VALUES"));
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
    public void ConvertsSingleInterpolationHolesWithoutUnaryConcat()
    {
        const string source = """
            int value = 42;
            string text = $"{value}";
            Console.WriteLine(text);
            """;

        var result = Compile(source);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        Assert.Contains("DECLARE @text NVARCHAR(MAX) = CONCAT(N'', @value);", result.Sql);
        Assert.DoesNotContain("CONCAT(@value)", result.Sql);
    }

    [Fact]
    public void LowersRandomInstancesToIndependentEphemeralState()
    {
        const string source = """
            Random random = new Random(12345);
            int value = random.Next();
            int bounded = random.Next(100);
            int ranged = random.Next(-10, 11);
            double fraction = random.NextDouble();
            int roll = Roll(random);
            int Roll(Random source) => source.Next(1, 7);
            """;

        var result = Compile(source);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        Assert.Contains("CREATE TABLE #__sharpsql_objects", result.Sql);
        Assert.Contains("__random_inext INT NULL", result.Sql);
        Assert.Contains("CREATE TABLE #__sharpsql_indexed_items", result.Sql);
        Assert.Contains("DECLARE @_random_seed INT = 12345;", result.Sql);
        Assert.Contains("Random maximum must be non-negative.", result.Sql);
        Assert.Contains("Random minimum must not exceed maximum.", result.Sql);
        Assert.Contains("DECLARE @_random_double FLOAT", result.Sql);
        Assert.Contains("stack-machine body: Roll", result.Sql);
        Assert.Contains("DROP TABLE IF EXISTS #__sharpsql_indexed_items;", result.Sql);
        Assert.DoesNotContain("#__sharpsql_randoms", result.Sql);
        Assert.DoesNotContain("#__sharpsql_random_state", result.Sql);
        Assert.DoesNotContain("RAND(", result.Sql);
    }

    [Fact]
    public void TrimsUnreachableLargeRangeRandomBranches()
    {
        const string source = """
            var random = new Random(42);
            int maximum = 100;
            int small = random.Next(0, maximum);
            int knownSmall = random.Next(-10, 11);
            int fullRange = random.Next(-2147483648, 2147483647);
            """;

        var result = Compile(source);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        Assert.Equal(1, Count(result.Sql, "DECLARE @_random_large_sample"));
        Assert.Contains("% 55 + 1", result.Sql);
    }

    [Fact]
    public void BuildsStringsFromRuntimeIndexedCharacterArraysInsideVmMethods()
    {
        const string source = """
            var people = new List<Person>();
            var random = new Random();

            for (int i = 0; i < 5; i++)
                people.Add(new Person(GenerateName(), random.Next(1, 100)));

            string GenerateName()
            {
                var chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";
                var stringChars = new char[8];
                var source = new Random();
                for (int i = 0; i < stringChars.Length; i++)
                    stringChars[i] = chars[source.Next(chars.Length)];
                return new string(stringChars);
            }

            record Person(string Name, int Age);
            """;

        var result = Compile(source);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        Assert.Contains("DATALENGTH(@_vm_GenerateName_chars) / 2", result.Sql);
        Assert.Contains("SUBSTRING(@_vm_GenerateName_chars", result.Sql);
        Assert.Contains("STRING_AGG(CONVERT(NVARCHAR(MAX), CONVERT(NCHAR(1), __value)), N'')", result.Sql);
        Assert.Contains("stack-machine body: GenerateName", result.Sql);
    }

    [Fact]
    public void MaterializesStatefulRepeatSelectPipelinesWithCapturedEntryVariables()
    {
        const string source = """
            var people = new List<Person>();
            var random = new Random();
            for (int i = 0; i < 5; i++)
                people.Add(new Person(RandomString(8), random.Next(1, 100)));

            string RandomString(int length)
            {
                const string chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
                return new string(Enumerable.Repeat(chars, length)
                    .Select(value => value[random.Next(value.Length)]).ToArray());
            }

            record Person(string Name, int Age);
            """;

        var result = Compile(source);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        Assert.Contains("stack-machine body: RandomString", result.Sql);
        Assert.Contains("Enumerable.Repeat count must be non-negative.", result.Sql);
        Assert.Contains("WHILE @_repeat_index <", result.Sql);
        Assert.Contains("WHERE __id = @random", result.Sql);
        Assert.Contains("CAST(1 AS FLOAT) / CAST(2147483647 AS FLOAT)", result.Sql);
        Assert.Contains("SUBSTRING(", result.Sql);
        Assert.Contains("STRING_AGG(", result.Sql);
    }

    [Fact]
    public void ReusesHeapTablesAcrossObjectsListsAndRandomState()
    {
        const string source = """
            var people = new List<Person>();
            var random = new Random();
            var names = new List<string> { "Bob", "Jane", "Billy", "James", "Saul" };

            for (int i = 0; i < 5; i++)
                people.Add(new Person(names[i], random.Next(1, 100)));

            foreach (var person in people)
                Console.WriteLine($"{person.Name} - {person.Age}");

            var total = 0;
            foreach (var person in people)
                total += person.Age;
            Console.WriteLine($"sum = {total}");

            record Person(string Name, int Age);
            """;

        var result = Compile(source);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        Assert.Equal(3, Count(result.Sql, "CREATE TABLE #__sharpsql_"));
        Assert.Contains("CREATE TABLE #__sharpsql_objects", result.Sql);
        Assert.Contains("CREATE TABLE #__sharpsql_type_1", result.Sql);
        Assert.Contains("CREATE TABLE #__sharpsql_indexed_items", result.Sql);
        Assert.DoesNotContain("#__sharpsql_lists", result.Sql);
        Assert.DoesNotContain("#__sharpsql_list_items", result.Sql);
        Assert.DoesNotContain("#__sharpsql_randoms", result.Sql);
        Assert.DoesNotContain("#__sharpsql_random_state", result.Sql);
    }

    [Fact]
    public void LowersLinqSumSelectorToOneRelationalAggregate()
    {
        const string source = """
            var people = new List<Person>
            {
                new Person("Bob", 40),
                new Person("Jane", 35)
            };

            var total = people.Sum(person => person.Age);
            Console.WriteLine($"sum = {total}");

            record Person(string Name, int Age);
            """;

        var result = Compile(source);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        Assert.Contains("DECLARE @total INT = COALESCE((SELECT SUM(__linq_terminal_", result.Sql);
        Assert.Contains("(SELECT [Age] FROM #__sharpsql_type_1", result.Sql);
        Assert.Contains("__object_id = __linq_source_", result.Sql);
        Assert.Contains("FROM #__sharpsql_indexed_items AS __linq_item_1", result.Sql);
        Assert.Contains("CAST(0 AS INT)", result.Sql);
    }

    [Fact]
    public void LowersLinqSumWithoutSelectorAndPreservesEmptySequenceZero()
    {
        const string source = """
            var values = new List<long>();
            var total = values.Sum();
            """;

        var result = Compile(source);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        Assert.Contains("CONVERT(BIGINT, __linq_item_1.__value) AS __value", result.Sql);
        Assert.Contains("CAST(0 AS BIGINT)", result.Sql);
    }

    [Fact]
    public void ComposesQueryableWhereSelectAndTerminalOperators()
    {
        const string source = """
            var people = new List<Person>
            {
                new Person("Bob", 40),
                new Person("Jane", 20),
                new Person("Saul", 55)
            };
            IQueryable<Person> query = people.AsQueryable();
            var adults = query.Where(person => person.Age >= 21);
            int total = adults.Select(person => person.Age).Sum();
            int count = adults.Count();
            bool anyJane = query.Any(person => person.Name == "Jane");
            bool allPositive = query.All(person => person.Age > 0);
            record Person(string Name, int Age);
            """;

        var result = Compile(source);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        Assert.Contains("DECLARE @query INT = @people;", result.Sql);
        Assert.Contains("DECLARE @adults INT = @query;", result.Sql);
        Assert.Contains("SELECT SUM(__linq_terminal_", result.Sql);
        Assert.Contains("SELECT COUNT(*)", result.Sql);
        Assert.Contains("CASE WHEN EXISTS", result.Sql);
        Assert.Contains("CASE WHEN NOT EXISTS", result.Sql);
        Assert.Contains("[Age]", result.Sql);
        Assert.Contains("[Name]", result.Sql);
    }

    [Fact]
    public void LowersQuerySyntaxForeachAndMaterialization()
    {
        const string source = """
            var people = new List<Person>
            {
                new Person("Bob", 40),
                new Person("Jane", 20)
            };
            var ages = from person in people
                       where person.Age >= 21
                       select person.Age;
            var materialized = ages.ToList();
            foreach (var age in ages)
                Console.WriteLine(age);
            bool contains = ages.Contains(40);
            int firstMissing = ages.Where(age => age > 100).FirstOrDefault();
            record Person(string Name, int Age);
            """;

        var result = Compile(source);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        Assert.Contains("DECLARE @ages INT = @people;", result.Sql);
        Assert.Contains("ROW_NUMBER() OVER (ORDER BY", result.Sql);
        Assert.Contains("INT = @@ROWCOUNT;", result.Sql);
        Assert.DoesNotContain("SET __count = (SELECT COUNT(*) FROM #__sharpsql_indexed_items", result.Sql);
        Assert.Contains("__sharpsql_linq_foreach_condition", result.Sql);
        Assert.Contains("CASE WHEN EXISTS", result.Sql);
        Assert.Contains("SELECT TOP (1)", result.Sql);
    }

    [Fact]
    public void LowersAdvancedOrderingPagingJoinAndGroupingStages()
    {
        const string source = """
            var values = new List<int> { 4, 1, 3, 1, 2, 4 };
            var page = values.Distinct()
                .OrderByDescending(value => value)
                .ThenBy(value => value)
                .Skip(1)
                .Take(2)
                .ToList();
            var people = new List<Person> { new Person("Bob", 40), new Person("Jane", 20) };
            var bands = new List<Band> { new Band(20, "young"), new Band(40, "older") };
            var joined = people.Join(bands, person => person.Age, band => band.Age,
                (person, band) => person.Name + ":" + band.Label).ToList();
            var groupKeys = people.GroupBy(person => person.Age)
                .Select(group => group.Key).OrderBy(value => value).ToArray();
            record Person(string Name, int Age);
            record Band(int Age, string Label);
            """;

        var result = Compile(source);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        Assert.Contains("ROW_NUMBER() OVER (ORDER BY", result.Sql);
        Assert.Contains("GROUP BY", result.Sql);
        Assert.Contains("INNER JOIN", result.Sql);
        Assert.Contains("__ordinal >=", result.Sql);
        Assert.Contains("__ordinal <", result.Sql);
    }

    [Fact]
    public void DiagnosesMaterializingFullGroupingValues()
    {
        const string source = """
            var values = new List<int> { 1, 2, 1 };
            var groups = values.GroupBy(value => value).ToList();
            """;

        var result = Compile(source);

        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "SS6411");
    }

    [Fact]
    public void EmitsExplicitGuardsForAggregateAndElementTerminals()
    {
        const string source = """
            var values = new List<int> { 4, 1, 3 };
            int minimum = values.Min();
            int maximum = values.Max();
            double average = values.Average();
            int minimumBy = values.MinBy(value => -value);
            int maximumBy = values.MaxBy(value => -value);
            int first = values.First();
            int last = values.Last();
            int single = values.Where(value => value == 3).Single();
            int singleDefault = values.Where(value => value == 99).SingleOrDefault();
            int element = values.ElementAt(1);
            int missing = values.ElementAtOrDefault(99);
            """;

        var result = Compile(source);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        Assert.Contains("LINQ sequence contains no elements.", result.Sql);
        Assert.Contains("LINQ sequence contains more than one element.", result.Sql);
        Assert.Contains("LINQ index was out of range.", result.Sql);
        Assert.Contains("SELECT MIN(", result.Sql);
        Assert.Contains("SELECT MAX(", result.Sql);
        Assert.Contains("SELECT AVG(", result.Sql);
        Assert.Contains("ROW_NUMBER() OVER (ORDER BY", result.Sql);
    }

    [Fact]
    public void FlowsDeferredQueriesAndCapturedDelegateVariablesThroughMethods()
    {
        const string source = """
            var values = new List<int> { 1, 2, 3, 4 };
            int threshold = 2;
            Func<int, bool> predicate = value => value > threshold;
            var filtered = Filter(values, predicate);
            var limited = FilterAbove(filtered, 2);
            Func<int, bool> returnedPredicate = AtLeast(threshold);
            threshold = 3;
            int count = CountMatches(limited, returnedPredicate);
            IEnumerable<int> Filter(IEnumerable<int> source, Func<int, bool> test) => source.Where(test);
            IEnumerable<int> FilterAbove(IEnumerable<int> source, int minimum) => source.Where(value => value > minimum);
            int CountMatches(IEnumerable<int> source, Func<int, bool> test) => source.Count(test);
            Func<int, bool> AtLeast(int minimum) => value => value >= minimum;
            """;

        var result = Compile(source);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        Assert.Contains("DECLARE @predicate INT = NULL;", result.Sql);
        Assert.Contains("DECLARE @filtered INT = @values;", result.Sql);
        Assert.Contains("__value > @threshold", result.Sql);
        Assert.Contains("DECLARE @_linq_capture", result.Sql);
        Assert.Contains("__value > @_linq_capture", result.Sql);
        Assert.Contains("__value >= @_linq_capture", result.Sql);
    }

    [Fact]
    public void ReportsDefiniteAssignmentFailuresBeforeLowering()
    {
        const string source = """
            int value;
            Console.WriteLine(value);
            """;

        var result = Compile(source);

        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "CS0165");
    }

    [Theory]
    [InlineData("void Assign(out int value) { }", "CS0177")]
    [InlineData("Console.WriteLine(value); int value = 1;", "CS0841")]
    public void ReportsOtherMethodBodyFlowFailures(string source, string expectedCode)
    {
        var result = Compile(source);

        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == expectedCode);
    }

    [Fact]
    public void UsesConstantFlowFactsWhenRenderingPredicates()
    {
        const string source = """
            if (1 + 1 == 2)
                Console.WriteLine("yes");
            """;

        var result = Compile(source);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        Assert.Contains("IF 1 = 1", result.Sql);
        Assert.DoesNotContain("1 + 1 = 2", result.Sql);
    }

    [Fact]
    public void MethodFlowSummaryRejectsReachableNonVoidEndpoint()
    {
        const string source = """
            int result = Choose(false);
            int Choose(bool choose)
            {
                if (choose)
                    return 1;
            }
            """;

        var result = Compile(source);

        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "CS0161");
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "SS3004");
    }

    [Fact]
    public void ProceduralIrPreservesNestedLoopAndBranchExits()
    {
        const string source = """
            int total = 0;
            for (int i = 0; i < 5; i++)
            {
                if (i == 1)
                    continue;
                if (i == 4)
                    break;
                total += i;
            }
            Console.WriteLine(total);
            """;

        var result = Compile(source);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        Assert.Contains("GOTO __sharpsql_for_continue", result.Sql);
        Assert.Contains("GOTO __sharpsql_for_break", result.Sql);
        Assert.Contains("SET @total = @total + @i", result.Sql);
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
        Assert.Contains("DECLARE @_vm_AddYears_value INT;", result.Sql);
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
        Assert.Contains("(__type_id, __count) VALUES (1003, 3)", result.Sql);
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
        Assert.Contains("DECLARE @_birthday_1_this INT = @person;", result.Sql);
        Assert.Contains("UPDATE #__sharpsql_type_1 SET [Age]", result.Sql);
        Assert.Contains("WHERE __object_id = @person", result.Sql);
    }

    [Fact]
    public void CompoundAssignmentsCanMutateHeapFields()
    {
        const string source = """
            Counter counter = new Counter(2);
            counter.Add(3);
            Console.WriteLine(counter.Value);
            class Counter
            {
                public Counter(int initial) { Value = initial; }
                public int Value { get; set; }
                public void Add(int amount) { Value += amount; }
            }
            """;

        var result = Compile(source);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        Assert.Contains("SET [Value] = (SELECT [Value]", result.Sql);
        Assert.Contains("+ (@_add_1_amount)", result.Sql);
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
    public void PreservesFloatingPointLiteralTypesDuringArithmetic()
    {
        const string source = """
            double ratio = 5.0 / 2.0;
            float single = 5f / 2f;
            decimal exact = 5m / 2m;
            """;

        var result = Compile(source);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        Assert.Contains("CAST(5 AS FLOAT) / CAST(2 AS FLOAT)", result.Sql);
        Assert.Contains("CAST(5 AS REAL) / CAST(2 AS REAL)", result.Sql);
        Assert.Contains("CAST(5 AS DECIMAL(38,18)) / CAST(2 AS DECIMAL(38,18))", result.Sql);
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

    [Fact]
    public void EmitsRuntimeGuardsForManagedIndexerFailures()
    {
        const string source = """
            var list = new List<int> { 1 };
            int[] array = new int[] { 1 };
            var dictionary = new Dictionary<string, int>();
            dictionary.Add("present", 1);
            string text = "x";

            Console.WriteLine(list[1]);
            Console.WriteLine(array[1]);
            Console.WriteLine(dictionary["missing"]);
            Console.WriteLine(text[1]);
            """;

        var result = Compile(source);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        Assert.Contains("THROW 51002, 'List index was out of range.'", result.Sql);
        Assert.Contains("THROW 51003, 'Array index was out of range.'", result.Sql);
        Assert.Contains("THROW 51010, 'The given key was not present in the dictionary.'", result.Sql);
        Assert.Contains("THROW 51003, 'String index was out of range.'", result.Sql);
    }

    private static TranspileResult Compile(string source) => new SharpSqlCompiler().Transpile(source);

    private static int Count(string value, string needle) =>
        (value.Length - value.Replace(needle, string.Empty, StringComparison.Ordinal).Length) / needle.Length;
}
