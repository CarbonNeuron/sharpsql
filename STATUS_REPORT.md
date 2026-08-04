# SharpSql status report

> Historical audit: the detailed findings below describe revision `dc34e4b` as
> inspected on 2026-07-30. Since that audit, `main` has gained the compact
> register-bytecode fallback. ABI 1.2 supports linked scalar methods, recursive
> bytecode calls, nullable string values, and typed `Console.WriteLine` host calls; current limitations
> and selection controls are documented in
> [Register-bytecode fallback](docs/register-bytecode.md).

Audit date: 2026-07-30

Audited revision: `dc34e4bbcc7546e0b48a69d3cecf1ad2688ad606` (`main`)

Package version in source: `0.1.5`

## Audit scope

The review covered all 80 C# files under `src/`, all 58 C# test files and the remaining tracked test data/configuration under `tests/`, all 24 example programs, the README, all five files under `docs/`, every project/solution/build/package file, both CI workflows, and the repository scripts. No `AGENTS.md` file exists in this checkout; the historical `CONFORMANCE_AGENTS.md` plan was reviewed instead. The ignored, downloaded Mono corpus was assessed through its runner, inventory, and saved baseline rather than by treating its 2,752 third-party source files as SharpSql-authored tests.

## Executive assessment

SharpSql is a substantial experimental C#-to-T-SQL compiler, not a general C# implementation. Its strongest working path is a closed-world console-style program using scalar expressions, structured loops, user methods, classes/records, one-dimensional managed collections, a sizeable managed-source LINQ subset, and `Random`. Small calls become ordinary SQL; recursion, dynamic dispatch, and large call graphs use a generated T-SQL stack machine. Differential SQL Server tests cover the documented examples and focused exception cases.

The project also contains three more specialized runtime/deployment paths:

| Area | Current status |
| --- | --- |
| Default ephemeral compiler/runtime | Broadest and best-tested path; one connection-local batch using temporary heap/VM tables. |
| Durable runtime | Implemented and integration-tested for synchronous concurrent execution, isolation, cleanup, heap, and VM state. It creates permanent shared tables partitioned by execution ID. |
| Service Broker async | Executable and deeply tested, but intentionally limited to one root `Task.WhenAll` over a materialized `Select` and one `Task.Delay(int)` in each worker method. It is not a general async state-machine compiler. |
| Memory-optimized runtime | Experimental. VM frames/slots and the complete managed heap use execution-partitioned In-Memory OLTP tables with typed binary scalar encoding. |
| Native kernels | Opt-in prototype. It extracts a narrow class of pure `int`/`long` loop methods into persistent natively compiled procedures. |
| Application publishing | Implemented for ephemeral or memory-optimized applications with a schema-local `Run` procedure and one-row manifest. There is no Service Broker publishing mode, uninstall, side-by-side versioning, or retention management. |
| Bytecode runtime | Not present on `main`. A two-commit `feat/bytecode-runtime` branch has a useful ephemeral scalar slice, but it diverged before the memory-optimized, native-kernel, publishing, and cleanup work now on `main`. |

The repository is structurally healthier after the recent split work, but the compiler remains concentrated in several 800–1,350-line partials. The most important immediate correctness/consistency item is the SDK's contradictory `MemoryOptimized` support. The most important product-level limitation is that documentation such as “useful subset” must continue to be read literally: switches, `finally`, general delegates, iterators, arbitrary BCL calls, structs/boxing, external `IQueryable<T>`, and general async are absent.

This was a source and configuration audit. No build or test command was run, to preserve the requested read-only workspace. Existing ignored test artifacts were not treated as proof for the current revision.

## 1. Feature inventory

### 1.1 Inputs, parsing, and entry points

- `SharpSqlCompiler.Transpile(string, options)` parses C# with Roslyn `LanguageVersion.Preview` and creates a console compilation with global `System`, `System.Collections.Generic`, and `System.Linq` imports (`src/SharpSql/SharpSqlCompiler.cs:55-62`, `src/SharpSql/SharpSqlCompiler.cs:274-286`).
- `SharpSqlCompiler.Transpile(CSharpCompilation, entryPoint, options)` accepts a caller-owned Roslyn compilation and compiles only reachable source methods (`src/SharpSql/SharpSqlCompiler.cs:65-84`, `src/SharpSql/SharpSqlCompiler.cs:121-132`).
- Project mode uses `MSBuildWorkspace`, preserves the project's actual references/options/generated sources, supports `Configuration` and a selected target framework, and reports workspace/project-load errors as `SSP0001`-`SSP0003` (`src/SharpSql.MSBuild/SharpSqlProjectCompiler.cs:20-105`).
- Entry selection supports top-level statements, the compilation's normal executable entry point, or a parameterless static method named as `Namespace.Type::Method`. Missing, ambiguous, instance, and parameterized selections are rejected with `SS0002` (`src/SharpSql/SharpSqlCompiler.cs:333-382`).
- Reachability walks ordinary calls, constructors, base/`this` constructor chains, and possible virtual/interface implementations in source (`src/SharpSql/SharpSqlCompiler.cs:390-519`). Referenced assemblies participate in binding, but their method bodies are not lowered (`docs/architecture.md:17`).

### 1.2 Supported language and runtime semantics

The table distinguishes what is actually lowered from syntax that Roslyn merely understands.

