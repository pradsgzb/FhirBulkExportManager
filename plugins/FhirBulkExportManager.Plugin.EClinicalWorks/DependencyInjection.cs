using FhirBulkExportManager.PluginContracts;
using Microsoft.Extensions.DependencyInjection;

namespace FhirBulkExportManager.Plugin.EClinicalWorks;

public static class DependencyInjection
{
    public static IServiceCollection AddEClinicalWorksPlugin(this IServiceCollection services)
    {
        services.AddSingleton<EClinicalWorksPlugin>();
        services.AddSingleton<IVendorPlugin>(provider => provider.GetRequiredService<EClinicalWorksPlugin>());
        return services;
    }
}

