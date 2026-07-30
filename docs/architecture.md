# Compiler architecture

The compiler pipeline is:

```text
C# source text or MSBuild project
  -> Roslyn syntax + semantic model
  -> C# frontend binding + supported-subset validation
  -> immutable SharpSql.Ir program
  -> one IR method graph plus purity, cost, and inlining passes
  -> SQL Server backend plans and lowering
  -> one or more executable batches
```

`SharpSql.Ir` is a separate assembly with no Roslyn or SQL Server dependency. Its immutable `IrProgram` owns typed methods, symbols, expressions, query clauses, type hierarchies, fields/properties, constructors, source spans/comments, and a `ProceduralStatement` hierarchy with explicit declarations, assignments, branches, loops, jumps, and returns. Stable semantic IDs connect method calls, member references, and object creation to their definitions; constructor IR retains parameters, `this`/`base` initializer arguments, and procedural bodies even when the SQL runtime cannot lower that behavior yet. Diagnostics and comments use neutral source records rather than retaining Roslyn nodes. The C# frontend finishes binding the entry point, methods, and used type definitions before SQL emission begins.

Single-file mode creates a Roslyn compilation directly. Project mode is isolated in `SharpSql.MSBuild`: `MSBuildWorkspace` performs a design-time build and supplies the resulting `CSharpCompilation`, so all documents share the project's actual references, generated sources, global usings, language version, nullable context, and conditional symbols. The user selects a parameterless static method with `Namespace.Type::Method`, or an executable project's compiler-selected entry point is used. The frontend collects methods and heap types across all syntax trees before lowering, while generated-file banner comments are excluded from SQL output. Project-reference assemblies participate in semantic binding, but SharpSql currently lowers method bodies only from the selected project's source compilation.

`SharpSql.Sdk` packages this boundary for ordinary C# projects. Its
`buildTransitive` assets attach `SharpSqlTranspile` after `CoreCompile`, while an
out-of-process .NET build host opens a design-time workspace and writes SQL for
the selected entry point. Design-time builds skip the target, preventing
recursive project loading. A `netstandard2.0` Roslyn analyzer uses the same
compiler frontend and diagnostics against the IDE's active `CSharpCompilation`;
the compiler and IR assemblies therefore multi-target `net10.0` for execution
and `netstandard2.0` for analyzer-host compatibility.

`SharpSql.SqlServer` is the shared execution boundary for CLI, build-host, and
verification workflows. It resolves named connections without storing secrets
in project files, or starts a project-scoped reusable SQL Server Testcontainer
when no connection is configured. The explicit `SharpSqlRun` target executes
the output of `SharpSqlTranspile`; it is never attached to an ordinary build.
The same session abstraction owns database selection, message capture, and
container cleanup, and is intended to back database-first scaffolding later.

`IrType` carries only language/runtime type identity. `CSharpTypeFactory` is the frontend mapping from Roslyn, while `SqlTypeMapper` belongs to the SQL Server backend. Likewise, `SqlScalarExpression` and the `SqlLinq*` plans are explicitly backend models: SQL text and precedence never appear in compiler IR. LINQ lambda bodies are bound IR expressions, and query syntax is represented by neutral query clauses before the backend chooses relational SQL.

Roslyn supplies semantic types, nullable flow state, constants, and data-flow facts while binding. A semantic preflight imports definite-assignment, use-before-declaration, out-parameter, and missing-return failures while leaving intentional SharpSql extensions—such as mutable positional-record fields—to the supported-subset validator. Each method carries the endpoint-reachability and statement-cost facts consumed by lowering. An overload-aware method catalog and the IR method graph use stable semantic IDs for call-site counts, caller/callee edges, recursion membership, effect propagation, and the connected closure required by VM selection; identical method names in different signatures or declaring types are never conflated. Method definitions retain abstract/virtual/override slots and implemented-interface identities for runtime dispatch. A backend-neutral fixed-point pass propagates effects through resolved calls, including mutable-state reads/writes, allocation, throwing, nondeterminism, I/O, unknown calls, parameter mutation and escape, returned parameter aliases, and fresh references. A shared intrinsic catalog supplies conservative behavior and recognition for collections, LINQ, console output, and stateful `Random` calls.

