# Troubleshooting

## CS1061: DbContextOptionsBuilder has no UseSqlite

`UseSqlite` is supplied by the Entity Framework Core SQLite provider, `Microsoft.EntityFrameworkCore.Sqlite`. `Microsoft.Data.Sqlite.Core` is the lower-level ADO.NET provider and does not add the EF Core `UseSqlite` extension method.

This solution centrally versions `Microsoft.EntityFrameworkCore`, `Microsoft.EntityFrameworkCore.Design`, and `Microsoft.EntityFrameworkCore.Sqlite` at `10.0.11`. The project containing the `UseSqlite` call must contain this versionless reference because Central Package Management supplies its version:

```xml
<PackageReference Include="Microsoft.EntityFrameworkCore.Sqlite" />
```

The source file calling the method must also contain:

```csharp
using Microsoft.EntityFrameworkCore;
```

Both are already present in `FhirBulkExportManager.Persistence.Sqlite`. Do not substitute `Microsoft.Data.Sqlite.Core` for the EF provider package, and do not put the SQLite provider reference only in another project.

## NU1903 for System.Security.Cryptography.Xml 9.0.0

Do not add or update a version in an individual `.csproj`. This repository uses NuGet Central Package Management and transitive pinning; the secure version is declared once in `Directory.Packages.props`. The repository pins `System.Security.Cryptography.Xml` to `10.0.11` for its .NET 10 projects.

After replacing an older source tree, close Visual Studio and restore from the repository root:

```powershell
dotnet nuget locals all --clear
dotnet restore FhirBulkExportManager.sln --force-evaluate
dotnet list FhirBulkExportManager.sln package --include-transitive
dotnet build FhirBulkExportManager.sln -c Release --no-restore
```

The package listing must resolve `System.Security.Cryptography.Xml` to `10.0.11`, not `9.0.0`. Keep `TreatWarningsAsErrors` enabled; do not suppress `NU1903`.

## Application will not start

Review sanitized logs under `%LOCALAPPDATA%\FhirBulkExportManager\Logs`. Startup stops safely for a newer database schema, unavailable local path, migration failure, incompatible plugin, or another running instance. Do not delete `exports.db` to bypass an error; preserve it and the data root for investigation.

## Job is Unknown after kickoff

The connection ended at an ambiguous kickoff boundary or restart found a dispatch journal without a confirmed `Content-Location`. Automatic re-kickoff is intentionally blocked. Ask the vendor to reconcile the correlation/time/site details, then decide whether to archive and create a new job.

## 401 or 403

401 triggers bounded token reacquisition. Persistent 401 usually indicates client credentials or token endpoint configuration. 403 indicates scope/authorization and is not treated as an expired token. Verify the exact vendor-approved scope, audience, tenant, and Group permissions.

## 429 or 5xx

The worker honors `Retry-After` and bounded backoff. Do not repeatedly use manual status while rate-limited. Other vendors/sites remain independent.

## File repeatedly restarts

Safe resume requires a partial file plus ETag or Last-Modified validator and a matching 206 Content-Range. Without proof, restarting is intentional. Check vendor Range/validator support and available disk space.

## Cleanup is blocked

Cleanup requires a committed job, exact status URL, matching manifest hash, all listed files, matching sizes, and fresh SHA-256 verification. Inspect the Detail view before changing or restoring local files. Never send DELETE to a manifest output URL.

## TLS failure

Correct the hostname, trust chain, certificate validity, system clock, proxy, or vendor endpoint. The production application has no ignore-certificate option.
