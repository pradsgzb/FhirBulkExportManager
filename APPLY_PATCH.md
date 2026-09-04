# Runtime, Storage, Restart, and Startup Fix Patch

This archive contains only the files changed or added for this patch. It is not a complete source-code archive.

## Apply

1. Close Enterprise FHIR Bulk Export Manager and Visual Studio.
2. Extract this ZIP into the root of your existing repository, for example:
   `D:\Projects\Pradeep\FhirBulkExportManager`
3. Allow Windows to merge the `src` and `tests` folders and overwrite the listed files.
4. Reopen the solution and run:

   ```powershell
   dotnet build FhirBulkExportManager.sln -c Release --no-restore
   ```

No NuGet package update and no database migration are required by this patch.

## Included behavior

- Fixes startup failure `Cannot set Owner property to itself` by assigning a dialog owner only when the real, loaded `MainWindow` exists.
- Makes configured export workers, per-site downloads, disk reserve, and managed data root editable under **Options > Runtime & Storage**.
- Keeps detected logical processors and effective export workers read-only because they are hardware-derived/calculated values.
- Validates and durably stages runtime/storage edits, then asks whether to restart the application now or later.
- Shows **Application restart pending** in the status bar and provides a **Restart Now** action.
- Keeps active processing and new jobs on the currently active data root until restart.
- If the old managed data root contains anything, asks whether to move it or leave it in place.
- Applies an approved move before export workers start. Same-volume moves are atomic; cross-volume moves use resumable copy, per-file SHA-256 verification, and delete the source only after verification.
- Refuses destination merging, path nesting, filesystem-root targets, and reparse-point traversal.
- Falls back to deployment defaults if persisted runtime settings are corrupt, without starting an export during recovery.

Changing these options requires an application restart, not a Windows operating-system reboot. Remote exports are not cancelled by restarting the desktop application.

## Validation performed

- Complete Release solution build: 19 projects, 0 warnings, 0 errors.
- 68 runnable tests passed across Application, Architecture, Domain, Infrastructure, Integration, Persistence, eClinicalWorks plugin, and Public R4 plugin test suites.
- The Windows-targeted WPF application and UI test assembly compiled successfully. UI execution remains a Windows-only validation step.

## Files in this patch

- `src/FhirBulkExportManager.Application/Abstractions/Services.cs`
- `src/FhirBulkExportManager.Application/Configuration/RuntimeConfiguration.cs`
- `src/FhirBulkExportManager.Application/DependencyInjection.cs`
- `src/FhirBulkExportManager.Application/Services/RuntimeConfigurationService.cs`
- `src/FhirBulkExportManager.Infrastructure/DependencyInjection.cs`
- `src/FhirBulkExportManager.Infrastructure/Storage/ManagedDataRootMigrationService.cs`
- `src/FhirBulkExportManager.App.Wpf/App.xaml.cs`
- `src/FhirBulkExportManager.App.Wpf/ApplicationRestartService.cs`
- `src/FhirBulkExportManager.App.Wpf/DependencyInjection.cs`
- `src/FhirBulkExportManager.App.Wpf/MainViewModel.cs`
- `src/FhirBulkExportManager.App.Wpf/MainWindow.xaml`
- `src/FhirBulkExportManager.App.Wpf/MainWindow.xaml.cs`
- `src/FhirBulkExportManager.App.Wpf/Presentation/DialogServices.cs`
- `tests/FhirBulkExportManager.Application.Tests/RuntimeConfigurationServiceTests.cs`
- `tests/FhirBulkExportManager.Infrastructure.Tests/ManagedDataRootMigrationServiceTests.cs`
- `tests/FhirBulkExportManager.Ui.Tests/UiCompositionTests.cs`
