# Durable register-bytecode design

## Status and scope

This document defines the incremental persistence model for the compact register-bytecode runtime. The bounded durable-rowstore inline slice described below is implemented. Other configurations still store the program image, call metadata, frames, and registers in connection-local temporary tables, and cross-session resumption remains future work.

The first implementation slice is deliberately limited to synchronous, inline execution with ordinary durable rowstore tables. It makes program images reusable and frame state execution-scoped without changing bytecode semantics or promising restart resumption.

The first slice does not include:

- Service Broker workers or suspension across sessions;
- memory-optimized bytecode frames;
- new bytecode types, opcodes, host operations, or exception handling;
- recovery or continuation of an abandoned inline batch;
- automatic program-image retention cleanup; or
- replacement of the legacy label VM.

## Identity model

Durable bytecode needs identities for distinct lifetimes. They must not be conflated:

- `ExecutionId UNIQUEIDENTIFIER` identifies one logical run. It partitions all mutable state and bounds cleanup. Durable inline execution can reuse the execution ID already created by the shared runtime.
- `BytecodeImageId BINARY(32)` identifies one immutable register-bytecode module. It is the full SHA-256 digest of the canonical image described below.
- `TaskId BIGINT` is reserved for a later Service Broker activation or resumable fiber. The first slice does not use it.
- Service Broker's existing `ProgramId NVARCHAR(32)` continues to identify and route a generated worker procedure. It is not a bytecode image ID. A later worker program may reference a bytecode image through an explicit link.

Every mutable frame and register lookup must include `ExecutionId`. Every instruction, parameter, and argument lookup must include `BytecodeImageId`. A frame also records its image ID so it can never be interpreted against a different installed module.

## Canonical program image

The compiler validates a `RegisterBytecodeModule` before hashing or emission. It then serializes the executable contract deterministically, including:

- ABI major and minor version;
- methods ordered by numeric bytecode method ID;
- instructions ordered by method ID and program counter;
- opcode and every populated instruction operand;
- parameters ordered by method ID and parameter index;
- call arguments ordered by method ID, program counter, and argument index; and
- runtime type codes wherever they affect interpretation.

The serialization excludes source names, comments, timestamps, SQL formatting, and compiler-local labels. Integers use a fixed-width or otherwise unambiguous binary representation; null fields have an explicit encoding. SHA-256 over those bytes produces `BytecodeImageId`.

The compact-row logic in `SharpSqlCompiler.RegisterBytecode.cs` is shared by temporary and durable storage, while `RegisterBytecodeImage` canonicalizes the same validated executable contract for hashing. This prevents local and durable encodings from drifting. Changing any executable row or ABI version changes the image ID.

## Proposed rowstore schema

Names are illustrative but the version boundary is intentional.

`[SharpSql].[BytecodeImages]` catalogs immutable modules:

```text
ImageId             BINARY(32)    primary key
AbiMajor            SMALLINT      not null
AbiMinor            SMALLINT      not null
InstructionCount    INT           not null
ArgumentCount       INT           not null
ParameterCount      INT           not null
InstalledAtUtc      DATETIME2(7)  not null
LastUsedAtUtc       DATETIME2(7)  not null
```

`[SharpSql].[BytecodeInstructionsV1]` mirrors the current compact program table, prefixed by `ImageId`:

```text
ImageId, MethodId, Pc, Opcode,
Destination, Type, OperandA, OperandB, Operation,
Target, FalseTarget, Constant, ConstantText
primary key (ImageId, MethodId, Pc)
```

`[SharpSql].[BytecodeArgumentsV1]` stores:

```text
ImageId, MethodId, Pc, ArgumentIndex, RegisterId, Type
primary key (ImageId, MethodId, Pc, ArgumentIndex)
```

`[SharpSql].[BytecodeParametersV1]` stores:

```text
ImageId, MethodId, ParameterIndex, RegisterId, Type
primary key (ImageId, MethodId, ParameterIndex)
```

Mutable execution state uses separate versioned tables:

`[SharpSql].[BytecodeFramesV1]` stores:

```text
ExecutionId, FrameId, ImageId, MethodId, Pc,
CallerFrameId, ResultDestination, ReturnId
primary key (ExecutionId, FrameId)
```

`FrameId` is a globally allocated `BIGINT`. `ReturnId` preserves the first slice's native batch return path; it is not a cross-session continuation contract.

`[SharpSql].[BytecodeRegistersV1]` stores:

```text
ExecutionId, FrameId, RegisterId, Type, Value, TextValue
primary key (ExecutionId, FrameId, RegisterId)
```

ABI 1.2 uses nullable `BIGINT` and `NVARCHAR(MAX)` lanes selected by the type tag.
Broader runtime values require a later schema/ABI decision rather than silently
changing this representation.

Program images are installed atomically under a transaction-owned application lock such as `SharpSql.Bytecode.Image.<hash>`. If an image already exists, installation verifies its ABI and recorded row counts and reuses it. Instruction, argument, and parameter rows are immutable and are never updated in place.

