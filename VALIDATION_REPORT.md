# Validation report

## Source-package checks performed

- The source tree contains 150 files, including 78 C# implementation/test files and four WPF XAML files.
- The solution contains 19 project entries.
- Every solution project path exists.
- Runtime projects contain C# and/or XAML implementation files.
- Test projects contain executable xUnit tests.
- Project-reference paths and XML/JSON documents are checked by the packaging audit.
- The initial migration `Up` and `Down` paths were executed against an isolated SQLite database: 13 tables, 20 indexes, schema version 1, and no foreign-key violations; rollback removed every application table.
- Production C# is scanned for `TODO`, `NotImplementedException`, `ReadAsStringAsync`, and certificate-validation bypass callbacks.
- Production C# is scanned for interpolated `ExecuteSqlRaw`/`ExecuteSqlRawAsync` calls. SQLite busy/locked retry behavior uses the bounded per-connection `DefaultTimeout` setting instead of dynamically constructed PRAGMA SQL.
- The EF Core runtime, design tooling, and SQLite provider are aligned at `10.0.11`; repository verification confirms that the persistence project directly references `Microsoft.EntityFrameworkCore.Sqlite`, which supplies `UseSqlite`.
- The EF migration snapshot is a frozen SQLite-specific model at `10.0.11`, is shared with the initial migration target, and is checked against accidental calls back into the live `DbContext` model. The raw initial schema also contains all convention-required foreign-key indexes.
- `System.Security.Cryptography.Xml` is centrally transitively pinned to secure .NET 10 servicing version `10.0.11`; repository verification rejects versions below the `10.0.10` patched floor for GHSA-23rf-6693-g89p.
- The mixed WPF/Windows Forms shell fully qualifies WPF `MessageBox` calls, configures `PerMonitorV2` through `ApplicationHighDpiMode`, and keeps legacy DPI declarations out of `app.manifest` to satisfy WFO0003.
- Nullable `_since` input is parsed into a non-nullable temporary before assignment, avoiding the invalid nullable `out` argument while preserving optional input semantics.
- The source ZIP is created only after archive listing, size, and checksum verification.

## Windows execution evidence

- A user-supplied Windows .NET SDK 10.0.11 run confirmed that all 19 solution projects compiled in Release configuration before the latest test/snapshot corrections.
- The subsequent user-supplied test run executed 24 tests: 22 passed and two exposed actionable defects. The eClinicalWorks failure was an invalid test fixture that attempted to place `Content-Location` in the general response-header collection; the fixture now uses the HTTP content-header collection and asserts header insertion. The persistence failure was caused by a non-provider-specific migration snapshot; it has been replaced with the frozen SQLite model described above.
- The two corrected tests still require a fresh Windows rerun; this report does not claim that unexecuted result as passed.

## Source-generation environment limitation

The source-generation environment did not contain the .NET SDK and could not perform an authoritative `dotnet restore`, Release compilation, WPF XAML compilation, or xUnit execution. Those gates are encoded in `scripts/build.ps1` and `.github/workflows/ci.yml` for a Windows .NET 10 environment.

Until that run succeeds and the organization completes vendor/security/operational acceptance, use this as a full engineering source baseline, not a signed production release.
