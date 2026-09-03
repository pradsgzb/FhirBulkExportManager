# Job history master-detail patch

This archive contains only the files changed for the Dashboard & Jobs master-detail view and durable HTTP exchange history. It is not the complete source repository.

## Apply

1. Close the application and Visual Studio.
2. Extract this archive into the root of your existing `FhirBulkExportManager` repository.
3. Keep the folders exactly as shown and approve **Replace files in the destination**.
4. Open a terminal in the repository root and run:

```powershell
dotnet restore FhirBulkExportManager.sln
dotnet build FhirBulkExportManager.sln -c Release --no-restore
dotnet test FhirBulkExportManager.sln -c Release --no-build
```

No database migration is required. HTTP history is stored in the existing `ExportJobEvents.MetadataJson` column.

## What changes

- Selecting a job opens an in-place detail pane under the job list.
- The detail pane shows every job status event, newest first.
- Selecting a status event exposes its preserved HTTP exchanges.
- Selecting an exchange shows request headers, response headers, request body, and response body.
- The latest non-empty `X-Progress` value is retained verbatim for the dashboard, including vendor-specific text such as percentages or patient-search counts.
- Authentication, kickoff, polling, cancellation, cleanup, redirects, and downloads are captured for new job activity.
- Download bodies remain the managed `.ndjson` files; the event records their storage reference instead of duplicating large files in SQLite.

## Security and compatibility

Reusable credentials are deliberately not persisted. Header names and body structure are retained, while bearer tokens, cookies, OAuth assertions, access/refresh tokens, client secrets, API keys, and passwords appear as `[REDACTED]`.

Existing timeline entries remain visible. Requests made before this patch cannot be backfilled with headers or bodies that were never stored.

The source environment used to prepare this patch did not contain the .NET SDK, so the authoritative Release build and test commands above must be run on your Windows machine.
