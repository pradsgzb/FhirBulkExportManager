using System.Net;
using System.Net.Http.Headers;

namespace FhirBulkExportManager.PluginContracts;

public sealed record PluginMetadata(
    string Id,
    string Name,
    Version Version,
    Version MinimumHostVersion,
    Version? MaximumHostVersion,
    string Vendor,
    string Publisher,
    int MaximumDownloadsPerSite = 4);

public sealed record SecretMaterial(string ClientSecret, IReadOnlyDictionary<string, string> AdditionalSecrets);

public sealed record AuthenticationContext(
    string ClientId,
    string TokenEndpoint,
    string? AssertionEndpoint,
    string? Scope,
    string? Audience,
    string AdditionalConfigurationJson,
    SecretMaterial Secrets);

public sealed record HttpHeaderContract(string Name, IReadOnlyList<string> Values);

public sealed record HttpExchangeContract(
    string Operation,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset CompletedAtUtc,
    string RequestMethod,
    string RequestUri,
    IReadOnlyList<HttpHeaderContract> RequestHeaders,
    string? RequestBody,
    int? ResponseStatusCode,
    string? ResponseReasonPhrase,
    IReadOnlyList<HttpHeaderContract> ResponseHeaders,
    string? ResponseBody,
    string? ResponseBodyStorageReference = null,
    string? Failure = null);

public sealed record JobEventMetadataContract(
    string? Progress,
    IReadOnlyList<HttpExchangeContract> HttpExchanges);

[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Design",
    "CA1032:Implement standard exception constructors",
    Justification = "The exchange-aware overload is required; legacy exception serialization is intentionally unsupported.")]
public sealed class RecordedHttpExchangeException : HttpRequestException
{
    public RecordedHttpExchangeException()
        : this("An HTTP exchange failed.")
    {
    }

    public RecordedHttpExchangeException(string message)
        : this(message, null, [])
    {
    }

    public RecordedHttpExchangeException(string message, Exception innerException)
        : this(message, null, [], innerException)
    {
    }

    public RecordedHttpExchangeException(
        string message,
        HttpStatusCode? statusCode,
        IReadOnlyList<HttpExchangeContract> httpExchanges,
        Exception? innerException = null)
        : base(message, innerException, statusCode)
    {
        HttpExchanges = httpExchanges;
    }

    public IReadOnlyList<HttpExchangeContract> HttpExchanges { get; }
}

public sealed record AccessToken(
    string Value,
    DateTimeOffset ExpiresAtUtc,
    string TokenType = "Bearer",
    IReadOnlyList<HttpExchangeContract>? HttpExchanges = null)
{
    public AccessToken(string value, DateTimeOffset expiresAtUtc, string tokenType)
        : this(value, expiresAtUtc, tokenType, null)
    {
    }

    public bool NeedsRefresh(DateTimeOffset now, TimeSpan skew) => ExpiresAtUtc <= now.Add(skew);

    public void Deconstruct(out string value, out DateTimeOffset expiresAtUtc, out string tokenType)
    {
        value = Value;
        expiresAtUtc = ExpiresAtUtc;
        tokenType = TokenType;
    }
}

public sealed record VendorSiteContext(
    Guid VendorId,
    Guid SiteId,
    Uri FhirBaseUrl,
    string? TenantIdentifier,
    string? PracticeCode,
    string VendorConfigurationJson);

public enum PluginExportType
{
    System,
    Group,
    Patient,
}

public sealed record BulkExportKickoffRequest(
    Guid JobId,
    Guid OperationId,
    VendorSiteContext Site,
    PluginExportType ExportType,
    string? GroupId,
    DateTimeOffset? SinceUtc,
    IReadOnlyList<string> ResourceTypes,
    IReadOnlyList<string> TypeFilters,
    string OutputFormat,
    string VendorOptionsJson,
    string CorrelationId);

public enum KickoffDisposition
{
    Accepted,
    Rejected,
    Ambiguous,
    RetryLater,
}

