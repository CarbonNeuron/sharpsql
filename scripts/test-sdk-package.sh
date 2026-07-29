#!/usr/bin/env bash
set -euo pipefail

if [[ "$#" -ne 2 ]]; then
    printf 'Usage: %s PACKAGE_DIRECTORY VERSION\n' "$0" >&2
    exit 2
fi

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
repo_root="$(cd -- "$script_dir/.." && pwd)"
package_dir="$(cd -- "$1" && pwd)"
version="$2"

if [[ ! "$version" =~ ^[0-9]+\.[0-9]+\.[0-9]+[-+0-9A-Za-z.]*$ ]]; then
    printf 'Invalid semantic version: %s\n' "$version" >&2
    exit 2
fi

work_dir="$(mktemp -d -t sharpsql-sdk-test.XXXXXX)"
cleanup() {
    rm -rf -- "$work_dir"
}
trap cleanup EXIT

cp -R "$repo_root/tests/fixtures/SdkConsumer/valid" "$work_dir/valid"
cp -R "$repo_root/tests/fixtures/SdkConsumer/invalid" "$work_dir/invalid"

for project in "$work_dir/valid/SdkConsumer.csproj" "$work_dir/invalid/SdkConsumer.csproj"; do
    dotnet add "$project" package SharpSql.Sdk \
        --version "$version" \
        --source "$package_dir" \
        --no-restore >/dev/null
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
