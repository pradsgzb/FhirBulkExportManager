using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows;
using FhirBulkExportManager.Application;
using FhirBulkExportManager.Application.Abstractions;
using FhirBulkExportManager.Application.Services;
using FhirBulkExportManager.Infrastructure;
using FhirBulkExportManager.Infrastructure.Logging;
using FhirBulkExportManager.Persistence.Sqlite;
using FhirBulkExportManager.Plugin.EClinicalWorks;
using FhirBulkExportManager.Plugin.PublicR4;
using FhirBulkExportManager.PluginHost;
using FhirBulkExportManager.Worker;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FhirBulkExportManager.App.Wpf;

public partial class App : System.Windows.Application
{
    private IHost? host;
    private Mutex? singleInstanceMutex;
    private bool ownsSingleInstanceMutex;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (!await WaitForRestartPredecessorAsync(e.Args).ConfigureAwait(true))
        {
            Shutdown(4);
            return;
        }

        singleInstanceMutex = new Mutex(initiallyOwned: true, "Local\\FhirBulkExportManager-1.0", out var createdNew);
        ownsSingleInstanceMutex = createdNew;
        if (!createdNew)
        {
            System.Windows.MessageBox.Show("Enterprise FHIR Bulk Export Manager is already running for this Windows user.", "Application already running", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown(2);
            return;
        }

        try
        {
            var builder = Host.CreateApplicationBuilder(e.Args);
            builder.Configuration.Sources.Clear();
            builder.Configuration
                .SetBasePath(AppContext.BaseDirectory)
                .AddJsonFile("appsettings.json", optional: false, reloadOnChange: false)
                .AddEnvironmentVariables("FHIR_EXPORT_");
            ExpandEnvironmentPaths(builder.Configuration);

            builder.Logging.ClearProviders();
            var logDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FhirBulkExportManager", "Logs");
            builder.Logging.AddProvider(new RollingFileLoggerProvider(logDirectory));

            builder.Services.AddApplication();
            builder.Services.AddInfrastructure(builder.Configuration);
            builder.Services.AddSqlitePersistence(builder.Configuration);
            builder.Services.AddEClinicalWorksPlugin();
            builder.Services.AddPublicFhirR4Plugin();
            builder.Services.AddPluginHost();
            builder.Services.AddExportWorkers();
            builder.Services.AddWpfPresentation();

            host = builder.Build();

            await host.Services.GetRequiredService<IDatabaseLifecycle>()
                .InitializeAsync(CancellationToken.None).ConfigureAwait(true);
            await host.Services.GetRequiredService<ApplicationSettingsService>()
                .LoadAsync(CancellationToken.None).ConfigureAwait(true);

            var passwordProtection = host.Services.GetRequiredService<PasswordProtectionService>();
            var dialogs = host.Services.GetRequiredService<FhirBulkExportManager.App.Wpf.Presentation.IUserDialogService>();
            if (e.Args.Any(argument => string.Equals(argument, "--reset-password", StringComparison.OrdinalIgnoreCase)))
            {
                await passwordProtection.ResetAsync(CancellationToken.None).ConfigureAwait(true);
                dialogs.ShowInfo(
                    "Application password reset",
                    "The application password was removed for this Windows user. The application is no longer password protected. You can configure a new password under Options > Security.");
            }
            else if (!await UnlockAsync(passwordProtection, dialogs).ConfigureAwait(true))
            {
                host.Dispose();
                host = null;
                Shutdown(3);
                return;
            }

            var runtimeConfigurationService = host.Services.GetRequiredService<RuntimeConfigurationService>();
            var runtimeConfiguration = await runtimeConfigurationService
                .LoadAsync(CancellationToken.None).ConfigureAwait(true);
            var migrationService = host.Services.GetRequiredService<IManagedDataRootMigrationService>();
            if (runtimeConfiguration.PendingDataRootMigration is { } pendingMigration)
            {
                dialogs.ShowInfo(
                    "Managed data migration",
                    "The approved managed-data-root change will now be applied before export workers start. " +
                    "For large data folders this can take time. Do not end the application process.\n\n" +
                    $"From: {pendingMigration.SourceDataRoot}\n" +
                    $"To: {pendingMigration.TargetDataRoot}");
                var result = await migrationService
                    .MigrateAsync(pendingMigration, CancellationToken.None).ConfigureAwait(true);
                runtimeConfiguration = await runtimeConfigurationService
                    .CompleteDataRootMigrationAsync(CancellationToken.None).ConfigureAwait(true);
                try
                {
                    migrationService.CleanupCompletedMigrationArtifacts(runtimeConfiguration.DataRoot);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    dialogs.ShowError(
                        "Migration cleanup warning",
                        "Managed data was moved and verified, but an internal migration marker could not be removed. " + exception.Message);
                }

                var method = result.UsedAtomicMove ? "moved atomically" : "copied and checksum-verified";
                dialogs.ShowInfo(
                    "Managed data migration completed",
                    $"The managed data root was {method}. {result.FilesMoved:N0} file(s), {result.BytesMoved:N0} byte(s). Export workers can now resume safely.");
            }

            runtimeConfigurationService.ApplyAtStartup(runtimeConfiguration);

            await host.Services.GetRequiredService<FirstRunBootstrapper>().InitializeAsync(CancellationToken.None).ConfigureAwait(true);
            await host.StartAsync().ConfigureAwait(true);
            var window = host.Services.GetRequiredService<MainWindow>();
            MainWindow = window;
            window.Show();
            await window.ViewModel.InitializeAsync(CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            System.Windows.MessageBox.Show("The application could not start safely. No export was started.\n\n" + exception.Message, "Startup failure", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    private static async Task<bool> WaitForRestartPredecessorAsync(IReadOnlyCollection<string> arguments)
    {
        const string prefix = "--restart-after=";
        var value = arguments.FirstOrDefault(argument => argument.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        if (value is null ||
            !int.TryParse(value[prefix.Length..], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var processId) ||
            processId <= 0 ||
            processId == Environment.ProcessId)
        {
            return true;
        }

        try
        {
            using var predecessor = Process.GetProcessById(processId);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            await predecessor.WaitForExitAsync(timeout.Token).ConfigureAwait(true);
            return true;
        }
        catch (ArgumentException)
        {
            return true;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
        catch (OperationCanceledException)
        {
            System.Windows.MessageBox.Show(
                "The previous application process did not exit within 60 seconds. Start the application again after confirming the old process has closed.",
                "Restart timed out",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            return false;
        }
    }

    private static async Task<bool> UnlockAsync(
        PasswordProtectionService passwordProtection,
        FhirBulkExportManager.App.Wpf.Presentation.IUserDialogService dialogs)
    {
        if (!await passwordProtection.IsConfiguredAsync(CancellationToken.None).ConfigureAwait(true))
        {
            return true;
        }

        for (var attempt = 1; attempt <= 5; attempt++)
        {
            var password = dialogs.RequestPassword(
                "Unlock Enterprise FHIR Bulk Export Manager",
                "Enter the application password. Local monitoring and downloads will not start until the application is unlocked.");
            if (password is null)
            {
                return false;
            }

            if (await passwordProtection.VerifyAsync(password, CancellationToken.None).ConfigureAwait(true))
            {
                return true;
            }

            dialogs.ShowError("Incorrect password", $"The application password is incorrect. {5 - attempt} attempt(s) remain.");
        }

        dialogs.ShowError("Application locked", "Too many incorrect password attempts. The application will close without starting local workers.");
        return false;
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        if (host is not null)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            try
            {
                await host.StopAsync(timeout.Token).ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
                // In-flight HTTP and file writes use cancellation-safe partial files and durable state.
            }

            host.Dispose();
        }

        if (ownsSingleInstanceMutex)
        {
            singleInstanceMutex?.ReleaseMutex();
        }
        singleInstanceMutex?.Dispose();
        base.OnExit(e);
    }

    private static void ExpandEnvironmentPaths(IConfiguration configuration)
    {
        foreach (var key in new[] { "Persistence:DatabasePath", "Storage:DataRoot", "Plugins:Directory" })
        {
            var value = configuration[key];
            if (!string.IsNullOrWhiteSpace(value))
            {
                var expanded = Environment.ExpandEnvironmentVariables(value);
                if (key == "Plugins:Directory" && !Path.IsPathRooted(expanded))
                {
                    expanded = Path.Combine(AppContext.BaseDirectory, expanded);
                }

                configuration[key] = expanded;
            }
        }
    }
}