public sealed record KickoffResult(
    KickoffDisposition Disposition,
    HttpStatusCode? StatusCode,
    Uri? ExactContentLocation,
    DateTimeOffset? RetryAtUtc,
    string RequestUri,
    string? Progress,
    string? OperationOutcomeJson,
    string? ErrorCode,
    string? SafeErrorMessage,
    IReadOnlyList<HttpExchangeContract>? HttpExchanges = null)
{
    public KickoffResult(
        KickoffDisposition disposition,
        HttpStatusCode? statusCode,
        Uri? exactContentLocation,
        DateTimeOffset? retryAtUtc,
        string requestUri,
        string? progress,
        string? operationOutcomeJson,
        string? errorCode,
        string? safeErrorMessage)
        : this(disposition, statusCode, exactContentLocation, retryAtUtc, requestUri, progress, operationOutcomeJson, errorCode, safeErrorMessage, null)
    {
    }

    public void Deconstruct(
        out KickoffDisposition disposition,
        out HttpStatusCode? statusCode,
        out Uri? exactContentLocation,
        out DateTimeOffset? retryAtUtc,
        out string requestUri,
        out string? progress,
        out string? operationOutcomeJson,
        out string? errorCode,
        out string? safeErrorMessage)
    {
        disposition = Disposition;
        statusCode = StatusCode;
        exactContentLocation = ExactContentLocation;
        retryAtUtc = RetryAtUtc;
        requestUri = RequestUri;
        progress = Progress;
        operationOutcomeJson = OperationOutcomeJson;
        errorCode = ErrorCode;
        safeErrorMessage = SafeErrorMessage;
    }
}

public enum PollDisposition
{
    Running,
    Complete,
    RetryLater,
    AuthenticationRequired,
    AuthorizationDenied,
    Expired,
    NotFound,
    FailedPermanent,
    Unknown,
}

public sealed record PollResult(
    PollDisposition Disposition,
    HttpStatusCode StatusCode,
    DateTimeOffset? RetryAtUtc,
    string? Progress,
    string? ExactManifestJson,
    string? OperationOutcomeJson,
    string? ErrorCode,
    string? SafeErrorMessage,
    IReadOnlyList<HttpExchangeContract>? HttpExchanges = null)
{
    public PollResult(
        PollDisposition disposition,
        HttpStatusCode statusCode,
        DateTimeOffset? retryAtUtc,
        string? progress,
        string? exactManifestJson,
        string? operationOutcomeJson,
        string? errorCode,
        string? safeErrorMessage)
        : this(disposition, statusCode, retryAtUtc, progress, exactManifestJson, operationOutcomeJson, errorCode, safeErrorMessage, null)
    {
    }

    public void Deconstruct(
        out PollDisposition disposition,
        out HttpStatusCode statusCode,
        out DateTimeOffset? retryAtUtc,
        out string? progress,
        out string? exactManifestJson,
        out string? operationOutcomeJson,
        out string? errorCode,
        out string? safeErrorMessage)
    {
        disposition = Disposition;
        statusCode = StatusCode;
        retryAtUtc = RetryAtUtc;
        progress = Progress;
        exactManifestJson = ExactManifestJson;
        operationOutcomeJson = OperationOutcomeJson;
        errorCode = ErrorCode;
        safeErrorMessage = SafeErrorMessage;
    }
}

public enum ManifestFileCategory
{
    Output,
    Deleted,
    Error,
}

public sealed record ManifestFileContract(
    ManifestFileCategory Category,
    string? ResourceType,
    Uri RemoteUri,
    long? ExpectedBytes = null);

public sealed record NormalizedManifest(
    string ExactJson,
    string? TransactionTime,
    string? Request,
    bool RequiresAccessToken,
    IReadOnlyList<ManifestFileContract> Files);

public sealed record ConnectionTestResult(
    bool Succeeded,
    HttpStatusCode? StatusCode,
    string Summary,
    string? SanitizedCapabilityStatementJson,
    IReadOnlyList<HttpExchangeContract>? HttpExchanges = null)
{
    public ConnectionTestResult(
        bool succeeded,
        HttpStatusCode? statusCode,
        string summary,
        string? sanitizedCapabilityStatementJson)
        : this(succeeded, statusCode, summary, sanitizedCapabilityStatementJson, null)
    {
    }

    public void Deconstruct(
        out bool succeeded,
        out HttpStatusCode? statusCode,
        out string summary,
        out string? sanitizedCapabilityStatementJson)
    {
        succeeded = Succeeded;
        statusCode = StatusCode;
        summary = Summary;
        sanitizedCapabilityStatementJson = SanitizedCapabilityStatementJson;
    }
}

