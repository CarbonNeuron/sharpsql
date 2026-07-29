#!/usr/bin/env bash
set -euo pipefail

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
cleanup() {
    rm -rf -- "$work_dir"
}
trap cleanup EXIT

cp -R "$repo_root/tests/fixtures/SdkConsumer/valid" "$work_dir/valid"
cp -R "$repo_root/tests/fixtures/SdkConsumer/invalid" "$work_dir/invalid"

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

for project in "$work_dir/valid/SdkConsumer.csproj" "$work_dir/invalid/SdkConsumer.csproj"; do
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

dotnet build "$work_dir/valid/SdkConsumer.csproj" \
    --configuration Release \
    --no-restore >/dev/null

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

if invalid_output="$(dotnet build "$work_dir/invalid/SdkConsumer.csproj" --no-restore 2>&1)"; then
    printf 'The analyzer accepted an unsupported multidimensional array.\n' >&2
    exit 1
fi
if [[ "$invalid_output" != *"error SS6301"* ]]; then
    printf '%s\n' "$invalid_output" >&2
    printf 'The analyzer did not report SS6301.\n' >&2
    exit 1
fi

printf 'Validated SharpSql.Sdk %s build generation, SQL execution, IDE profile, and analyzer diagnostics.\n' "$version"
