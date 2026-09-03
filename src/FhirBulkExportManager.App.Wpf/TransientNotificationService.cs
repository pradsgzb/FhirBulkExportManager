using FhirBulkExportManager.Application.Abstractions;
using FhirBulkExportManager.Application.Services;

namespace FhirBulkExportManager.App.Wpf;

public sealed class TransientNotificationService(ApplicationSettingsService settings, TimeProvider timeProvider) : ITransientNotificationService
{
    private readonly object synchronization = new();
    private DateTimeOffset lastShownAtUtc = DateTimeOffset.MinValue;

    public void ShowNetworkAccessHint(string operation)
    {
        if (!settings.Current.ShowNetworkAccessHintOnBadRequest)
        {
            return;
        }

        lock (synchronization)
        {
            if (timeProvider.GetUtcNow() - lastShownAtUtc < TimeSpan.FromSeconds(5))
            {
                return;
            }

            lastShownAtUtc = timeProvider.GetUtcNow();
        }

        _ = System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
        {
            var notice = new TransientNotificationWindow(operation);
            notice.Show();
        });
    }
}