| Feature | Implemented surface | Important boundary |
| --- | --- | --- |
| Scalars | `bool`, signed and unsigned integer widths, `float`, `double`, `decimal`, `char`, `string`, nullable values, `DateTime`, `DateOnly`, `TimeOnly`, `TimeSpan`, `Guid`, `byte[]`, reference IDs, and an `object`/`SQL_VARIANT` fallback (`src/SharpSql/SqlTypeMapper.cs:8-34`). Binary arrays support construction, reference identity, length, indexed reads/writes, iteration, and `SequenceEqual`. | SQL arithmetic/null/culture/overflow semantics are not universally CLR-exact. `TimeSpan` shares the SQL `TIME` representation. General boxing/unboxing is absent. |
| Expressions | Literals, variables, arithmetic/comparison/Boolean operators, null checks/coalescing, conditional expressions, assignment/compound assignment, increment/decrement, casts, interpolation, member/index access, object construction, `with`, array construction, invocation, lambdas, and query expressions are represented in IR (`src/SharpSql/SharpSqlCompiler.CSharpFrontend.cs:160-267`). | Support after binding depends on the backend path. Pattern matching, tuples, `typeof`, anonymous methods, ref expressions, and arbitrary expression forms fall into `SS4002`/`SS4003`. |
| Statements | Blocks, declarations, expression statements, `if`, `while`, `do`, `for`, supported `foreach`, `try`/`catch`, `throw`, `break`, `continue`, `return`, empty statements, and local functions (`src/SharpSql/SharpSqlCompiler.CSharpFrontend.cs:38-96`). | `switch`, `using`, `lock`, `fixed`, `yield`, and other unlisted statements become `ProceduralUnsupported`. `try/finally` is explicitly rejected (`src/SharpSql/SharpSqlCompiler.CSharpFrontend.cs:98-102`). |
| Methods | Static/local/instance methods, expression bodies, overload identities, early returns, nested calls, direct and mutual recursion, virtual/abstract/interface dispatch, and `base` calls. | Generic semantic binding exists, but arbitrary framework/library methods are not intrinsically supported. Shadowed locals fail in VM-backed methods (`SS5001`, `src/SharpSql/SharpSqlCompiler.Vm.cs:92-101`). |
| Objects | Classes and records with reference identity, typed rows, fields and field-backed properties, inherited layouts, hidden members, initializers, overloaded constructors, `this(...)`/`base(...)` chains, constructor control flow, object initializers, record formatting, and record `with` cloning (`docs/architecture.md:122-132`). | User structs, copy semantics, boxing/unboxing, finalization, and garbage collection are not implemented. The heap is allocation-only until execution cleanup. Arbitrary property accessor behavior is not a general runtime facility. |
| Arrays | One-dimensional arrays, default initialization, explicit/implicit initializers, length, indexing/mutation, and iteration. Each `byte[]` uses a managed object ID and one native `VARBINARY(MAX)` payload row, preserving aliases without per-byte rows. Initializer inserts for other arrays are batched and split at SQL Server's 1,000-row `VALUES` limit. | Multidimensional arrays produce `SS6301`. |
| `List<T>` | Empty and collection-initialized construction; `Count`, index get/set, `Add`, `Clear`, `RemoveAt`, `Contains`, and `foreach`. Scalar and reference elements use a shared indexed heap table. | Constructors with arguments produce `SS6102`; this is not the full BCL list API (`src/SharpSql/SharpSqlCompiler.Heap.Creation.cs:488-497`). |
| `Dictionary<TKey,TValue>` | Empty construction; `Count`, index get/set, `Add`, `Clear`, `Remove`, `ContainsKey`, and `ContainsValue`; scalar/string/binary/reference key/value storage. String keys use `Latin1_General_100_BIN2` for ordinal-like equality. | Dictionary collection initializers are rejected with `SS6005`; use `Add` calls (`src/SharpSql/SharpSqlCompiler.Heap.Creation.cs:640-651`). Custom comparers and the wider dictionary API are absent. |
| Strings/output | Concatenation, interpolation, Boolean formatting, `Length`, indexing, `new string(char[])`, `Console.Write`, and `Console.WriteLine`. Default mode uses `PRINT`; Service Broker persists and proxies ordered output events. | Other string constructors/APIs and general formatting/culture behavior are not supported. |
| `Random` | Per-object state, parameterless and seeded construction, `Next()`, `Next(max)`, `Next(min,max)`, and `NextDouble()`. Seeded behavior implements the .NET compatibility subtractive PRNG (`src/SharpSql/SharpSqlCompiler.Random.cs:13-257`). | `NextInt64`, `NextSingle`, `NextBytes`, and unsupported overloads are absent (`README.md:399-406`). Shared Service Broker instances serialize state transitions with an application lock. |
| Exceptions | `try`/ordered `catch`, catch filters that remain scalar, rethrow, `throw new ApplicationException()` or `(message)`, reserved SQL-error-to-.NET mappings, and `SharpSql.DatabaseException` for non-reserved SQL errors (`src/SharpSql/SharpSqlCompiler.Statements.cs:91-280`). | No `finally`; no general exception construction; unmapped catch types produce `SS2011`; filters cannot invoke runtime operations (`SS2012`); VM exception unwinding remains limited. |
| Async/tasks | Async/await is preserved in IR. Service Broker supports one root `await Task.WhenAll(tasks)` where tasks come from `source.Select(AsyncMethod).ToList()`, and exactly one `await Task.Delay(int)` per async worker (`src/SharpSql/SharpSqlCompiler.Async.cs:52-183`). | General awaits, multiple/nested awaits, async recursion, cancellation, nested returns, pre-await locals live across suspension, mutable captured entry locals, and VM-backed calls from continuations are rejected with `SS7001`-`SS7005`. Ordinary backends diagnose await rather than blocking it. |
| Comments | Line, block, and documentation comments are captured from Roslyn trivia, associated with IR source records, and emitted once near generated statements/types (`src/SharpSql/SharpSqlCompiler.Comments.cs:9-106`). | Comment placement is best-effort after rewrites rather than source-layout preservation. |

Unsafe code, `dynamic`, reflection, COM/native interop, iterators, and arbitrary assembly loading are outside the intended surface; the conformance harness skips these categories (`tests/SharpSql.ConformanceTests/ConformanceRunner.cs:230-242`).

### 1.3 LINQ

LINQ is a major implemented subsystem, but only for SharpSql-managed sources.

Supported source/planning forms:

- Arrays and `List<T>`; `AsEnumerable()` and managed `AsQueryable()` retain the managed plan.
- Lazy `Enumerable.Range` via SQL Server 2022 `GENERATE_SERIES`; compatibility level 160 is required. Bounds are validated before enumeration (`README.md:368-369`).
- `Enumerable.Repeat`, including a special ordered procedural materializer for stateful selectors.
- Method syntax and query syntax. Query syntax supports `where`, `orderby`, `select`, and identity `group item by key`; continuations and richer clauses produce `SS6410` (`src/SharpSql/SharpSqlCompiler.Linq.Planning.cs:698-764`).
- Stored delegates, scalar captures, lexical closures, returned delegate factories, and expression-bodied/single-return helpers that pass predicates or query plans.

Supported pipeline operators:

- `Where`, `Select`, `OrderBy`, `OrderByDescending`, `ThenBy`, `ThenByDescending`, `Distinct`, `Skip`, `Take`, `GroupBy` (key/group identity only), and `Join` (`src/SharpSql/SharpSqlCompiler.Linq.Planning.cs:426-584`).
- Materialization to `ToList()` and `ToArray()`.
- Iteration directly with `foreach` when the plan has a supported managed source.

Supported terminals:

- `Sum`, `Count`, `LongCount`, `Any`, `All`, `Contains`.
- `First`, `FirstOrDefault`, `Last`, `LastOrDefault`, `Single`, `SingleOrDefault`, `ElementAt`, `ElementAtOrDefault`.
- `Min`, `Max`, `Average`, `MinBy`, and `MaxBy`.
- Predicate/selector overloads where the lowerer accepts a single expression lambda.

SharpSql emits explicit C#-style guards for empty sequences, multiple `Single` values, invalid element indices, and overflowing `Range` construction (`src/SharpSql/SharpSqlCompiler.Linq.Execution.cs:320-477`). Grouping is a key-only representation: count/key projection is supported, but iterating or materializing full `IGrouping<TKey,TElement>` values produces `SS6411` (`src/SharpSql/SharpSqlCompiler.Linq.Execution.cs:580-713`). There is no external database-table mapping or external `IQueryable<T>` provider (`README.md:506-510`).

### 1.4 Runtime storage and execution modes

