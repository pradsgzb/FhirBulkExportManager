using FhirBulkExportManager.Application.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace FhirBulkExportManager.PluginHost;

public static class DependencyInjection
{
    public static IServiceCollection AddPluginHost(this IServiceCollection services)
    {
        services.AddSingleton<PluginCatalog>();
        services.AddSingleton<IVendorPluginCatalog>(provider => provider.GetRequiredService<PluginCatalog>());
        services.AddHostedService<PluginDiscoveryService>();
        return services;
    }
}

