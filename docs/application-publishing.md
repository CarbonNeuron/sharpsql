# Publishing applications to SQL Server

`sharpsql publish` installs a compiled SharpSql application as a versioned,
schema-scoped package in an existing SQL Server database. The default entry point is
`[schema].[Run]`. Publishing the same package again is safe: the installer creates
missing package infrastructure and creates or alters the application procedure.

Publishing is intended for an explicitly configured deployment database. It does not
start an implicit Testcontainer.

## Publish a source file or project

Use a named connection from the same .NET configuration sources supported by
`sharpsql run`:

```bash
sharpsql publish src/BillingJob/BillingJob.csproj \
  --connection Production \
  --schema BillingJobs \
  --name BillingReconciliation \
  --version 2026.07.30
```

Keep the connection string outside source control, for example in an environment
variable:

```bash
export ConnectionStrings__Production='Server=sql.example;Database=Jobs;Encrypt=true;...'
```

For a custom environment-variable name, use `--connection-string-env`. Source files
can be published directly. Projects also accept the same `--entry`, `--configuration`,
and `--framework` selection options as transpilation.

The schema is the application's isolation boundary. Choose a separate schema for each
independently deployed application. Schema, package, version, and entry-procedure
values are validated before any SQL is executed; SQL identifiers are quoted by the
installer rather than interpolated as executable SQL.

## Installed objects and versions

The installer owns these objects within the selected application schema:

- `PackageManifest`, which records the application name, published version, runtime
  mode, entry procedure, and SHA-256 hash of the compiled program SQL.
- `Run` by default, or the procedure selected for the package, which wraps the
  compiled application program.
- Versioned memory-optimized runtime types and native procedures when those features
  are enabled.

Publishing is idempotent and suitable for deployment retries. Existing infrastructure
is retained, the entry procedure is updated with `CREATE OR ALTER`, and the manifest
is updated to describe the installed package. Query it after deployment to confirm
which application version is present:

```sql
SELECT * FROM [BillingJobs].[PackageManifest];
```

Run the installed application with:

```sql
EXEC [BillingJobs].[Run];
```

Treat a schema as owned by one SharpSql application. Publishing a different
application into the same schema is rejected; it is not a side-by-side deployment
mechanism. Changing the entry-procedure name during an upgrade removes the previous
entry procedure after the replacement has been created successfully.

## Uninstall an application

Remove the installed entry procedure, manifest, application-local native kernels,
and memory-optimized runtime types with the same persistent connection configuration:

```bash
sharpsql unpublish \
  --connection Production \
  --schema BillingJobs \
  --name BillingReconciliation
```

The command checks the application identity in `PackageManifest` before removing
anything and serializes against concurrent publishers. It deliberately retains the
schema and any objects it does not recognize as SharpSql-owned. A failed removal is
transactionally rolled back; dependent operator-created objects must be removed
before retrying.

## Memory-optimized and native packages

Enable application-local memory-optimized runtime objects with
`--memory-optimized`:

```bash
sharpsql publish src/BillingJob/BillingJob.csproj \
  --connection Production \
  --schema BillingJobs \
  --name BillingReconciliation \
  --version 2026.07.30 \
  --memory-optimized
```

The target database must already have a `MEMORY_OPTIMIZED_DATA` filegroup and physical
container. SharpSql checks this prerequisite and deliberately does not create the
deployment-specific storage. See the
[memory-optimized runtime guide](memory-optimized-runtime.md) for the required database
provisioning and its lifecycle implications.

Native kernels require the memory-optimized runtime and SQL Server In-Memory OLTP.
Enable both options together:

```bash
sharpsql publish src/BillingJob/BillingJob.csproj \
  --connection Production \
  --schema BillingJobs \
  --name BillingReconciliation \
  --version 2026.07.30 \
  --memory-optimized \
  --native-kernels
```

The deployment principal needs permission to create or alter the application schema's
tables, types, and procedures. The runtime principal needs permission to execute the
installed entry procedure.