`RuntimeStorageKind` has four stable values: `Ephemeral=0`, `Durable=1`, `ServiceBroker=2`, and the later-added `MemoryOptimized=3` (`src/SharpSql/TranspileOptions.cs:4-22`). These modes are storage/scheduling choices, not separate language frontends.

#### Ephemeral

- Default and most complete mode.
- Creates connection-local `#` tables for the object header, indexed items, dictionary entries, per-type object rows, LINQ buffers, and—when required—VM frames/slots.
- Reference IDs are `INT`; VM scalar slots use `SQL_VARIANT` plus dedicated string/binary columns.
- Normal completion drops runtime tables. Failed batches rely on local-temp lifetime at connection close; startup pre-drops stale temp tables for connection reuse (`docs/architecture.md:83-112`).

#### Durable

- Uses permanent `SharpSql` schema tables partitioned by `ExecutionId`; program-specific typed heap table names include stable fingerprints.
- Allocates globally unique object/frame IDs, provisions under an application lock, creates an execution row, and deletes only that execution's rows on success or failure.
- Integration tests exercise concurrent isolation and both normal and exceptional cleanup (`tests/SharpSql.IntegrationTests/DurableRuntimeIntegrationTests.cs:9-130`).
- This mode is still a caller-session synchronous execution model; “durable” describes storage/isolation, not background scheduling.

#### Generated stack machine (“VM”)

- Selected for recursive strongly connected components, dynamically dispatched methods, random/state-connected call closures, and methods whose expansion would exceed the inline budgets. Defaults are 40 statements and eight call sites (`src/SharpSql/TranspileOptions.cs:27-31`, `src/SharpSql/MethodGraph.cs:75-329`).
- `#__sharpsql_stack` stores activation frames and `#__sharpsql_slots` stores arguments, locals, spills, and expression temporaries. Each call site receives a static continuation ID; returns jump through one generated T-SQL label trampoline (`docs/architecture.md:83-108`).
- This is a T-SQL label-based interpreter/runtime, not bytecode. Method entries and return sites remain literal labels in the emitted batch.
- It supports typed scalar/reference parameters and results, declarations, assignments, arithmetic/conditionals, loops, nested calls, direct/mutual recursion, managed calls, and dispatch paths. Shadowing and some effectful expressions/library calls remain unsupported.

#### Memory-optimized

- Preserves the same direct SQL and label VM while moving VM frames/slots and the complete managed heap to provisioned, execution-partitioned memory-optimized tables.
- Because memory-optimized tables cannot store `SQL_VARIANT`, ordinary scalars round-trip through `VARBINARY(8000)` using their statically known type.
- Object headers, typed field payloads, indexed collection/LINQ rows, and dictionary entries use shared memory-optimized tables (`docs/memory-optimized-runtime.md`).
- The database operator must create a `MEMORY_OPTIMIZED_DATA` filegroup/container; SharpSql intentionally emits only the idempotent schema/type provisioning.

#### Native kernels

- Enabled by `TranspileOptions.EnableNativeKernels` and allowed only with memory-optimized storage; misuse produces `SS8201` (`src/SharpSql/SharpSqlCompiler.NativeKernels.cs:12-20`).
- Eligible methods must be static, synchronous, nonrecursive, deterministic/pure according to the effect analysis, contain a loop, and use only `int`/`long` parameters, locals, and return values. Supported bodies include declarations, assignment/mutation, arithmetic/comparison, `if`, `while`, and return (`docs/native-kernels.md:41-48`).
- The compiler emits a content-addressed `WITH NATIVE_COMPILATION, SCHEMABINDING` procedure and calls it through scalar parameters plus an output result.
- Unsupported methods silently remain on the legacy lowering path; this is an optimization, not a new semantic capability.
- Procedures are persistent, and no catalog/retention/garbage collection exists; old content hashes accumulate (`docs/native-kernels.md:55-59`).

#### Service Broker

- Uses durable heap/VM tables plus durable `Executions`, `Tasks`, `TaskTimers`, `TaskJoins`, `TaskDependencies`, and `OutputEvents` tables (`src/SharpSql/ExecutionInfrastructureSqlEmitter.cs:10-27`, `src/SharpSql/ExecutionInfrastructureSqlEmitter.cs:161-424`).
- Provisioning creates versioned message types, a contract, launcher/worker queues and services, transactional task/output procedures, and an activated `DispatchWorker` procedure with up to eight readers (`src/SharpSql/ServiceBrokerWorkerDispatcherSqlEmitter.cs:292-304`). It refuses an ambient provisioning transaction and a database where Broker is disabled.
- Each compiled async program installs `[SharpSql].[Program_<32-hex-hash>]`. Worker messages contain execution/task/route identity; the dispatcher validates the durable task and compiler hash before dynamic procedure invocation (`src/SharpSql/ServiceBrokerWorkerDispatcherSqlEmitter.cs:87-135`).
- Delays use the millisecond timer table; the launcher periodically calls `ClaimDueContinuations`. `WhenAll` joins are generation-scoped and enqueue the continuation once.
- Output is allocated/persisted transactionally and sent to the launcher conversation; the launcher emits it with a low-latency path, preserving per-task order but not deterministic order among concurrent tasks (`src/SharpSql/ExecutionInfrastructureSqlEmitter.OutputProcedures.cs:5-108`).
- Deadlock victims are rolled back and transparently redelivered; broker delivery failures and worker exceptions fault the durable task/execution (`src/SharpSql/ServiceBrokerWorkerDispatcherSqlEmitter.cs:138-285`).
- There is no abandoned-execution lease/reaper, cancellation model, general state spilling, or installed-program retention policy. Generated `Program_<hash>` procedures persist after execution.

### 1.5 CLI commands

The executable registers six Spectre.Console commands; unrecognized root arguments are routed to `transpile` for backward compatibility (`src/SharpSql.Cli/Program.cs:11-22`, `src/SharpSql.Cli/CliArgumentRouter.cs:20-30`).

| Command | Behavior and notable options |
| --- | --- |
| `transpile [INPUT]` | Reads `.cs`, `.csproj`, or stdin; selects entry/configuration/framework for projects; chooses all four runtime modes; optionally enables native kernels; writes program SQL and, for memory-optimized/Service Broker modes, a separate installer (`src/SharpSql.Cli/TranspileCommand.cs:14-179`). |
| `run [INPUT]` | Resolves a project/directory or existing `.sql`, transpiles projects, optionally saves artifacts, resolves a configured SQL Server or starts/reuses a scoped Testcontainer, provisions required runtime SQL, streams messages, and supports `--debug` showplan/heap counters plus one warm-up/three-sample `--profile` (`src/SharpSql.Cli/RunCommand.cs:15-336`, `src/SharpSql.Cli/SqlRunModels.cs:82-318`). |
| `verify [INPUT]` | Compiles and executes C# locally, transpiles and runs SQL in a Testcontainer, then compares normalized stdout and runtime failure type. It supports projects, saved SQL output, debug plan/heap data, profiling, and retained containers (`src/SharpSql.Cli/VerifyCommand.cs:14-363`). It always uses the default ephemeral compiler mode. |
| `init [PROJECT]` | Discovers a console `.csproj`, adds/updates a private `SharpSql.Sdk` reference (including central package `VersionOverride`), writes build/analyzer/run settings, adds a portable `SharpSql (SQL Server)` launch profile, and normally runs restore (`src/SharpSql.Cli/InitCommand.cs:15-247`, `src/SharpSql.Cli/ProjectSdkInstaller.cs:17-344`). It rejects library projects. |
| `publish INPUT` | Compiles a source/project and installs a schema-scoped application on an explicitly configured persistent server. Options select name/schema/version, entry/framework, memory optimization, and native kernels (`src/SharpSql.Cli/PublishCommand.cs:13-194`). It deliberately has no Testcontainer fallback. |
| `conformance` | Downloads/discovers the Mono corpus, transpiles cases in parallel with a per-test timeout, writes JSON, prints deltas, and optionally replaces the baseline (`src/SharpSql.Cli/ConformanceCommand.cs:13-113`). It catches all failures and always returns zero by design, so setup/runner failures are informational too. |

