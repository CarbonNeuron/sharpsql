# Register-bytecode fallback

SharpSql can lower eligible managed fallback methods from procedural IR through
compact Core IR into a versioned register program. This is a hybrid backend: LINQ,
heap operations, and other set-oriented work remain native SQL, while bytecode owns
scalar sequencing and control flow.

Select it through the API:

```csharp
new TranspileOptions { ManagedFallback = ManagedFallbackKind.Bytecode }
```

or through the CLI/MSBuild SDK:

```bash
sharpsql transpile App.csproj --managed-fallback Bytecode
```

```xml
<SharpSqlManagedFallback>Bytecode</SharpSqlManagedFallback>
```

`Auto` is the default and uses a conservative SQL-image cost check, `Legacy` pins the label-based VM, and `Bytecode` requires
every selected fallback method to lower successfully. A strict failure produces
`SS8001` with the rejected Core IR operation or runtime type.

## ABI 1.2

The program contract has exactly eight instruction families: `Constant`, `Move`,
`Convert`, `Unary`, `Binary`, `Branch`, `Call`, and `Return`. Validation rejects
unknown registers, invalid branch targets, call arity mismatches, unsupported types,
and return-shape mismatches before SQL emission. Disassembly is deterministic.

The executable slice supports `bool`, `int`, `long`, and nullable `string`; constants,
conversions, arithmetic, comparisons, bit operations and C# shift-count behavior;
locals and parameters; `if`, `while`, conditional values, and returns. Calls from
native SQL into bytecode evaluate arguments once and return a typed scalar. Bytecode
methods can call one another recursively through typed child frames. The first typed
host operation maps `Console.WriteLine` for supported scalar values back to native SQL
output without adding another opcode family.

String registers use a separate `NVARCHAR(MAX)` value lane. ABI 1.2 supports string
literals and `default(string)`, moves, string parameters and results, string-to-string
concatenation, null-safe ordinal equality and inequality, and
`Console.WriteLine(string)`. Concatenation treats null as empty, matching C# string
concatenation. Equality distinguishes null from empty and compares UTF-16 bytes, so it
is case-sensitive and preserves trailing-space differences independently of the
database collation.

Interpolation, coalescing, string members and methods, mixed string/scalar
concatenation, and non-identity string conversions remain outside this compact slice.
Methods requiring heap values, exceptions, async suspension, or general
relational host operations remain on the legacy VM in `Auto`.
Synchronous inline execution with durable rowstore storage installs immutable,
content-addressed program images and keeps execution-partitioned frames and registers
in versioned shared tables. The interpreter and native return labels still execute in
one batch, so this storage is not yet restart-resumable. Service Broker workers can
embed the local interpreter for eligible synchronous helpers, but each call must finish
within its activation; worker bytecode frames are not persisted or resumed. Memory-optimized
bytecode state and bytecode suspension/resumption remain later work. Their staged
architecture is described in
[Durable register-bytecode design](durable-register-bytecode.md).

Service Broker schema version 3 provisions durable activation rows and explicit
program/image links as a foundation for later resumption. Synchronous workers install
and link their canonical image today, but do not create activation rows or resume the
interpreter across worker invocations.
