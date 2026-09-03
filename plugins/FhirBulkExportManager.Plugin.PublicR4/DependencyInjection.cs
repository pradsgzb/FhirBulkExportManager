using FhirBulkExportManager.PluginContracts;
using Microsoft.Extensions.DependencyInjection;

namespace FhirBulkExportManager.Plugin.PublicR4;

public static class DependencyInjection
{
    public static IServiceCollection AddPublicFhirR4Plugin(this IServiceCollection services)
    {
        services.AddSingleton<PublicFhirR4Plugin>();
        services.AddSingleton<IVendorPlugin>(provider => provider.GetRequiredService<PublicFhirR4Plugin>());
        return services;
    }
}
