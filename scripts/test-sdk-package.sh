#!/usr/bin/env bash
set -euo pipefail

validation_stage="startup"
report_failure() {
    local exit_code="$?"
    printf 'SharpSql SDK package validation failed during %s at line %s: %s (exit %s).\n' \
        "$validation_stage" "${BASH_LINENO[0]}" "$BASH_COMMAND" "$exit_code" >&2
    exit "$exit_code"
}
stage() {
    validation_stage="$1"
    printf 'SharpSql SDK package validation: %s\n' "$validation_stage"
}
trap report_failure ERR

if [[ "$#" -lt 2 || "$#" -gt 3 ]]; then
    printf 'Usage: %s PACKAGE_DIRECTORY VERSION [SHARPSQL_TOOL]\n' "$0" >&2
    exit 2
fi

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
repo_root="$(cd -- "$script_dir/.." && pwd)"
package_dir="$(cd -- "$1" && pwd)"
version="$2"
tool_path="${3:-}"
if [[ -n "$tool_path" ]]; then
    tool_path="$(realpath "$tool_path")"
fi

if [[ ! "$version" =~ ^[0-9]+\.[0-9]+\.[0-9]+[-+0-9A-Za-z.]*$ ]]; then
    printf 'Invalid semantic version: %s\n' "$version" >&2
    exit 2
fi

work_dir="$(mktemp -d -t sharpsql-sdk-test.XXXXXX)"
export NUGET_PACKAGES="$work_dir/packages"
memory_container_id=""
cleanup() {
    if [[ -n "$memory_container_id" ]]; then
        docker rm -f "$memory_container_id" >/dev/null 2>&1 || true
    fi
    rm -rf -- "$work_dir"
}
trap cleanup EXIT

cp -R "$repo_root/tests/fixtures/SdkConsumer/valid" "$work_dir/valid"
cp -R "$repo_root/tests/fixtures/SdkConsumer/invalid" "$work_dir/invalid"
cp -R "$repo_root/tests/fixtures/SdkConsumer/async" "$work_dir/async"
cp -R "$repo_root/tests/fixtures/SdkConsumer/memory" "$work_dir/memory"

stage "default SDK build output"
dotnet new console --name PackageDefault --output "$work_dir/package-default" --no-restore >/dev/null
dotnet add "$work_dir/package-default/PackageDefault.csproj" package SharpSql.Sdk \
    --version "$version" \
    --source "$package_dir" \
    --no-restore >/dev/null
dotnet restore "$work_dir/package-default/PackageDefault.csproj" --source "$package_dir" >/dev/null
dotnet build "$work_dir/package-default/PackageDefault.csproj" \
    --configuration Release \
    --no-restore >/dev/null
if [[ ! -s "$work_dir/package-default/obj/Release/net10.0/sharpsql/PackageDefault.sql" ]]; then
    printf 'The SDK package default output path was not resolved at build time.\n' >&2
    exit 1
fi

if [[ -n "$tool_path" ]]; then
    stage "init command and IDE profile"
    dotnet new console --name InitDefault --output "$work_dir/default" --no-restore >/dev/null
    "$tool_path" init "$work_dir/default/InitDefault.csproj" \
        --sdk-version "$version" \
        --no-restore >/dev/null
    dotnet restore "$work_dir/default/InitDefault.csproj" --source "$package_dir" >/dev/null
    dotnet build "$work_dir/default/InitDefault.csproj" \
        --configuration Release \
        --no-restore >/dev/null
    if [[ ! -s "$work_dir/default/bin/Release/net10.0/InitDefault.sql" ]]; then
        printf 'The init command did not generate SQL beside the compiled application.\n' >&2
        exit 1
    fi
    if [[ ! -s "$work_dir/default/Properties/launchSettings.json" ]] || \
       ! grep -q 'SharpSqlRun' "$work_dir/default/Properties/launchSettings.json"; then
        printf 'The init command did not add the SQL Server IDE launch profile.\n' >&2
        exit 1
    fi
    if grep -Fq "$work_dir/default" "$work_dir/default/Properties/launchSettings.json" || \
       ! grep -Fq 'InitDefault.csproj' "$work_dir/default/Properties/launchSettings.json" || \
       ! grep -Fq -- '--tl:off' "$work_dir/default/Properties/launchSettings.json" || \
       ! grep -Fq '"workingDirectory": "."' "$work_dir/default/Properties/launchSettings.json"; then
        printf 'The SQL Server IDE launch profile is not project-relative.\n' >&2
        exit 1
    fi
    stage "initialized project SQL execution"
    default_run_output="$(cd "$work_dir/default" && dotnet msbuild "InitDefault.csproj" \
        -t:SharpSqlRun \
        -p:Configuration=Release \
        -p:SharpSqlKeepContainer=false \
        -verbosity:minimal)"
    if [[ "$default_run_output" != *"Hello, World!"* ]] || \
       [[ "$default_run_output" != *"SharpSql: parsing and transpiling"* ]] || \
       [[ "$default_run_output" != *"SharpSql: starting or reusing SQL Server container"* ]] || \
       [[ "$default_run_output" != *"SharpSql: executing SQL batch"* ]] || \
       [[ "$default_run_output" != *"SharpSql: SQL execution completed"* ]]; then
        printf '%s\n' "$default_run_output" >&2
        printf 'The initialized project run target did not report and execute every SQL stage.\n' >&2
        exit 1
    fi
