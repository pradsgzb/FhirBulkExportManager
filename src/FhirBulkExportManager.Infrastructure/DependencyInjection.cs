using System.Net;
using FhirBulkExportManager.Application.Abstractions;
using FhirBulkExportManager.Application.Configuration;
using FhirBulkExportManager.Infrastructure.Diagnostics;
using FhirBulkExportManager.Infrastructure.Http;
using FhirBulkExportManager.Infrastructure.Security;
using FhirBulkExportManager.Infrastructure.Storage;
using FhirBulkExportManager.Infrastructure.Validation;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace FhirBulkExportManager.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<ProcessingOptions>().Bind(configuration.GetSection(ProcessingOptions.SectionName)).ValidateDataAnnotations().ValidateOnStart();
        services.AddOptions<HttpOptions>().Bind(configuration.GetSection(HttpOptions.SectionName)).ValidateDataAnnotations().ValidateOnStart();
        services.AddOptions<StorageOptions>().Bind(configuration.GetSection(StorageOptions.SectionName)).ValidateDataAnnotations().ValidateOnStart();
        services.AddOptions<PluginOptions>().Bind(configuration.GetSection(PluginOptions.SectionName)).ValidateDataAnnotations().ValidateOnStart();
        var http = configuration.GetSection(HttpOptions.SectionName).Get<HttpOptions>() ?? new HttpOptions();

        services.AddHttpClient("FhirDownloads")
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
                ConnectTimeout = TimeSpan.FromSeconds(http.ConnectionTimeoutSeconds),
                PooledConnectionLifetime = TimeSpan.FromMinutes(10),
                MaxConnectionsPerServer = 16,
            })
            .ConfigureHttpClient(client => client.Timeout = Timeout.InfiniteTimeSpan);

        services.AddHttpClient("VendorApi")
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
                ConnectTimeout = TimeSpan.FromSeconds(http.ConnectionTimeoutSeconds),
                PooledConnectionLifetime = TimeSpan.FromMinutes(10),
                MaxConnectionsPerServer = 16,
            })
            .ConfigureHttpClient(client => client.Timeout = TimeSpan.FromSeconds(http.RequestTimeoutSeconds));

        services.AddSingleton<IWindowsIdentityProvider, WindowsIdentityProvider>();
        if (OperatingSystem.IsWindows())
        {
            services.AddSingleton<ISecretStore, WindowsDpapiSecretStore>();
        }

        services.AddSingleton<IManagedStorage, ManagedStorage>();
        services.AddSingleton<IManagedDataRootMigrationService, ManagedDataRootMigrationService>();
        services.AddSingleton<INdjsonValidator, NdjsonValidator>();
        services.AddSingleton<ILocalCommitService, LocalCommitService>();
        services.AddSingleton<IFileDownloadService, SecureFileDownloadService>();
        services.AddSingleton<IDiagnosticBundleService, DiagnosticBundleService>();
        return services;
    }
}
