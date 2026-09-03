using System.IO;
using System.Threading;
using System.Windows;
using FhirBulkExportManager.Application;
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
            await host.StartAsync().ConfigureAwait(true);
            await host.Services.GetRequiredService<FirstRunBootstrapper>().InitializeAsync(CancellationToken.None).ConfigureAwait(true);
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
