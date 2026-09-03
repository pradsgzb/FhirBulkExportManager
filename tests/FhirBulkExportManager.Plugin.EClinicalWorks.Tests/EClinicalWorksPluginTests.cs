using System.Net;
using FhirBulkExportManager.Plugin.EClinicalWorks;
using FhirBulkExportManager.PluginContracts;

namespace FhirBulkExportManager.Plugin.EClinicalWorks.Tests;

public sealed class EClinicalWorksPluginTests
{
    [Fact]
    public void Manifest_keeps_duplicate_resource_type_files()
    {
        var plugin = new EClinicalWorksPlugin(new StaticHttpClientFactory(new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)))));
        const string json = "{\"transactionTime\":\"2026-09-03T00:00:00Z\",\"requiresAccessToken\":true,\"output\":[{\"type\":\"Patient\",\"url\":\"https://example.test/a\"},{\"type\":\"Patient\",\"url\":\"https://example.test/b\"}],\"deleted\":[],\"error\":[]}";

        var result = plugin.NormalizeManifest(json);

        Assert.Equal(2, result.Files.Count);
        Assert.All(result.Files, x => Assert.Equal("Patient", x.ResourceType));
    }

    [Fact]
    public async Task Kickoff_captures_exact_content_location()
    {
        const string exact = "https://example.test/status?job=A%2FB&opaque=x%20y";
        var handler = new StubHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Accepted)
            {
                Content = new ByteArrayContent([]),
            };

            Assert.True(response.Content.Headers.TryAddWithoutValidation("Content-Location", exact));
            Assert.True(response.Headers.TryAddWithoutValidation("Retry-After", "120"));
            return response;
        });
        var plugin = new EClinicalWorksPlugin(new StaticHttpClientFactory(new HttpClient(handler)));
        var site = new VendorSiteContext(Guid.NewGuid(), Guid.NewGuid(), new Uri("https://example.test/fhir/"), null, null, "{}");
        var request = new BulkExportKickoffRequest(Guid.NewGuid(), Guid.NewGuid(), site, PluginExportType.System, null, null, [], [], "application/fhir+ndjson", "{}", "correlation");

        var result = await plugin.KickoffAsync(request, new AccessToken("token", DateTimeOffset.MaxValue), TimeProvider.System, CancellationToken.None);

        Assert.Equal(KickoffDisposition.Accepted, result.Disposition);
        Assert.Equal(exact, result.ExactContentLocation?.AbsoluteUri);
    }

    private sealed class StaticHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(response(request));
    }
}
