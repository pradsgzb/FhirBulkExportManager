using FhirBulkExportManager.Application.Abstractions;
using FhirBulkExportManager.Domain;

namespace FhirBulkExportManager.App.Wpf;

public sealed class FirstRunBootstrapper(
    IConfigurationStore configurationStore,
    IWindowsIdentityProvider identityProvider,
    TimeProvider timeProvider)
{
    public const string PublicFhirR4PluginId = "public-fhir-r4";
    private const string BuiltInPluginVersion = "1.0.0";
    private const string AnonymousAuthenticationType = "None";
    private const string AnonymousCredentialName = "Anonymous public test access";

    private static readonly PublicTestServerDefinition[] PublicTestServers =
    [
        new(
            "HAPI Test Server (R4 FHIR) [PUBLIC TEST - NO PHI]",
            "HAPI FHIR public R4 endpoint",
            new Uri("https://hapi.fhir.org/baseR4"),
            "Public HAPI FHIR R4 demo endpoint for general API testing. Data is regularly purged and Bulk Data operations depend on the current server configuration. Synthetic test data only; never submit PHI or confidential data."),
        new(
            "SMART Health IT R4 Sandbox [PUBLIC TEST - NO PHI]",
            "SMART public R4 endpoint",
            new Uri("https://r4.smarthealthit.org"),
            "Public SMART Health IT R4 sandbox for general FHIR REST API testing. Bulk Data operations are not guaranteed. Synthetic test data only; never submit PHI or confidential data."),
        new(
            "SMART Bulk Data Server R4 [PUBLIC TEST - NO PHI]",
            "SMART public Bulk Data endpoint",
            new Uri("https://bulk-data.smarthealthit.org/fhir"),
            "Public SMART Health IT Bulk Data reference server supporting System, Group, and Patient exports with synthetic data. Test use only; never submit PHI or confidential data."),
    ];

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        var vendors = (await configurationStore.GetVendorsAsync(enabledOnly: false, cancellationToken: cancellationToken).ConfigureAwait(false)).ToList();
        await EnsureEClinicalWorksVendorAsync(vendors, cancellationToken).ConfigureAwait(false);

        foreach (var server in PublicTestServers)
        {
            var (vendor, vendorCreated) = await EnsurePublicVendorAsync(vendors, server, cancellationToken).ConfigureAwait(false);
            if (vendorCreated)
            {
                await EnsureAnonymousConnectionAsync(vendor, server, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task EnsureEClinicalWorksVendorAsync(List<Vendor> vendors, CancellationToken cancellationToken)
    {
        if (vendors.Any(x => string.Equals(x.PluginId, "eclinicalworks", StringComparison.Ordinal)))
        {
            return;
        }

        var now = timeProvider.GetUtcNow();
        var vendor = new Vendor(Guid.NewGuid(), "eClinicalWorks", "eclinicalworks", BuiltInPluginVersion, now);
        await configurationStore.AddVendorAsync(vendor, Audit(now, "VendorCreated", nameof(Vendor), vendor.Id), cancellationToken).ConfigureAwait(false);
        vendors.Add(vendor);
    }

    private async Task<(Vendor Vendor, bool Created)> EnsurePublicVendorAsync(
        List<Vendor> vendors,
        PublicTestServerDefinition server,
        CancellationToken cancellationToken)
    {
        var existing = vendors.FirstOrDefault(x => string.Equals(x.Name, server.VendorName, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            if (!string.Equals(existing.PluginId, PublicFhirR4PluginId, StringComparison.Ordinal) ||
                !string.Equals(existing.PluginVersion, BuiltInPluginVersion, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"The built-in vendor name '{server.VendorName}' is already assigned to a different plugin. Rename that vendor before restarting.");
            }

            return (existing, false);
        }

        var now = timeProvider.GetUtcNow();
        var vendor = new Vendor(Guid.NewGuid(), server.VendorName, PublicFhirR4PluginId, BuiltInPluginVersion, now);
        await configurationStore.AddVendorAsync(vendor, Audit(now, "VendorCreated", nameof(Vendor), vendor.Id), cancellationToken).ConfigureAwait(false);
        vendors.Add(vendor);
        return (vendor, true);
    }

    private async Task EnsureAnonymousConnectionAsync(
        Vendor vendor,
        PublicTestServerDefinition server,
        CancellationToken cancellationToken)
    {
        var credentials = await configurationStore.GetCredentialProfilesAsync(vendor.Id, cancellationToken).ConfigureAwait(false);
        var credential = credentials.FirstOrDefault(x =>
            string.Equals(x.AuthenticationType, AnonymousAuthenticationType, StringComparison.OrdinalIgnoreCase));
        if (credential is null)
        {
            var now = timeProvider.GetUtcNow();
            credential = new CredentialProfile(
                Guid.NewGuid(),
                vendor.Id,
                AnonymousCredentialName,
                AnonymousAuthenticationType,
                clientId: null,
                secretReference: null,
                tokenEndpoint: null,
                createdAtUtc: now);
            await configurationStore.AddCredentialProfileAsync(
                credential,
                Audit(now, "CredentialProfileCreated", nameof(CredentialProfile), credential.Id),
                cancellationToken).ConfigureAwait(false);
        }

        var sites = await configurationStore.GetSitesAsync(vendor.Id, enabledOnly: false, cancellationToken: cancellationToken).ConfigureAwait(false);
        var existingSite = sites.FirstOrDefault(x =>
            string.Equals(x.FhirBaseUrl.TrimEnd('/'), server.FhirBaseUrl.AbsoluteUri.TrimEnd('/'), StringComparison.OrdinalIgnoreCase));
        if (existingSite is not null)
        {
            return;
        }

        var nameCollision = sites.FirstOrDefault(x => string.Equals(x.Name, server.SiteName, StringComparison.OrdinalIgnoreCase));
        if (nameCollision is not null)
        {
            throw new InvalidOperationException($"The built-in site name '{server.SiteName}' already uses a different FHIR base URL. Rename that site before restarting.");
        }

        var siteCreatedAt = timeProvider.GetUtcNow();
        var site = new Site(
            Guid.NewGuid(),
            vendor.Id,
            server.SiteName,
            server.FhirBaseUrl,
            credential.Id,
            siteCreatedAt,
            server.Description);
        await configurationStore.AddSiteAsync(site, Audit(siteCreatedAt, "SiteCreated", nameof(Site), site.Id), cancellationToken).ConfigureAwait(false);
    }

    private AuditEvent Audit(DateTimeOffset timestamp, string action, string entityType, Guid entityId) =>
        new(
            timestamp,
            identityProvider.GetCurrentIdentity(),
            action,
            entityType,
            entityId.ToString("D"),
            "Succeeded",
            Guid.NewGuid().ToString("N"),
            "{\"source\":\"built-in-public-test-catalog\"}");

    private sealed record PublicTestServerDefinition(string VendorName, string SiteName, Uri FhirBaseUrl, string Description);
}
