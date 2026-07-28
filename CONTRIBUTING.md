# Contributing to SharpSql

SharpSql is an experimental compiler, and focused contributions are welcome. Bug reports are most useful when they include the smallest C# input that reproduces the problem, the generated SQL or diagnostic, and the SQL Server version involved.

## Development setup

You need the .NET 10 SDK. SQL Server is optional for most changes; the compiler test suite does not require a database.

```bash
dotnet restore SharpSql.slnx
dotnet build SharpSql.slnx --configuration Release --no-restore
dotnet test SharpSql.slnx --configuration Release --no-build
```

Run an example through the CLI with:

```bash
dotnet run --project src/SharpSql.Cli -- examples/inlining.cs
```

Before opening a pull request, run `dotnet format SharpSql.slnx --verify-no-changes` and the Release test suite.

## Adding language support

- Prefer semantic information from Roslyn over matching source text.
- Emit a source-positioned diagnostic for unsupported constructs; never silently produce SQL with doubtful semantics.
- Keep generated names collision-safe and all runtime state session-scoped.
- Add tests for normal lowering, nested control flow, and interactions with inline and VM-backed methods where applicable.
- Document intentional differences between C# and SQL Server behavior.

For compiler internals and the generated calling convention, see [docs/architecture.md](docs/architecture.md).
