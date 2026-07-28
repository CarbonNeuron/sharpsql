# SharpSql

[![.NET 10](https://img.shields.io/badge/.NET-10-512BD4?logo=dotnet)](https://dotnet.microsoft.com/)
[![SQL Server](https://img.shields.io/badge/target-SQL%20Server-CC2927?logo=microsoftsqlserver)](https://www.microsoft.com/sql-server)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)

**Compile a useful subset of C# into one self-contained T-SQL batch.**

SharpSql uses Roslyn syntax and semantic analysis to lower C# control flow, methods, objects, and collections into SQL Server. Small methods are inlined. Recursive or over-budget methods run on an ephemeral stack machine built from local temporary tables and static `GOTO` labels. The generated batch creates no persistent functions or procedures and cleans up its runtime state when it finishes.

> [!WARNING]
> SharpSql is an experimental compiler, not a production-safe way to run arbitrary C#. The supported language surface is intentionally explicit, and C# and SQL Server still differ in numeric, null, collation, evaluation-order, and exception semantics.

## A quick example

```csharp
int Square(int value) => value * value;

int Clamp(int value, int low, int high)
{
    if (value < low) return low;
    if (value > high) return high;
    return value;
}

int result = Clamp(Square(12), 0, 100);
Console.WriteLine($"result={result}");
```

```sql
SET NOCOUNT ON;

DECLARE @_clamp_1_value INT = 12 * 12;
DECLARE @_clamp_1_low INT = 0;
DECLARE @_clamp_1_high INT = 100;
DECLARE @result INT;
IF @_clamp_1_value < @_clamp_1_low
BEGIN
    SET @result = @_clamp_1_low;
    GOTO __sharpsql__clamp_1_end;
END;
-- ...
PRINT CONCAT(N'result=', @result);
```

## Try it

Requirements:

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- [Docker](https://docs.docker.com/get-docker/) for the SQL Server parity tests

No preinstalled database is required. Testcontainers starts and removes SQL Server automatically.

From the repository root:

```bash
dotnet restore SharpSql.slnx
dotnet run --project src/SharpSql.Cli -- examples/inlining.cs
```

Compile to a file:

```bash
dotnet run --project src/SharpSql.Cli -- examples/objects.cs -o objects.sql
```

The CLI reads C# from standard input when no input path is supplied:

```bash
echo 'Console.WriteLine("Hello from SQL");' | dotnet run --project src/SharpSql.Cli
```

The compiler can also be embedded directly:

```csharp
using SharpSql;

var result = new SharpSqlCompiler().Transpile(source);
if (!result.Success)
    throw new InvalidOperationException(string.Join(Environment.NewLine, result.Diagnostics));

Console.WriteLine(result.Sql);
```

## What works today

- Top-level C# statements and conventional `Main` bodies
- Core numeric types, `bool`, `char`, `string`, nullable values, date/time types, `Guid`, `byte[]`, and `object`
- Declarations, assignment, arithmetic, comparisons, boolean expressions, interpolation, and casts
- `if`/`else`, `while`, `do`, `for`, `foreach`, `break`, and `continue`
- `Console.WriteLine` and `Console.Write` lowered to `PRINT`
- Pure-expression and procedural method inlining with hygienic variables and labels
- Recursive, mutually recursive, and over-budget calls through one generated stack and return trampoline
- Classes and records with reference identity, typed fields, object initializers, mapped constructors, and instance methods
- One-dimensional arrays and `List<T>` with indexing, mutation, iteration, and common operations
- `Dictionary<TKey,TValue>` with indexing and common mutation/query operations
- Stateful `Random` instances with `Next()`, bounded/ranged `Next(...)`, and `NextDouble()`
- Roslyn semantic typing for `var`, generics, members, and expression results
- C# line, block, and documentation comments preserved near their generated SQL
- Source-positioned diagnostics for unsupported syntax

See the runnable [examples](examples) and the detailed [compiler architecture](docs/architecture.md).

## Differential compatibility corpus

The files under [`examples/`](examples) are more than demos: Testcontainers executes every one as both real C# and transpiled SQL Server code, then compares their output. The corpus currently exercises:

- Operator precedence, signed division and modulo, increment/decrement, and compound assignment
- Integral widths, unsigned values, `float`, `double`, and `decimal` arithmetic
- Null checks, nullable coalescing, conditionals, boolean formatting, and short-circuit evaluation
- Unicode, apostrophe escaping, comment markers inside strings, characters, and embedded newlines
- Nested `for`, `while`, and `do` loops with `break` and `continue`
- Initialized/default arrays, indexed mutation, and `foreach`
- List and dictionary mutation, clearing, removal, lookup, and case-sensitive string keys
- Mutable class aliasing, constructors, instance methods, inlining, direct recursion, and mutual recursion
- Independent seeded/unseeded random instances, bounded ranges, and deterministic seeded sequences

Adding another `.cs` file to that directory automatically adds it to the parity suite.

### Random numbers

`Random` is an ephemeral heap object, so each instance advances independently and can be passed around like any other reference:

```csharp
Random random = new Random(12345);
int die = random.Next(1, 7);
double fraction = random.NextDouble();
```

For `new Random(seed)`, SharpSql implements the same compatibility PRNG used by .NET 10, producing the same sequence in C# and SQL. Parameterless construction uses a SQL-generated seed; it has the same range and state behavior, but—as with parameterless `Random` in C#—its exact sequence is intentionally nondeterministic. `NextInt64`, `NextSingle`, and `NextBytes` are not implemented yet.

## How method calls stay ephemeral

SQL Server does not support temporary user-defined functions. SharpSql therefore chooses among three lowering strategies:

1. Substitute a side-effect-free expression directly at its call site.
2. Expand a small procedural body with renamed parameters, locals, and collision-safe labels.
3. Emit larger or recursive methods once as stack-machine blocks inside the batch.

The fallback stores activation frames and typed slots in local temporary tables. Every static call site receives an integer continuation ID, and all returns share one generated dispatcher that jumps to literal T-SQL labels. Normal completion drops the tables; closing the SQL connection provides failure-path cleanup.

## Managed objects and collections

References are represented by `BIGINT` object IDs. Each reachable class or record receives a typed local temporary table. A shared object header holds identity, collection counts, and small intrinsic metadata; arrays, lists, and `Random` state reuse one indexed runtime table. Dictionaries add an entry table only when needed. Copying a class variable copies its ID, so aliases observe the same mutations.

```csharp
Person ada = new Person("Ada", 36);
List<Person> people = new List<Person> { ada };
Dictionary<string, Person> byName = new Dictionary<string, Person>();
byName.Add("ada", people[0]);
byName["ada"].Age = 37;

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
```

The heap is allocation-only for the life of the script. Dropping its temporary tables reclaims the whole heap at once.

## Roadmap

The long-term experiment is to discover how much idiomatic C# can execute faithfully inside a SQL Server batch. Major missing layers include:

- A typed intermediate representation and broader data-flow analysis
- LINQ and query-expression lowering to relational SQL where possible
- Constructor bodies, inheritance, interfaces, and virtual dispatch
- Delegates, closures, iterators, and async-state-machine diagnostics
- Exceptions and structured unwinding across VM frames
- More of the base class library through explicit compiler intrinsics
- Exact overflow, culture-sensitive formatting, and exception parity across the two runtimes

## Build and contribute

```bash
dotnet restore SharpSql.slnx
dotnet build SharpSql.slnx --configuration Release --no-restore
dotnet test SharpSql.slnx --configuration Release --no-build
```

The full test command starts SQL Server 2022 through Testcontainers and runs every file in `examples/` both as C# and as transpiled SQL. Their normalized outputs must match. To run only the compiler unit tests without Docker:

```bash
dotnet test tests/SharpSql.Tests --configuration Release
```

Contributions are welcome. Read [CONTRIBUTING.md](CONTRIBUTING.md) before proposing new language behavior, and include tests that make any C#/T-SQL semantic difference explicit.

## License

SharpSql is available under the [MIT License](LICENSE).