fi

stage "fixture initialization and restore"
for project in \
    "$work_dir/valid/SdkConsumer.csproj" \
    "$work_dir/invalid/SdkConsumer.csproj" \
    "$work_dir/async/SdkConsumer.csproj" \
    "$work_dir/memory/SdkConsumer.csproj"; do
    if [[ -n "$tool_path" ]]; then
        "$tool_path" init "$project" --sdk-version "$version" --no-restore >/dev/null
    else
        dotnet add "$project" package SharpSql.Sdk \
            --version "$version" \
            --source "$package_dir" \
            --no-restore >/dev/null
    fi
    dotnet restore "$project" --source "$package_dir" >/dev/null
done

stage "SDK fixture build and lowering"
dotnet build "$work_dir/valid/SdkConsumer.csproj" \
    --configuration Release \
    --no-restore >/dev/null

dotnet build "$work_dir/async/SdkConsumer.csproj" \
    --configuration Release \
    --no-restore >/dev/null
if [[ ! -s "$work_dir/async/generated.sql" ]] || \
   ! grep -q 'CREATE OR ALTER PROCEDURE \[SharpSql\]\.\[Program_' "$work_dir/async/generated.sql"; then
    printf 'The SDK package did not lower the Service Broker async project.\n' >&2
    exit 1
fi
if invalid_storage_output="$(dotnet build "$work_dir/async/SdkConsumer.csproj" \
    --configuration Release \
    --no-restore \
    -p:SharpSqlRuntimeStorage=SomewhereElse 2>&1)"; then
    printf 'The SDK package accepted an invalid SharpSqlRuntimeStorage value.\n' >&2
    exit 1
fi
if [[ "$invalid_storage_output" != *"SharpSqlRuntimeStorage must be Ephemeral, MemoryOptimized, Durable, or ServiceBroker"* ]]; then
    printf '%s\n' "$invalid_storage_output" >&2
    printf 'The SDK package did not explain the invalid SharpSqlRuntimeStorage value.\n' >&2
    exit 1
fi

dotnet build "$work_dir/memory/SdkConsumer.csproj" \
    --configuration Release \
    --no-restore >/dev/null
memory_sql="$work_dir/memory/generated.sql"
if [[ ! -s "$memory_sql" ]] || \
   ! grep -Fq 'DECLARE @__sharpsql_memory_stack [SharpSql].[MemoryVmStackV1];' "$memory_sql" || \
   ! grep -Fq 'DECLARE @__sharpsql_memory_slots [SharpSql].[MemoryVmSlotsV1];' "$memory_sql"; then
    printf 'The SDK package did not lower the memory-optimized recursive project with memory-optimized VM types.\n' >&2
    exit 1
fi

clr_output="$(dotnet run --project "$work_dir/valid/SdkConsumer.csproj" \
    --configuration Release \
    --no-build \
    --no-restore \
    --no-launch-profile)"
if [[ "$clr_output" != *"answer=42"* ]]; then
    printf '%s\n' "$clr_output" >&2
    printf 'The SDK package did not expose its DatabaseException runtime assembly.\n' >&2
    exit 1
fi

generated_sql="$work_dir/valid/generated.sql"
if [[ ! -s "$generated_sql" ]] || ! grep -q "answer=" "$generated_sql"; then
    printf 'The SDK package did not generate the expected SQL output.\n' >&2
    exit 1
fi

run_output="$(dotnet msbuild "$work_dir/valid/SdkConsumer.csproj" \
    -t:SharpSqlRun \
    -p:Configuration=Release \
    -p:SharpSqlKeepContainer=false \
    -verbosity:minimal)"
if [[ "$run_output" != *"answer=42"* ]] || [[ "$run_output" != *"SharpSql: SQL execution completed"* ]]; then
    printf '%s\n' "$run_output" >&2
    printf 'The SharpSqlRun target did not execute generated SQL in Testcontainers.\n' >&2
    exit 1
fi

