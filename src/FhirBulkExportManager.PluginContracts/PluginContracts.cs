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

public sealed record AccessToken(string Value, DateTimeOffset ExpiresAtUtc, string TokenType = "Bearer")
{
    public bool NeedsRefresh(DateTimeOffset now, TimeSpan skew) => ExpiresAtUtc <= now.Add(skew);
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
    string? SafeErrorMessage);

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
    string? SafeErrorMessage);

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
    string? SanitizedCapabilityStatementJson);

public enum CleanupVerificationDisposition
{
    VerifiedDeleted,
    StillPresent,
    RetryLater,
    Unknown,
}

public sealed record CleanupResult(bool Succeeded, HttpStatusCode StatusCode, DateTimeOffset? RetryAtUtc, string? SafeMessage);

public sealed record CleanupVerificationResult(CleanupVerificationDisposition Disposition, HttpStatusCode StatusCode, DateTimeOffset? RetryAtUtc);

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
