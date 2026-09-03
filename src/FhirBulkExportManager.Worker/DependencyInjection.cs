using FhirBulkExportManager.Application.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace FhirBulkExportManager.Worker;

public static class DependencyInjection
{
    public static IServiceCollection AddExportWorkers(this IServiceCollection services)
    {
        services.AddSingleton<WorkSignal>();
        services.AddSingleton<IWorkSignal>(provider => provider.GetRequiredService<WorkSignal>());
        services.AddSingleton<AuthenticationTokenProvider>();
        services.AddSingleton<SiteDownloadLimiter>();
        services.AddSingleton<SiteResilienceGate>();
        services.AddSingleton<DownloadCoordinator>();
        services.AddSingleton<ExportJobProcessor>();
        services.AddHostedService<WorkerRecoveryService>();
        services.AddHostedService<ExportSchedulerService>();
        return services;
    }
}
