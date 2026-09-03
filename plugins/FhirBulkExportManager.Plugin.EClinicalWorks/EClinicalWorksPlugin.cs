using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using FhirBulkExportManager.PluginContracts;
using FhirBulkExportManager.Shared;

namespace FhirBulkExportManager.Plugin.EClinicalWorks;

public sealed class EClinicalWorksPlugin(IHttpClientFactory httpClientFactory) : IVendorPlugin
{
    private const int MaximumControlResponseBytes = 2 * 1024 * 1024;
    private const int MaximumMetadataResponseBytes = 16 * 1024 * 1024;
    private static readonly MediaTypeWithQualityHeaderValue FhirJson = new("application/fhir+json");
    private static readonly MediaTypeWithQualityHeaderValue Json = new("application/json");

    public PluginMetadata Metadata { get; } = new(
        "eclinicalworks",
        "eClinicalWorks Bulk Data",
        new Version(1, 0, 0),
        new Version(1, 0, 0),
        null,
        "eClinicalWorks",
        "FHIR Bulk Export Manager Engineering",
        MaximumDownloadsPerSite: 4);

    public async Task<AccessToken> AuthenticateAsync(AuthenticationContext context, TimeProvider timeProvider, CancellationToken cancellationToken)
    {
        var configuration = DeserializeAuthenticationConfiguration(context.AdditionalConfigurationJson);
        var exchanges = new List<HttpExchangeContract>();
        try
        {
        string? assertion = null;
        if (!string.IsNullOrWhiteSpace(context.AssertionEndpoint))
        {
            var assertionFields = configuration.AssertionRequestFields.Count == 0
                ? new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["clientId"] = context.ClientId,
                    ["aud"] = GetAssertionAudience(context),
                }
                : configuration.AssertionRequestFields.ToDictionary(
                    x => x.Key,
                    x => ResolveTemplate(x.Value, context),
                    StringComparer.Ordinal);
            var assertionRequestBody = JsonSerializer.Serialize(assertionFields, JsonDefaults.Options);
            using var assertionRequest = new HttpRequestMessage(HttpMethod.Post, RequireHttpsOrLoopback(context.AssertionEndpoint))
            {
                Content = new StringContent(assertionRequestBody, Encoding.UTF8),
            };
            assertionRequest.Content!.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            var assertionStarted = timeProvider.GetUtcNow();
            using var assertionResponse = await SendAuthenticationRequestAsync(
                "HealthWizz assertion",
                assertionRequest,
                RedactKnownValues(assertionRequestBody, GetConfiguredSecrets(context)),
                assertionStarted,
                timeProvider,
                exchanges,
                cancellationToken).ConfigureAwait(false);
            var assertionPayload = assertionResponse.Body;
            assertion = ReadAssertion(assertionPayload, configuration.AssertionResponseProperty);
        }

        var tokenFields = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["grant_type"] = configuration.GrantType,
        };
        if (!string.IsNullOrWhiteSpace(context.Scope))
        {
            tokenFields["scope"] = context.Scope;
        }

        if (assertion is not null)
        {
            tokenFields["client_assertion_type"] = configuration.ClientAssertionType;
            tokenFields["client_assertion"] = assertion;
        }
        else
        {
            if (string.IsNullOrWhiteSpace(context.Secrets.ClientSecret))
            {
                throw new InvalidOperationException("A client secret is required when no HealthWizz assertion endpoint is configured.");
            }

            tokenFields["client_id"] = context.ClientId;
            tokenFields["client_secret"] = context.Secrets.ClientSecret;
        }

        foreach (var field in configuration.TokenAdditionalFields)
        {
            tokenFields[field.Key] = ResolveTemplate(field.Value, context);
        }

        var tokenRequestBody = EncodeForm(tokenFields);
        using var tokenRequest = new HttpRequestMessage(HttpMethod.Post, RequireHttpsOrLoopback(context.TokenEndpoint))
        {
            Content = new FormUrlEncodedContent(tokenFields),
        };
        var tokenStarted = timeProvider.GetUtcNow();
        using var tokenResponse = await SendAuthenticationRequestAsync(
            "OAuth token",
            tokenRequest,
            RedactKnownValues(tokenRequestBody, GetConfiguredSecrets(context).Append(assertion ?? string.Empty)),
            tokenStarted,
            timeProvider,
            exchanges,
            cancellationToken).ConfigureAwait(false);
        var tokenJson = tokenResponse.Body;
        using var tokenDocument = JsonDocument.Parse(tokenJson);
        if (!tokenDocument.RootElement.TryGetProperty("access_token", out var accessTokenElement) || string.IsNullOrWhiteSpace(accessTokenElement.GetString()))
        {
            throw new InvalidOperationException("OAuth response did not include access_token.");
        }

