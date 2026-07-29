# SharpSql Conformance Test Suite — Build Spec

Do NOT ask for permission. Do NOT plan. Do NOT summarize. START WRITING FILES IMMEDIATELY. Write the first file within your first action. GO.

## Goal

Add a **Mono compiler conformance test suite** to SharpSql that measures C# language feature parity. This is a NON-BLOCKING CI step — it reports a percentage, it does NOT gate PRs.

## Background

SharpSql is a C# → T-SQL compiler. We want to measure what percentage of C# language features we can handle by running SharpSql against the Mono compiler's test suite (~2,885 test files).

The Mono test suite lives at: `https://github.com/mono/mono/tree/main/mcs/tests`

Test files follow this naming convention:
- `test-*.cs` — core language tests
- `gtest-*.cs` — generics tests  
- `dtest-*.cs` — dynamic tests

Each test is a small self-contained C# program with a `Main()` method that returns 0 on success (non-zero on failure) and may print output.

## What to Build

### 1. Test Corpus Download Script

Create `tests/conformance/download-mono-tests.sh`:
- Clones (sparse checkout) just `mcs/tests/` from `https://github.com/mono/mono.git` into `tests/conformance/mono-tests/`
- Only grabs `.cs` files (not Makefile, dlls, etc.)
- Idempotent — skips if already downloaded
- Add `tests/conformance/mono-tests/` to `.gitignore` (the test files aren't committed, they're downloaded fresh)

### 2. Conformance Test Runner

Create a new test project: `tests/SharpSql.ConformanceTests/`

This project should:
- **Discover** all `.cs` files in the mono-tests directory
- **Categorize** them by prefix (`test-` = Core, `gtest-` = Generics, `dtest-` = Dynamic)
- **For each test file**, attempt to:
  1. Feed the C# source to `SharpSqlCompiler.Transpile()` 
  2. Record the result: compilation success/failure, any diagnostics
  3. Do NOT actually execute against SQL Server — just test whether SharpSql can compile each test without errors
- **Generate a report** with:
  - Per-category counts: `Core: 142/186 (76%)`
  - Overall total: `Total: 248/753 (32%)`
  - List of test files that failed with their error codes/messages (for triaging)
  - Delta tracking: save results to a JSON baseline file so future runs can show `+12 since last run`

The runner should be robust:
- Skip tests that reference assemblies/features SharpSql explicitly doesn't target (unsafe, dynamic, reflection, COM interop, etc.) — mark these as "skipped" not "failed"
- Handle tests that won't parse (syntax errors in very old C# for newer Roslyn) gracefully
- Timeout protection per test (some might have infinite loops in their logic)

### 3. CLI Integration

Add a `sharpsql conformance` command (or similar) to `src/SharpSql.Cli/` that:
- Downloads the test corpus if not present
- Runs the conformance suite
- Prints the summary report to stdout
- Saves detailed results to a JSON file
- Exit code 0 always (non-blocking)
- Support a `--baseline` flag to save current results as the new baseline
- Support a `--parallel` flag (default: use available cores) for parallel test execution

### 4. CI Integration

Modify `.github/workflows/ci.yml` to add a new job (NOT a step in the existing job — a separate job so it can't affect the main build):

```yaml
  conformance:
    runs-on: ubuntu-latest
    needs: build-and-test  # only run after main CI passes
    if: always() && needs.build-and-test.result == 'success'
    steps:
      - checkout
      - setup dotnet
      - restore & build
      - run download script
      - run conformance suite
      - upload results as artifact
      # Future: post summary as PR comment, update badge
```

Key: `continue-on-error: true` is NOT needed because exit code is always 0. The conformance job is informational only.

### 5. Baseline File

Create `tests/conformance/baseline.json` with the initial run results. Format:
```json
{
  "timestamp": "2025-01-15T12:00:00Z",
  "total": { "passed": 248, "failed": 412, "skipped": 93, "total": 753 },
  "categories": {
    "core": { "passed": 142, "failed": 30, "skipped": 14, "total": 186 },
    "generics": { "passed": 0, "failed": 280, "skipped": 60, "total": 340 }
  },
  "failures": {
    "test-042.cs": { "error": "SS1001", "message": "Unsupported: multi-dimensional arrays" }
  }
}
```

## Constraints

- The conformance test project should reference the main `SharpSql` project directly (project reference, not NuGet)
- Use xUnit for the test project structure but the conformance runner itself should be a standalone tool, not individual xUnit test cases per mono test (that would be 2,885 test methods, unmanageable)
- The download script should work on both Linux and macOS (CI is ubuntu, devs might be on mac)
- Do NOT modify any existing test files or the main SharpSql source code
- Do NOT modify the existing CI job — only ADD a new job

## Project Structure Reference

```
sharpsql/
├── src/
│   ├── SharpSql/              # Main compiler
│   ├── SharpSql.Cli/          # CLI tool (sharpsql command)
│   ├── SharpSql.Analyzers/    # Roslyn analyzers
│   ├── SharpSql.Build/        # Build host
│   ├── SharpSql.Ir/           # IR types
│   ├── SharpSql.MSBuild/      # MSBuild tasks
│   ├── SharpSql.Sdk/          # SDK package
│   └── SharpSql.SqlServer/    # SQL Server backend
├── tests/
│   ├── SharpSql.Tests/        # Existing unit tests
│   ├── SharpSql.IntegrationTests/  # Existing integration tests
│   └── conformance/           # NEW — conformance suite
│       ├── mono-tests/        # Downloaded (gitignored)
│       ├── download-mono-tests.sh
│       └── baseline.json
├── .github/workflows/
│   ├── ci.yml                 # Existing — ADD conformance job
│   └── publish.yml            # Existing — DO NOT touch
└── SharpSql.slnx              # Solution file
```

## Verification

After building everything:
1. Run `bash tests/conformance/download-mono-tests.sh` and verify it downloads test files
2. Build the solution: `dotnet build SharpSql.slnx`
3. Run a quick smoke test: compile a few known-simple mono tests through SharpSqlCompiler and verify you get results
4. Verify the CI yaml is valid syntax
5. Print the conformance summary
