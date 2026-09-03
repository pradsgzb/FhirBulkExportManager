using FhirBulkExportManager.Application.Abstractions;
using FhirBulkExportManager.Domain;

namespace FhirBulkExportManager.App.Wpf;

public sealed class FirstRunBootstrapper(
    IConfigurationStore configurationStore,
    IWindowsIdentityProvider identityProvider,
    TimeProvider timeProvider)
{
    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        var vendors = await configurationStore.GetVendorsAsync(enabledOnly: false, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (vendors.Any(x => string.Equals(x.PluginId, "eclinicalworks", StringComparison.Ordinal)))
        {
            return;
        }

        var now = timeProvider.GetUtcNow();
        var vendor = new Vendor(Guid.NewGuid(), "eClinicalWorks", "eclinicalworks", "1.0.0", now);
        var correlation = Guid.NewGuid().ToString("N");
        var audit = new AuditEvent(now, identityProvider.GetCurrentIdentity(), "VendorCreated", nameof(Vendor), vendor.Id.ToString("D"), "Succeeded", correlation, "{\"source\":\"first-run-bootstrap\"}");
        await configurationStore.AddVendorAsync(vendor, audit, cancellationToken).ConfigureAwait(false);
    }
}
