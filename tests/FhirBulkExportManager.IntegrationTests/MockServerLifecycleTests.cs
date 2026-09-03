using FhirBulkExportManager.Plugin.EClinicalWorks;
using FhirBulkExportManager.PluginContracts;
using Microsoft.AspNetCore.Mvc.Testing;

namespace FhirBulkExportManager.IntegrationTests;

public sealed class MockServerLifecycleTests : IClassFixture<WebApplicationFactory<global::Program>>
{
    private readonly WebApplicationFactory<global::Program> factory;

    public MockServerLifecycleTests(WebApplicationFactory<global::Program> factory) => this.factory = factory;

    [Fact]
    public async Task Standard_202_202_200_delete_404_lifecycle_works()
    {
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var plugin = new EClinicalWorksPlugin(new StaticFactory(client));
        var origin = new Uri(client.BaseAddress!, "/");
        var authentication = new AuthenticationContext("client", new Uri(origin, "oauth/token").AbsoluteUri, null, null, null, "{}", new SecretMaterial("secret", new Dictionary<string, string>()));
        var token = await plugin.AuthenticateAsync(authentication, TimeProvider.System, CancellationToken.None);
        var site = new VendorSiteContext(Guid.NewGuid(), Guid.NewGuid(), new Uri(origin, "fhir/"), null, null, "{}");
        var kickoffRequest = new BulkExportKickoffRequest(Guid.NewGuid(), Guid.NewGuid(), site, PluginExportType.Group, "group-1", null, ["Patient", "Observation"], [], "application/fhir+ndjson", "{}", Guid.NewGuid().ToString("N"));

        var kickoff = await plugin.KickoffAsync(kickoffRequest, token, TimeProvider.System, CancellationToken.None);
        Assert.Equal(KickoffDisposition.Accepted, kickoff.Disposition);
        Assert.NotNull(kickoff.ExactContentLocation);

        var first = await plugin.PollAsync(kickoff.ExactContentLocation!, site, token, TimeProvider.System, CancellationToken.None);
        var second = await plugin.PollAsync(kickoff.ExactContentLocation!, site, token, TimeProvider.System, CancellationToken.None);
        var complete = await plugin.PollAsync(kickoff.ExactContentLocation!, site, token, TimeProvider.System, CancellationToken.None);
        Assert.Equal(PollDisposition.Running, first.Disposition);
        Assert.Equal(PollDisposition.Running, second.Disposition);
        Assert.Equal(PollDisposition.Complete, complete.Disposition);
        Assert.Equal(3, plugin.NormalizeManifest(complete.ExactManifestJson!).Files.Count);

        var cleanup = await plugin.CleanupAsync(kickoff.ExactContentLocation!, site, token, TimeProvider.System, CancellationToken.None);
        var verification = await plugin.VerifyCleanupAsync(kickoff.ExactContentLocation!, site, token, TimeProvider.System, CancellationToken.None);
        Assert.True(cleanup.Succeeded);
        Assert.Equal(CleanupVerificationDisposition.VerifiedDeleted, verification.Disposition);
    }

    private sealed class StaticFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }
}

