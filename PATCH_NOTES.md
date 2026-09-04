# Download recovery fixes

This archive contains only the changed and new files for the interrupted-download fix. It is not a complete source tree.

## Apply the fix

1. Close **Enterprise FHIR Bulk Export Manager** and Visual Studio.
2. Extract this ZIP into the root of your existing `FhirBulkExportManager` repository.
3. Keep the directory structure and approve replacement of the existing files.
4. From the repository root, run:

   ```powershell
   dotnet build FhirBulkExportManager.sln -c Release --no-restore
   ```

No database migration and no new NuGet package are required.

## Recover the currently failed job

1. Reconnect the VPN and confirm the saved connection is correct.
2. Open **Dashboard & Jobs** and select the failed job.
3. Click **Resume Remaining Downloads** and confirm.

The action retains every file already checksummed and NDJSON-validated. It queues only unfinished file entries and does not send `$export`, poll the export status, or modify the remote export.

## Automatic recovery option

Open **Options > Downloads** and enable **Automatically resume interrupted downloads (remaining files only)**. It is enabled by default, including for installations whose older preferences do not contain this option.

Transient network, VPN, timeout, throttling, server, truncated-transfer, and stale byte-range failures retry with exponential backoff. An HTTP 401 invalidates the cached bearer token and uses a bounded refresh retry. Bad requests, forbidden responses, and invalid NDJSON still stop safely for operator review.

If a complete file is already present locally but validation was interrupted, the app validates the existing bytes before making another network request.

## Validation performed

- Full Release rebuild: **succeeded with 0 warnings and 0 errors**.
- Non-Windows automated tests: **88 passed, 0 failed**.
- The WPF app and Windows UI test assembly compiled successfully. Windows-only UI tests cannot execute in the Linux validation environment.

## Included files

- `src/FhirBulkExportManager.Application/Configuration/ApplicationPreferences.cs`
- `src/FhirBulkExportManager.Application/Services/ExportJobService.cs`
- `src/FhirBulkExportManager.Domain/ExportFile.cs`
- `src/FhirBulkExportManager.Domain/ExportJob.cs`
- `src/FhirBulkExportManager.Worker/DownloadCoordinator.cs`
- `src/FhirBulkExportManager.Worker/ExportJobProcessor.cs`
- `src/FhirBulkExportManager.Worker/Properties/AssemblyInfo.cs`
- `src/FhirBulkExportManager.App.Wpf/MainViewModel.cs`
- `src/FhirBulkExportManager.App.Wpf/MainWindow.xaml`
- `tests/FhirBulkExportManager.Application.Tests/ApplicationSettingsAndPasswordTests.cs`
- `tests/FhirBulkExportManager.Application.Tests/DownloadRetryPolicyTests.cs`
- `tests/FhirBulkExportManager.Application.Tests/ExportJobStatusRecoveryTests.cs`
- `tests/FhirBulkExportManager.Domain.Tests/ExportFileTests.cs`
- `tests/FhirBulkExportManager.Ui.Tests/UiCompositionTests.cs`
