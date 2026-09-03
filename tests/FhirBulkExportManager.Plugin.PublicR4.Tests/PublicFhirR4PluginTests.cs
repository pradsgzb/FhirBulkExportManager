using System.Net;
using FhirBulkExportManager.Plugin.PublicR4;
using FhirBulkExportManager.PluginContracts;

namespace FhirBulkExportManager.Plugin.PublicR4.Tests;

public sealed class PublicFhirR4PluginTests
{
    [Fact]
    public async Task Connection_test_is_anonymous_and_accepts_large_hapi_metadata()
    {
        var capabilityStatement = "{\"resourceType\":\"CapabilityStatement\",\"text\":\"" + new string('x', 3 * 1024 * 1024) + "\"}";
        var handler = new StubHandler(request =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("https://hapi.fhir.org/baseR4/metadata", request.RequestUri?.AbsoluteUri);
            Assert.Null(request.Headers.Authorization);
            Assert.Contains(request.Headers.Accept, value => value.MediaType == "application/fhir+json");
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(capabilityStatement),
            };
        });
        var plugin = CreatePlugin(handler);
        var site = CreateSite("https://hapi.fhir.org/baseR4");

        var result = await plugin.TestConnectionAsync(
            site,
            new AccessToken("must-not-be-sent", DateTimeOffset.MaxValue),
            CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.NotNull(result.SanitizedCapabilityStatementJson);
        Assert.Equal(capabilityStatement.Length, result.SanitizedCapabilityStatementJson.Length);
    }

    [Fact]
    public async Task Authentication_is_a_noop_for_public_servers()
    {
        var handler = new StubHandler(_ => throw new InvalidOperationException("No HTTP request was expected."));
        var plugin = CreatePlugin(handler);
        var context = new AuthenticationContext(string.Empty, string.Empty, null, null, null, "{}", new SecretMaterial(string.Empty, new Dictionary<string, string>()));

        var token = await plugin.AuthenticateAsync(context, TimeProvider.System, CancellationToken.None);

        Assert.Equal(string.Empty, token.Value);
        Assert.Equal(DateTimeOffset.MaxValue, token.ExpiresAtUtc);
    }

    [Fact]
    public async Task Kickoff_is_anonymous_and_preserves_exact_content_location()
    {
        const string exactStatus = "https://bulk-data.smarthealthit.org/status?job=A%2FB&opaque=x%20y";
        var handler = new StubHandler(request =>
        {
            Assert.Null(request.Headers.Authorization);
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.NotNull(request.RequestUri);
            Assert.Contains("_type=Patient%2CObservation", request.RequestUri.Query, StringComparison.Ordinal);
            Assert.Contains("respond-async", request.Headers.GetValues("Prefer"));
            var response = new HttpResponseMessage(HttpStatusCode.Accepted)
            {
                Content = new ByteArrayContent([]),
            };
            Assert.True(response.Content.Headers.TryAddWithoutValidation("Content-Location", exactStatus));
            return response;
        });
        var plugin = CreatePlugin(handler);
        var request = new BulkExportKickoffRequest(
            Guid.NewGuid(),
            Guid.NewGuid(),
            CreateSite("https://bulk-data.smarthealthit.org/fhir"),
            PluginExportType.System,
            null,
            null,
            ["Patient", "Observation"],
            [],
            "application/fhir+ndjson",
            "{}",
            "correlation");

        var result = await plugin.KickoffAsync(request, new AccessToken("must-not-be-sent", DateTimeOffset.MaxValue), TimeProvider.System, CancellationToken.None);

        Assert.Equal(KickoffDisposition.Accepted, result.Disposition);
        Assert.Equal(exactStatus, result.ExactContentLocation?.AbsoluteUri);
    }

    [Fact]
    public async Task Poll_and_download_requests_never_send_authorization()
    {
        var handler = new StubHandler(request =>
        {
            Assert.Null(request.Headers.Authorization);
            return new HttpResponseMessage(HttpStatusCode.Accepted)
            {
                Content = new ByteArrayContent([]),
            };
        });
        var plugin = CreatePlugin(handler);
        var site = CreateSite("https://bulk-data.smarthealthit.org/fhir");
        var token = new AccessToken("must-not-be-sent", DateTimeOffset.MaxValue);
        var statusUri = new Uri("https://bulk-data.smarthealthit.org/status?job=opaque");

        var result = await plugin.PollAsync(statusUri, site, token, TimeProvider.System, CancellationToken.None);
        using var download = plugin.CreateDownloadRequest(
            new Uri("https://bulk-data.smarthealthit.org/files/patient.ndjson"),
            statusUri,
            token,
            resumeOffset: 100,
            ifRangeEtag: null,
            ifRangeLastModified: null);

        Assert.Equal(PollDisposition.Running, result.Disposition);
        Assert.Null(download.Headers.Authorization);
        Assert.NotNull(download.Headers.Range);
        Assert.Equal(100L, download.Headers.Range.Ranges.Single().From.GetValueOrDefault());
    }

    private static PublicFhirR4Plugin CreatePlugin(HttpMessageHandler handler) =>
        new(new StaticHttpClientFactory(new HttpClient(handler)));

    private static VendorSiteContext CreateSite(string baseUrl) =>
        new(Guid.NewGuid(), Guid.NewGuid(), new Uri(baseUrl), null, null, "{}");

    private sealed class StaticHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(response(request));
    }
}