### 1.6 SQL Server utility layer

`SharpSql.SqlServer` is a reusable but non-packable project. It exposes:

- Connection resolution from a named custom environment variable, `SHARPSQL_CONNECTION_STRING`, `appsettings.json`, environment-specific appsettings, user secrets, and `ConnectionStrings__Name`, with environment variables taking final precedence (`src/SharpSql.SqlServer/SqlServerConnectionResolver.cs:14-47`).
- Existing-server sessions or project-path-scoped reusable Testcontainers, database creation/selection, and container cleanup (`src/SharpSql.SqlServer/SqlServerSession.cs:80-199`).
- SQL execution with informational-message streaming, SQL error capture, optional statistics XML parsing, and SharpSql heap diagnostic extraction (`src/SharpSql.SqlServer/SqlBatchExecutor.cs:54-242`).

## 2. Architecture map

### 2.1 End-to-end flow

```text
source string                         .csproj
     |                                  |
Roslyn preview parse              MSBuildWorkspace design-time build
     |                                  |
     +---------- CSharpCompilation -----+
                       |
          parse + semantic diagnostics
                       |
         entry selection and reachability
                       |
       C# syntax/semantic binding to SharpSql.Ir
       (types, symbols, expressions, procedural CFG,
        methods, constructors, heap types, comments)
                       |
        method/effect/alias/call-graph analysis
                       |
          heap and LINQ runtime planning
                       |
     +-----------------+------------------+
     |                 |                  |
 expression       procedural expand   stack-machine method
 substitution     with renamed state  + static return trampoline
     +-----------------+------------------+
                       |
         optional native kernel extraction
                       |
     ordinary/durable/memory-optimized batch
                       |
     OR narrow Service Broker program + launcher
```

The source compiler performs the C# binding and SQL emission in one `SharpSqlCompiler` instance, but the boundary is real: it creates an immutable `IrProgram`, prepares heap/graph/VM state, and emits from that program (`src/SharpSql/SharpSqlCompiler.cs:121-216`). The internal `Transpile(IrProgram)` path and extensive hand-built IR tests demonstrate backend use without Roslyn (`src/SharpSql/SharpSqlCompiler.cs:219-258`, `tests/SharpSql.Tests/IrBoundaryTests.cs:107-1176`).

### 2.2 IR boundary

`SharpSql.Ir` contains backend-neutral internal records for:

- Source spans/comments and stable symbol/type/member/method/constructor IDs.
- Typed constants, variables, binary/unary/conversion/conditional/invocation/construction/array/collection/interpolation/await/lambda/query expressions.
- Procedural declarations, expressions, blocks, branches, loops, jumps, returns, `try`/`catch`/throw, local functions, and explicit unsupported nodes.
- Method flow/effect summaries, constructor bodies/chains, type hierarchies, dispatch slots, and query clauses.

No IR contract contains a Roslyn or SQL type; boundary tests reflect over the assembly to enforce that (`tests/SharpSql.Tests/IrBoundaryTests.cs:1375-1406`). The frontend does maintain a side dictionary from an `IrSource` object to its originating Roslyn node so a small number of heap/LINQ compatibility fallbacks can consult syntax (`src/SharpSql/SharpSqlCompiler.CSharpFrontend.cs:9`, `src/SharpSql/SharpSqlCompiler.CSharpFrontend.cs:767-810`). This is a remaining migration seam, not an IR dependency.

All IR declarations are `internal` (for example `src/SharpSql.Ir/CompilerIr.cs:3-82`). The assembly is therefore a compiler implementation boundary, not a supported third-party extension API, despite being separately packable.

### 2.3 Analyses and selection

- `MethodCatalog` resolves overload-aware stable method IDs rather than names (`src/SharpSql/MethodCatalog.cs:5-87`).
- `MethodGraph` computes call counts, callers/callees, SCC recursion, and the connected closure that must share the VM (`src/SharpSql/MethodGraph.cs:22-329`).
- The behavior fixed point propagates mutable-state reads/writes, allocation, throw, nondeterminism, I/O, unknown calls, parameter mutation/escape, returned aliases, and fresh references (`src/SharpSql/SharpSqlCompiler.Analysis.cs:12-470`). This informs safe expression substitution, call expansion, native-kernel eligibility, and whether discarded calls may be removed.
- The intrinsic catalog centralizes recognized LINQ, collection, console, thread, and random effects (`src/SharpSql/IntrinsicCatalog.cs:5-148`).
- `AsyncStateMachinePlan` identifies suspension points and conservatively live symbols; the Service Broker backend performs further shape validation (`src/SharpSql/AsyncStateMachinePlan.cs:7-260`).

### 2.4 Heap and relational bridge

The heap is a typed closed-world object model rather than a universal EAV store:

- `Objects` is the shared header/identity table.
- Each reachable user type has a typed row table; inherited objects have a row for every base-to-derived layer.
- `IndexedItems` stores arrays, lists, and `Random` state.
- `DictionaryEntries` stores dictionary keys/values.
- LINQ builds SQL relational plans over these typed sources and materializes only when required.

This keeps ordinary member access typed and allows the SQL optimizer to see set operations. It also explains the current boundaries: no per-object collection, no arbitrary external object graph, and no transparent external query provider.

### 2.5 Application package flow

`SharpSqlApplicationPackage.GenerateInstallSql()` validates schema/entry identifiers, hashes the compiled program, acquires a schema-specific application lock, creates the schema and `PackageManifest`, optionally provisions schema-local memory table types, creates or alters the entry procedure around the compiled batch, and upserts the single manifest row (`src/SharpSql/SharpSqlApplicationPackage.cs:9-195`).

This is deployment wrapping, not a new compiler backend. `PublishService` selects only `Ephemeral` or `MemoryOptimized`, compiles with `ApplicationSchema`, generates the package, requires a persistent connection, and executes the installer (`src/SharpSql.Cli/PublishModels.cs:35-110`).