public enum CleanupVerificationDisposition
{
    VerifiedDeleted,
    StillPresent,
    RetryLater,
    Unknown,
}

public sealed record CleanupResult(
    bool Succeeded,
    HttpStatusCode StatusCode,
    DateTimeOffset? RetryAtUtc,
    string? SafeMessage,
    IReadOnlyList<HttpExchangeContract>? HttpExchanges = null)
{
    public CleanupResult(bool succeeded, HttpStatusCode statusCode, DateTimeOffset? retryAtUtc, string? safeMessage)
        : this(succeeded, statusCode, retryAtUtc, safeMessage, null)
    {
    }

    public void Deconstruct(out bool succeeded, out HttpStatusCode statusCode, out DateTimeOffset? retryAtUtc, out string? safeMessage)
    {
        succeeded = Succeeded;
        statusCode = StatusCode;
        retryAtUtc = RetryAtUtc;
        safeMessage = SafeMessage;
    }
}

public sealed record CleanupVerificationResult(
    CleanupVerificationDisposition Disposition,
    HttpStatusCode StatusCode,
    DateTimeOffset? RetryAtUtc,
    IReadOnlyList<HttpExchangeContract>? HttpExchanges = null)
{
    public CleanupVerificationResult(
        CleanupVerificationDisposition disposition,
        HttpStatusCode statusCode,
        DateTimeOffset? retryAtUtc)
        : this(disposition, statusCode, retryAtUtc, null)
    {
    }

    public void Deconstruct(
        out CleanupVerificationDisposition disposition,
        out HttpStatusCode statusCode,
        out DateTimeOffset? retryAtUtc)
    {
        disposition = Disposition;
        statusCode = StatusCode;
        retryAtUtc = RetryAtUtc;
    }
}

public interface IVendorPlugin
{
    PluginMetadata Metadata { get; }

    Task<AccessToken> AuthenticateAsync(AuthenticationContext context, TimeProvider timeProvider, CancellationToken cancellationToken);

    Task<ConnectionTestResult> TestConnectionAsync(VendorSiteContext site, AccessToken? token, CancellationToken cancellationToken);

    Uri BuildKickoffUri(BulkExportKickoffRequest request);

    Task<KickoffResult> KickoffAsync(BulkExportKickoffRequest request, AccessToken token, TimeProvider timeProvider, CancellationToken cancellationToken);

    Task<PollResult> PollAsync(Uri exactContentLocation, VendorSiteContext site, AccessToken token, TimeProvider timeProvider, CancellationToken cancellationToken);

    NormalizedManifest NormalizeManifest(string exactManifestJson);

    HttpRequestMessage CreateDownloadRequest(Uri remoteUri, Uri exactStatusUri, AccessToken? token, long? resumeOffset, EntityTagHeaderValue? ifRangeEtag, DateTimeOffset? ifRangeLastModified);

    Task<CleanupResult> CancelAsync(Uri exactContentLocation, VendorSiteContext site, AccessToken token, TimeProvider timeProvider, CancellationToken cancellationToken);

    Task<CleanupResult> CleanupAsync(Uri exactContentLocation, VendorSiteContext site, AccessToken token, TimeProvider timeProvider, CancellationToken cancellationToken);

    Task<CleanupVerificationResult> VerifyCleanupAsync(Uri exactContentLocation, VendorSiteContext site, AccessToken token, TimeProvider timeProvider, CancellationToken cancellationToken);
}

public interface IExportPostProcessor
{
    string Id { get; }

    Task ProcessAsync(Guid jobId, string committedExportDirectory, CancellationToken cancellationToken);
}

public sealed record PluginManifest(
    string Id,
    string Name,
    string Version,
    string MinimumHostVersion,
    string? MaximumHostVersion,
    string EntryAssembly,
    string Vendor,
    string Publisher,
    string? Sha256,
    bool Enabled = true);
