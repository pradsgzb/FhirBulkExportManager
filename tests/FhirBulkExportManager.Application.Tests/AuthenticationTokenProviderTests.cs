using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using FhirBulkExportManager.Application.Abstractions;
using FhirBulkExportManager.Application.Configuration;
using FhirBulkExportManager.Application.Models;
using FhirBulkExportManager.Application.Services;
using FhirBulkExportManager.Domain;
using FhirBulkExportManager.PluginContracts;
using FhirBulkExportManager.Worker;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace FhirBulkExportManager.Application.Tests;

public sealed class AuthenticationTokenProviderTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse(
        "2026-09-03T15:26:18Z",
        System.Globalization.CultureInfo.InvariantCulture);

    [Fact]
    public async Task Forced_refresh_bypasses_an_unexpired_cached_token()
    {
        var credential = CreateCredential("ClientAssertion");
        var plugin = new CountingPlugin((attempt, exchanges) =>
            new AccessToken($"token-{attempt}", Now.AddMinutes(5), "Bearer", exchanges));
        var provider = new AuthenticationTokenProvider(
            new CredentialOnlyConfigurationStore(credential),
            new EmptySecretStore(),
            new FixedTimeProvider(Now));
        var job = CreateJob(credential);

        var first = await provider.GetAsync(job, plugin, CancellationToken.None);
        var cached = await provider.GetAsync(job, plugin, CancellationToken.None);
        var refreshed = await provider.GetAsync(job, plugin, CancellationToken.None, forceRefresh: true);

        Assert.Equal(2, plugin.AuthenticationCount);
        Assert.Equal("token-1", first.Value);
        Assert.Equal("token-1", cached.Value);
        Assert.Null(cached.HttpExchanges);
        Assert.Equal("token-2", refreshed.Value);
        Assert.Single(refreshed.HttpExchanges!);
    }

    [Fact]
    public async Task Authenticated_profile_rejects_an_empty_access_token()
    {
        var credential = CreateCredential("ClientAssertion");
        var plugin = new CountingPlugin((_, exchanges) =>
            new AccessToken(string.Empty, Now.AddMinutes(5), "Bearer", exchanges));
        var provider = new AuthenticationTokenProvider(
            new CredentialOnlyConfigurationStore(credential),
            new EmptySecretStore(),
            new FixedTimeProvider(Now));

        var exception = await Assert.ThrowsAsync<RecordedHttpExchangeException>(() =>
            provider.GetAsync(CreateJob(credential), plugin, CancellationToken.None));

        Assert.Contains("empty access token", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Single(exception.HttpExchanges);
    }

    [Fact]
    public async Task Anonymous_profile_allows_the_public_plugin_empty_token_sentinel()
    {
        var credential = CreateCredential("None");
        var plugin = new CountingPlugin((_, exchanges) =>
            new AccessToken(string.Empty, DateTimeOffset.MaxValue, "Bearer", exchanges));
        var provider = new AuthenticationTokenProvider(
            new CredentialOnlyConfigurationStore(credential),
            new EmptySecretStore(),
            new FixedTimeProvider(Now));

        var token = await provider.GetAsync(CreateJob(credential), plugin, CancellationToken.None);

        Assert.Empty(token.Value);
        Assert.Equal(1, plugin.AuthenticationCount);
    }

    [Fact]
    public async Task Import_rejects_a_credential_not_assigned_to_the_selected_site()
    {
        var vendor = new Vendor(Guid.NewGuid(), "eClinicalWorks", "test-plugin", "1.0.0", Now);
        var assignedCredentialId = Guid.NewGuid();
        var selectedCredential = new CredentialProfile(
            Guid.NewGuid(),
            vendor.Id,
            "Different site credential",
            "ClientAssertion",
            "client-id",
            null,
            "https://oauth.example.test/token",
            Now,
            assertionEndpoint: "https://assertion.example.test/token");
        var site = new Site(
            Guid.NewGuid(),
            vendor.Id,
            "Imported export site",
            new Uri("https://fhir.example.test/r4/site"),
            assignedCredentialId,
            Now);
        var configurationStore = new CredentialOnlyConfigurationStore(selectedCredential, vendor, site);
        var service = new ExportJobService(
            null!,
            configurationStore,
            null!,
            null!,
            null!,
            new FixedTimeProvider(Now));
        var command = new ImportExistingExportCommand(
            "Existing export",
            null,
            vendor.Id,
            site.Id,
            selectedCredential.Id,
            "https://fhir.example.test/status?job=1",
            Now.AddMinutes(-5),
            ExportType.Group,
            "group-1",
            null,
            [],
            [],
            "application/fhir+ndjson",
            "{}",
            "C:\\Exports",
            false,
            JobPriority.Normal);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ImportExistingAsync(command, CancellationToken.None));

        Assert.Contains("credential profile assigned", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Import_rejects_a_content_location_from_a_different_connection_origin()
    {
        var credential = CreateCredential("ClientAssertion");
        var vendor = new Vendor(credential.VendorId, "eClinicalWorks", "test-plugin", "1.0.0", Now);
        var site = CreateSite(credential);
        var plugin = new CountingPlugin((attempt, exchanges) =>
            new AccessToken($"token-{attempt}", Now.AddMinutes(5), "Bearer", exchanges));
        var service = new ExportJobService(
            null!,
            new CredentialOnlyConfigurationStore(credential, vendor, site),
            new SinglePluginCatalog(plugin),
            null!,
            null!,
            new FixedTimeProvider(Now));
        var command = new ImportExistingExportCommand(
            "Existing export",
            null,
            vendor.Id,
            site.Id,
            credential.Id,
            "https://different-fhir.example.test/status?job=1",
            Now.AddMinutes(-5),
            ExportType.Group,
            "group-1",
            null,
            [],
            [],
            "application/fhir+ndjson",
            "{}",
            "C:\\Exports",
            false,
            JobPriority.Normal);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ImportExistingAsync(command, CancellationToken.None));

        Assert.Contains("does not match the selected connection origin", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Existing_import_can_be_reassigned_to_the_correct_authenticated_connection()
    {
        var credential = CreateCredential("ClientAssertion");
        var vendor = new Vendor(credential.VendorId, "eClinicalWorks", "test-plugin", "1.0.0", Now);
        var site = CreateSite(credential);
        var job = new ExportJob(
            Guid.NewGuid(),
            "Imported with wrong vendor",
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            "public-fhir-r4",
            "1.0.0",
            ExportType.Group,
            JobPriority.Normal,
            "C:\\Exports",
            Now,
            groupId: "group-1");
        const string exactContentLocation = "https://fhir.example.test/r4/site/$export-poll-location?job_id=opaque";
        job.ConfirmRemoteJob(exactContentLocation, Now, Now);
        Transition(job, ExportJobState.WaitingForPoll);
        Transition(job, ExportJobState.Running);
        job.ScheduleRetry(Now, "Unauthorized", "Anonymous request was rejected.");
        Transition(job, ExportJobState.RetryPending);
        var exportStore = new RecordingExportStore(job);
        var configurationStore = new CredentialOnlyConfigurationStore(credential, vendor, site);
        var plugin = new CountingPlugin((attempt, exchanges) =>
            new AccessToken($"token-{attempt}", Now.AddMinutes(5), "Bearer", exchanges));
        var signal = new RecordingWorkSignal();
        var service = new ExportJobService(
            exportStore,
            configurationStore,
            new SinglePluginCatalog(plugin),
            new TestIdentityProvider(),
            signal,
            new FixedTimeProvider(Now.AddMinutes(1)));

        var result = await service.ReassignImportedConnectionAsync(
            job.Id,
            vendor.Id,
            site.Id,
            credential.Id,
            CancellationToken.None);

        Assert.Equal(ExportJobState.Running, result.State);
        Assert.Equal(vendor.Id, job.VendorId);
        Assert.Equal(site.Id, job.SiteId);
        Assert.Equal(credential.Id, job.CredentialProfileId);
        Assert.Equal("test-plugin", job.PluginId);
        Assert.Equal(exactContentLocation, job.RemoteStatusUrl);
        Assert.Null(job.RemoteRequestUrl);
        Assert.Null(job.LastErrorCode);
        Assert.Equal(0, job.RetryCount);
        Assert.Equal(1, signal.SignalCount);
        Assert.NotNull(exportStore.LastEvent);
        Assert.Equal("ImportedConnectionReassigned", exportStore.LastEvent!.EventType);
    }

    [Fact]
    public async Task First_poll_of_an_imported_export_forces_fresh_authentication()
    {
        var credential = CreateCredential("ClientAssertion");
        var site = CreateSite(credential);
        var job = CreateRunningJob(credential, site, imported: true);
        var plugin = new CountingPlugin(
            (attempt, exchanges) => new AccessToken($"token-{attempt}", Now.AddMinutes(5), "Bearer", exchanges),
            (_, token) => RunningPoll(token));
        var configurationStore = new CredentialOnlyConfigurationStore(credential, site: site);
        var tokenProvider = new AuthenticationTokenProvider(
            configurationStore,
            new EmptySecretStore(),
            new FixedTimeProvider(Now));
        _ = await tokenProvider.GetAsync(job, plugin, CancellationToken.None);
        var exportStore = new RecordingExportStore(job);
        var processor = CreateProcessor(exportStore, configurationStore, plugin, tokenProvider);

        await processor.ProcessAsync(job.Id, CancellationToken.None);

        Assert.Equal(2, plugin.AuthenticationCount);
        Assert.Equal(new[] { "token-2" }, plugin.PollTokens);
        Assert.Equal(ExportJobState.WaitingForPoll, job.State);
        Assert.NotNull(exportStore.LastEvent);
        Assert.Equal(2, ReadHttpExchangeCount(exportStore.LastEvent!.MetadataJson));
    }

    [Fact]
    public async Task Rejected_cached_token_is_refreshed_and_status_is_retried_once()
    {
        var credential = CreateCredential("ClientAssertion");
        var site = CreateSite(credential);
        var job = CreateRunningJob(credential, site, imported: false);
        var plugin = new CountingPlugin(
            (attempt, exchanges) => new AccessToken($"token-{attempt}", Now.AddMinutes(5), "Bearer", exchanges),
            (attempt, token) => attempt == 1
                ? UnauthorizedPoll(token)
                : RunningPoll(token));
        var configurationStore = new CredentialOnlyConfigurationStore(credential, site: site);
        var tokenProvider = new AuthenticationTokenProvider(
            configurationStore,
            new EmptySecretStore(),
            new FixedTimeProvider(Now));
        _ = await tokenProvider.GetAsync(job, plugin, CancellationToken.None);
        var exportStore = new RecordingExportStore(job);
        var processor = CreateProcessor(exportStore, configurationStore, plugin, tokenProvider);

        await processor.ProcessAsync(job.Id, CancellationToken.None);

        Assert.Equal(2, plugin.AuthenticationCount);
        Assert.Equal(2, plugin.PollCount);
        Assert.Equal(new[] { "token-1", "token-2" }, plugin.PollTokens);
        Assert.Equal(ExportJobState.WaitingForPoll, job.State);
        Assert.NotNull(exportStore.LastEvent);
        Assert.Equal(3, ReadHttpExchangeCount(exportStore.LastEvent!.MetadataJson));
    }

    private static ExportJobProcessor CreateProcessor(
        IExportStore exportStore,
        IConfigurationStore configurationStore,
        IVendorPlugin plugin,
        AuthenticationTokenProvider tokenProvider)
    {
        var timeProvider = new FixedTimeProvider(Now);
        return new ExportJobProcessor(
            exportStore,
            configurationStore,
            new SinglePluginCatalog(plugin),
            tokenProvider,
            null!,
            null!,
            null!,
            new SiteResilienceGate(timeProvider, Options.Create(new HttpOptions())),
            new TestIdentityProvider(),
            Options.Create(new ProcessingOptions()),
            timeProvider,
            NullLogger<ExportJobProcessor>.Instance);
    }

    private static Site CreateSite(CredentialProfile credential) =>
        new(
            Guid.NewGuid(),
            credential.VendorId,
            "Test site",
            new Uri("https://fhir.example.test/r4/site"),
            credential.Id,
            Now);

    private static ExportJob CreateRunningJob(CredentialProfile credential, Site site, bool imported)
    {
        var job = new ExportJob(
            Guid.NewGuid(),
            imported ? "Imported export" : "Locally started export",
            credential.VendorId,
            site.Id,
            credential.Id,
            "test-plugin",
            "1.0.0",
            ExportType.Group,
            JobPriority.Normal,
            "C:\\Exports",
            Now,
            groupId: "group-1");
        if (!imported)
        {
            job.RecordKickoffRequest("https://fhir.example.test/r4/site/Group/group-1/$export");
        }

        job.ConfirmRemoteJob("https://fhir.example.test/r4/site/$export-poll-location?job_id=1", Now, Now);
        Transition(job, ExportJobState.WaitingForPoll);
        Transition(job, ExportJobState.Running);
        return job;
    }

    private static void Transition(ExportJob job, ExportJobState state)
    {
        var operation = Guid.NewGuid();
        _ = job.Transition(state, Now, "Test", "Test", operation, operation.ToString("N"));
    }

    private static PollResult RunningPoll(AccessToken token) =>
        new(
            PollDisposition.Running,
            HttpStatusCode.Accepted,
            Now.AddMinutes(1),
            "17% Completed",
            null,
            null,
            null,
            null,
            [CreateExchange("Bulk export status", token.Value, HttpStatusCode.Accepted)]);

    private static PollResult UnauthorizedPoll(AccessToken token) =>
        new(
            PollDisposition.AuthenticationRequired,
            HttpStatusCode.Unauthorized,
            Now,
            null,
            null,
            "{\"resourceType\":\"OperationOutcome\"}",
            "Unauthorized",
            "Access token was rejected.",
            [CreateExchange("Bulk export status", token.Value, HttpStatusCode.Unauthorized)]);

    private static HttpExchangeContract CreateExchange(string operation, string token, HttpStatusCode statusCode) =>
        new(
            operation,
            Now,
            Now,
            operation == "OAuth token" ? "POST" : "GET",
            operation == "OAuth token"
                ? "https://oauth.example.test/token"
                : "https://fhir.example.test/r4/site/$export-poll-location?job_id=1",
            string.IsNullOrEmpty(token) ? [] : [new HttpHeaderContract("Authorization", ["[REDACTED]"])],
            null,
            (int)statusCode,
            statusCode == HttpStatusCode.Unauthorized ? "Unauthorized" : "Accepted",
            [],
            null);

    private static int ReadHttpExchangeCount(string metadataJson)
    {
        using var document = JsonDocument.Parse(metadataJson);
        return document.RootElement.GetProperty("httpExchanges").GetArrayLength();
    }

    private static CredentialProfile CreateCredential(string authenticationType) =>
        new(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "Test credential",
            authenticationType,
            authenticationType == "None" ? null : "client-id",
            null,
            authenticationType == "None" ? null : "https://oauth.example.test/token",
            Now,
            assertionEndpoint: authenticationType == "None" ? null : "https://assertion.example.test/token");

    private static ExportJob CreateJob(CredentialProfile credential) =>
        new(
            Guid.NewGuid(),
            "Imported export",
            credential.VendorId,
            Guid.NewGuid(),
            credential.Id,
            "test-plugin",
            "1.0.0",
            ExportType.Group,
            JobPriority.Normal,
            "C:\\Exports",
            Now,
            groupId: "group-1");

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class TestIdentityProvider : IWindowsIdentityProvider
    {
        public string GetCurrentIdentity() => "TEST\\operator";
    }

    private sealed class RecordingWorkSignal : IWorkSignal
    {
        public int SignalCount { get; private set; }

        public void Signal() => SignalCount++;

        public Task WaitAsync(TimeSpan maximumWait, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class SinglePluginCatalog(IVendorPlugin plugin) : IVendorPluginCatalog
    {
        public IReadOnlyCollection<PluginMetadata> Available => [plugin.Metadata];

        public IVendorPlugin GetRequired(string pluginId, string pinnedVersion)
        {
            Assert.Equal(plugin.Metadata.Id, pluginId);
            Assert.Equal(plugin.Metadata.Version.ToString(), pinnedVersion);
            return plugin;
        }
    }

    private sealed class RecordingExportStore(ExportJob job) : IExportStore
    {
        public ExportJobEvent? LastEvent { get; private set; }

        public Task<ExportJob?> GetJobAsync(Guid jobId, CancellationToken cancellationToken) =>
            Task.FromResult<ExportJob?>(jobId == job.Id ? job : null);

        public Task UpdateJobAsync(ExportJob updatedJob, ExportJobEvent jobEvent, AuditEvent auditEvent, long expectedVersion, CancellationToken cancellationToken)
        {
            Assert.Same(job, updatedJob);
            Assert.True(expectedVersion < updatedJob.Version);
            LastEvent = jobEvent;
            return Task.CompletedTask;
        }

        public Task AddJobAsync(ExportJob exportJob, ExportJobEvent createdEvent, AuditEvent auditEvent, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ExportJob?> GetJobByRemoteStatusUrlAsync(string exactContentLocation, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<PagedResult<ExportJob>> QueryJobsAsync(JobQuery query, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<ExportJob>> GetRunnableJobsAsync(DateTimeOffset now, int take, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task SaveManifestAsync(ExportJob exportJob, ExportJobEvent jobEvent, IReadOnlyCollection<ExportFile> files, AuditEvent auditEvent, long expectedVersion, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<ExportFile>> GetFilesAsync(Guid jobId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ExportFile?> GetFileAsync(Guid fileId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task UpdateFileAsync(ExportFile file, long expectedVersion, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task AddJobEventAsync(ExportJobEvent jobEvent, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<ExportJobEvent>> GetTimelineAsync(Guid jobId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> TryAcquireLeaseAsync(Guid jobId, string owner, DateTimeOffset now, TimeSpan duration, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task ReleaseLeaseAsync(Guid jobId, string owner, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task ClearExpiredLeasesAsync(DateTimeOffset now, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task DeleteJobMetadataAsync(Guid jobId, AuditEvent auditEvent, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class EmptySecretStore : ISecretStore
    {
        public Task<string> SaveAsync(string purpose, ReadOnlyMemory<char> secret, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<string?> GetAsync(string secretReference, CancellationToken cancellationToken) =>
            Task.FromResult<string?>(null);

        public Task DeleteAsync(string secretReference, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class CredentialOnlyConfigurationStore(
        CredentialProfile credential,
        Vendor? vendor = null,
        Site? site = null) : IConfigurationStore
    {
        public Task<CredentialProfile?> GetCredentialProfileAsync(Guid id, CancellationToken cancellationToken) =>
            Task.FromResult<CredentialProfile?>(id == credential.Id ? credential : null);

        public Task<IReadOnlyList<Vendor>> GetVendorsAsync(bool enabledOnly, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Vendor?> GetVendorAsync(Guid id, CancellationToken cancellationToken) =>
            Task.FromResult(vendor is not null && id == vendor.Id ? vendor : null);
        public Task AddVendorAsync(Vendor vendor, AuditEvent auditEvent, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<Site>> GetSitesAsync(Guid? vendorId, bool enabledOnly, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Site?> GetSiteAsync(Guid id, CancellationToken cancellationToken) =>
            Task.FromResult(site is not null && id == site.Id ? site : null);
        public Task AddSiteAsync(Site site, AuditEvent auditEvent, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task AddConnectionAsync(Site site, CredentialProfile credentialProfile, AuditEvent auditEvent, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task UpdateConnectionAsync(Site site, CredentialProfile credentialProfile, AuditEvent auditEvent, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ConnectionDeleteResult> DeleteConnectionAsync(Guid siteId, Guid credentialProfileId, AuditEvent auditEvent, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ConnectionMetadataHistory?> GetConnectionMetadataAsync(string pluginId, Guid siteId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task RecordConnectionMetadataAsync(string pluginId, Guid siteId, CredentialProfile credentialProfile, ConnectionMetadataAttempt attempt, string? capabilityStatementJson, string? discoveredTokenEndpoint, AuditEvent auditEvent, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<CredentialProfile>> GetCredentialProfilesAsync(Guid? vendorId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task AddCredentialProfileAsync(CredentialProfile profile, AuditEvent auditEvent, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class CountingPlugin(
        Func<int, IReadOnlyList<HttpExchangeContract>, AccessToken> createToken,
        Func<int, AccessToken, PollResult>? createPollResult = null) : IVendorPlugin
    {
        public int AuthenticationCount { get; private set; }
        public int PollCount { get; private set; }
        public List<string> PollTokens { get; } = [];

        public PluginMetadata Metadata { get; } = new(
            "test-plugin",
            "Test plugin",
            new Version(1, 0, 0),
            new Version(1, 0, 0),
            null,
            "Test vendor",
            "Tests");

        public Task<AccessToken> AuthenticateAsync(AuthenticationContext context, TimeProvider timeProvider, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AuthenticationCount++;
            IReadOnlyList<HttpExchangeContract> exchanges =
                [CreateExchange("OAuth token", string.Empty, HttpStatusCode.OK)];
            return Task.FromResult(createToken(AuthenticationCount, exchanges));
        }

        public Task<ConnectionTestResult> TestConnectionAsync(VendorSiteContext site, AccessToken? token, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Uri BuildKickoffUri(BulkExportKickoffRequest request) => throw new NotSupportedException();
        public Task<KickoffResult> KickoffAsync(BulkExportKickoffRequest request, AccessToken token, TimeProvider timeProvider, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<PollResult> PollAsync(Uri exactContentLocation, VendorSiteContext site, AccessToken token, TimeProvider timeProvider, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            PollCount++;
            PollTokens.Add(token.Value);
            return Task.FromResult((createPollResult ?? throw new NotSupportedException())(PollCount, token));
        }
        public NormalizedManifest NormalizeManifest(string exactManifestJson) => throw new NotSupportedException();
        public HttpRequestMessage CreateDownloadRequest(Uri remoteUri, Uri exactStatusUri, AccessToken? token, long? resumeOffset, EntityTagHeaderValue? ifRangeEtag, DateTimeOffset? ifRangeLastModified) => throw new NotSupportedException();
        public Task<CleanupResult> CancelAsync(Uri exactContentLocation, VendorSiteContext site, AccessToken token, TimeProvider timeProvider, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<CleanupResult> CleanupAsync(Uri exactContentLocation, VendorSiteContext site, AccessToken token, TimeProvider timeProvider, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<CleanupVerificationResult> VerifyCleanupAsync(Uri exactContentLocation, VendorSiteContext site, AccessToken token, TimeProvider timeProvider, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
