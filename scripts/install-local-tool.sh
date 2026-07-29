#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
repo_root="$(cd -- "$script_dir/.." && pwd)"
project="$repo_root/src/SharpSql.Cli/SharpSql.Cli.csproj"
base_version="$(dotnet msbuild "$project" -nologo -getProperty:VersionPrefix)"
timestamp="$(date -u +%Y%m%d%H%M%S)"
version="${1:-$base_version-local.$timestamp.$BASHPID}"

if [[ ! "$version" =~ ^[0-9]+\.[0-9]+\.[0-9]+[-+0-9A-Za-z.]*$ ]]; then
    printf 'Invalid semantic version: %s\n' "$version" >&2
    exit 2
fi

package_dir="$(mktemp -d -t sharpsql-tool.XXXXXX)"
validation_dir="$package_dir/validation"

cleanup() {
    rm -rf -- "$package_dir"
}
trap cleanup EXIT

printf 'Packing SharpSql.Tool %s...\n' "$version"
dotnet pack "$project" \
    --configuration Release \
    -p:Version="$version" \
    -p:PackageVersion="$version" \
    --output "$package_dir"

printf 'Validating package...\n'
dotnet tool install SharpSql.Tool \
    --tool-path "$validation_dir" \
    --source "$package_dir" \
    --version "$version" \
    --no-http-cache >/dev/null
"$validation_dir/sharpsql" --version >/dev/null

if dotnet tool list --global | awk 'tolower($1) == "sharpsql.tool" { found = 1 } END { exit !found }'; then
    dotnet tool uninstall SharpSql.Tool --global >/dev/null
fi

dotnet tool install SharpSql.Tool \
    --global \
    --source "$package_dir" \
    --version "$version" \
    --no-http-cache >/dev/null

printf 'Installed SharpSql.Tool %s globally.\n' "$(sharpsql --version)"