## 3. Test coverage assessment

### 3.1 Unit tests

`tests/SharpSql.Tests` declares 193 public test methods across 15 files; theories expand this count. Coverage is unusually strong for generated SQL structure and compiler boundaries:

| Test area | What is covered |
| --- | --- |
| Compiler core (`CompilerTests.cs`, 73 methods) | Scalars, precedence, loops, all three call strategies, recursion/mutual recursion, durable state, objects/records/inheritance/constructors/dispatch, arrays/lists/dictionaries, runtime guards, random, LINQ from basic to advanced, delegate/query flow, data-flow errors, comments, and runtime cleanup. |
| IR (`IrBoundaryTests.cs`, 25) | Frontend binding, Roslyn-free hand-built IR lowering for methods/VM/heap/random/LINQ, stable identities, dispatch/constructors, neutral-contract reflection, effects and aliases. |
| CLI (`CliTests.cs`, 28) | Routing, stdin/files/projects, output/installer files, all CLI runtime modes, native-kernel option, help/validation, verify rendering, init/idempotence/central package management/launch profiles, and run request binding. Most use replaceable services rather than SQL Server. |
| Async (`AsyncStateMachinePlanTests.cs`, 11) | Delay/WhenAll planning, overload recognition, live-state rejection, nested-return/VM/capture diagnostics, actual continuation SQL, and program identity inputs. |
| Service Broker infrastructure (16 total) | Provisioning idempotence, task/output tables and transaction rules, scheduling/joins/timers, validated dispatcher routing, deadlock retry, lifecycle messages, and per-message variable reset. These are primarily SQL-text assertions. |
| Packages/build/analyzer | Application package safety and schema scoping, build-host arguments/runtime selection, project compilation, analyzer locations/configuration, `DatabaseException`, and connection resolution. |
| Experimental modes | Memory table-type SQL and current heap boundary; native-kernel extraction/fallback/precondition. |

The unit suite asserts SQL fragments and invariants extensively, but it does not parse every emitted batch with a SQL grammar or execute it. That role belongs to integration tests.

### 3.2 SQL Server integration tests

The integration project declares 35 test methods. Five theories expand over source corpora, so the effective current set is approximately 122 SQL Server cases:

- Default differential corpus: 24 `examples/*.cs` files plus nine focused success cases = 33 successful parity runs; 12 mapped runtime-exception cases; two exact diagnostic cases (`tests/SharpSql.IntegrationTests/ExampleParityTests.cs:8-76`).
- The same 33 success and 12 runtime-failure cases run again with memory-optimized VM storage (`tests/SharpSql.IntegrationTests/MemoryOptimizedLegacyIntegrationTests.cs:16-64`).
- Three durable-runtime tests cover concurrent execution isolation, top-level-return cleanup, and failure cleanup.
- Seven Service Broker infrastructure tests execute provisioning, ambient savepoint behavior, completion notification, concurrent dependency completion, due timers, bulk due claims, and dispatcher drain behavior.
- Ten end-to-end Service Broker async tests cover broker delivery failure, scheduling rollback, child faults, missing execution detection, concurrent isolation, caught SQL errors after await, random/record workloads, streaming output, multi-reader/equal-delay behavior, and the complete fork/join flow.
- Publishing has two live database tests: idempotent manifest/procedure update and memory-optimized package installation/run.
- Native kernels have one functional compilation/execution test and one 30-sample performance experiment. Memory-optimized VM also includes a performance experiment; these performance tests are tagged but not skipped.
- SQL execution utility tests verify live message timing, showplan/heap capture, and error paths.

All SQL integration classes share a SQL Server 2022 Testcontainers fixture, and the collection disables parallelization (`tests/SharpSql.IntegrationTests/SqlServerFixture.cs:7-23`). This reduces interference but means CI does not exercise multiple SQL Server versions, Windows SQL Server, Azure SQL variants, non-default collations, or compatibility levels other than the fixture database defaults.

### 3.3 Conformance baseline

The saved baseline is timestamped 2026-07-29 and records (`tests/conformance/baseline.json:1-24`):

| Category | Passed | Failed | Skipped | Total | Pass/total |
| --- | ---: | ---: | ---: | ---: | ---: |
| Core | 391 | 1,009 | 210 | 1,610 | 24.3% |
| Generics | 258 | 715 | 72 | 1,045 | 24.7% |
| Dynamic | 0 | 0 | 86 | 86 | 0% (all intentionally skipped) |
| **Total** | **649** | **1,724** | **368** | **2,741** | **23.7%** |

Interpretation cautions:

- “Passed” means only that `SharpSqlCompiler.Transpile` returned no diagnostics. Generated SQL is not executed and program output/return code is not checked (`tests/SharpSql.ConformanceTests/ConformanceRunner.cs:175-205`).
- Each `.cs` file is compiled independently. Mono's multi-file/library cases therefore produce failures such as “no entry point” and duplicate/missing definitions; the metric is useful for trend tracking but not a normalized C# feature-compliance percentage.
- The locally downloaded corpus contains 2,752 `.cs` files, while only the 2,741 files matching `test-`, `gtest-`, or `dtest-` naming are categorized.
- The timeout uses `WaitAsync`, but the underlying `Task.Run` compilation is not canceled after a timeout (`tests/SharpSql.ConformanceTests/ConformanceRunner.cs:194-218`). Repeated pathological timeouts can leave work running in the process.
- The CLI deliberately exits zero even if corpus download or the run throws (`src/SharpSql.Cli/ConformanceCommand.cs:61-112`), matching its non-gating CI role but also masking infrastructure regressions unless logs/artifacts are inspected.

### 3.4 CI and coverage gaps

The main Ubuntu job restores, formatting-checks, builds in Release, runs the full solution tests (including Docker parity), packs/smoke-tests a Linux tool, and packs/smoke-tests the SDK. A separate non-gating conformance job downloads Mono tests and uploads JSON (`.github/workflows/ci.yml:11-134`). The publish job repeats build/tests, expects eight packages, installs the root tool, validates the SDK, and pushes six RID tools plus root tool and SDK (`.github/workflows/publish.yml:82-172`).

Obvious gaps:

1. No CI matrix for Windows/macOS tool execution, other SQL Server releases/editions, compatibility levels, or collations.
2. The six RID-specific tool packages are counted but not individually launched; the root tool is smoke-tested only on Ubuntu.
3. The SDK package test covers default and Service Broker projects, analyzer failure, init, and `SharpSqlRun`, but not `MemoryOptimized`—which allowed the current target validation defect to survive (`scripts/test-sdk-package.sh:89-180`).
4. No end-to-end SDK property exists/test exists for native kernels or application publishing.
5. No general async negative/positive matrix beyond the narrow supported shape; no cancellation, restart-after-worker-process-loss, abandoned execution cleanup, or version-skew migration tests.
6. No tests for full grouping values, external query sources, structs/boxing, general delegates, iterators, `finally`, switches, or arbitrary BCL calls because these layers are not implemented.
7. Application publishing lacks tests for concurrent publishers, permission failures, transaction rollback halfway through install, uninstall, old native/program asset retention, or incompatible upgrades.
8. Conformance is compile-only and structurally noisy for multi-file Mono tests.

