# Compiler architecture

The compiler pipeline is becoming:

```text
C# source
  -> Roslyn syntax + semantic model
  -> supported-subset validation
  -> procedural IR
  -> call graph, purity, cost, and inlining passes
  -> SQL Server lowering
  -> one or more executable batches
```

Roslyn syntax and semantic typing are active today; procedural syntax is still lowered directly. Before adding exceptions, virtual dispatch, and broad library support, the direct lowering should be replaced by a typed IR with nodes such as `Declare`, `Assign`, `If`, `Loop`, `Break`, `Continue`, `Return`, `Print`, `Allocate`, `LoadField`, `StoreField`, and `Call`.

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
3. Emit recursive strongly connected components and over-budget methods once using the stack-machine backend.
4. Apply specialized transformations such as tail-recursion elimination later where they outperform the general fallback.

The budget needs both per-method size and total expanded-size limits; a small method called hundreds of times can still cause pathological output growth.

## Control-flow lowering

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

Emitted scalar expressions carry their SQL operator precedence. Parent expressions request an operand at a minimum precedence, so parentheses are added only when removing them would change the syntax tree. This produces `5 * 5` for `Square(5)`, while retaining both required pairs in `Square(a + b)` as `(@a + @b) * (@a + @b)`. A text-level regular expression is deliberately avoided because it cannot safely distinguish expressions from strings or preserve associativity such as `a - (b - c)`.

## Stack-machine fallback

The fallback stays inside one T-SQL batch. `#__sharpsql_stack` holds activation frames, while `#__sharpsql_slots` holds parameters, spilled locals, and intermediate expression values. Strings and binary values have dedicated columns; the remaining scalar types use `SQL_VARIANT` and are converted back to their declared type when loaded.

```sql
-- call site
INSERT INTO #__sharpsql_stack (__function_id, __return_id) VALUES (1, 7);
SET @__sharpsql_new_frame_id = CONVERT(BIGINT, SCOPE_IDENTITY());
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

## Managed heap and collections

Every allocated reference receives a `BIGINT` ID from `#__sharpsql_objects`. Each closed-world class or record gets a typed table keyed by that ID. Consequently, member reads remain typed scalar subqueries and member writes are direct updates; no universal EAV conversion is required for ordinary objects.

Arrays and `List<T>` share an indexed item runtime with separate scalar, string, binary, and reference columns. `Dictionary<TKey,TValue>` uses the same typed-union layout for keys and values. Closed generic types determine which column and conversion the compiler emits. String dictionary operations use a binary collation to approximate ordinal .NET equality.

References are normal VM scalar values, so callers spill object, list, and dictionary IDs into activation slots just like integers. There is no per-object garbage collection: scripts are ephemeral, and dropping the heap tables reclaims the entire heap at once.

Current heap lowering deliberately diagnoses constructors containing behavior beyond direct field assignments. The next object-runtime layers are constructor-body lowering, inheritance and virtual dispatch, structs and boxing, delegates/closures, exception unwinding, and then `IEnumerable<T>`/`IQueryable<T>` LINQ lowering.

## Comment preservation

Comments are read from Roslyn trivia rather than by scanning source text, so comment markers inside string literals are never mistaken for comments. Leading and trailing comments are attached to their generated statement; comments inside a rewritten expression are emitted immediately before its containing SQL statement. Method, type, and member documentation follows the inlined/stack-machine body or temporary heap table it describes. Each source comment is tracked by source position and emitted once, with any otherwise unattached comment retained near the end of the batch.

## Differential integration tests

Every source file under `examples/` is executable specification. The integration suite compiles each file with Roslyn and captures its real .NET console output, then transpiles the same source, executes the batch against a SQL Server 2022 container, and captures `PRINT` messages through `SqlConnection.InfoMessage`. Output is compared ordinally after normalizing line endings.

Testcontainers owns container startup, readiness, random host-port allocation, and cleanup. The suite shares one SQL Server instance for the example corpus but opens a fresh connection for every batch, preserving local-temporary-table isolation. Adding an example automatically adds it to the parity suite.

The corpus spans arithmetic and numeric widths, null/boolean behavior, Unicode and escaping, nested control flow, arrays, mutable collections, dictionary key collation, heap aliasing, instance methods, inlining, recursion, and short-circuit evaluation. Narrow compiler unit tests accompany any lowering defect first discovered by a differential example.

SQL Server's scalar-UDF inlining feature does not solve object lifetime: it optimizes eligible schema UDFs after they have been created. Persisted UDF emission should therefore remain an opt-in deployment mode, never the script default.
