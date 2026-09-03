# Public FHIR R4 vendor patch

Extract this ZIP over the root of the existing `FhirBulkExportManager` source tree, preserving directories and replacing files when prompted.

## Built-in public test vendors

| Vendor shown in the app | FHIR base URL | Intended use |
| --- | --- | --- |
| HAPI Test Server (R4 FHIR) | `https://hapi.fhir.org/baseR4` | General anonymous FHIR R4 API testing. The public server is regularly purged; Bulk Data availability depends on its current configuration. |
| SMART Health IT R4 Sandbox | `https://r4.smarthealthit.org` | General anonymous FHIR R4 REST API testing. Bulk Data is not guaranteed on this endpoint. |
| SMART Bulk Data Server R4 | `https://bulk-data.smarthealthit.org/fhir` | Anonymous System, Group, and Patient Bulk Data export testing with synthetic data. |

All three entries are backed by the new `public-fhir-r4` plugin. It never adds an `Authorization` header and does not require a client ID, client secret, or token endpoint.

**Never send PHI, confidential data, or production data to these public test servers.** Availability, contents, software versions, capabilities, throttling, and retention policies can change without notice.

## After applying the patch

1. Run `dotnet restore FhirBulkExportManager.sln` because the solution contains two new projects.
2. Run `dotnet build FhirBulkExportManager.sln -c Release --no-restore`.
3. Run `dotnet test FhirBulkExportManager.sln -c Release --no-build`.
4. Start the application once. The bootstrapper adds the three vendors, sites, and anonymous credential profiles to both new and existing databases; no database migration is required.
5. On **Create Export**, select the matching vendor. The UI automatically selects that vendor's site and credential profile.

The HAPI endpoint reported `HAPI FHIR Server - 8.11.16-SNAPSHOT/7e7129efb5/2026-07-08` when this patch was prepared on 2026-09-03. This is informational only because the public server is continuously updated.
