using FhirBulkExportManager.Application.Services;
using Microsoft.Extensions.DependencyInjection;

namespace FhirBulkExportManager.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<ApplicationSettingsService>();
        services.AddSingleton<RuntimeConfigurationService>();
        services.AddSingleton<PasswordProtectionService>();
        services.AddTransient<ExportJobService>();
        return services;
    }
}
