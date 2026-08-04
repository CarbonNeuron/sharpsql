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

## ABI 1.1

The program contract has exactly eight instruction families: `Constant`, `Move`,
`Convert`, `Unary`, `Binary`, `Branch`, `Call`, and `Return`. Validation rejects
unknown registers, invalid branch targets, call arity mismatches, unsupported types,
and return-shape mismatches before SQL emission. Disassembly is deterministic.

The executable slice supports `bool`, `int`, and `long`; constants,
conversions, arithmetic, comparisons, bit operations and C# shift-count behavior;
locals and parameters; `if`, `while`, conditional values, and returns. Calls from
native SQL into bytecode evaluate arguments once and return a typed scalar. Bytecode
methods can call one another recursively through typed child frames. The first typed
host operation maps `Console.WriteLine` for supported scalar values back to native SQL
output without adding another opcode family.

Methods requiring heap values, strings, exceptions, async suspension, or general
relational host operations remain on the legacy VM in `Auto`.
Durable frame persistence and Service Broker resumption are also later ABI work;
the current interpreter state is execution-local even when the surrounding heap is
durable.
