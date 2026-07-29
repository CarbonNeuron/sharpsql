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

if invalid_output="$(dotnet build "$work_dir/invalid/SdkConsumer.csproj" --no-restore 2>&1)"; then
    printf 'The analyzer accepted an unsupported multidimensional array.\n' >&2
    exit 1
fi
if [[ "$invalid_output" != *"error SS6301"* ]]; then
    printf '%s\n' "$invalid_output" >&2
    printf 'The analyzer did not report SS6301.\n' >&2
    exit 1
fi

printf 'Validated SharpSql.Sdk %s build generation and analyzer diagnostics.\n' "$version"
