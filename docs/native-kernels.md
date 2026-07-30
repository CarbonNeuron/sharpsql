# Native compiled legacy kernels

Status: opt-in prototype for SQL Server 2022 In-Memory OLTP.

Native kernels preserve the interpreted legacy runtime as the orchestrator while
extracting supported pure scalar methods into natively compiled stored procedures.
The caller evaluates live-in arguments, invokes the kernel entirely inside SQL Server,
receives the result through an `OUTPUT` parameter, and continues normal generated SQL.

```text
legacy SQL variables
        |
        | scalar input parameters
        v
natively compiled pure method
        |
        | result OUTPUT + integer status
        v
legacy SQL applies/uses result
```

Enable the experiment with the memory-optimized runtime:

```bash
sharpsql transpile Program.cs \
  --execution Inline --memory-optimized \
  --native-kernels \
  --output Program.sql
```

Or through the compiler API:

```csharp
new TranspileOptions
{
    UseMemoryOptimizedTables = true,
    EnableNativeKernels = true
};
```

## Current extraction boundary

The prototype extracts nonrecursive, static, deterministic methods whose parameters,
locals, and return value are `Int32` or `Int64`. It supports declarations,
assignments, increment/decrement, arithmetic/comparison expressions, `if`, `while`,
and return statements. Mutable state, I/O, allocation, nondeterminism, unknown calls,
objects, collections, strings, async code, and recursion stay on the existing legacy
path without changing program semantics.

Arguments are first captured into caller variables to preserve C# evaluation order.
The generated call uses scalar parameters, one result `OUTPUT` parameter, and an
integer return status. This avoids trying to expose temporary or disk tables to the
native module.

Each supported method receives a content-addressed procedure name. The program batch
creates a missing procedure through dynamic DDL before normal execution; subsequent
runs reuse it. Provisioning records each procedure in the application schema's
`NativeKernelCatalog` and refreshes `LastUsedAtUtc`. Catalog creation and procedure
installation are protected by application locks.

Inspect installed kernels with:

```csharp
string sql = SharpSqlNativeKernelRuntime.GenerateStatusSql("SharpSql");
```

Retention is explicit and supports dry-run previews:

```csharp
string preview = SharpSqlNativeKernelRuntime.GenerateCleanupSql(
    "SharpSql",
    unusedFor: TimeSpan.FromDays(7),
    batchSize: 20,
    dryRun: true);
```

Execute the generated SQL in the application database, then repeat with
`dryRun: false` to remove the selected catalog rows and procedures. Cleanup takes an
exclusive per-kernel lock and rechecks `LastUsedAtUtc`; generated calls take the
matching shared lock, so a procedure cannot be dropped while it is running. Package
uninstall also removes its kernel catalog and all content-addressed kernel procedures.

## Initial result

A native accumulator kernel performed 100,000 loop iterations, then returned its
updated scalar to an interpreted wrapper which wrote it back to a temporary state
table. Thirty alternating warmed samples on the SQL Server 2022 test container measured:

| Execution | Median |
| --- | ---: |
| Interpreted loop | 68.255 ms |
| Native kernel | 1.591 ms |

The native/interpreted ratio was `0.023x`, approximately 43 times faster. This confirms
that the procedure and `OUTPUT` boundary is inexpensive relative to a substantial hot
loop. It does not imply that tiny kernels will win: extraction should require a cost
threshold so procedure calls are amortized across enough work.

## Transaction behavior

When invoked without an ambient transaction, successful completion of the native
procedure commits its atomic block. When invoked inside an existing transaction, the
native procedure participates through SQL Server's ambient transaction/savepoint
semantics; it does not independently commit the caller's entire transaction. A thrown
error rolls the atomic work back. The interpreted wrapper can therefore apply returned
updates in the same outer transaction when atomic cross-boundary behavior is required.
