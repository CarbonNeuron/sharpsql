# Memory-optimized runtime tables

Status: experimental SQL Server 2022 storage option.

`UseMemoryOptimizedTables` is independent from execution and durability. It preserves
direct-SQL and static-label lowering while moving the stack-machine activation frames
and spilled slots into database-global memory-optimized tables. Every row is partitioned
by `ExecutionId`, allowing inline batches and Service Broker sessions to share the same
physical runtime safely.

Ephemeral configuration uses `DURABILITY = SCHEMA_ONLY`; its rows are not recovered
after SQL Server restarts. Durable configuration uses `DURABILITY = SCHEMA_AND_DATA`.
Separate versioned table names allow both configurations to coexist in one database.

## Provisioning

SQL Server requires a database-scoped `MEMORY_OPTIMIZED_DATA` filegroup and physical
container. Choose a path valid for the SQL Server host and provision it explicitly:

```sql
ALTER DATABASE [ApplicationDatabase]
    ADD FILEGROUP [SharpSqlMemoryOptimized] CONTAINS MEMORY_OPTIMIZED_DATA;

ALTER DATABASE [ApplicationDatabase]
    ADD FILE
    (
        NAME = N'SharpSqlMemoryOptimized',
        FILENAME = N'/replace/with/a/sql-server-data-path/SharpSqlMemoryOptimized'
    )
    TO FILEGROUP [SharpSqlMemoryOptimized];
```

On SQL Server 2022 and older, removing the final memory-optimized container/filegroup
requires dropping the database. SharpSql consequently never emits this physical DDL.

After the filegroup exists, run the idempotent SQL returned by:

```csharp
SharpSqlMemoryOptimizedRuntime.GenerateProvisioningSql(
    new RuntimeConfiguration(
        RuntimeExecutionKind.Auto,
        RuntimeDurabilityKind.Ephemeral,
        UseMemoryOptimizedTables: true))
```

The CLI writes that script beside an output program automatically:

```bash
sharpsql transpile Program.cs \
  --execution Inline --memory-optimized \
  --output Program.sql
```

The legacy `--runtime-storage MemoryOptimized` spelling remains available as a
compatibility alias.

This provisions the compatibility table types plus a durability-specific pair of
global VM tables. Generated programs verify that the selected tables exist, scope all
access by execution ID, and remove their rows on normal completion, early return, or
failure. A server restart automatically clears `SCHEMA_ONLY` rows.

## Representation

In-Memory OLTP does not support `SQL_VARIANT`. The slot type therefore has dedicated
string and binary columns, plus a `VARBINARY(8000)` scalar column. Scalar values are
converted to their SQL binary representation when spilled and converted back using
the statically known IR type when restored. This keeps one fixed table type while
preserving Boolean, numeric, character, temporal, GUID, reference-ID, string, and
binary behavior covered by the parity suite.

Memory-optimized tables cannot be `MERGE` targets. Register spilling uses a delete and
multi-row insert instead of the ephemeral runtime's `MERGE` upsert.

## Initial measurement

The earlier table-variable prototype measured the following on the repository's SQL
Server 2022 Linux container. The new global-table design needs a fresh benchmark before
these numbers can be treated as representative:

| Storage | Median execution |
| --- | ---: |
| Temp-table VM | 59.056 ms |
| Memory-optimized VM | 40.074 ms |

The observed memory/temp ratio was `0.679x`, approximately a 32% elapsed-time
reduction. This is a focused microbenchmark, not a universal throughput claim.

The global-table implementation remains covered by the parity corpus, runtime-failure
cases, and concurrent execution isolation tests.

## Current boundary

Managed object headers, per-type object rows, collection/dictionary storage, and LINQ
buffers still use local temporary tables. Moving the fixed-shape object and indexed
item tables is a reasonable next experiment. Per-program object tables have dynamic,
strongly typed columns and need a separate provisioning/versioning design rather than
being forced into an untyped shared heap.

Service Broker can provision and address either memory-table durability. Its current
worker continuation slice still rejects calls that require the VM fallback (`SS7005`),
so global VM state is ready for that integration but is not yet exercised inside an
activated async continuation.
