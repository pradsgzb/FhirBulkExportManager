using FhirBulkExportManager.App.Wpf.Presentation;
using Microsoft.Extensions.DependencyInjection;

namespace FhirBulkExportManager.App.Wpf;

public sealed class WindowNavigationService(IServiceProvider serviceProvider) : IWindowNavigationService
{
    public async Task ShowJobDetailAsync(Guid jobId, CancellationToken cancellationToken)
    {
        var window = serviceProvider.GetRequiredService<JobDetailWindow>();
        await window.ViewModel.LoadAsync(jobId, cancellationToken).ConfigureAwait(true);
        window.Owner = System.Windows.Application.Current.MainWindow;
        window.ShowDialog();
    }
}