## 4. Known limitations, diagnostics, and TODO scan

### 4.1 Explicit TODO/FIXME/HACK scan

There are no `TODO`, `FIXME`, `HACK`, or `XXX` markers in the current authored source, tests, README, or docs. Incomplete work is documented through diagnostics, status documents, comments, and roadmap text instead.

### 4.2 Diagnostic families

The analyzer exposes compiler diagnostic IDs as Roslyn errors and has a fallback `SSA0001` internal-error descriptor (`src/SharpSql.Analyzers/SharpSqlCompatibilityAnalyzer.cs:11-26`, `src/SharpSql.Analyzers/SharpSqlCompatibilityAnalyzer.cs:154-183`). Important families are:

| IDs | Limitation revealed |
| --- | --- |
| `SS0001`, `SS0002`, `SSP0001`-`SSP0003` | Missing source/entry, invalid entry selection, or MSBuild project loading failure. |
| `SS1001` | Duplicate semantic method identities. |
| `SS2001`, `SS2003`, `SS2005` | Invalid continue/return/break placement or a value returned from a script entry. |
| `SS2010`-`SS2013` | Unsupported catch set/type/filter, no SQL exception mapping, or unsupported throw form. |
| `SS3001`-`SS3004` | Argument mismatch, unresolved recursive/over-budget fallback conditions, or reachable non-void endpoint. |
| `SS4001`-`SS4003` | Unknown identifier or generic unsupported expression/statement. This is the broad catch-all for missing language/BCL support. |
| `SS5001` | Shadowed local in the label VM. |
| `SS6001`, `SS6003`-`SS6006` | Duplicate heap type, missing constructor/member/initializer mapping, dictionary initializer, or constructor cycle/chain issue. |
| `SS6101`/`SS6102`, `SS6201`/`SS6202` | Narrow list/dictionary construction and method arities. |
| `SS6301`, `SS6302` | One-dimensional arrays only; `foreach` only on arrays, lists, or a successfully planned LINQ source. |
| `SS6401`-`SS6403`, `SS6410`, `SS6411` | Unsupported random/LINQ overloads, lambda/query shape, selector type, query clause, or full grouping value. |
| `SS7001`-`SS7005` | Narrow Service Broker state-machine shape, invalid task source, nonspillable locals/captures, or VM calls from workers. |
| `SS8201` | Native kernels requested outside memory-optimized mode. |

### 4.3 Semantic differences and edge cases

- SQL Server arithmetic, overflow, division edge cases, `decimal` scale, floating-point behavior, null propagation, collation, and culture formatting cannot generally be assumed identical to .NET. The README correctly warns about these differences (`README.md:11-12`).
- The type mapper is necessarily lossy for some CLR types: `ulong` becomes `DECIMAL(20,0)`, `decimal` becomes `DECIMAL(38,18)`, `DateTime` becomes `DATETIME2(7)`, and `TimeSpan`/`TimeOnly` become `TIME(7)` (`src/SharpSql/SqlTypeMapper.cs:8-34`).
- Heap reference IDs are `INT`; an ephemeral program is bounded to roughly 2.1 billion allocations. There is no per-object reclaim (`docs/architecture.md:122-130`).
- Dictionary string equality uses a fixed binary SQL collation, which approximates ordinal equality but is not a configurable .NET comparer.
- `Thread.GetCurrentProcessorId()` intentionally maps to `@@SPID`, so in Service Broker it identifies the current SQL worker session, not a processor, and can change across awaits (`docs/service-broker-async.md:42-44`).
- Service Broker output order among concurrently running tasks is scheduler/commit order, not source enumeration order.
- Application schemas are single-package ownership boundaries. Publishing another name/version into the same schema replaces the entry procedure and manifest; there is no side-by-side version (`docs/application-publishing.md:60-68`).

## 5. Public API and packaging surface

### 5.1 Compiler/library APIs

The intended public library surface is small:

- `SharpSqlCompiler` with source and `CSharpCompilation` overloads (`src/SharpSql/SharpSqlCompiler.cs:51-84`). The direct `IrProgram` overload is internal.
- `TranspileOptions`, `RuntimeStorageKind`, `TranspileResult`, and `CompilerDiagnostic` (`src/SharpSql/TranspileOptions.cs:4-53`, `src/SharpSql/TranspileResult.cs:6-11`, `src/SharpSql/CompilerDiagnostic.cs:9-19`).
- `SharpSqlMemoryOptimizedRuntime.GenerateProvisioningSql([schema])` and `SharpSqlServiceBrokerRuntime.GenerateProvisioningSql()`.
- `SharpSqlApplicationPackage.GenerateInstallSql()`.
- `SharpSqlProjectCompiler`, `ProjectTranspileOptions`, and `ProjectCompilationResult` in `SharpSql.MSBuild`.
- `SharpSql.DatabaseException` in the SDK runtime assembly.
- `SqlServerConnectionResolver`, `SqlServerSessionFactory`/session/options, and `SqlBatchExecutor`/result/debug options in the non-packable `SharpSql.SqlServer` project.

The CLI assembly exposes its commands, settings, request/result models, replaceable service interfaces, parity runner, and SQL run/publish services as public types. These are useful test seams but are not documented as a stable programmatic SDK.

### 5.2 Analyzer

`SharpSqlCompatibilityAnalyzer` is a compilation-end analyzer. It skips analysis if Roslyn already has C# errors, reads `build_property.SharpSqlEntryPoint` and `build_property.SharpSqlRuntimeStorage`, runs the same compiler, maps known SharpSql IDs to error descriptors, and converts unknown/internal failures to `SSA0001` (`src/SharpSql.Analyzers/SharpSqlCompatibilityAnalyzer.cs:28-148`). It does not emit warnings/info by itself; users can override severity through normal `.editorconfig` rules.

### 5.3 SDK props and targets

The SDK's `buildTransitive` props default to enabled analyzer/build generation, ephemeral storage, intermediate output, SQL Server 2022 latest, database `SharpSql`, a 60-second timeout, and non-retained containers. `SharpSqlTranspileOnly` sets `SkipCompilerExecution` (`src/SharpSql.Sdk/buildTransitive/SharpSql.Sdk.props:1-19`).

Targets provide:

- Analyzer loading from packaged `tools/analyzers`.
- `SharpSqlTranspile`, resolving explicit/build/intermediate output and invoking the packaged out-of-process build host.
- `SharpSqlGenerateOnBuild` after `CoreCompile`.
- Explicit `SharpSqlRun`, which transpiles and invokes the host to provision/run SQL. It is not attached to normal builds (`src/SharpSql.Sdk/buildTransitive/SharpSql.Sdk.targets:1-48`).