The SQL backend can be invoked with a manually constructed `IrProgram`, independently of the C# frontend for scalar and procedural IR, including graph and VM preparation. Managed arrays, user objects, target-typed construction, record cloning, `List<T>`, `Dictionary<TKey,TValue>`, and seeded `Random` lower directly from IR and prepare their runtime tables without Roslyn source. Managed-source LINQ planning also works directly from IR, including query syntax, grouping and joins, guarded terminals, delegate and query helpers, nested materialization, and sequential stateful `Repeat` selectors. Boundary tests compile C# to IR, compile hand-built IR to SQL, and reflect over the IR contract to reject Roslyn types and SQL payload properties.

## Inlining policy

Each method gets a summary containing:

- Call-graph edges and recursion/SCC membership
- Statement count and estimated expanded size
- Purity and observable side effects
- Parameter use counts
- Control-flow exits

The decision order is:

1. Substitute a supported, side-effect-free expression. Arguments must either be side-effect-free or materialized once when a parameter is used more than once.
2. Expand a procedural body with hygienically renamed parameter/local variables. Unique labels and `GOTO` preserve early returns and loop exits without maintaining a returned flag.
3. Lower eligible over-budget scalar control flow through compact Core IR into versioned register bytecode; use the label-based VM for richer or recursive fallback methods.
4. Apply specialized transformations such as tail-recursion elimination later where they outperform the general fallback.

The budget needs both per-method size and total expanded-size limits; a small method called hundreds of times can still cause pathological output growth.

## Control-flow lowering

Roslyn statements first bind to procedural IR. Ordinary lowering, Core IR, register bytecode, and the label-based VM consume that shared model. Neutral source spans and captured comments provide diagnostics and comment placement. Legacy syntax statement emitters have been removed; compatibility fallbacks for a small set of heap and LINQ intrinsics still consult their originating syntax when IR lowering declines them.

Core IR is the compact backend boundary: registers, basic blocks, five scalar instruction shapes, and three terminators. Its first executable consumer maps values directly to register bytecode with eight stable instruction families: constant, move, convert, unary, binary, branch, call, and return. The SQL image stores only populated operands, and one generic interpreter handles every selected method. `ManagedFallbackKind.Auto` requires both complete lowering and a projected image-size win; `Legacy` preserves the label VM; strict `Bytecode` reports `SS8001` for any required fallback method outside the current scalar subset. See [register bytecode](register-bytecode.md).

Each inlined method receives a unique end label. A source `return value;` assigns the result and jumps directly to that label. Loops similarly receive condition/body, continue, and break labels. This is a compact target for arbitrary control-flow graphs while keeping source-level `if` statements structured:

```sql
__sharpsql_for_condition:;
IF NOT (@i < 10) GOTO __sharpsql_for_break;
-- body
__sharpsql_for_continue:;
SET @i = @i + 1;
GOTO __sharpsql_for_condition;
__sharpsql_for_break:;
```

Labels are batch-scoped rather than block-scoped, so every generated label goes through a global collision-safe allocator.

## Expression rendering

IR expressions carry language type, null-flow state, constant facts, and explicit operators. The SQL backend converts them to `SqlScalarExpression`, where SQL precedence and rendered text belong. Parent expressions request an operand at a minimum precedence, producing `5 * 5` for `Square(5)` while retaining both required pairs in `Square(a + b)`. A text-level regular expression is deliberately avoided because it cannot safely distinguish expressions from strings or preserve associativity such as `a - (b - c)`.

## Stack-machine fallback

The fallback stays inside one T-SQL batch. `#__sharpsql_stack` holds activation frames, while `#__sharpsql_slots` holds parameters, spilled locals, and intermediate expression values. Strings and binary values have dedicated columns; the remaining scalar types use `SQL_VARIANT` and are converted back to their declared type when loaded.

```sql
-- call site
INSERT INTO #__sharpsql_stack (__function_id, __return_id) VALUES (1, 7);
SET @__sharpsql_new_frame_id = CONVERT(INT, SCOPE_IDENTITY());
-- store arguments in slots for the new frame
GOTO __sharpsql_vm_Fibonacci_entry;

__sharpsql_return_7:;
SET @answer = CONVERT(INT, @__sharpsql_result);
```

Every method return copies its value to a typed result register, reads its frame's static continuation ID, deletes the frame, and jumps to the shared trampoline:

