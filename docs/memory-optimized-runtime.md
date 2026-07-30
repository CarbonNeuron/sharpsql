# Memory-optimized legacy runtime

Status: experimental SQL Server 2022 storage mode.

`RuntimeStorageKind.MemoryOptimized` preserves the legacy direct-SQL and static-label
lowering. Only the stack-machine fallback's activation frames and spilled slots change:
local temp tables become execution-local memory-optimized table variables. Directly
lowered SQL is therefore identical, and concurrent executions do not share runtime rows.

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
SharpSqlMemoryOptimizedRuntime.GenerateProvisioningSql()
```

The CLI writes that script beside an output program automatically:

```bash
sharpsql transpile Program.cs \
  --runtime-storage MemoryOptimized \
  --output Program.sql
```

This provisions two versioned table types, `SharpSql.MemoryVmStackV1` and
`SharpSql.MemoryVmSlotsV1`. Each generated batch declares its own variables of those
types, so state lifetime and isolation match the ordinary ephemeral mode.

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

On the repository's SQL Server 2022 Linux container, 20 alternating warmed runs of
recursive Fibonacci(12) measured:

| Storage | Median execution |
| --- | ---: |
| Temp-table VM | 59.056 ms |
| Memory-optimized VM | 40.074 ms |

The observed memory/temp ratio was `0.679x`, approximately a 32% elapsed-time
reduction. This is a focused microbenchmark, not a universal throughput claim.

The mode also passes all 33 successful parity programs, all 12 runtime-failure parity
programs, and an eight-connection concurrent-state isolation test.

## Current boundary

Managed object headers, per-type object rows, collection/dictionary storage, and LINQ
buffers still use local temporary tables. Moving the fixed-shape object and indexed
item tables is a reasonable next experiment. Per-program object tables have dynamic,
strongly typed columns and need a separate provisioning/versioning design rather than
being forced into an untyped shared heap.