Material defect: `RuntimeStorageKind`, CLI help, README, and props support `MemoryOptimized`, and the build host can parse it, but the shipped target permits only `ephemeral`, `durable`, or `servicebroker` and emits an error otherwise (`src/SharpSql.Sdk/buildTransitive/SharpSql.Sdk.targets:17-23`). The build host's invalid-value text also omits memory-optimized even though enum parsing accepts it (`src/SharpSql.Build/Program.cs:235-251`). Therefore `SharpSqlRuntimeStorage=MemoryOptimized` is not usable through the SDK today.

The SDK target also has no property/argument for `EnableNativeKernels` or `ApplicationSchema`; those are compiler/CLI publishing surfaces only.

### 5.4 NuGet contents and release status

| Project/package | Packability and actual release workflow |
| --- | --- |
| `SharpSql.Tool` | Pack-as-tool, command `sharpsql`, ReadyToRun in Release, six RIDs (`linux/win/osx`, x64/arm64), plus a root tool package (`src/SharpSql.Cli/SharpSql.Cli.csproj:1-24`). Published by workflow. |
| `SharpSql.Sdk` | Packable `DevelopmentDependency`; includes its `lib/net10.0` build output (notably `DatabaseException`), `buildTransitive` props/targets, complete build-host output under `tools/net10.0/any`, and analyzer/compiler/IR DLLs under `tools/analyzers` (`src/SharpSql.Sdk/SharpSql.Sdk.csproj:1-33`). Published by workflow. |
| `SharpSql` | Packable, multi-targeted `net10.0;netstandard2.0`, with package metadata/readme. The publish workflow does not pack or push it as a standalone package. |
| `SharpSql.Ir` | Packable and multi-targeted, but every contract is internal; no meaningful public extension surface. The publish workflow does not publish it. |
| `SharpSql.MSBuild` | Packable with package metadata, but not packed/published by the release workflow. Its functionality is bundled into tool/SDK outputs. |
| Analyzer, Build host, SQL Server utility | Explicitly non-packable and bundled where needed. |

The publish workflow explicitly expects exactly eight packages: six RID tool packages, one root tool package, and one SDK package (`.github/workflows/publish.yml:108-125`, `.github/workflows/publish.yml:143-170`). Thus README examples that embed `SharpSqlCompiler` or `SharpSqlProjectCompiler` describe source/project APIs but are not backed by standalone compiler/MSBuild packages from this repository's current release workflow. There is no symbols package, SourceLink configuration, assembly/package signing, or NuGet package-validation baseline in the project files.

## 6. Code health

### 6.1 Improvements visible after cleanup

- The compiler is split into feature-focused partials rather than one monolithic file: frontend, IR backend, expressions/statements, heap access/creation/mutation, LINQ planning/execution/helpers, VM, async, random, dispatch, comments, analysis, and native kernels.
- CLI initialization and parity execution have been split into separate files/services; SQL Server connection/session/execution moved to a shared project.
- Service Broker infrastructure procedures are split across base, output, task, and completion emitters.
- Backend-neutral types live in a dedicated dependency-light assembly, and tests actively enforce the Roslyn/SQL-free contract.
- Deterministic builds, nullable analysis, warnings-as-errors, formatting verification, package smoke tests, XML docs, and integration parity are all configured (`Directory.Build.props:1-16`, `.github/workflows/ci.yml:28-89`).
- The initial worktree was clean, so this report did not have to reason around unrelated tracked edits.

### 6.2 Remaining large files

The split improved ownership but several files are still review and maintenance hotspots:

| File | Lines | Suggested boundary |
| --- | ---: | --- |
| `SharpSqlCompiler.Async.cs` | 1,352 | Separate plan validation, worker procedure emission, launcher emission, payload serialization, and output/task helpers. |
| `SharpSqlCompiler.Vm.cs` | 1,275 | Separate VM planning/layout, expression lowering, statement lowering, call/return convention, and storage-specific SQL. |
| `SharpSqlCompiler.Linq.Planning.cs` | 1,148 | Separate source recognition, fluent operators, query syntax, lambda/capture binding, and plan transformations. |
| `SharpSqlCompiler.Heap.Creation.cs` | 1,026 | Separate object/constructor, list, dictionary, array, record clone, and initializer emitters. |
| `SharpSqlCompiler.Linq.Execution.cs` | 947 | Separate terminal aggregation/guarding, iteration, and materialization. |
| `SharpSqlCompiler.CSharpFrontend.cs` | 873 | Separate procedural binding, expression binding, heap-type binding, query binding, and source mapping. |
| `SharpSqlCompiler.Heap.cs` | 805 | Separate runtime schema/preamble, type metadata, durable naming, and cleanup. |

The corresponding tests are also concentrated: `CompilerTests.cs` is 1,708 lines, `IrBoundaryTests.cs` 1,486, `CliTests.cs` 933, and the two Service Broker integration files are 734/785 lines. These are less risky than source hotspots but make ownership and selective execution harder.

### 6.3 Duplication and transitional seams

- Plan/debug parsing and SQL execution logic exist both in `SharpSql.SqlServer.SqlBatchExecutor` and `TestcontainersParityRunner.SqlServer`; the integration `ParityHarness` adds a third narrower execution/capture path. Their output normalization, showplan parsing, error mapping, and message handling can drift (`src/SharpSql.SqlServer/SqlBatchExecutor.cs:91-359`, `src/SharpSql.Cli/TestcontainersParityRunner.SqlServer.cs:51-258`, `tests/SharpSql.IntegrationTests/ParityHarness.cs:77-130`).
- C# compile/load/capture logic is duplicated between the CLI parity runner and integration harness.
- The CLI directly links `tests/SharpSql.ConformanceTests/ConformanceRunner.cs` into the production tool (`src/SharpSql.Cli/SharpSql.Cli.csproj:23`) rather than placing the conformance runner in a neutral source project. This couples shipped tool source to a test project path.
- The source compiler's IR backend still consults a Roslyn-source side map for a small heap/LINQ compatibility layer (`src/SharpSql/SharpSqlCompiler.IrBackend.cs:145-153`). The architecture is clean at the assembly boundary, but source and hand-built-IR paths can still diverge.
- `SharpSql.Ir` is separately packable while having no public contracts. Either make it explicitly non-packable/internal-only or define a versioned supported public extension surface.
- Packable standalone compiler/MSBuild projects are not part of release publishing. This may be intentional bundling, but csproj metadata and README embedded-library examples imply a broader NuGet surface than the workflow actually ships.

### 6.4 Consistency issues

1. **SDK memory mode contradiction:** documented and accepted in compiler/CLI/analyzer, rejected by `SharpSql.Sdk.targets`.
2. **Build-host error text:** says only three modes even though `Enum.TryParse` accepts `MemoryOptimized`.
3. **README retained-container label:** README says verify containers use `io.sharpsql.verify.reusable=true` (`README.md:283-288`), while the current shared session factory labels them `io.sharpsql.sqlserver.reusable` and `io.sharpsql.sqlserver.scope` (`src/SharpSql.SqlServer/SqlServerSession.cs:83-118`).
4. **Service Broker guide mode list:** it says SDK `SharpSqlRuntimeStorage` accepts only Ephemeral/Durable/ServiceBroker (`docs/service-broker-async.md:109-113`), while README and enum include MemoryOptimized. The target happens to match the narrower text, not the intended overall surface.
5. **Conformance timeout:** timed-out compiler work is not canceled, weakening the promised protection.
6. **Application/native asset retention:** published procedures and content-addressed native/Service Broker procedures have no uninstall or garbage-collection mechanism.