        var expiresIn = tokenDocument.RootElement.TryGetProperty("expires_in", out var expiresElement) && expiresElement.TryGetInt32(out var parsedExpires)
            ? Math.Clamp(parsedExpires, 30, 86400)
            : 300;
        var tokenType = tokenDocument.RootElement.TryGetProperty("token_type", out var tokenTypeElement)
            ? tokenTypeElement.GetString() ?? "Bearer"
            : "Bearer";
        return new AccessToken(accessTokenElement.GetString()!, timeProvider.GetUtcNow().AddSeconds(expiresIn), tokenType, exchanges);
        }
        catch (RecordedHttpExchangeException)
        {
            throw;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            throw new RecordedHttpExchangeException(
                SecurityRedactor.Redact(exception.Message),
                null,
                exchanges.ToArray(),
                exception);
        }
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
                ? new ConnectionTestResult(true, response.StatusCode, "FHIR metadata request succeeded without authentication.", body, exchanges, tokenEndpoint)
                : new ConnectionTestResult(false, response.StatusCode, $"FHIR metadata request returned {HttpStatusText.Format(response.StatusCode)}.", null, exchanges);
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
                "FHIR metadata request failed: " + SecurityRedactor.Redact(exception.Message),
                null,
                [exchange]);
        }
        finally
        {
            response?.Dispose();
        }
    }

    public async Task<KickoffResult> KickoffAsync(BulkExportKickoffRequest request, AccessToken token, TimeProvider timeProvider, CancellationToken cancellationToken)
    {
        var endpoint = BuildKickoffUri(request);
        using var message = CreateAuthorizedRequest(HttpMethod.Get, endpoint, token);
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

    public Uri BuildKickoffUri(BulkExportKickoffRequest request) => BuildExportEndpoint(request);

    public async Task<PollResult> PollAsync(Uri exactContentLocation, VendorSiteContext site, AccessToken token, TimeProvider timeProvider, CancellationToken cancellationToken)
    {
        _ = site;
        using var request = CreateAuthorizedRequest(HttpMethod.Get, exactContentLocation, token);
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
                    return new PollResult(PollDisposition.AuthenticationRequired, response.StatusCode, now, progress, null, responseBody, "Unauthorized", "Access token was rejected.", exchanges);
                case HttpStatusCode.Forbidden:
                    return new PollResult(PollDisposition.AuthorizationDenied, response.StatusCode, null, progress, null, responseBody, "Forbidden", "FHIR server denied the required scope or permission.", exchanges);
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
        var request = new HttpRequestMessage(HttpMethod.Get, remoteUri);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/fhir+ndjson"));
        if (token is not null && SameOrigin(remoteUri, exactStatusUri))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue(token.TokenType, token.Value);
        }

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
        using var request = CreateAuthorizedRequest(HttpMethod.Get, exactContentLocation, token);
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
                HttpStatusCode.NotFound => new CleanupVerificationResult(CleanupVerificationDisposition.VerifiedDeleted, response.StatusCode, null, exchanges),
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
        using var request = CreateAuthorizedRequest(HttpMethod.Delete, exactContentLocation, token);
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
            var success = response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.NotFound;
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
        var builder = new UriBuilder(endpoint) { Query = string.Join("&", query) };
        return builder.Uri;
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

    private static EcwAuthenticationConfiguration DeserializeAuthenticationConfiguration(string json) =>
        JsonSerializer.Deserialize<EcwAuthenticationConfiguration>(string.IsNullOrWhiteSpace(json) ? "{}" : json, JsonDefaults.Options) ?? new EcwAuthenticationConfiguration();

    private static string GetAssertionAudience(AuthenticationContext context) =>
        string.IsNullOrWhiteSpace(context.Audience) ? context.TokenEndpoint : context.Audience;

    private static string ReadAssertion(string payload, string responseProperty)
    {
        var value = payload.Trim();
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException("HealthWizz assertion response was empty.");
        }

        if (!value.StartsWith('{') && !value.StartsWith('"'))
        {
            return value;
        }

        using var document = JsonDocument.Parse(value);
        if (document.RootElement.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(document.RootElement.GetString()))
        {
            return document.RootElement.GetString()!;
        }

        if (document.RootElement.ValueKind == JsonValueKind.Object &&
            document.RootElement.TryGetProperty(responseProperty, out var assertionElement) &&
            assertionElement.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(assertionElement.GetString()))
        {
            return assertionElement.GetString()!;
        }

        throw new InvalidOperationException($"HealthWizz assertion response was neither a raw JWT nor a JSON string property named '{responseProperty}'.");
    }

    private static string ResolveTemplate(string template, AuthenticationContext context)
    {
        var result = template
            .Replace("${clientId}", context.ClientId, StringComparison.Ordinal)
            .Replace("${clientSecret}", context.Secrets.ClientSecret, StringComparison.Ordinal)
            .Replace("${scope}", context.Scope ?? string.Empty, StringComparison.Ordinal)
            .Replace("${audience}", GetAssertionAudience(context), StringComparison.Ordinal);
        foreach (var secret in context.Secrets.AdditionalSecrets)
        {
            result = result.Replace("${secret:" + secret.Key + "}", secret.Value, StringComparison.Ordinal);
        }

        return result;
    }

    private static HttpRequestMessage CreateAuthorizedRequest(HttpMethod method, Uri uri, AccessToken token)
    {
        var request = new HttpRequestMessage(method, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue(token.TokenType, token.Value);
        return request;
    }

    private async Task<CapturedAuthenticationResponse> SendAuthenticationRequestAsync(
        string operation,
        HttpRequestMessage request,
        string requestBody,
        DateTimeOffset startedAtUtc,
        TimeProvider timeProvider,
        List<HttpExchangeContract> exchanges,
        CancellationToken cancellationToken)
    {
        HttpResponseMessage? response = null;
        string? responseBody = null;
        try
        {
            response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
            responseBody = await ReadSmallBodyAsync(response.Content, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            response?.Dispose();
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or OperationCanceledException)
        {
            exchanges.Add(HttpExchangeCapture.Create(
                operation,
                startedAtUtc,
                timeProvider.GetUtcNow(),
                request,
                response,
                requestBody,
                responseBody,
                failure: exception.Message));
            var statusCode = response?.StatusCode ?? (exception as HttpRequestException)?.StatusCode;
            response?.Dispose();
            throw new RecordedHttpExchangeException(
                SecurityRedactor.Redact(exception.Message),
                statusCode,
                exchanges.ToArray(),
                exception);
        }

        var completedResponse = response ?? throw new InvalidOperationException("Authentication request completed without an HTTP response.");
        var completedBody = responseBody ?? string.Empty;
        exchanges.Add(HttpExchangeCapture.Create(
            operation,
            startedAtUtc,
            timeProvider.GetUtcNow(),
            request,
            completedResponse,
            requestBody,
            completedBody));
        if (!completedResponse.IsSuccessStatusCode)
        {
            var statusCode = completedResponse.StatusCode;
            var reason = completedResponse.ReasonPhrase;
            completedResponse.Dispose();
            throw new RecordedHttpExchangeException(
                $"{operation} returned {(int)statusCode} {reason}.",
                statusCode,
                exchanges.ToArray());
        }

        return new CapturedAuthenticationResponse(completedResponse, completedBody);
    }

    private static string EncodeForm(IReadOnlyDictionary<string, string> fields) =>
        string.Join("&", fields.Select(field => Uri.EscapeDataString(field.Key) + "=" + Uri.EscapeDataString(field.Value)));

    private static IEnumerable<string> GetConfiguredSecrets(AuthenticationContext context)
    {
        if (!string.IsNullOrEmpty(context.Secrets.ClientSecret))
        {
            yield return context.Secrets.ClientSecret;
        }

        foreach (var value in context.Secrets.AdditionalSecrets.Values)
        {
            if (!string.IsNullOrEmpty(value))
            {
                yield return value;
            }
        }
    }

    private static string RedactKnownValues(string value, IEnumerable<string> secrets)
    {
        var redacted = value;
        foreach (var secret in secrets.Where(secret => !string.IsNullOrEmpty(secret)).Distinct(StringComparer.Ordinal))
        {
            redacted = redacted.Replace(secret, "[REDACTED]", StringComparison.Ordinal);
            redacted = redacted.Replace(Uri.EscapeDataString(secret), Uri.EscapeDataString("[REDACTED]"), StringComparison.Ordinal);
        }

        return redacted;
    }

    private Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        httpClientFactory.CreateClient("VendorApi").SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

    private static Task<string> ReadSmallBodyAsync(HttpContent content, CancellationToken cancellationToken, int maximumBytes = MaximumControlResponseBytes) =>
        BoundedHttpContentReader.ReadAsStringAsync(content, maximumBytes, cancellationToken);

    private static string? GetHeader(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) ? values.FirstOrDefault() :
        response.Content.Headers.TryGetValues(name, out values) ? values.FirstOrDefault() : null;

    private static Uri EnsureTrailingSlash(Uri uri) => uri.AbsoluteUri.EndsWith("/", StringComparison.Ordinal) ? uri : new Uri(uri.AbsoluteUri + '/', UriKind.Absolute);

    private static Uri RequireHttpsOrLoopback(string value)
    {
        var uri = new Uri(value, UriKind.Absolute);
        if (uri.Scheme != Uri.UriSchemeHttps && !(uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback))
        {
            throw new InvalidOperationException("Authentication endpoints require HTTPS; HTTP is permitted only for loopback integration tests.");
        }

        return uri;
    }

    private static bool SameOrigin(Uri first, Uri second) =>
        string.Equals(first.Scheme, second.Scheme, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(first.Host, second.Host, StringComparison.OrdinalIgnoreCase) &&
        first.Port == second.Port;

    private sealed class EcwAuthenticationConfiguration
    {
        public string GrantType { get; set; } = "client_credentials";
        public string ClientAssertionType { get; set; } = "urn:ietf:params:oauth:client-assertion-type:jwt-bearer";
        public string AssertionResponseProperty { get; set; } = "client_assertion";
        public Dictionary<string, string> AssertionRequestFields { get; set; } = new(StringComparer.Ordinal);
        public Dictionary<string, string> TokenAdditionalFields { get; set; } = new(StringComparer.Ordinal);
    }

    private sealed class CapturedAuthenticationResponse(HttpResponseMessage response, string body) : IDisposable
    {
        public string Body { get; } = body;

        public void Dispose() => response.Dispose();
    }
}
