# Imported Job Connection Repair Fix

This archive contains only replacement files for the manually imported `Content-Location` authentication issue. Extract it into the `FhirBulkExportManager` repository root and overwrite the matching files.

## Root cause

The affected existing job was persisted with the anonymous `public-fhir-r4` plugin/connection. That adapter intentionally does not authenticate, which explains why its history contained only one status request and no `Authorization` header. Changing token-refresh behavior alone cannot change the vendor, site, credential, and plugin identifiers already stored on that job.

## Fixed behavior

- Adds an explicit repair action for an existing externally-started job. It reassigns the selected job to the correct saved vendor/site/credential/plugin without sending `$export`.
- Preserves the exact opaque `Content-Location`, start information, and all existing status/HTTP history.
- Records an `ImportedConnectionReassigned` event, clears the obsolete retry error, and queues an immediate authenticated status check.
- Limits repair to externally-started jobs that have a confirmed `Content-Location` and have not received a manifest. Locally-started exports cannot be reassigned.
- Requires the `Content-Location` and selected site base URL to have the same scheme, host, and port. New imports receive the same validation, preventing this mismatch from recurring.
- Displays the persisted plugin ID and version in the jobs grid so an anonymous assignment is visible.
- Stops an anonymous Public/Open FHIR job on HTTP 401 with `AnonymousConnectionCannotAuthenticate` instead of scheduling an authentication loop that can never succeed.
- Retains the earlier imported-job token protections: first-poll forced authentication, cached-token 401 refresh/retry once, nonempty token validation, and site-assigned credential enforcement.

There is no database schema change and no migration in this patch.

## Apply and build

1. Close the running application.
2. Extract this ZIP into the repository root (the folder containing `FhirBulkExportManager.sln`) and allow replacement of matching files.
3. Run:

```powershell
dotnet build FhirBulkExportManager.sln -c Release --no-restore
dotnet test FhirBulkExportManager.sln -c Release --no-build
```

## Repair the affected job

1. Start the application and select the affected row on **Dashboard & Jobs**.
2. Confirm that its **Plugin** column shows `public-fhir-r4` (the bad persisted assignment).
3. Open **Import Existing Export**.
4. Select the eClinicalWorks vendor and the correct `GBCJCD` site connection. The site-assigned credential is selected automatically.
5. Select **Repair Selected Imported Job with This Connection** and confirm.

No form values need to be re-entered. The action operates on the job selected in **Dashboard & Jobs**. It does not create a new job and does not start a new remote export.

The next poll should preserve three exchanges in its history:

1. HealthWizz assertion request
2. eClinicalWorks OAuth token request
3. Bulk export status request with `Authorization: [REDACTED]`

## Replacement files

- `src/FhirBulkExportManager.Domain/ExportJob.cs`
- `src/FhirBulkExportManager.Application/Services/ExportJobService.cs`
- `src/FhirBulkExportManager.Worker/AuthenticationTokenProvider.cs`
- `src/FhirBulkExportManager.Worker/ExportJobProcessor.cs`
- `src/FhirBulkExportManager.App.Wpf/MainViewModel.cs`
- `src/FhirBulkExportManager.App.Wpf/MainWindow.xaml`
- `plugins/FhirBulkExportManager.Plugin.PublicR4/PublicFhirR4Plugin.cs`
- `tests/FhirBulkExportManager.Domain.Tests/ExportJobStateMachineTests.cs`
- `tests/FhirBulkExportManager.Application.Tests/AuthenticationTokenProviderTests.cs`
- `tests/FhirBulkExportManager.Plugin.PublicR4.Tests/PublicFhirR4PluginTests.cs`
- `docs/IMPORT_EXISTING_EXPORT.md`