No clear wholly dead subsystem was found on `main`: durable storage, async infrastructure, memory types, native kernels, publishing, build host, analyzer, SQL utilities, and conformance are all referenced and tested. The closest candidates for simplification are duplicated parity/SQL infrastructure, legacy Roslyn compatibility fallbacks, and packaging metadata for artifacts that are never released.

## 7. Experimental and incomplete features

### 7.1 Bytecode runtime branch

`feat/bytecode-runtime` has two commits not on `main` (`f4fc644`, `5918f57`), while `main` has eight later commits absent from that branch. Its merge-base predates memory-optimized VM storage, native kernels, application publishing, and the recent file-splitting/package cleanup. The branch adds about 5,487 lines across bytecode IR, optimizer, lowerer, SQL interpreter, docs, CLI selection, and tests.

What the branch actually implements:

- An explicit `ExecutionBackendKind.Legacy|Bytecode` option.
- Immutable bytecode contracts, validation/disassembly concepts, and a deterministic optimizer.
- IR lowering for a synchronous scalar subset: `int`, `bool`, `string`, and void; declarations/assignments; arithmetic/comparison/Boolean logic; branches and loops; direct calls/returns/recursion; interpolation/string concat; and `WriteLine` host calls.
- A data-driven ephemeral SQL interpreter using temporary program/method/instruction/frame/slot/evaluation-stack tables and numeric PCs.
- Compiler, optimizer, contract, emitter, parity, and live SQL Server tests.

Explicit branch limits:

- Bytecode is allowed only with ephemeral storage (`feat/bytecode-runtime:src/SharpSql/SharpSqlCompiler.BytecodeBackend.cs:18-25`).
- The lowerer supports only `int`, `bool`, `string`, and void (`feat/bytecode-runtime:src/SharpSql/IrBytecodeLowerer.cs:1146-1150`).
- No heap objects/collections, LINQ relational host operations, virtual dispatch, exceptions, async, durable program catalog/state, Service Broker scheduling, or memory-optimized/native interpreter.
- Unsupported IR fails atomically with `SS8101`-`SS8109`; it does not fall back per method to the legacy backend.

The branch's ADR correctly treats bytecode as a downstream backend artifact and proposes eventual durable numeric PCs, versioned ABI/schema, host operations for relational islands, and a shared async runtime. That target is substantially ahead of implementation. Because the branch modifies core files that have since diverged, it needs a deliberate rebase/port and a decision about interaction with `MemoryOptimized`, native kernels, publishing/application schema, and the refactored files before merge. It should not be described as a current SharpSql runtime mode.

### 7.2 Application publishing

Publishing is functional and tested but still a first deployment slice:

- One schema owns one application and one mutable manifest row.
- Re-publish updates the same `Run` procedure/manifest; no historical versions, blue/green aliases, rollback, migrations, uninstall, or asset retention exist.
- Only ephemeral and memory-optimized compiler modes are exposed. Service Broker application provisioning/routing is not integrated.
- The package embeds a whole generated batch inside a procedure. Long-term compatibility/versioning of compiler/runtime objects is represented only by manifest text/hash, not an enforced runtime ABI.
- Native content-addressed procedures can accumulate in the application schema.

### 7.3 Memory-optimized runtime

The mode is explicitly experimental and moves VM state plus the complete managed heap into memory-optimized tables. SharpSql still does not provision the physical filegroup/container. Its documented benchmark (roughly 32% lower elapsed time on recursive Fibonacci) is a microbenchmark, not a general performance guarantee (`docs/memory-optimized-runtime.md`).

### 7.4 Native kernels

Native kernels are an optimization prototype rather than a general compiler backend. Eligibility is deliberately narrow, there is no profitability threshold despite docs identifying that need, procedure creation happens from program execution/deployment SQL, and retention is unmanaged. The performance test demonstrates a large benefit for a 100,000-iteration scalar loop, but tiny kernels may regress due to call/deployment overhead (`docs/native-kernels.md:61-75`).

### 7.5 Service Broker async

The infrastructure is much more general than the frontend lowering. Tables/procedures model timers, joins, typed results, durable references, faults, routes, output, and concurrent workers, but the compiler accepts only one hard-coded fork/join shape. The major missing layer is a general transformation from arbitrary async control flow to durable frames/PCs, including multiple awaits, `finally`, exception unwinding, cancellation, closure cells, leases, and versioned installed-program retention (`docs/service-broker-async.md:136-160`).

### 7.6 Other incomplete layers

- Struct value semantics, boxing/unboxing, and general `object` behavior.
- General delegate invocation outside supported LINQ plan flow.
- Iterator/yield state machines.
- `finally` and exception unwinding through recursive VM frames.
- External `IQueryable<T>` source mapping and schema metadata.
- Full grouping/nested sequences and per-group aggregate projections.
- Broader BCL intrinsics and exact overflow/culture behavior.
- Runtime program/schema ABI migration and installed-asset cleanup.

## 8. Recommended priorities

1. Fix and test the SDK `MemoryOptimized` validation path and align README/guide/build-host messages.
2. Decide and document the intended NuGet surface: either publish standalone `SharpSql`/`SharpSql.MSBuild` packages or remove packability/embedded-library guidance that implies they are released.
3. Add an SDK package smoke case for memory-optimized generation/run and, if desired, a property for native kernels.
4. Move shared parity C# execution, SQL execution, showplan parsing, and outcome normalization into one reusable layer.
5. Split async, VM, LINQ planning, and heap creation along the responsibilities listed above before adding more language features.
6. Make the conformance runner multi-file-aware where possible, validate CLR compilation, distinguish “transpiles” from “semantically conforms,” and use genuinely cancellable isolation for timeouts.
7. Define retention/uninstall/versioning for published applications, Service Broker `Program_<hash>` procedures, and native kernels.
8. Rebase the bytecode work as an explicit research track only after defining how it composes with the four current storage modes and the application package ABI.

## 9. Bottom line

SharpSql's default compiler is beyond a toy: it has a real neutral IR, semantic reachability and effect analysis, three method-lowering strategies, a typed object heap, meaningful managed LINQ, exception mapping, differential SQL tests, SDK/analyzer integration, and working durable/Service Broker experiments. The implementation and tests substantiate those claims.

It is still experimental in the exact places the documentation admits: broad C# syntax and BCL coverage, CLR-exact semantics, general async, deployment lifecycle, and alternative runtime maturity. The current release surface also has two actionable mismatches—SDK memory-mode rejection and unreleased standalone compiler packages—that should be resolved before presenting all documented APIs/modes as uniformly consumable.
