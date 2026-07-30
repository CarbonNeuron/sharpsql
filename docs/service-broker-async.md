# Service Broker async runtime

SharpSql's async runtime uses SQL Server Service Broker as a worker pool. A logical
program run is identified by an `execution_id` rather than a database session,
because the launcher and activated workers use different connections.

## Runtime invariants

- Durable runtime tables are ordinary database tables under the `SharpSql` schema.
- Every heap object, collection item, dictionary entry, VM frame, slot, task, and
  output event belongs to one execution.
- Globally allocated object and frame IDs avoid collisions; execution predicates
  provide isolation and make cleanup bounded.
- Generated typed heap tables include a stable fingerprint of their type schema so
  independently compiled programs cannot interpret one another's rows.
- Runtime objects are provisioned under an application lock. Executions reuse those
  objects and delete rows, never shared tables.
- Temporary relational buffers that cannot survive an `await` remain local temporary
  tables. Only live state crossing a suspension point is durable.

Durable task states are `0` waiting, `1` ready, `2` enqueued, `3` running,
`4` succeeded, `5` faulted, and `6` canceled. Result kinds are `0` none, `1` scalar,
`2` text, `3` binary, and `4` execution-scoped object reference.

## Execution flow

1. The generated launcher installs a deterministic `Program_<hash>` procedure,
   inserts an execution, and schedules its root task.
2. An activated worker receives `{ program_id, execution_id, task_id }`, loads the
   durable payload, and runs the selected continuation until completion or an
   incomplete `await`.
3. At suspension, the worker stores the next instruction and live locals, registers
   the awaited dependency, and commits. The worker thread is then free.
4. Completing a dependency sends a continuation message. `Task.WhenAll` resumes its
   waiter only when the final child reaches a terminal state.
5. The launcher pumps due millisecond timers, drains output in sequence, observes
   completion, and removes all rows for the execution.

The entry point is itself the root task. The outer generated batch is only its
launcher, so ordinary entry-point code can suspend and resume on activated sessions.

`Thread.GetCurrentProcessorId()` reports the SQL Server session ID (`@@SPID`) of the
activated worker executing the current slice. It identifies the worker rather than a
physical processor; a continuation can report a different ID after another suspension.

## Console output

Workers do not use `PRINT`, because informational messages are returned only to the
worker's own connection. `Console.WriteLine` appends an execution-scoped output event:

```text
OutputEvents(execution_id, sequence_number, output_text, created_at_utc)
```

Sequence allocation and insertion are atomic. When the execution has a response
conversation, the append operation also sends an output notification. The launcher
drains committed events and emits them on its connection. Writes within one task stay
ordered. Output from concurrent tasks is ordered by whichever worker appends first.

The CLI receives ordinary output lines with `NOWAIT`, so they are visible while the
execution is still running. Lines above 2,000 UTF-16 code units retain the larger
buffered `PRINT` path rather than being truncated by SQL Server's informational-error
limit.

## Provisioning

The infrastructure script is available programmatically:

```csharp
string sql = SharpSqlServiceBrokerRuntime.GenerateProvisioningSql();
```

Run it in the target user database with Service Broker enabled. Provisioning requires
database permissions to create a schema, tables, procedures, message types, queues,
contracts, services, and the activated dispatcher; it intentionally does not run
`ALTER DATABASE ... ENABLE_BROKER`. Run provisioning as a standalone batch rather
than inside an existing transaction.

The CLI keeps that deployment boundary explicit. Transpiling or running with
`--runtime-storage ServiceBroker --output out.sql` writes the program to `out.sql`
and the idempotent standalone installer to `out.installer.sql`. Override the latter
with `--installer-output`. `run` executes the installer once before the program and
excludes installation and container startup from `--profile` measurements.

The dispatcher accepts only 32-character hexadecimal compiler program IDs and maps
them to `[SharpSql].[Program_<hash>]`; message data cannot supply an arbitrary
procedure name. Its worker queue uses up to eight activated readers. Internal task
dialogs originate from and target the worker service, so both request and lifecycle
messages remain on the worker queue.

## Compiler mode

Enable async lowering explicitly:

```csharp
var result = new SharpSqlCompiler().Transpile(
    source,
    new TranspileOptions { RuntimeStorage = RuntimeStorageKind.ServiceBroker });
```

For a `SharpSql.Sdk` project, use the matching MSBuild property:

```xml
<PropertyGroup>
  <SharpSqlRuntimeStorage>ServiceBroker</SharpSqlRuntimeStorage>
</PropertyGroup>
```

`SharpSqlRuntimeStorage` accepts `Ephemeral` (the default), `Durable`, or
`ServiceBroker`. The SDK passes it to the analyzer and build host, and
`SharpSqlRun` provisions the infrastructure as a separate batch before running
the generated program SQL. The CLI exposes the same choice as
`--runtime-storage ServiceBroker` for `transpile` and `run`. The `run` command also
supports live output, `--debug`, `--profile`, and `--output` without requiring the
C# side of a parity run. Profile timings cover the end-to-end execution, including
activated workers; plan diagnostics cover statements observed on the launcher session.

The executable first slice includes:

- explicit async/await nodes and async method metadata in compiler IR;
- execution-partitioned durable heap, VM, task, timer, dependency, result, error, and
  output storage;
- concurrency-safe provisioning and successful-run cleanup;
- program-scoped typed heap tables and deterministic worker procedures;
- a validated activated dispatcher and generated root orchestrator;
- execution-scoped durable tasks with program/handler routing, continuation state,
  JSON payloads, typed scalar/text/binary/reference results, and fault details;
- source-ordered execution of each async invocation through its first incomplete
  `Task.Delay`, with pre-await failures captured on the corresponding child task;
- generation-scoped `Task.WhenAll` dependency joins whose final child queues exactly
  one continuation message;
- millisecond `Task.Delay`, task results, fault propagation, and proxied worker output; and
- ordinary `try`/`catch` around resumed code, including `ApplicationException` and
  `SharpSql.DatabaseException` mappings.

The currently accepted fork/join shape is intentionally narrow: one entry-point
`await Task.WhenAll(...)`, tasks materialized from
`source.Select(AsyncMethod).ToList()`, and one `Task.Delay` suspension in each async
method, using the `Task.Delay(int milliseconds)` overload. Parameters and captured
entry scalars/object references are spilled to JSON. Captured objects keep their shared
durable identity, but direct assignment to a captured entry local is rejected until
shared closure cells exist. Other locals declared before an await, nested returns, and
calls requiring the stack-machine fallback are likewise rejected with async-specific
diagnostics rather than producing invalid worker SQL or silently losing state. General
local/closure spilling, control-flow splitting, multiple/nested awaits, async recursion,
cancellation, and abandoned-execution leases remain future work.

Sub-second `Task.Delay` uses the durable timer table because Broker conversation timers
use whole seconds. The generated launcher calls `SharpSql.ClaimDueContinuations` while
it waits. Async prefixes run in source enumeration order, matching C# execution through
the first incomplete await. All due continuations are enqueued in due-time/task order,
but separate Broker readers may execute them concurrently and completion/output order is
intentionally unspecified, like .NET task scheduling. The worker queue currently allows
up to eight active readers across all executions.

Shared `Random` instances use an execution-and-object-scoped transaction lock so their
multi-row PRNG state transitions cannot overlap or lose updates. Concurrent callers
therefore consume one valid sequence, but the task receiving each sample is intentionally
nondeterministic. SQL deadlock victims are rolled back and transparently redelivered by
the worker dispatcher rather than being exposed as failed tasks.