```sql
__sharpsql_dispatch:;
IF @__sharpsql_jump = 1 GOTO __sharpsql_return_1;
IF @__sharpsql_jump = 2 GOTO __sharpsql_return_2;
-- exactly one entry per static call site
GOTO __sharpsql_halt;
```

The continuation table is finite even for recursion because recursive activations reuse the same static call sites. Caller registers are spilled before a nested call and restored from the surviving parent frame afterward. Expression temporaries are frame-indexed, so two recursive calls in an expression such as `Fib(n - 1) + Fib(n - 2)` cannot overwrite each other's values.

The current VM lowering supports scalar and heap-reference parameters/results, declarations, assignments, arithmetic and conditional expressions, branches, loops, nested calls, direct recursion, and mutual recursion. It does not yet support shadowed locals, exceptions, `switch`, `foreach` inside VM-backed methods, or arbitrary library calls. Unsupported forms remain diagnostics.

Both runtime tables are dropped at normal halt and pre-dropped at startup to recover from an earlier failed batch on a reused connection. Local temporary tables are also removed when their SQL connection closes.

The independent memory-optimized option moves VM state and the complete managed heap
into database-global, execution-partitioned In-Memory OLTP tables. Ephemeral tables use
`SCHEMA_ONLY`; durable tables use `SCHEMA_AND_DATA`, with separate versioned names so
both can coexist. Statically typed scalar slots, fields, indexed values, dictionary keys,
and dictionary values use `VARBINARY(8000)` round-tripping because memory-optimized
tables cannot contain `SQL_VARIANT`; text, binary, and references use dedicated columns.
Generic field rows are keyed by declaring-type and field IDs, so programs do not require
dynamic per-type memory-table DDL.

## Managed heap and collections

Every allocated reference receives an `INT` ID from `#__sharpsql_objects`. Each closed-world class or record gets a typed table keyed by that ID. Consequently, member reads remain typed scalar subqueries and member writes are direct updates; no universal EAV conversion is required for ordinary objects. The ephemeral runtime therefore supports up to roughly 2.1 billion allocations per batch while keeping its keys and indexes narrow.

The object header also stores collection counts and small intrinsic metadata. Arrays and `List<T>` share an indexed item runtime with separate scalar, string, binary, and reference columns. `Random` reuses that table for its indexed state array, since globally unique owner IDs prevent collisions with collection elements. `Dictionary<TKey,TValue>` uses the same typed-union layout for keys and values in its entry table. Closed generic types determine which column and conversion the compiler emits. String dictionary operations use a binary collation to approximate ordinal .NET equality.

`byte[]` is the deliberate exception to the per-element indexed-array representation. Variables and fields carry a managed object ID, while one indexed payload row stores the complete native `VARBINARY(MAX)` value. Construction, `Length`, indexed reads and writes, `foreach`, and `SequenceEqual` therefore avoid per-byte heap rows while assignments, parameters, fields, lists, and dictionary keys retain CLR-style reference identity.

References are normal VM scalar values, so callers spill object, list, and dictionary IDs into activation slots just like integers. There is no per-object garbage collection: scripts are ephemeral, and dropping the heap tables reclaims the entire heap at once.

`Random` instances are also heap references. Their cursor pair lives in the shared object header and their 56-element state arrays live in the shared indexed item table. Seeded construction implements .NET's compatibility subtractive PRNG, allowing `Next()`, `Next(max)`, `Next(min,max)`, and `NextDouble()` to advance independently per object and reproduce seeded .NET sequences. Parameterless construction supplies a SQL-generated seed to that same state machine.

Heap lowering executes instance field/property initializers, procedural constructor bodies, overloaded constructor identities, same-type `this(...)` chains, and explicit or implicit base-constructor chains. A class instance has one shared object identity and one typed row for each class in its base-to-derived hierarchy, so inherited and hidden fields are read and written through their declaring-type tables. Constructor arguments are captured before allocation; all hierarchy rows are default-initialized before base-to-derived construction; object initializers run last. Constructor bodies share the normal procedural and VM-aware expression lowerers, so control flow, managed allocations, method calls, and construction inside recursive VM frames preserve their state. Virtual, abstract, and interface call sites use the object header's runtime type ID to choose an override or implementation, then enter that concrete method through the shared VM dispatcher; `base.Method()` remains a direct call. Side-effect-free expression implementations can instead render as scalar `CASE` dispatch inside relational LINQ plans. The next object-runtime layers are structs and boxing, broader delegate execution outside LINQ, exception unwinding, and external-source `IQueryable<T>` lowering.

