# SharpSql

[![.NET 10](https://img.shields.io/badge/.NET-10-512BD4?logo=dotnet)](https://dotnet.microsoft.com/)
[![SQL Server](https://img.shields.io/badge/target-SQL%20Server-CC2927?logo=microsoftsqlserver)](https://www.microsoft.com/sql-server)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)

**Compile a useful subset of C# into one self-contained T-SQL batch.**

SharpSql uses Roslyn syntax and semantic analysis to lower C# control flow, methods, objects, and collections into SQL Server. Small methods are inlined. Recursive or over-budget methods run on an ephemeral stack machine built from local temporary tables and static `GOTO` labels. In the default mode, the generated batch creates no persistent functions or procedures and cleans up its runtime state when it finishes.

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

Install the latest published command-line tool from NuGet:

```bash
dotnet tool install --global SharpSql.Tool
sharpsql --help
```

The supported NuGet surface consists of `SharpSql.Tool` and `SharpSql.Sdk`.
The compiler, IR, and MSBuild-loading assemblies are internal implementation
components bundled with those products; they are not published as standalone
packages.

### Project build and IDE integration

Let the tool install and configure the SDK in a console project:

```bash
sharpsql init path/to/MyApp.csproj
```

`init` pins `SharpSql.Sdk` to the tool version, enables build generation and
live diagnostics, restores the project, and writes SQL beside the compiled
application at `$(OutputPath)$(AssemblyName).sql`. It can discover the only
`.csproj` in a directory, so running `sharpsql init` from the project directory
is enough. Existing project elements, comments, custom output paths, and entry
settings are preserved; generation and analyzer switches follow the options on
each run. It also adds a `SharpSql (SQL Server)` profile to
`Properties/launchSettings.json`, preserving existing IDE profiles. The
profile uses a project-relative working directory, so IDEs can launch it from
any solution directory and the file remains portable when committed to Git.
The profile uses MSBuild's classic console logger so Rider and Visual Studio
show SQL generation, SQL Server startup, execution, and cleanup progress.

Customize the generated path or select a non-default static entry method while
initializing:

```bash
sharpsql init path/to/MyApp.csproj \
  --output '$(MSBuildProjectDirectory)/generated/MyApp.sql' \
  --entry MyApp.SqlJob::Run
```

Use `--analyzer-only` to skip SQL generation during normal builds,
`--no-analyzer` to disable live diagnostics, or `--no-restore` when restore is
handled separately. Central package management is supported with a version
override. Use `--no-launch-profile` if the IDE profile is not wanted.

The equivalent manual project configuration is:

```xml
<ItemGroup>
  <PackageReference Include="SharpSql.Sdk" Version="0.1.6" PrivateAssets="all" />
</ItemGroup>

<PropertyGroup>
  <!-- Required for libraries; executable projects use their normal entry point. -->
  <SharpSqlEntryPoint>MyApp.SqlJob::Run</SharpSqlEntryPoint>
  <SharpSqlOutputLocation>BuildOutput</SharpSqlOutputLocation>
  <SharpSqlExecution>Auto</SharpSqlExecution>
  <SharpSqlDurability>Ephemeral</SharpSqlDurability>
  <SharpSqlMemoryOptimized>false</SharpSqlMemoryOptimized>
  <SharpSqlKeepContainer>false</SharpSqlKeepContainer>
  <SharpSqlContainerDatabase>SharpSql</SharpSqlContainerDatabase>
</PropertyGroup>
```

`Auto` is the default execution strategy: reachable async/await IR selects Service
Broker, while synchronous programs execute inline. Durability and memory-optimized
tables are independent choices. The same properties drive live diagnostics and
build-time generation, and `SharpSqlRun` provisions the effective runtime before
execution. The legacy `SharpSqlRuntimeStorage` property remains a compatibility alias.

The SDK also supplies `SharpSql.DatabaseException` for native SQL Server
failures inside transpiled code. Its `Number`, `Severity`, `State`, `Procedure`,
`LineNumber`, and inherited `Message` properties map to SQL Server's `ERROR_*`
metadata:

```csharp
try
{
    RunDatabaseWork();
}
catch (SharpSql.DatabaseException exception)
{
    Console.WriteLine($"SQL {exception.Number}: {exception.Message}");
}
```

SharpSql's reserved errors (`51000`-`51999`) retain their existing .NET
exception mappings. `throw new ApplicationException(message)` uses a reserved
error and can be filtered or rethrown by an ordinary `catch` block.

The package reports SharpSql compatibility errors through Roslyn while editing
and emits SQL during a normal build:

```bash
dotnet build
```

`SharpSqlOutputLocation` can be `BuildOutput` or `Intermediate`; the latter is
the package default and writes beneath `$(IntermediateOutputPath)sharpsql`.
An explicit `SharpSqlOutputPath` takes precedence over both. Paths are resolved
when the transpile target runs, after normal SDK output properties are known.
To generate SQL without running the C# compiler, use transpile-only mode, or
invoke the dedicated target:

```bash
dotnet build -p:SharpSqlTranspileOnly=true
dotnet msbuild -t:SharpSqlTranspile
```

Run the generated SQL directly in SQL Server:

```bash
sharpsql run path/to/MyApp.csproj
dotnet msbuild path/to/MyApp.csproj -t:SharpSqlRun
```

The IDE launch profile invokes the same `SharpSqlRun` target. Without a
configured connection, both commands start an isolated SQL Server 2022
Testcontainer and remove it afterward. Retain and reuse the project-scoped
container when initializing or per invocation:

```bash
sharpsql init path/to/MyApp.csproj --keep-container
sharpsql run path/to/MyApp.csproj --keep-container
sharpsql run path/to/MyApp.csproj --remove-container
```

To use an existing development server, store only its connection name in the
project and keep the secret in normal .NET configuration:

```bash
sharpsql init path/to/MyApp.csproj --connection Development
dotnet user-secrets init --project path/to/MyApp.csproj
dotnet user-secrets set --project path/to/MyApp.csproj \
  'ConnectionStrings:Development' 'Server=localhost;Database=MyApp;Integrated Security=true;TrustServerCertificate=true'
```

Named connections are resolved from `appsettings.json`, environment-specific
appsettings, user secrets, and `ConnectionStrings__Development`. Set
`SHARPSQL_CONNECTION_STRING` for an unnamed external connection, use
`--connection-string-env` for a custom variable, or pass `--container` to
override a configured connection. Generated `.sql` files can also be executed
directly with `sharpsql run generated.sql`.

### Publish an application

Install a compiled application into its own schema in an existing database:

```bash
sharpsql publish path/to/MyApp.csproj \
  --connection Production \
  --schema MyApp \
  --name MyApp \
  --version 1.4.0
```

Publishing creates or updates `[MyApp].[Run]` and records the installed application
and version in `[MyApp].[PackageManifest]`. The installer is idempotent, so the same
deployment can be retried safely. Publishing requires an explicit configured
connection and does not start a Testcontainer.

Remove an installed package with its schema and manifest identity. The application
schema is retained, while the entry procedure, manifest, memory runtime types, and
native kernels owned by SharpSql are removed:

```bash
sharpsql unpublish \
  --connection Production \
  --schema MyApp \
  --name MyApp
```

Use `--memory-optimized` for schema-local memory runtime objects and add
`--native-kernels` for eligible native procedures. The database must already have a
`MEMORY_OPTIMIZED_DATA` filegroup and container; SharpSql does not create that physical
infrastructure. See [publishing applications](docs/application-publishing.md) for the
installed object model, connection setup, permissions, and deployment prerequisites.

`run` streams ordinary SQL informational output as it arrives. Service Broker
lines above 2,000 UTF-16 code units retain SQL Server's larger buffered `PRINT`
fallback. Add `--debug` for the
actual SQL plan and SharpSql heap counters, `--profile` for one warm-up and
three measured SQL runs, and `--output` to retain the generated program SQL:

```bash
sharpsql run path/to/MyApp.csproj \
  --execution ServiceBroker --durability Durable \
  --debug --profile \
  --output out.sql
```

Only the warm-up output is streamed during profiling; measured and debug-only
repeats are silent, so program output is shown once. For Service Broker programs,
`--output out.sql` also writes the standalone,
idempotent runtime installer to `out.installer.sql`. Use `--installer-output` to
select another path. The installer intentionally does not enable Service Broker
at the database level. `--execution Auto` is the default and writes the same
installer when transpilation selects Service Broker. The legacy
`--runtime-storage ServiceBroker` spelling remains available as a compatibility
alias.

Set `SharpSqlGenerateOnBuild` to `false` for IDE/build diagnostics without SQL
generation, `SharpSqlEnableAnalyzer` to `false` to disable live diagnostics, or
`SharpSqlEnabled` to `false` to disable both integrations. Standard Roslyn
`.editorconfig` severity settings apply, including category-wide configuration:

```ini
[*.cs]
dotnet_analyzer_diagnostic.category-SharpSql.severity = warning
```

From the repository root:

```bash
dotnet restore SharpSql.slnx
dotnet run --project src/SharpSql.Cli -- examples/inlining.cs
```

Compile to a file:

```bash
dotnet run --project src/SharpSql.Cli -- examples/objects.cs -o objects.sql
```

When `--memory-optimized` or `--execution ServiceBroker` is selected, this also writes the
standalone runtime installer beside the program as `objects.installer.sql`.

Verify a source file by running it as C# locally and running its generated SQL
against an ephemeral SQL Server 2022 container, then comparing console output
and runtime success or failure:

```bash
dotnet run --project src/SharpSql.Cli -- verify examples/hello.cs
```

Use `--sql-output generated.sql` to retain the generated batch. The `verify`
command requires Docker and accepts `.cs` files, `.csproj` projects, or standard
input. Project verification supports the same `--entry`, `--configuration`, and
`--framework` options as transpilation:

```bash
sharpsql verify path/to/MyProject.csproj \
  --entry MyProject.SqlJob::Run \
  --configuration Release
```

Add `--keep-container` to retain and reuse the matching SQL Server container on
later verification runs. Without the option, verification still reuses a
matching retained container when one exists, then removes it at the end.
Retained containers are labeled
`io.sharpsql.sqlserver.reusable=true` so they can be found or removed with Docker
Desktop or the Docker CLI. The `io.sharpsql.sqlserver.scope` label separates
containers belonging to different projects or source scopes.

Use `--debug` to report actual SQL plan statement/operator counts, estimated
cost, compile resources, generated SQL size, and SharpSql heap allocations
captured immediately before cleanup. Use `--profile` for one warm-up followed
by three measured C# and SQL Server runs; the reported median excludes container
startup:

```bash
sharpsql verify examples/linq_sum.cs --debug --profile --keep-container
```

Compile all C# documents in an MSBuild project by selecting a parameterless static entry method:

```bash
dotnet run --project src/SharpSql.Cli -- \
  path/to/MyProject.csproj \
  --entry MyCompany.Reporting.MonthEnd::Run \
  --configuration Release \
  --framework net10.0 \
  -o MonthEnd.sql
```

`--framework` is only needed for multi-targeted projects. For executable projects, the normal C# entry point is selected automatically when `--entry` is omitted. Library projects should provide `--entry`.

The CLI uses Spectre.Console.Cli for generated help, option binding, and validation:

```bash
dotnet run --project src/SharpSql.Cli -- --help
```

The CLI reads C# from standard input when no input path is supplied:

```bash
echo 'Console.WriteLine("Hello from SQL");' | dotnet run --project src/SharpSql.Cli
```

The repository contains compiler and MSBuild-loading source APIs used by the tool,
SDK, and tests. They are implementation surfaces, not separately versioned or
published libraries; use `SharpSql.Tool` or `SharpSql.Sdk` for released
integrations.

## What works today

- Top-level C# statements and conventional `Main` bodies
- Multi-file SDK-style C# projects loaded with their real references, generated sources, global usings, language version, and conditional symbols
- Installable `SharpSql.Sdk` integration with build-time SQL output, explicit transpile/run targets, IDE launch profiles, named development connections, reusable Testcontainers, transpile-only builds, and live Roslyn compatibility diagnostics
- Core numeric types, `bool`, `char`, `string`, nullable values, date/time types, `Guid`, `byte[]`, and `object`
- Declarations, assignment, arithmetic, comparisons, boolean expressions, interpolation, and casts
- String length/indexing and `string(char[])` construction
- `if`/`else`, `while`, `do`, `for`, `foreach`, `break`, and `continue`
- `Console.WriteLine` and `Console.Write` lowered to `PRINT`
- `Thread.GetCurrentProcessorId()` lowered to the current SQL session/worker ID (`@@SPID`)
- Pure-expression and procedural method inlining with hygienic variables and labels
- Recursive, mutually recursive, and over-budget calls through one generated stack and return trampoline
- Classes with reference identity, inherited typed-field layouts, base-to-derived initialization, procedural constructor bodies, `this(...)`/`base(...)` chaining, virtual/interface dispatch, object initializers, and instance methods; records use the same typed heap model
- One-dimensional arrays and `List<T>` with indexing, mutation, iteration, and common operations
- `Dictionary<TKey,TValue>` with indexing and common mutation/query operations
- Relational LINQ over arrays, `List<T>`, and lazy virtual `Enumerable.Range` sources: filtering/projection, ordering/paging, distinct values, joins, grouped-key pipelines, aggregates, and element operators, plus ordered `ToList`/`ToArray` and `Enumerable.Repeat` materialization
- Deferred query variables, managed `AsEnumerable`/`AsQueryable`, query syntax, stored/captured delegates, helper-method plan flow, LINQ `foreach`, and `ToList`/`ToArray` materialization
- Stateful `Random` instances with `Next()`, bounded/ranged `Next(...)`, and `NextDouble()`
- Roslyn semantic typing for `var`, generics, members, and expression results
- C# line, block, and documentation comments preserved near their generated SQL
- Source-positioned diagnostics for unsupported syntax

Virtual `Enumerable.Range` sources use SQL Server 2022 `GENERATE_SERIES` and
therefore require database compatibility level 160.

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

For `new Random(seed)`, SharpSql implements the same compatibility PRNG used by .NET 10,
producing the same sequence in C# and SQL. Parameterless construction uses a
SQL-generated seed; it has the same range and state behavior, but—as with parameterless
`Random` in C#—its exact sequence is intentionally nondeterministic. In Service Broker
executions, calls sharing one `Random` instance are protected by an instance-scoped
lock; every state transition is atomic, while which concurrent task receives each
sample remains scheduler-dependent. `NextInt64`, `NextSingle`, and `NextBytes` are not
implemented yet.

## How method calls stay ephemeral

SQL Server does not support temporary user-defined functions. SharpSql therefore chooses among three lowering strategies:

1. Substitute a side-effect-free expression directly at its call site.
2. Expand a small procedural body with renamed parameters, locals, and collision-safe labels.
3. Emit larger or recursive methods once as stack-machine blocks inside the batch.

The fallback stores activation frames and typed slots in local temporary tables. Every static call site receives an integer continuation ID, and all returns share one generated dispatcher that jumps to literal T-SQL labels. Normal completion drops the tables; closing the SQL connection provides failure-path cleanup.

### Memory-optimized VM state

The independent memory-optimized flag keeps the same direct SQL and label-based VM
lowering, but stores activation frames and spilled slots in database-global,
execution-partitioned In-Memory OLTP tables. Ephemeral state uses `SCHEMA_ONLY`;
durable state uses `SCHEMA_AND_DATA`. Provision the tables once with
`SharpSqlMemoryOptimizedRuntime.GenerateProvisioningSql(...)` or use the CLI installer:

```bash
sharpsql transpile examples/recursion.cs \
  --execution Inline --memory-optimized \
  --output recursion.sql
```

SQL Server requires the target database to already have a
`MEMORY_OPTIMIZED_DATA` filegroup and physical container. SharpSql deliberately
does not create that deployment-specific, effectively irreversible infrastructure.
The current experiment optimizes legacy VM frames, slots, and the fixed-shape managed
heap object registry. Typed object payloads, collection/dictionary rows, and LINQ buffer
tables remain ordinary temporary or durable rowstore tables. Scalar slot values use a
typed binary round-trip because In-Memory OLTP does not support `SQL_VARIANT`.
See the [memory-optimized runtime guide](docs/memory-optimized-runtime.md) for
provisioning, measurements, and the current storage boundary.

Supported pure scalar loop methods can additionally be extracted into natively
compiled stored-procedure kernels with `--native-kernels`. The interpreted legacy
batch passes live values as scalar arguments and receives the result through an
`OUTPUT` parameter. See the [native kernel prototype](docs/native-kernels.md) for
the measured call-boundary result and current extraction limits.

## Managed objects and collections

References are represented by `INT` object IDs. Each reachable class or record receives a typed local temporary table. A shared object header holds identity, collection counts, and small intrinsic metadata; arrays, lists, and `Random` state reuse one indexed runtime table. Dictionaries add an entry table only when needed. Copying a class variable copies its ID, so aliases observe the same mutations.
Target-typed `new(...)` participates in the same heap lowering, and record `with`
expressions allocate a clone before applying their member initializers in C#
evaluation order.

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

Repository source integrations also expose the experimental durable modes. `Durable`
partitions shared heap and VM tables by execution ID. `ServiceBroker` additionally
lowers the supported `Task.Delay`/`Task.WhenAll` fork-join shape into durable
continuations executed by an activated SQL Server worker pool, with task results,
faults, and `Console.WriteLine` output routed back to the entry connection. See the
[Service Broker async runtime guide](docs/service-broker-async.md) for provisioning,
isolation rules, the currently supported shape, and remaining state-machine work.

## Roadmap

The long-term experiment is to discover how much idiomatic C# can execute faithfully inside a SQL Server batch.

### LINQ milestone: managed collection queries

The managed-collection LINQ milestones are complete:

- A composable relational query plan over managed arrays and `List<T>`
- `Where`, `Select`, `Sum`, `Count`, `LongCount`, `Any`, `All`, `Contains`, and `FirstOrDefault`
- Deferred query variables, `AsEnumerable()`/`AsQueryable()`, and `where`/`select` query expressions
- Direct query iteration plus `ToList()` and `ToArray()` materialization
- Ordered procedural lowering for stateful `Enumerable.Repeat(...).Select(...)` pipelines
- Targeted entry-scope capture for VM-backed local functions used by stateful selectors
- Batched multi-row collection initialization, chunked at SQL Server's 1,000-row `VALUES` limit
- Supporting string length/indexing, `string(char[])`, and exact seeded bounded-`Random` behavior
- `Join`, key-based `GroupBy`, `OrderBy`/`ThenBy`, `Distinct`, `Skip`, and `Take`
- `Min`, `Max`, `MinBy`, `MaxBy`, `Average`, `First`, `Last`, `Single`, `SingleOrDefault`, `ElementAt`, and their supported `OrDefault` forms, with explicit empty/multiple/out-of-range guards
- Stored delegates and closures plus delegate/query-plan flow through expression-bodied or single-return helper methods, including returned delegate factories

The runnable specifications are [`linq_sum.cs`](examples/linq_sum.cs), [`linq_queries.cs`](examples/linq_queries.cs), [`linq_advanced.cs`](examples/linq_advanced.cs), and [`generated_names.cs`](examples/generated_names.cs).

The remaining LINQ milestone is:

- External `IQueryable<T>` source mapping with explicit table/schema metadata

`GroupBy` currently supports distinct group production, group counts, and `group.Key` projection. Materializing or iterating full `IGrouping<TKey,TElement>` values and aggregate projections over each group remain outside this phase.

### Compiler-analysis foundation

The first typed-IR and data-flow phase is complete:

- A typed scalar SQL IR carrying C# type, SQL precedence, and nullable flow state
- A backend-neutral `SharpSql.Ir` assembly with typed programs, symbols, expressions, query clauses, source spans, and procedural control flow
- Frontend-bound type hierarchies and constructor bodies with stable type/member/method/constructor identities
- Separate C# type binding and SQL Server type mapping
- Independent C#-to-IR and hand-built-IR-to-SQL test seams
- Centralized scalar casts and rendering
- Typed substitutions shared by method inlining, closures, and LINQ captures
- One procedural lowering boundary shared by direct/inlined code and the stack-machine backend
- Roslyn constant-flow facts used during predicate lowering
- Definite-assignment, use-before-declaration, missing-return, and out-parameter preflight diagnostics
- An overload-aware, semantic-ID method catalog and IR graph for resolved calls, caller/callee edges, recursion, effects, and VM closure
- Focused method flow summaries for endpoint reachability and statement cost
- Conservative fixed-point method effects with parameter mutation, escape, returned-alias, and fresh-reference summaries
- Shared intrinsic metadata for LINQ, collections, console output, and `Random`
- Distinct scalar, lazy-query, and delegate bindings without placeholder SQL variables
- IR-native user-method calls, managed heap operations, seeded `Random`, and managed-source LINQ planning/materialization—including advanced and stateful paths—without attached Roslyn source

### Broader missing layers

- Struct instance semantics, boxing, and unboxing
- General-purpose delegate invocation outside LINQ, iterators, and general multi-await async-state-machine lowering
- `finally` and structured exception unwinding across recursive VM calls
- More of the base class library through explicit compiler intrinsics
- Exact overflow, culture-sensitive formatting, and exception parity across the two runtimes

## Build and contribute

```bash
dotnet restore SharpSql.slnx
dotnet build SharpSql.slnx --configuration Release --no-restore
dotnet test SharpSql.slnx --configuration Release --no-build
```

Rebuild, validate, and replace the globally installed tool from the current source tree with one command:

```bash
./scripts/install-local-tool.sh
```

The script generates a cache-safe local version automatically. Pass a semantic version when an exact version is useful:

```bash
./scripts/install-local-tool.sh 0.1.15-local
```

The full test command starts one shared SQL Server 2022 container. Every file in `examples/` is reported as an independent C#/SQL parity test, and the integration corpus also checks expected runtime exceptions and exact compiler diagnostic codes under [`tests/SharpSql.IntegrationTests/cases`](tests/SharpSql.IntegrationTests/cases). CLI parsing, help, validation, standard input, output files, and project options are exercised through `Spectre.Console.Cli.Testing`. To run only the compiler unit tests without Docker:

```bash
dotnet test tests/SharpSql.Tests --configuration Release
```

Add a runtime-failure case under `cases/runtime-exceptions` with a directive such as `// sharpsql-expect-exception: KeyNotFoundException`. Add an intentionally unsupported but valid C# case under `cases/diagnostics` with `// sharpsql-expect-diagnostics: SS6301` (comma-separate multiple expected codes). Case paths are sorted deterministically and included in test names and failure reports; parity failures also print both structured outcomes, generated SQL, and source.

Contributions are welcome. Read [CONTRIBUTING.md](CONTRIBUTING.md) before proposing new language behavior, and include tests that make any C#/T-SQL semantic difference explicit.

The `conformance` command measures CLR-valid transpilation by default. Add
`--semantic COUNT` to execute up to that many observable transpiled corpus cases on
both the CLR and SQL Server; the JSON report records this opt-in sample separately as
`semanticResults` and does not treat it as whole-corpus semantic coverage.

## License

SharpSql is available under the [MIT License](LICENSE).
