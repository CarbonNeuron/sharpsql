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

## ABI 1.0

The program contract has exactly eight instruction families: `Constant`, `Move`,
`Convert`, `Unary`, `Binary`, `Branch`, `Call`, and `Return`. Validation rejects
unknown registers, invalid branch targets, call arity mismatches, unsupported types,
and return-shape mismatches before SQL emission. Disassembly is deterministic.

The first executable slice supports `bool`, `int`, and `long`; constants,
conversions, arithmetic, comparisons, bit operations and C# shift-count behavior;
locals and parameters; `if`, `while`, conditional values, and returns. Calls from
native SQL into bytecode evaluate arguments once and return a typed scalar.

The `Call` family is reserved and validated but nested bytecode calls are not yet
enabled by the SQL ABI. Methods requiring calls, heap values, strings, exceptions,
async suspension, or host/relational operations remain on the legacy VM in `Auto`.
Durable frame persistence and Service Broker resumption are also later ABI work;
the current interpreter state is execution-local even when the surrounding heap is
durable.