The LINQ lowerer builds a compile-time relational plan over an array or `List<T>` heap source, or a virtual `Enumerable.Range` source rendered with SQL Server 2022 `GENERATE_SERIES`. Range arguments are captured and validated immediately like C#, while values remain lazy and compose into downstream predicates and `TOP` paging without heap insertion. Filtering, projection, ordering, pagination, distinct, joins, and grouped-key stages compose as derived-table operations. Aggregates and element operators render terminal scalar queries; operators whose .NET contracts throw emit explicit guards for empty, multiply populated, or out-of-range results. A query variable stores its plan in the compiler binding while its SQL `INT` captures a heap source object when one exists, retaining deferred predicate/projection evaluation. `AsEnumerable()`, managed `AsQueryable()`, query syntax, direct `foreach`, and `ToList()`/`ToArray()` materialization all consume the same plan.

Lambda bindings carry their syntax plus scalar captures. This lets stored delegates, lexical closures, returned delegate factories, and expression-bodied or single-return helper methods pass predicates and deferred query plans through parameters and return values. Method-local captures are spilled when the helper is invoked, preserving capture-by-value for returned closures while ordinary lexical closures continue to observe later mutations. Grouping currently represents distinct group keys, which supports group counts and `group.Key` projections; full `IGrouping<TKey,TElement>` materialization and per-group aggregate projections require a richer nested-sequence representation.

`Enumerable.Repeat(...).Select(...).ToArray()`/`ToList()` also has an ordered procedural lowering. This path permits stateful selectors such as `value => value[random.Next(value.Length)]`, which cannot be represented as a pure relational projection without changing evaluation semantics.

This `IQueryable<T>` support applies only to SharpSql-managed arrays and lists. Translating a query rooted in an external database table still requires a source-mapping API and schema/type metadata.

## Comment preservation

Comments are read from Roslyn trivia rather than by scanning source text, so comment markers inside string literals are never mistaken for comments. Leading and trailing comments are attached to their generated statement; comments inside a rewritten expression are emitted immediately before its containing SQL statement. Method, type, and member documentation follows the inlined/stack-machine body or temporary heap table it describes. Each source comment is tracked by source position and emitted once, with any otherwise unattached comment retained near the end of the batch.

## Differential integration tests

Every source file under `examples/` is an executable specification and an independently reported theory case. The integration suite compiles each file with Roslyn and captures a structured .NET outcome, then transpiles the same source, executes the batch against SQL Server 2022, and captures `PRINT` messages and SQL failures. Successful cases compare normalized standard output and entry-point return values ordinally.

Two focused corpora live under `tests/SharpSql.IntegrationTests/cases`. Runtime-failure cases declare their expected .NET exception type in a `sharpsql-expect-exception` source directive; the suite compares output produced before the failure and maps SharpSql's reserved SQL error numbers back to that type. Diagnostic cases must first be valid C#, then declare the exact expected SharpSql code set with `sharpsql-expect-diagnostics`. This separates unsupported-language contracts from runtime semantic parity.

Testcontainers owns container startup, readiness, random host-port allocation, and cleanup. The collection fixture shares one SQL Server instance for the full corpus but opens a fresh connection for every batch, preserving local-temporary-table isolation. Discovery is path-sorted, and failures include the case path, both structured outcomes, generated SQL, and original source for deterministic reproduction. Adding an example or corpus file automatically adds one test.

The corpus spans arithmetic and numeric widths, null/boolean behavior, Unicode and escaping, nested control flow, arrays, mutable collections, dictionary key collation, heap aliasing, instance methods, inlining, recursion, short-circuit evaluation, relational LINQ pipelines, managed `IQueryable<T>`, advanced ordering/paging/join/group stages, guarded terminals, delegate/query-plan flow, query syntax, stateful `Enumerable.Repeat` materialization, and character-array string construction. Narrow compiler unit tests accompany any lowering defect first discovered by a differential example.

SQL Server's scalar-UDF inlining feature does not solve object lifetime: it optimizes eligible schema UDFs after they have been created. Persisted UDF emission should therefore remain an opt-in deployment mode, never the script default.
