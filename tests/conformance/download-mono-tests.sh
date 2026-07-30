#!/usr/bin/env bash
set -euo pipefail

script_dir=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
destination="$script_dir/mono-tests"

if find "$destination" -type f -name '*.cs' -print -quit 2>/dev/null | grep -q .; then
  printf 'Mono conformance tests already exist in %s\n' "$destination"
  exit 0
fi

temporary_root=$(mktemp -d "${TMPDIR:-/tmp}/sharpsql-mono-tests.XXXXXX")
trap 'rm -rf "$temporary_root"' EXIT HUP INT TERM

git clone --depth 1 --filter=blob:none --sparse https://github.com/mono/mono.git "$temporary_root/mono"
git -C "$temporary_root/mono" sparse-checkout set mcs/tests

mkdir -p "$destination"
source_root="$temporary_root/mono/mcs/tests"
while IFS= read -r source_file; do
  relative_path=${source_file#"$source_root"/}
  destination_file="$destination/$relative_path"
  mkdir -p "$(dirname -- "$destination_file")"
  cp "$source_file" "$destination_file"
done < <(find "$source_root" -type f -name '*.cs' -print)

printf 'Downloaded %s Mono C# tests to %s\n' \
  "$(find "$destination" -type f -name '*.cs' | wc -l | tr -d ' ')" \
  "$destination"