## Bounded first implementation slice

The first slice applies only when all of the following are true:

```text
Execution = Inline
Durability = Durable
UseMemoryOptimizedTables = false
```

It consists of:

1. Extracting deterministic image encoding and hashing from the current temporary-table emitter.
2. Provisioning the rowstore catalog, image, frame, and register tables under the existing `SharpSql.Runtime.Provisioning` lock.
3. Installing or reusing the immutable image before execution.
4. Making the existing batch-local interpreter select image rows by `BytecodeImageId` and mutable rows by `ExecutionId`.
5. Replacing hard-coded temporary frame/register names with a storage abstraction.
6. Deleting this execution's registers and frames on normal completion, top-level return, and error cleanup.
7. Retaining installed program images for reuse.

The interpreter, its generated labels, and native-to-bytecode return dispatch remain in the generated batch. This slice therefore proves durable representation, identity, isolation, and cleanup, but not the ability to resume execution after the batch or connection disappears. Ephemeral configurations retain the current temporary tables unchanged.

## Cleanup and concurrency

Frames and registers are owned by one execution. Cleanup deletes registers before frames using `ExecutionId`; it runs through the same success, early-return, and catch paths used by durable legacy VM and heap cleanup. A new GUID prevents a later execution from observing orphaned state from an abandoned inline batch.

The first slice intentionally retains immutable images and provides no image garbage collector. Later retention cleanup must use `LastUsedAtUtc`, exclude images referenced by live frames or activations, and coordinate with installation through the same per-image application lock.

Concurrent executions may share an image but never mutable rows. All frame, caller-frame, register, and cleanup predicates include `ExecutionId`. Image installation occurs in one transaction, so readers observe either the complete image or no image. Existing-image validation fails rather than repairing or overwriting inconsistent rows.

Durable inline execution does not have a lease or durable scheduler. If its process disappears, its rows may remain until administrative cleanup, matching the current durable legacy-runtime limitation. Service Broker integration later uses its execution leases and reaper to provide a stronger lifecycle.

## Versioning

Bytecode ABI version and database schema version are independent:

- each image records the ABI version it requires;
- the interpreter rejects unsupported ABI versions before dispatch;
- a `RegisterBytecode` entry in the shared runtime manifest tracks the database schema version; and
- incompatible physical changes create new versioned tables and an explicit migration rather than redefining `V1` columns.

Provisioning uses the existing global runtime lock. The Service Broker schema version need not change for the standalone first slice. A later migration that adds task activation state or worker/image links must increment `ExecutionInfrastructureSqlEmitter.CurrentSchemaVersion` and retain the existing forward/backward-version checks.

## Later increments

### Memory-optimized storage

Add execution-partitioned physical tables through `MemoryOptimizedRuntimeSqlEmitter` and `RuntimeTableSqlEmitter`:

```text
__sharpsql_memory_bytecode_frames_ephemeral_v1
__sharpsql_memory_bytecode_registers_ephemeral_v1
__sharpsql_memory_bytecode_frames_durable_v1
__sharpsql_memory_bytecode_registers_durable_v1
```

Ephemeral tables use `SCHEMA_ONLY`; durable tables use `SCHEMA_AND_DATA`. Their hash keys begin with `ExecutionId`. Program images remain ordinary rowstore because they are immutable, shared, read-mostly metadata and should not be duplicated per physical profile.

### Service Broker without suspension

The next worker increment permits synchronous register-bytecode calls that always run to completion. The interpreter must be emitted into the worker procedure, or moved into a shared slice runner; making storage global alone is insufficient. Worker output maps `WriteLine` to `AppendOutput` rather than `PRINT`. Legacy VM calls may remain rejected while this narrower path removes `SS7005` for eligible register bytecode.

### Suspension and resumption

Add `[SharpSql].[BytecodeActivations]`, keyed by `(ExecutionId, TaskId)`, to store the image and current frame for a resumable worker activation. Add an explicit link from retained Service Broker worker programs to their bytecode images.

The existing Broker transaction supplies the required boundary: message receive, task claim, bytecode execution, PC/register updates, dependency or timer registration, and transition back to waiting commit together. Worker failure rolls the transaction back and redelivers the message. `SuspensionGeneration` prevents stale or duplicate dependency completion from resuming the wrong instruction.

Async lowering then needs explicit bytecode/Core IR host operations. An incomplete await records a deterministic resume PC and activation state before returning from the worker. Resumption loads the activation by `(ExecutionId, TaskId)` and continues against the exact `BytecodeImageId`. Native SQL labels are never persisted as resumable continuations.

Cancellation and abandoned-execution reaping must delete activation, register, and frame state by `ExecutionId`. Worker-program cleanup removes its image links under the existing per-program lock; separate image retention cleanup removes only old, unreferenced images. Multiple awaits, exception unwinding, `finally`, cancellation tokens, broader value types, heap operations, and async recursion remain subsequent ABI work.
