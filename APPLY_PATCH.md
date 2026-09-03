# Enterprise FHIR Bulk Export Manager — Options, Dashboard, and Security Patch

This archive contains only the 35 changed or new source/test files required for this patch. It is not a complete repository and contains no `bin`, `obj`, package cache, database, secrets, or build output.

## Apply the patch

1. Close Enterprise FHIR Bulk Export Manager and Visual Studio.
2. Back up your repository and local application data.
3. Extract this ZIP directly over the root of `FhirBulkExportManager` and allow Windows to overwrite matching files.
4. Run:

   ```powershell
   dotnet restore FhirBulkExportManager.sln
   dotnet build FhirBulkExportManager.sln -c Release --no-restore
   dotnet test FhirBulkExportManager.sln -c Release --no-build --no-restore
   ```

No Entity Framework migration or NuGet package change is required. Preferences and the password verifier use the existing `ApplicationSettings` table.

## Included behavior

- Visual Studio-style, left-category Options screen with automatically saved preferences.
- UTC or Windows-local date/time presentation. Durable database timestamps remain UTC.
- Dashboard auto refresh, 5–300 second slider, and visible countdown beside **Refresh Now**.
- Auto refresh reloads local dashboard data only; it does not force an early remote FHIR poll.
- Job-specific progress dashboard using the latest authoritative `X-Progress` value.
- Estimated average export rate and remaining time from preserved progress samples.
- Average file download speed from completed transfers.
- Downloaded-file count versus total manifest-file count, plus downloaded/known data size.
- Durable aggregate download counters updated after every file-processing step.
- Optional startup password protection and an extra password check before remote cancellation.
- Current password is required to change or remove a configured password.
- Passwords are never stored. A salted PBKDF2-HMAC-SHA256 verifier is stored with 600,000 iterations.
- `--reset-password` removes only application password protection and records an audit event.
- Auto-closing VPN/network/IP-whitelist hint for HTTP 400 during metadata or OAuth token requests.
- Minimize-to-tray behavior is configurable under Options.

The existing authentication sequence is unchanged by this patch.

## Password recovery

Close all running instances, then execute:

```powershell
src\FhirBulkExportManager.App.Wpf\bin\Release\net10.0-windows10.0.19041.0\FhirBulkExportManager.App.Wpf.exe --reset-password
```

If you installed or published the application elsewhere, run that installed executable with the same argument.

## Files in this patch

```text
src/FhirBulkExportManager.App.Wpf/App.xaml
src/FhirBulkExportManager.App.Wpf/App.xaml.cs
src/FhirBulkExportManager.App.Wpf/DateTimeDisplay.cs
src/FhirBulkExportManager.App.Wpf/DependencyInjection.cs
src/FhirBulkExportManager.App.Wpf/JobDetailViewModel.cs
src/FhirBulkExportManager.App.Wpf/JobDetailWindow.xaml
src/FhirBulkExportManager.App.Wpf/JobHistoryViewModels.cs
src/FhirBulkExportManager.App.Wpf/JobProgressAnalytics.cs
src/FhirBulkExportManager.App.Wpf/MainViewModel.cs
src/FhirBulkExportManager.App.Wpf/MainWindow.xaml
src/FhirBulkExportManager.App.Wpf/MainWindow.xaml.cs
src/FhirBulkExportManager.App.Wpf/PasswordChangeDialog.xaml
src/FhirBulkExportManager.App.Wpf/PasswordChangeDialog.xaml.cs
src/FhirBulkExportManager.App.Wpf/PasswordPromptDialog.xaml
src/FhirBulkExportManager.App.Wpf/PasswordPromptDialog.xaml.cs
src/FhirBulkExportManager.App.Wpf/Presentation/DialogServices.cs
src/FhirBulkExportManager.App.Wpf/TransientNotificationService.cs
src/FhirBulkExportManager.App.Wpf/TransientNotificationWindow.xaml
src/FhirBulkExportManager.App.Wpf/TransientNotificationWindow.xaml.cs
src/FhirBulkExportManager.Application/Abstractions/Persistence.cs
src/FhirBulkExportManager.Application/Abstractions/Services.cs
src/FhirBulkExportManager.Application/Configuration/ApplicationPreferences.cs
src/FhirBulkExportManager.Application/DependencyInjection.cs
src/FhirBulkExportManager.Application/Services/ApplicationSettingsService.cs
src/FhirBulkExportManager.Application/Services/ExportJobService.cs
src/FhirBulkExportManager.Application/Services/PasswordProtectionService.cs
src/FhirBulkExportManager.Domain/OperationalEntities.cs
src/FhirBulkExportManager.Persistence.Sqlite/DependencyInjection.cs
src/FhirBulkExportManager.Persistence.Sqlite/SqliteConfigurationStore.cs
src/FhirBulkExportManager.Worker/AuthenticationTokenProvider.cs
src/FhirBulkExportManager.Worker/ExportJobProcessor.cs
tests/FhirBulkExportManager.Application.Tests/ApplicationSettingsAndPasswordTests.cs
tests/FhirBulkExportManager.Plugin.EClinicalWorks.Tests/EClinicalWorksPluginTests.cs
tests/FhirBulkExportManager.Ui.Tests/JobProgressAnalyticsTests.cs
tests/FhirBulkExportManager.Ui.Tests/UiCompositionTests.cs
```

## Validation performed before packaging

- .NET SDK 10.0.400 / runtime 10.0.11 restore succeeded.
- Full Release solution build succeeded with 0 warnings and 0 errors.
- All 60 cross-platform automated tests passed.
- The Windows-only UI test assembly compiled successfully. Execute the full `dotnet test` command above on Windows to run those WPF tests.
- XAML/XML parsing, C# syntax parsing, archive integrity, and secret-pattern checks were performed.
