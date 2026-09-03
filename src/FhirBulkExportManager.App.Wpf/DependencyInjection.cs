using FhirBulkExportManager.App.Wpf.Presentation;
using Microsoft.Extensions.DependencyInjection;

namespace FhirBulkExportManager.App.Wpf;

public static class DependencyInjection
{
    public static IServiceCollection AddWpfPresentation(this IServiceCollection services)
    {
        services.AddSingleton<IUserDialogService, UserDialogService>();
        services.AddSingleton<IWindowNavigationService, WindowNavigationService>();
        services.AddSingleton<FirstRunBootstrapper>();
        services.AddTransient<MainViewModel>();
        services.AddTransient<MainWindow>();
        services.AddTransient<JobDetailViewModel>();
        services.AddTransient<JobDetailWindow>();
        return services;
    }
}
