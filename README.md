# Enterprise FHIR Bulk Export Manager 1.0.0

A Windows desktop application for operating durable, asynchronous HL7 FHIR Bulk Data exports across vendors and sites. The first adapter targets eClinicalWorks; core workflow, persistence, storage, scheduling, and UI code remain vendor-neutral.

This repository contains source code—not an empty solution. `FhirBulkExportManager.sln` references 19 populated projects: WPF, Domain, Application, Infrastructure, SQLite persistence, plugin contracts/host, worker engine, eClinicalWorks, mock server, and eight test suites.

## Safety model

The application treats an export as a durable workflow:

1. Creating a job saves `Draft` only.
2. An operator explicitly selects **Start Export**.
3. Kickoff intent is committed before network dispatch.
4. The exact `Content-Location` header is stored as an opaque value.
5. Polling honors seconds or HTTP-date `Retry-After` values.
6. Every manifest item is represented separately, including duplicate resource types.
7. NDJSON is streamed to `.partial` files, hashed, and validated line-by-line.
8. `local-commit.json` is atomically created only after all required files validate.
9. Automatic remote cleanup is impossible unless the local commit is revalidated.
10. Cleanup is verified independently with a subsequent status request.

No production setting disables TLS certificate validation. Bearer tokens, assertions, secrets, and FHIR resources are excluded from normal logs.

## Prerequisites

- Windows 10 22H2, Windows 11, or Windows Server with Desktop Experience
- Visual Studio 2022 with .NET desktop development, or .NET 10 SDK
- Internet access to the configured vendor endpoints and NuGet for the first restore
- An eClinicalWorks/HealthWizz integration account and vendor-documented endpoint/field mappings

## Build and test

From PowerShell:

```powershell
Set-ExecutionPolicy -Scope Process Bypass
./scripts/build.ps1 -Configuration Release
```

Equivalent commands:

```powershell
dotnet restore FhirBulkExportManager.sln
dotnet build FhirBulkExportManager.sln -c Release --no-restore
dotnet test FhirBulkExportManager.sln -c Release --no-build
```

Run the desktop application:

```powershell
dotnet run --project src/FhirBulkExportManager.App.Wpf/FhirBulkExportManager.App.Wpf.csproj
```

Run the deterministic test server separately:

```powershell
dotnet run --project tools/MockBulkFhirServer/MockBulkFhirServer.csproj
```

It listens on `http://127.0.0.1:5198` by default. Loopback HTTP is accepted only by protocol/test internals; the production connection UI requires HTTPS.

## First launch

The application initializes and validates SQLite, creates an eClinicalWorks vendor entry, loads trusted plugins, clears stale leases, and opens the Connections page. It never creates or starts an export during onboarding.

1. Open **Connections**.
2. Enter the site FHIR base URL, client ID, secret, token endpoint, and optional HealthWizz assertion endpoint.
3. When an assertion endpoint is used, enter the vendor-documented field mapping JSON described below.
4. Save the profile. The secret is protected with Windows DPAPI for the current Windows user.
5. Select the saved site and run **Test Connection**. This authenticates and requests `/metadata`; it never invokes `$export`.
6. Open **Add New Export Job**, save a Draft, review it, and explicitly start it from **Dashboard & Jobs**.

## eClinicalWorks assertion mapping

The proprietary assertion request schema is deliberately configuration-driven. Do not guess field names. Use the mapping supplied for the specific eClinicalWorks/HealthWizz integration. Templates supported in values are `${clientId}`, `${clientSecret}`, `${scope}`, and `${audience}`.

Example structure (field names are illustrative and must be replaced with documented names):

```json
{
  "grantType": "client_credentials",
  "clientAssertionType": "urn:ietf:params:oauth:client-assertion-type:jwt-bearer",
  "assertionResponseProperty": "client_assertion",
  "assertionRequestFields": {
    "documented_client_field": "${clientId}",
    "documented_secret_field": "${clientSecret}",
    "documented_audience_field": "${audience}"
  },
  "tokenAdditionalFields": {}
}
```

If no assertion endpoint is configured, the adapter uses the standard OAuth 2.0 client-credentials fields.

## Durable data locations

Defaults are under `%LOCALAPPDATA%\FhirBulkExportManager`:

- `State\exports.db` — SQLite control-plane state
- `State\Backups` — pre-migration database backups
- `Data` — manifests, NDJSON, checksums, and local commits
- `Secrets` — DPAPI-protected secret blobs
- `Logs` — sanitized rolling operational logs

Export files are never stored as SQLite BLOBs. Changing/uninstalling application binaries does not intentionally remove state or exported data.

## Repository layout

```text
src/
  FhirBulkExportManager.App.Wpf/          desktop control plane
  FhirBulkExportManager.Domain/           entities and state machine
  FhirBulkExportManager.Application/      use cases and ports
  FhirBulkExportManager.Infrastructure/   storage, HTTP, security, validation
  FhirBulkExportManager.Persistence.Sqlite/
  FhirBulkExportManager.PluginContracts/
  FhirBulkExportManager.PluginHost/
  FhirBulkExportManager.Worker/           scheduler and processors
plugins/
  FhirBulkExportManager.Plugin.EClinicalWorks/
tests/                                    eight automated test projects
tools/MockBulkFhirServer/                 deterministic Bulk Data server
installer/                                MSIX source and build script
docs/                                     architecture and operations
.github/workflows/                        CI, CodeQL, and release
```

## Configuration limits

- Effective export workers: `min(MaxConcurrentExports, Environment.ProcessorCount)`
- Per-site file downloads: `min(configured limit, plugin limit, 4)`
- Default per-site download limit: 4
- Default free-disk reserve: 10 GB
- SQLite: local disk only, WAL, busy timeout, short transactions

## Release qualification

The source archive is not a signed deployment artifact. A release must pass Windows restore/build/test, dependency audit, CodeQL, integration tests, MSIX creation, checksum generation, and organizational acceptance testing. See `VALIDATION_REPORT.md` and `docs/DEPLOYMENT.md`.

## Documentation index

- `docs/ARCHITECTURE.md`
- `docs/DATABASE.md`
- `docs/EXPORT_LIFECYCLE.md`
- `docs/CONCURRENCY.md`
- `docs/SECURITY.md`
- `docs/PLUGIN_DEVELOPMENT.md`
- `docs/ECW_PLUGIN.md`
- `docs/RECOVERY.md`
- `docs/DEPLOYMENT.md`
- `docs/CI_CD.md`
- `docs/TESTING.md`
- `docs/TROUBLESHOOTING.md`
- `docs/REQUIREMENTS_TRACEABILITY.md`

