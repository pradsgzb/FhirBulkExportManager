# Enterprise FHIR Bulk Export Manager 1.0.0

A Windows desktop application for operating durable, asynchronous HL7 FHIR Bulk Data exports across vendors and sites. The first adapter targets eClinicalWorks; core workflow, persistence, storage, scheduling, and UI code remain vendor-neutral.

This repository contains source code—not an empty solution. `FhirBulkExportManager.sln` references 21 populated projects: WPF, Domain, Application, Infrastructure, SQLite persistence, plugin contracts/host, worker engine, eClinicalWorks, anonymous public FHIR R4 support, mock server, and nine test suites.

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
- An eClinicalWorks/HealthWizz integration account with its client ID, endpoints, and approved system scopes

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
2. Enter the site FHIR base URL, client ID, eCW token endpoint, HealthWizz assertion endpoint, and scope. Leave the client secret empty for the HealthWizz flow.
3. Leave Authentication mapping JSON as `{}` for the standard HealthWizz request shown below. Advanced field overrides remain available for integrations that use a different schema.
4. Save the profile. A client secret is only required for direct OAuth without HealthWizz; if supplied, it is protected with Windows DPAPI for the current Windows user.
5. Select the saved site and run **Test Connection**. This requests `/metadata` anonymously and never invokes authentication or `$export`.
6. Open **Add New Export Job**, save a Draft, review it, and explicitly start it from **Dashboard & Jobs**.

## eClinicalWorks / HealthWizz authentication

When a HealthWizz assertion endpoint is configured, the adapter posts JSON. If Audience is blank, `aud` defaults to the configured eCW token endpoint:

```json
{
  "clientId": "<configured client ID>",
  "aud": "<configured eCW token endpoint>"
}
```

The response is read as a raw JWT. The adapter then exchanges it at the eCW token endpoint with the form fields `grant_type`, `client_assertion_type`, `client_assertion`, and `scope`. It does not send `client_id` or `client_secret` in this assertion exchange.

Authentication mapping JSON is optional and should normally remain `{}`. For an integration with a different assertion request field schema, overrides may use these templates: `${clientId}`, `${clientSecret}`, `${scope}`, and `${audience}`. JSON-wrapped assertion responses are also supported through `assertionResponseProperty`.

Example advanced override:

```json
{
  "grantType": "client_credentials",
  "clientAssertionType": "urn:ietf:params:oauth:client-assertion-type:jwt-bearer",
  "assertionResponseProperty": "client_assertion",
  "assertionRequestFields": {
    "clientId": "${clientId}",
    "aud": "${audience}"
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
  FhirBulkExportManager.Plugin.PublicR4/  anonymous public/test R4 servers
tests/                                    nine automated test projects
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
