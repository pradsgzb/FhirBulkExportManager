using System.Net;
using System.Text.Json;
using FhirBulkExportManager.Plugin.EClinicalWorks;
using FhirBulkExportManager.PluginContracts;

namespace FhirBulkExportManager.Plugin.EClinicalWorks.Tests;

public sealed class EClinicalWorksPluginTests
{
    [Fact]
    public async Task Connection_test_requests_metadata_without_authentication()
    {
        var requestCount = 0;
        var handler = new StubHandler(request =>
        {
            requestCount++;
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("https://example.test/fhir/r4/practice/metadata", request.RequestUri?.AbsoluteUri);
            Assert.Null(request.Headers.Authorization);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"resourceType\":\"CapabilityStatement\"}"),
            };
        });
        var plugin = new EClinicalWorksPlugin(new StaticHttpClientFactory(new HttpClient(handler)));
        var site = new VendorSiteContext(Guid.NewGuid(), Guid.NewGuid(), new Uri("https://example.test/fhir/r4/practice"), null, null, "{}");

        var result = await plugin.TestConnectionAsync(
            site,
            new AccessToken("this-token-must-not-be-sent", DateTimeOffset.MaxValue),
            CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(1, requestCount);
        Assert.Contains("without authentication", result.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HealthWizz_flow_posts_json_and_exchanges_raw_assertion_without_client_secret()
    {
        const string tokenEndpoint = "https://oauth.example.test/oauth/oauth2/token";
        const string assertionEndpoint = "https://healthwizz.example.test/enterprise/v1.1/oauth/token";
        const string assertion = "test-header.test-payload.test-signature";
        const string scope = "system/Patient.read system/Group.read";
        var requestCount = 0;
        var handler = new StubHandler(request =>
        {
            requestCount++;
            if (requestCount == 1)
            {
                Assert.Equal(assertionEndpoint, request.RequestUri?.AbsoluteUri);
                Assert.Equal("application/json", request.Content?.Headers.ContentType?.MediaType);
                Assert.Null(request.Content?.Headers.ContentType?.CharSet);
                Assert.Null(request.Headers.Authorization);
                using var body = JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
                var properties = body.RootElement.EnumerateObject().ToArray();
                Assert.Equal(2, properties.Length);
                Assert.Equal("client-for-test", body.RootElement.GetProperty("clientId").GetString());
                Assert.Equal(tokenEndpoint, body.RootElement.GetProperty("aud").GetString());
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("  " + assertion + "\r\n"),
                };
            }

            Assert.Equal(2, requestCount);
            Assert.Equal(tokenEndpoint, request.RequestUri?.AbsoluteUri);
            Assert.Equal("application/x-www-form-urlencoded", request.Content?.Headers.ContentType?.MediaType);
            Assert.Null(request.Headers.Authorization);
            var fields = ParseForm(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            Assert.Equal(4, fields.Count);
            Assert.Equal("client_credentials", fields["grant_type"]);
            Assert.Equal("urn:ietf:params:oauth:client-assertion-type:jwt-bearer", fields["client_assertion_type"]);
            Assert.Equal(assertion, fields["client_assertion"]);
            Assert.Equal(scope, fields["scope"]);
            Assert.False(fields.ContainsKey("client_id"));
            Assert.False(fields.ContainsKey("client_secret"));
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"access_token\":\"access-token-for-test\",\"token_type\":\"Bearer\",\"expires_in\":299}"),
            };
        });
        var plugin = new EClinicalWorksPlugin(new StaticHttpClientFactory(new HttpClient(handler)));
        var context = new AuthenticationContext(
            "client-for-test",
            tokenEndpoint,
            assertionEndpoint,
            scope,
            null,
            "{}",
            new SecretMaterial(string.Empty, new Dictionary<string, string>()));

        var token = await plugin.AuthenticateAsync(context, TimeProvider.System, CancellationToken.None);

        Assert.Equal("access-token-for-test", token.Value);
        Assert.Equal("Bearer", token.TokenType);
        Assert.Equal(2, requestCount);
    }

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
            Assert.Equal("Bearer", _.Headers.Authorization?.Scheme);
            Assert.Equal("token", _.Headers.Authorization?.Parameter);
            Assert.Contains("respond-async", _.Headers.GetValues("Prefer"));
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

    [Fact]
    public async Task Poll_uses_bearer_token_and_json_accept_header()
    {
        var handler = new StubHandler(request =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("https://example.test/export-status?job=opaque", request.RequestUri?.AbsoluteUri);
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal("access-token-for-test", request.Headers.Authorization?.Parameter);
            Assert.Contains(request.Headers.Accept, value => value.MediaType == "application/json");
            return new HttpResponseMessage(HttpStatusCode.Accepted)
            {
                Content = new ByteArrayContent([]),
            };
        });
        var plugin = new EClinicalWorksPlugin(new StaticHttpClientFactory(new HttpClient(handler)));
        var site = new VendorSiteContext(Guid.NewGuid(), Guid.NewGuid(), new Uri("https://example.test/fhir/"), null, null, "{}");

        var result = await plugin.PollAsync(
            new Uri("https://example.test/export-status?job=opaque"),
            site,
            new AccessToken("access-token-for-test", DateTimeOffset.MaxValue),
            TimeProvider.System,
            CancellationToken.None);

        Assert.Equal(PollDisposition.Running, result.Disposition);
    }

    private static Dictionary<string, string> ParseForm(string body) =>
        body.Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(field => field.Split('=', 2))
            .ToDictionary(
                field => Uri.UnescapeDataString(field[0].Replace('+', ' ')),
                field => Uri.UnescapeDataString(field[1].Replace('+', ' ')),
                StringComparer.Ordinal);

    private sealed class StaticHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(response(request));
    }
}
