using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using FhirBulkExportManager.PluginContracts;
using FhirBulkExportManager.Shared;

namespace FhirBulkExportManager.Plugin.PublicR4;

public sealed class PublicFhirR4Plugin(IHttpClientFactory httpClientFactory) : IVendorPlugin
{
    private const int MaximumControlResponseBytes = 2 * 1024 * 1024;
    private const int MaximumMetadataResponseBytes = 16 * 1024 * 1024;
    private static readonly MediaTypeWithQualityHeaderValue FhirJson = new("application/fhir+json");
    private static readonly MediaTypeWithQualityHeaderValue Json = new("application/json");

    public PluginMetadata Metadata { get; } = new(
        "public-fhir-r4",
        "Public/Open FHIR R4",
        new Version(1, 0, 0),
        new Version(1, 0, 0),
        null,
        "Open-source public test servers",
        "FHIR Bulk Export Manager Engineering",
        MaximumDownloadsPerSite: 4);

    public Task<AccessToken> AuthenticateAsync(AuthenticationContext context, TimeProvider timeProvider, CancellationToken cancellationToken)
    {
        _ = context;
        _ = timeProvider;
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new AccessToken(string.Empty, DateTimeOffset.MaxValue));
    }

    public async Task<ConnectionTestResult> TestConnectionAsync(VendorSiteContext site, AccessToken? token, CancellationToken cancellationToken)
    {
        _ = token;
        var metadataUri = new Uri(EnsureTrailingSlash(site.FhirBaseUrl), "metadata");
        using var request = new HttpRequestMessage(HttpMethod.Get, metadataUri);
        request.Headers.Accept.Add(FhirJson);
        var startedAtUtc = DateTimeOffset.UtcNow;
        HttpResponseMessage? response = null;
        string? body = null;
        try
        {
            response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
            body = await ReadSmallBodyAsync(response.Content, cancellationToken, MaximumMetadataResponseBytes).ConfigureAwait(false);
            var exchanges = new[]
            {
                HttpExchangeCapture.Create("FHIR capability statement", startedAtUtc, DateTimeOffset.UtcNow, request, response, responseBody: body),
            };
            var tokenEndpoint = response.IsSuccessStatusCode
                ? CapabilityStatementDiscovery.FindOAuthTokenEndpoint(body)
                : null;
            return response.IsSuccessStatusCode
                ? new ConnectionTestResult(true, response.StatusCode, "Public FHIR R4 metadata request succeeded anonymously.", body, exchanges, tokenEndpoint)
                : new ConnectionTestResult(false, response.StatusCode, $"Public FHIR R4 metadata request returned {HttpStatusText.Format(response.StatusCode)}.", null, exchanges);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or InvalidDataException or OperationCanceledException)
        {
            var exchange = HttpExchangeCapture.Create(
                "FHIR capability statement",
                startedAtUtc,
                DateTimeOffset.UtcNow,
                request,
                response,
                responseBody: body,
                failure: exception.Message);
            var statusCode = response?.StatusCode ?? (exception as HttpRequestException)?.StatusCode;
            return new ConnectionTestResult(
                false,
                statusCode,
                "Public FHIR R4 metadata request failed: " + SecurityRedactor.Redact(exception.Message),
                null,
                [exchange]);
        }
        finally
        {
            response?.Dispose();
        }
    }

    public Uri BuildKickoffUri(BulkExportKickoffRequest request) => BuildExportEndpoint(request);

    public async Task<KickoffResult> KickoffAsync(BulkExportKickoffRequest request, AccessToken token, TimeProvider timeProvider, CancellationToken cancellationToken)
    {
        _ = token;
        var endpoint = BuildKickoffUri(request);
        using var message = new HttpRequestMessage(HttpMethod.Get, endpoint);
        message.Headers.Accept.Add(FhirJson);
        message.Headers.TryAddWithoutValidation("Prefer", "respond-async");
        message.Headers.TryAddWithoutValidation("X-Correlation-Id", request.CorrelationId);
        var startedAtUtc = timeProvider.GetUtcNow();
        HttpResponseMessage? response = null;
        string? responseBody = null;
        try
        {
            response = await SendAsync(message, cancellationToken).ConfigureAwait(false);
            responseBody = await ReadSmallBodyAsync(response.Content, cancellationToken).ConfigureAwait(false);
            var exchanges = new[]
            {
                HttpExchangeCapture.Create("Bulk export kickoff", startedAtUtc, timeProvider.GetUtcNow(), message, response, responseBody: responseBody),
            };
            var now = timeProvider.GetUtcNow();
            var retryAt = RetryAfterParser.Parse(response.Headers.RetryAfter, now);
            if (response.StatusCode == HttpStatusCode.Accepted)
            {
                var rawContentLocation = GetHeader(response, "Content-Location");
                if (string.IsNullOrWhiteSpace(rawContentLocation) || !Uri.TryCreate(rawContentLocation, UriKind.Absolute, out var exactContentLocation))
                {
                    return new KickoffResult(KickoffDisposition.Rejected, response.StatusCode, null, null, endpoint.AbsoluteUri, null, responseBody, "MissingContentLocation", "Server returned 202 without an absolute Content-Location header.", exchanges);
                }

                return new KickoffResult(KickoffDisposition.Accepted, response.StatusCode, exactContentLocation, retryAt, endpoint.AbsoluteUri, GetHeader(response, "X-Progress"), responseBody, null, null, exchanges);
            }

            if (response.StatusCode == HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500)
            {
                return new KickoffResult(KickoffDisposition.RetryLater, response.StatusCode, null, retryAt ?? now.AddSeconds(30), endpoint.AbsoluteUri, GetHeader(response, "X-Progress"), responseBody, "RemoteTransient", $"Kickoff returned {(int)response.StatusCode}.", exchanges);
            }

            return new KickoffResult(KickoffDisposition.Rejected, response.StatusCode, null, null, endpoint.AbsoluteUri, GetHeader(response, "X-Progress"), responseBody, "KickoffRejected", $"Kickoff returned {HttpStatusText.Format(response.StatusCode)}.", exchanges);
        }
        catch (HttpRequestException exception)
        {
            var exchange = HttpExchangeCapture.Create("Bulk export kickoff", startedAtUtc, timeProvider.GetUtcNow(), message, response, responseBody: responseBody, failure: exception.Message);
            response?.Dispose();
            return new KickoffResult(KickoffDisposition.Ambiguous, exception.StatusCode, null, null, endpoint.AbsoluteUri, null, responseBody, "AmbiguousKickoff", "The connection failed after kickoff was attempted; server acceptance is unknown. " + SecurityRedactor.Redact(exception.Message), [exchange]);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            var exchange = HttpExchangeCapture.Create("Bulk export kickoff", startedAtUtc, timeProvider.GetUtcNow(), message, response, responseBody: responseBody, failure: exception.Message);
            response?.Dispose();
            return new KickoffResult(KickoffDisposition.Ambiguous, null, null, null, endpoint.AbsoluteUri, null, responseBody, "AmbiguousKickoffTimeout", "Kickoff timed out after dispatch; server acceptance is unknown. " + SecurityRedactor.Redact(exception.Message), [exchange]);
        }
        finally
        {
            response?.Dispose();
        }
    }

    public async Task<PollResult> PollAsync(Uri exactContentLocation, VendorSiteContext site, AccessToken token, TimeProvider timeProvider, CancellationToken cancellationToken)
    {
        _ = site;
        _ = token;
        using var request = new HttpRequestMessage(HttpMethod.Get, exactContentLocation);
        request.Headers.Accept.Add(Json);
        var startedAtUtc = timeProvider.GetUtcNow();
        HttpResponseMessage? response = null;
        string? responseBody = null;
        try
        {
            response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
            responseBody = await ReadSmallBodyAsync(
                response.Content,
                cancellationToken,
                response.StatusCode == HttpStatusCode.OK ? 16 * 1024 * 1024 : MaximumControlResponseBytes).ConfigureAwait(false);
            var exchanges = new[]
            {
                HttpExchangeCapture.Create("Bulk export status", startedAtUtc, timeProvider.GetUtcNow(), request, response, responseBody: responseBody),
            };
            var now = timeProvider.GetUtcNow();
            var retryAt = RetryAfterParser.Parse(response.Headers.RetryAfter, now);
            var progress = GetHeader(response, "X-Progress");
            switch (response.StatusCode)
            {
                case HttpStatusCode.Accepted:
                    return new PollResult(PollDisposition.Running, response.StatusCode, retryAt, progress, null, responseBody, null, null, exchanges);
                case HttpStatusCode.OK:
                    return new PollResult(PollDisposition.Complete, response.StatusCode, null, progress, responseBody, null, null, null, exchanges);
                case HttpStatusCode.Unauthorized:
                    return new PollResult(
                        PollDisposition.AuthorizationDenied,
                        response.StatusCode,
                        null,
                        progress,
                        null,
                        responseBody,
                        "AnonymousConnectionCannotAuthenticate",
                        "This job uses an anonymous Public/Open FHIR R4 connection, but the status endpoint requires authentication. Repair or re-import the job using the authenticated vendor connection that started the export.",
                        exchanges);
                case HttpStatusCode.Forbidden:
                    return new PollResult(PollDisposition.AuthorizationDenied, response.StatusCode, null, progress, null, responseBody, "Forbidden", "The public server denied this operation.", exchanges);
                case HttpStatusCode.Gone:
                    return new PollResult(PollDisposition.Expired, response.StatusCode, null, progress, null, responseBody, "RemoteExpired", "The remote export has expired.", exchanges);
                case HttpStatusCode.NotFound:
                    return new PollResult(PollDisposition.NotFound, response.StatusCode, null, progress, null, responseBody, "RemoteNotFound", "Remote job was not found; retention expiry or prior cleanup may be the cause.", exchanges);
                case HttpStatusCode.TooManyRequests:
                    return new PollResult(PollDisposition.RetryLater, response.StatusCode, retryAt ?? now.AddSeconds(60), progress, null, responseBody, "RateLimited", "FHIR server requested backoff.", exchanges);
                default:
                    if ((int)response.StatusCode >= 500 || response.StatusCode == HttpStatusCode.RequestTimeout)
                    {
                        return new PollResult(PollDisposition.RetryLater, response.StatusCode, retryAt ?? now.AddSeconds(30), progress, null, responseBody, "RemoteTransient", $"Status request returned {(int)response.StatusCode}.", exchanges);
                    }

                    return new PollResult(PollDisposition.FailedPermanent, response.StatusCode, null, progress, null, responseBody, "RemoteRejected", $"Status request returned {HttpStatusText.Format(response.StatusCode)}.", exchanges);
            }
        }
        catch (HttpRequestException exception)
        {
            var exchange = HttpExchangeCapture.Create("Bulk export status", startedAtUtc, timeProvider.GetUtcNow(), request, response, responseBody: responseBody, failure: exception.Message);
            response?.Dispose();
            return new PollResult(PollDisposition.RetryLater, exception.StatusCode ?? HttpStatusCode.ServiceUnavailable, timeProvider.GetUtcNow().AddSeconds(30), null, null, responseBody, "NetworkTransient", SecurityRedactor.Redact(exception.Message), [exchange]);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            var exchange = HttpExchangeCapture.Create("Bulk export status", startedAtUtc, timeProvider.GetUtcNow(), request, response, responseBody: responseBody, failure: exception.Message);
            response?.Dispose();
            return new PollResult(PollDisposition.RetryLater, HttpStatusCode.RequestTimeout, timeProvider.GetUtcNow().AddSeconds(30), null, null, responseBody, "RequestTimeout", SecurityRedactor.Redact(exception.Message), [exchange]);
        }
        finally
        {
            response?.Dispose();
        }
    }

    public NormalizedManifest NormalizeManifest(string exactManifestJson)
    {
        using var document = JsonDocument.Parse(exactManifestJson);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("Bulk Data completion manifest must be a JSON object.");
        }

        var transactionTime = GetOptionalString(document.RootElement, "transactionTime");
        var originalRequest = GetOptionalString(document.RootElement, "request");
        var requiresAccessToken = document.RootElement.TryGetProperty("requiresAccessToken", out var accessElement) && accessElement.ValueKind == JsonValueKind.True;
        var files = new List<ManifestFileContract>();
        AddFiles(document.RootElement, "output", ManifestFileCategory.Output, files);
        AddFiles(document.RootElement, "deleted", ManifestFileCategory.Deleted, files);
        AddFiles(document.RootElement, "error", ManifestFileCategory.Error, files);
        if (files.Count == 0)
        {
            throw new JsonException("Completion manifest contains no output, deleted, or error files.");
        }

        return new NormalizedManifest(exactManifestJson, transactionTime, originalRequest, requiresAccessToken, files);
    }

    public HttpRequestMessage CreateDownloadRequest(Uri remoteUri, Uri exactStatusUri, AccessToken? token, long? resumeOffset, EntityTagHeaderValue? ifRangeEtag, DateTimeOffset? ifRangeLastModified)
    {
        _ = exactStatusUri;
        _ = token;
        var request = new HttpRequestMessage(HttpMethod.Get, remoteUri);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/fhir+ndjson"));
        if (resumeOffset is > 0)
        {
            request.Headers.Range = new RangeHeaderValue(resumeOffset, null);
            if (ifRangeEtag is not null)
            {
                request.Headers.IfRange = new RangeConditionHeaderValue(ifRangeEtag);
            }
            else if (ifRangeLastModified is not null)
            {
                request.Headers.IfRange = new RangeConditionHeaderValue(ifRangeLastModified.Value);
            }
        }

        return request;
    }

    public Task<CleanupResult> CancelAsync(Uri exactContentLocation, VendorSiteContext site, AccessToken token, TimeProvider timeProvider, CancellationToken cancellationToken) =>
        SendDeleteAsync(exactContentLocation, site, token, timeProvider, cancellationToken);

    public Task<CleanupResult> CleanupAsync(Uri exactContentLocation, VendorSiteContext site, AccessToken token, TimeProvider timeProvider, CancellationToken cancellationToken) =>
        SendDeleteAsync(exactContentLocation, site, token, timeProvider, cancellationToken);

    public async Task<CleanupVerificationResult> VerifyCleanupAsync(Uri exactContentLocation, VendorSiteContext site, AccessToken token, TimeProvider timeProvider, CancellationToken cancellationToken)
    {
        _ = site;
        _ = token;
        using var request = new HttpRequestMessage(HttpMethod.Get, exactContentLocation);
        var startedAtUtc = timeProvider.GetUtcNow();
        HttpResponseMessage? response = null;
        string? responseBody = null;
        try
        {
            response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
            responseBody = await ReadSmallBodyAsync(response.Content, cancellationToken).ConfigureAwait(false);
            var exchanges = new[]
            {
                HttpExchangeCapture.Create("Cleanup verification", startedAtUtc, timeProvider.GetUtcNow(), request, response, responseBody: responseBody),
            };
            return response.StatusCode switch
            {
                HttpStatusCode.NotFound or HttpStatusCode.Gone => new CleanupVerificationResult(CleanupVerificationDisposition.VerifiedDeleted, response.StatusCode, null, exchanges),
                HttpStatusCode.OK or HttpStatusCode.Accepted => new CleanupVerificationResult(CleanupVerificationDisposition.StillPresent, response.StatusCode, timeProvider.GetUtcNow().AddSeconds(30), exchanges),
                HttpStatusCode.TooManyRequests => new CleanupVerificationResult(CleanupVerificationDisposition.RetryLater, response.StatusCode, RetryAfterParser.Parse(response.Headers.RetryAfter, timeProvider.GetUtcNow()) ?? timeProvider.GetUtcNow().AddSeconds(60), exchanges),
                _ when (int)response.StatusCode >= 500 => new CleanupVerificationResult(CleanupVerificationDisposition.RetryLater, response.StatusCode, timeProvider.GetUtcNow().AddSeconds(30), exchanges),
                _ => new CleanupVerificationResult(CleanupVerificationDisposition.Unknown, response.StatusCode, null, exchanges),
            };
        }
        catch (HttpRequestException exception)
        {
            var exchange = HttpExchangeCapture.Create("Cleanup verification", startedAtUtc, timeProvider.GetUtcNow(), request, response, responseBody: responseBody, failure: exception.Message);
            response?.Dispose();
            return new CleanupVerificationResult(CleanupVerificationDisposition.RetryLater, exception.StatusCode ?? HttpStatusCode.ServiceUnavailable, timeProvider.GetUtcNow().AddSeconds(30), [exchange]);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            var exchange = HttpExchangeCapture.Create("Cleanup verification", startedAtUtc, timeProvider.GetUtcNow(), request, response, responseBody: responseBody, failure: exception.Message);
            response?.Dispose();
            return new CleanupVerificationResult(CleanupVerificationDisposition.RetryLater, HttpStatusCode.RequestTimeout, timeProvider.GetUtcNow().AddSeconds(30), [exchange]);
        }
        finally
        {
            response?.Dispose();
        }
    }

    private async Task<CleanupResult> SendDeleteAsync(Uri exactContentLocation, VendorSiteContext site, AccessToken token, TimeProvider timeProvider, CancellationToken cancellationToken)
    {
        _ = site;
        _ = token;
        using var request = new HttpRequestMessage(HttpMethod.Delete, exactContentLocation);
        var startedAtUtc = timeProvider.GetUtcNow();
        HttpResponseMessage? response = null;
        string? responseBody = null;
        try
        {
            response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
            responseBody = await ReadSmallBodyAsync(response.Content, cancellationToken).ConfigureAwait(false);
            var exchanges = new[]
            {
                HttpExchangeCapture.Create("Remote DELETE", startedAtUtc, timeProvider.GetUtcNow(), request, response, responseBody: responseBody),
            };
            var success = response.IsSuccessStatusCode || response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone;
            return new CleanupResult(success, response.StatusCode, success ? null : RetryAfterParser.Parse(response.Headers.RetryAfter, timeProvider.GetUtcNow()) ?? timeProvider.GetUtcNow().AddSeconds(30), success ? null : $"DELETE returned {HttpStatusText.Format(response.StatusCode)}.", exchanges);
        }
        catch (HttpRequestException exception)
        {
            var exchange = HttpExchangeCapture.Create("Remote DELETE", startedAtUtc, timeProvider.GetUtcNow(), request, response, responseBody: responseBody, failure: exception.Message);
            response?.Dispose();
            return new CleanupResult(false, exception.StatusCode ?? HttpStatusCode.ServiceUnavailable, timeProvider.GetUtcNow().AddSeconds(30), SecurityRedactor.Redact(exception.Message), [exchange]);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            var exchange = HttpExchangeCapture.Create("Remote DELETE", startedAtUtc, timeProvider.GetUtcNow(), request, response, responseBody: responseBody, failure: exception.Message);
            response?.Dispose();
            return new CleanupResult(false, HttpStatusCode.RequestTimeout, timeProvider.GetUtcNow().AddSeconds(30), SecurityRedactor.Redact(exception.Message), [exchange]);
        }
        finally
        {
            response?.Dispose();
        }
    }

    private static Uri BuildExportEndpoint(BulkExportKickoffRequest request)
    {
        var baseUri = EnsureTrailingSlash(request.Site.FhirBaseUrl);
        var relative = request.ExportType switch
        {
            PluginExportType.System => "$export",
            PluginExportType.Group when !string.IsNullOrWhiteSpace(request.GroupId) => $"Group/{Uri.EscapeDataString(request.GroupId)}/$export",
            PluginExportType.Patient => "Patient/$export",
            _ => throw new InvalidOperationException("A Group export requires a Group ID."),
        };
        var endpoint = new Uri(baseUri, relative);
        var query = new List<string>();
        if (request.SinceUtc is { } since)
        {
            query.Add("_since=" + Uri.EscapeDataString(since.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)));
        }

        if (request.ResourceTypes.Count > 0)
        {
            query.Add("_type=" + Uri.EscapeDataString(string.Join(",", request.ResourceTypes)));
        }

        foreach (var filter in request.TypeFilters)
        {
            query.Add("_typeFilter=" + Uri.EscapeDataString(filter));
        }

        query.Add("_outputFormat=" + Uri.EscapeDataString(request.OutputFormat));
        return new UriBuilder(endpoint) { Query = string.Join("&", query) }.Uri;
    }

    private static void AddFiles(JsonElement root, string propertyName, ManifestFileCategory category, List<ManifestFileContract> output)
    {
        if (!root.TryGetProperty(propertyName, out var array) || array.ValueKind == JsonValueKind.Null)
        {
            return;
        }

        if (array.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException($"Manifest property '{propertyName}' must be an array.");
        }

        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("url", out var urlElement) || !Uri.TryCreate(urlElement.GetString(), UriKind.Absolute, out var uri))
            {
                throw new JsonException($"Manifest '{propertyName}' entry is missing an absolute url.");
            }

            output.Add(new ManifestFileContract(category, GetOptionalString(item, "type"), uri));
        }
    }

    private static string? GetOptionalString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String ? property.GetString() : null;

    private Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        httpClientFactory.CreateClient("VendorApi").SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

    private static Task<string> ReadSmallBodyAsync(HttpContent content, CancellationToken cancellationToken, int maximumBytes = MaximumControlResponseBytes) =>
        BoundedHttpContentReader.ReadAsStringAsync(content, maximumBytes, cancellationToken);

    private static string? GetHeader(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) ? values.FirstOrDefault() :
        response.Content.Headers.TryGetValues(name, out values) ? values.FirstOrDefault() : null;

    private static Uri EnsureTrailingSlash(Uri uri) =>
        uri.AbsoluteUri.EndsWith("/", StringComparison.Ordinal) ? uri : new Uri(uri.AbsoluteUri + '/', UriKind.Absolute);
}
