using FhirBulkExportManager.Application.Abstractions;
using FhirBulkExportManager.Domain;

namespace FhirBulkExportManager.Ui.Tests;

public sealed class FirstRunBootstrapperTests
{
    [Fact]
    public async Task Initialization_adds_public_test_vendors_connections_and_is_idempotent()
    {
        var store = new InMemoryConfigurationStore();
        var bootstrapper = new FhirBulkExportManager.App.Wpf.FirstRunBootstrapper(
            store,
            new TestIdentityProvider(),
            TimeProvider.System);

        await bootstrapper.InitializeAsync(CancellationToken.None);
        await bootstrapper.InitializeAsync(CancellationToken.None);

        Assert.Equal(4, store.Vendors.Count);
        var publicVendors = store.Vendors
            .Where(x => x.PluginId == FhirBulkExportManager.App.Wpf.FirstRunBootstrapper.PublicFhirR4PluginId)
            .ToArray();
        Assert.Equal(3, publicVendors.Length);
        Assert.Contains(publicVendors, x => x.Name.StartsWith("HAPI Test Server (R4 FHIR)", StringComparison.Ordinal));
        Assert.Contains(publicVendors, x => x.Name.StartsWith("SMART Health IT R4 Sandbox", StringComparison.Ordinal));
        Assert.Contains(publicVendors, x => x.Name.StartsWith("SMART Bulk Data Server R4", StringComparison.Ordinal));

        Assert.Equal(3, store.Sites.Count);
        Assert.Contains(store.Sites, x => x.FhirBaseUrl == "https://hapi.fhir.org/baseR4");
        Assert.Contains(store.Sites, x => x.FhirBaseUrl == "https://r4.smarthealthit.org");
        Assert.Contains(store.Sites, x => x.FhirBaseUrl == "https://bulk-data.smarthealthit.org/fhir");
        Assert.All(store.Sites, x =>
        {
            Assert.NotNull(x.Description);
            Assert.Contains("never submit PHI", x.Description, StringComparison.OrdinalIgnoreCase);
        });

        Assert.Equal(3, store.Credentials.Count);
        Assert.All(store.Credentials, credential =>
        {
            Assert.Equal("None", credential.AuthenticationType);
            Assert.Equal(string.Empty, credential.ClientId);
            Assert.Equal(string.Empty, credential.TokenEndpoint);
            Assert.Equal(string.Empty, credential.SecretReference);
        });
    }

    [Fact]
    public async Task Deleted_built_in_connection_is_not_recreated_on_restart()
    {
        var store = new InMemoryConfigurationStore();
        var bootstrapper = new FhirBulkExportManager.App.Wpf.FirstRunBootstrapper(
            store,
            new TestIdentityProvider(),
            TimeProvider.System);
        await bootstrapper.InitializeAsync(CancellationToken.None);
        var hapiVendor = Assert.Single(
            store.Vendors,
            vendor => vendor.Name.StartsWith("HAPI Test Server (R4 FHIR)", StringComparison.Ordinal));
        var hapiSite = Assert.Single(store.Sites, site => site.VendorId == hapiVendor.Id);
        var hapiCredential = Assert.Single(store.Credentials, credential => credential.Id == hapiSite.CredentialProfileId);
        store.Sites.Remove(hapiSite);
        store.Credentials.Remove(hapiCredential);

        await bootstrapper.InitializeAsync(CancellationToken.None);

        Assert.DoesNotContain(store.Sites, site => site.VendorId == hapiVendor.Id);
    }

    private sealed class TestIdentityProvider : IWindowsIdentityProvider
    {
        public string GetCurrentIdentity() => "TEST\\operator";
    }

    private sealed class InMemoryConfigurationStore : IConfigurationStore
    {
        public List<Vendor> Vendors { get; } = [];
        public List<Site> Sites { get; } = [];
        public List<CredentialProfile> Credentials { get; } = [];

        public Task<IReadOnlyList<Vendor>> GetVendorsAsync(bool enabledOnly, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<Vendor>>(Vendors.Where(x => !enabledOnly || x.Enabled).ToArray());

        public Task<Vendor?> GetVendorAsync(Guid id, CancellationToken cancellationToken) =>
            Task.FromResult(Vendors.SingleOrDefault(x => x.Id == id));

        public Task AddVendorAsync(Vendor vendor, AuditEvent auditEvent, CancellationToken cancellationToken)
        {
            Vendors.Add(vendor);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<Site>> GetSitesAsync(Guid? vendorId, bool enabledOnly, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<Site>>(Sites.Where(x =>
                (vendorId is null || x.VendorId == vendorId) && (!enabledOnly || x.Enabled)).ToArray());

        public Task<Site?> GetSiteAsync(Guid id, CancellationToken cancellationToken) =>
            Task.FromResult(Sites.SingleOrDefault(x => x.Id == id));

        public Task AddSiteAsync(Site site, AuditEvent auditEvent, CancellationToken cancellationToken)
        {
            Sites.Add(site);
            return Task.CompletedTask;
        }

        public Task AddConnectionAsync(Site site, CredentialProfile credentialProfile, AuditEvent auditEvent, CancellationToken cancellationToken)
        {
            Credentials.Add(credentialProfile);
            Sites.Add(site);
            return Task.CompletedTask;
        }

        public Task UpdateConnectionAsync(Site site, CredentialProfile credentialProfile, AuditEvent auditEvent, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task<ConnectionDeleteResult> DeleteConnectionAsync(Guid siteId, Guid credentialProfileId, AuditEvent auditEvent, CancellationToken cancellationToken)
        {
            var site = Sites.Single(x => x.Id == siteId);
            Sites.Remove(site);
            var credentialDeleted = Sites.All(x => x.CredentialProfileId != credentialProfileId);
            if (credentialDeleted)
            {
                Credentials.RemoveAll(x => x.Id == credentialProfileId);
            }

            return Task.FromResult(new ConnectionDeleteResult(credentialDeleted));
        }

        public Task<ConnectionMetadataHistory?> GetConnectionMetadataAsync(string pluginId, Guid siteId, CancellationToken cancellationToken) =>
            Task.FromResult<ConnectionMetadataHistory?>(null);

        public Task RecordConnectionMetadataAsync(
            string pluginId,
            Guid siteId,
            CredentialProfile credentialProfile,
            ConnectionMetadataAttempt attempt,
            string? capabilityStatementJson,
            string? discoveredTokenEndpoint,
            AuditEvent auditEvent,
            CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<CredentialProfile?> GetCredentialProfileAsync(Guid id, CancellationToken cancellationToken) =>
            Task.FromResult(Credentials.SingleOrDefault(x => x.Id == id));

        public Task<IReadOnlyList<CredentialProfile>> GetCredentialProfilesAsync(Guid? vendorId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<CredentialProfile>>(Credentials.Where(x => vendorId is null || x.VendorId == vendorId).ToArray());

        public Task AddCredentialProfileAsync(CredentialProfile profile, AuditEvent auditEvent, CancellationToken cancellationToken)
        {
            Credentials.Add(profile);
            return Task.CompletedTask;
        }
    }
}