stage "memory-optimized container preparation"
memory_database="SharpSqlMemorySdk${RANDOM}${RANDOM}"
memory_prime_output="$(dotnet msbuild "$work_dir/memory/SdkConsumer.csproj" \
    -t:SharpSqlRun \
    -p:Configuration=Release \
    -p:SharpSqlRuntimeStorage=Ephemeral \
    -p:SharpSqlForceContainer=true \
    -p:SharpSqlKeepContainer=true \
    -p:SharpSqlContainerDatabase="$memory_database" \
    -verbosity:minimal)"
if [[ "$memory_prime_output" != *"memory-answer=42"* ]] || \
   [[ "$memory_prime_output" != *"SharpSql: SQL execution completed"* ]]; then
    printf '%s\n' "$memory_prime_output" >&2
    printf 'The SDK package could not prepare the SQL Server container for memory-optimized execution.\n' >&2
    exit 1
fi

mapfile -t memory_container_ids < <(
    docker ps \
        --filter "label=io.sharpsql.sqlserver.database=$memory_database" \
        --format '{{.ID}}'
)
if [[ "${#memory_container_ids[@]}" -ne 1 ]]; then
    printf 'Expected one retained SQL Server container for database %s, found %s.\n' \
        "$memory_database" "${#memory_container_ids[@]}" >&2
    exit 1
fi
memory_container_id="${memory_container_ids[0]}"
memory_filegroup_sql="
ALTER DATABASE [$memory_database]
    ADD FILEGROUP [SharpSqlMemoryOptimized] CONTAINS MEMORY_OPTIMIZED_DATA;
ALTER DATABASE [$memory_database]
    ADD FILE
    (
        NAME = N'SharpSqlMemoryOptimized',
        FILENAME = N'/var/opt/mssql/data/${memory_database}_xtp'
    )
    TO FILEGROUP [SharpSqlMemoryOptimized];
"
docker exec "$memory_container_id" /bin/bash -c '
    if [[ -x /opt/mssql-tools18/bin/sqlcmd ]]; then
        sqlcmd_path=/opt/mssql-tools18/bin/sqlcmd
    else
        sqlcmd_path=/opt/mssql-tools/bin/sqlcmd
    fi
    "$sqlcmd_path" -C -S localhost -U sa -P "$MSSQL_SA_PASSWORD" -b -Q "$1"
' _ "$memory_filegroup_sql" >/dev/null

stage "memory-optimized provisioning and execution"
memory_run_output="$(dotnet msbuild "$work_dir/memory/SdkConsumer.csproj" \
    -t:SharpSqlRun \
    -p:Configuration=Release \
    -p:SharpSqlForceContainer=true \
    -p:SharpSqlKeepContainer=false \
    -p:SharpSqlContainerDatabase="$memory_database" \
    -verbosity:minimal)"
if [[ "$memory_run_output" != *"memory-answer=42"* ]] || \
   [[ "$memory_run_output" != *"SharpSql: provisioning memory-optimized runtime"* ]] || \
   [[ "$memory_run_output" != *"SharpSql: memory-optimized runtime ready"* ]] || \
   [[ "$memory_run_output" != *"SharpSql: SQL execution completed"* ]]; then
    printf '%s\n' "$memory_run_output" >&2
    printf 'The SharpSqlRun target did not provision and execute the memory-optimized project.\n' >&2
    exit 1
fi
memory_container_id=""

stage "Service Broker provisioning and execution"
async_run_output="$(dotnet msbuild "$work_dir/async/SdkConsumer.csproj" \
    -t:SharpSqlRun \
    -p:Configuration=Release \
    -p:SharpSqlKeepContainer=false \
    -verbosity:minimal)"
if [[ "$async_run_output" != *"done"* ]] || \
   [[ "$async_run_output" != *"SharpSql: provisioning Service Broker runtime"* ]] || \
   [[ "$async_run_output" != *"SharpSql: Service Broker runtime ready"* ]] || \
   [[ "$async_run_output" != *"SharpSql: SQL execution completed"* ]]; then
    printf '%s\n' "$async_run_output" >&2
    printf 'The SharpSqlRun target did not provision and execute the Service Broker async project.\n' >&2
    exit 1
fi

stage "analyzer diagnostics"
if invalid_output="$(dotnet build "$work_dir/invalid/SdkConsumer.csproj" --no-restore 2>&1)"; then
    printf 'The analyzer accepted an unsupported multidimensional array.\n' >&2
    exit 1
fi
if [[ "$invalid_output" != *"error SS6301"* ]]; then
    printf '%s\n' "$invalid_output" >&2
    printf 'The analyzer did not report SS6301.\n' >&2
    exit 1
fi

printf 'Validated SharpSql.Sdk %s build generation, memory-optimized and Service Broker lowering/execution, IDE profile, and analyzer diagnostics.\n' "$version"
