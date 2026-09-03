using System.Net;
using System.Reflection;
using System.Text.Json;
using FhirBulkExportManager.Application.Abstractions;
using FhirBulkExportManager.Application.Configuration;
using FhirBulkExportManager.Domain;
using FhirBulkExportManager.Infrastructure.Storage;
using FhirBulkExportManager.PluginContracts;
using FhirBulkExportManager.Shared;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FhirBulkExportManager.Worker;

public sealed class ExportJobProcessor(
    IExportStore exportStore,
    IConfigurationStore configurationStore,
    IVendorPluginCatalog pluginCatalog,
    AuthenticationTokenProvider tokenProvider,
    DownloadCoordinator downloadCoordinator,
    IManagedStorage storage,
    ILocalCommitService commitService,
    SiteResilienceGate siteResilienceGate,
    IWindowsIdentityProvider identityProvider,
    IOptions<ProcessingOptions> options,
    TimeProvider timeProvider,
    ILogger<ExportJobProcessor> logger)
{
    public async Task ProcessAsync(Guid jobId, CancellationToken cancellationToken)
    {
        var job = await exportStore.GetJobAsync(jobId, cancellationToken).ConfigureAwait(false);
        if (job is null || job.IsTerminal || job.State is ExportJobState.Draft or ExportJobState.Paused or ExportJobState.HeldForReview or ExportJobState.Unknown)
        {
            return;
        }

        try
        {
            switch (job.State)
            {
                case ExportJobState.Queued:
                    await TransitionAsync(job, ExportJobState.Authenticating, "AuthenticationStarted", "Obtaining a short-lived access token.", cancellationToken).ConfigureAwait(false);
                    break;
                case ExportJobState.Authenticating:
                    await AuthenticateAsync(job, cancellationToken).ConfigureAwait(false);
                    break;
                case ExportJobState.Starting:
                    await StartAsync(job, cancellationToken).ConfigureAwait(false);
                    break;
                case ExportJobState.Running:
                    await PollAsync(job, cancellationToken).ConfigureAwait(false);
                    break;
                case ExportJobState.WaitingForPoll:
                    await TransitionAsync(job, ExportJobState.Running, "PollDue", "Scheduled status check became due.", cancellationToken).ConfigureAwait(false);
                    break;
                case ExportJobState.ManifestReady:
                    await TransitionAsync(job, ExportJobState.QueuedForDownload, "FilesQueued", "Manifest files were queued for durable download.", cancellationToken).ConfigureAwait(false);
                    break;
                case ExportJobState.QueuedForDownload:
                    await TransitionAsync(job, ExportJobState.Downloading, "DownloadsStarted", "Site-aware download processing started.", cancellationToken).ConfigureAwait(false);
                    break;
                case ExportJobState.Downloading:
                    await DownloadAsync(job, cancellationToken).ConfigureAwait(false);
                    break;
                case ExportJobState.Validating:
                    await CommitAsync(job, cancellationToken).ConfigureAwait(false);
                    break;
                case ExportJobState.Committed:
                    await CompleteOrCleanupAsync(job, cancellationToken).ConfigureAwait(false);
                    break;
                case ExportJobState.CleanupRequested:
                    await CleanupAsync(job, cancellationToken).ConfigureAwait(false);
                    break;
                case ExportJobState.CancelRequested:
                    await CancelAsync(job, cancellationToken).ConfigureAwait(false);
                    break;
                case ExportJobState.RetryPending:
                case ExportJobState.FailedTransient:
                    await RouteRetryAsync(job, cancellationToken).ConfigureAwait(false);
                    break;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Job processing step failed for {JobId} in state {State}", job.Id, job.State);
            await RecordUnexpectedFailureAsync(job.Id, exception, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task AuthenticateAsync(ExportJob job, CancellationToken cancellationToken)
    {
        var plugin = pluginCatalog.GetRequired(job.PluginId, job.PluginVersion);
        try
        {
            var token = await tokenProvider.GetAsync(job, plugin, cancellationToken).ConfigureAwait(false);
            await TransitionAsync(
                job,
                ExportJobState.Starting,
                "AuthenticationSucceeded",
                "Authentication succeeded; kickoff is ready.",
                cancellationToken,
                httpExchanges: token.HttpExchanges).ConfigureAwait(false);
        }
        catch (RecordedHttpExchangeException exception)
        {
            var transient = exception.StatusCode is null or HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests || (int?)exception.StatusCode >= 500;
            await FailOrRetryAsync(job, transient, "AuthenticationFailed", exception.Message, cancellationToken, httpExchanges: exception.HttpExchanges).ConfigureAwait(false);
        }
        catch (HttpRequestException exception)
        {
            var transient = exception.StatusCode is null or HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests || (int?)exception.StatusCode >= 500;
            await FailOrRetryAsync(job, transient, "AuthenticationFailed", exception.Message, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task StartAsync(ExportJob job, CancellationToken cancellationToken)
    {
        if (job.RemoteRequestUrl is not null)
        {
            await TransitionAsync(job, ExportJobState.Unknown, "AmbiguousKickoffRecovered", "A durable kickoff dispatch record exists without a confirmed Content-Location. Automatic duplicate kickoff is blocked.", cancellationToken).ConfigureAwait(false);
            return;
        }

        var plugin = pluginCatalog.GetRequired(job.PluginId, job.PluginVersion);
        var siteContext = await GetSiteContextAsync(job, cancellationToken).ConfigureAwait(false);
        var token = await tokenProvider.GetAsync(job, plugin, cancellationToken).ConfigureAwait(false);
        var operation = Guid.NewGuid();
        var request = CreateKickoffRequest(job, siteContext, operation);
        var requestUri = plugin.BuildKickoffUri(request);
        var gate = await siteResilienceGate.TryAcquireAsync(job.SiteId, cancellationToken).ConfigureAwait(false);
        if (!gate.Acquired)
        {
            await ScheduleRetryAsync(job, gate.RetryAtUtc, "SiteRateLimited", "Another control request or site circuit breaker is active.", cancellationToken).ConfigureAwait(false);
            return;
        }

        using var gateLease = gate.Lease!;

        var journalExpected = job.Version;
        job.RecordKickoffRequest(requestUri.AbsoluteUri);
        var journalEvent = new ExportJobEvent(job.Id, job.State, job.State, "KickoffDispatchPrepared", timeProvider.GetUtcNow(), "Kickoff request was durably journaled before network dispatch.", operation, request.CorrelationId);
        await exportStore.UpdateJobAsync(job, journalEvent, Audit(job, "KickoffDispatchPrepared", "Succeeded", request.CorrelationId), journalExpected, cancellationToken).ConfigureAwait(false);

        var result = await plugin.KickoffAsync(request, token, timeProvider, cancellationToken).ConfigureAwait(false);
        if (result.Disposition is KickoffDisposition.RetryLater or KickoffDisposition.Ambiguous)
        {
            siteResilienceGate.ReportTransientFailure(job.SiteId, result.RetryAtUtc);
        }
        else
        {
            siteResilienceGate.ReportSuccess(job.SiteId);
        }
        var expected = job.Version;
        if (result.Disposition != KickoffDisposition.Accepted)
        {
            job.RecordRemoteProgress(result.Progress);
        }
        switch (result.Disposition)
        {
            case KickoffDisposition.Accepted when result.ExactContentLocation is not null:
                job.ConfirmRemoteJob(result.ExactContentLocation.AbsoluteUri, timeProvider.GetUtcNow(), result.RetryAtUtc ?? DefaultPollAt(job.RetryCount), result.Progress);
                await PersistTransitionAsync(job, expected, ExportJobState.WaitingForPoll, "RemoteExportAccepted", "Remote export accepted and exact Content-Location committed.", request.CorrelationId, cancellationToken, result.Progress, result.HttpExchanges).ConfigureAwait(false);
                break;
            case KickoffDisposition.Ambiguous:
                job.RecordFailure(result.ErrorCode ?? "AmbiguousKickoff", result.SafeErrorMessage ?? "Remote acceptance is unknown.", incrementRetry: false);
                await PersistTransitionAsync(job, expected, ExportJobState.Unknown, "KickoffOutcomeUnknown", "Automatic retries are blocked to avoid creating a duplicate remote export.", request.CorrelationId, cancellationToken, result.Progress, result.HttpExchanges).ConfigureAwait(false);
                break;
            case KickoffDisposition.RetryLater:
                job.ClearKickoffJournalAfterConfirmedRejection();
                job.ScheduleRetry(result.RetryAtUtc ?? DefaultPollAt(job.RetryCount), result.ErrorCode ?? "KickoffRetry", result.SafeErrorMessage ?? "Remote server requested retry.");
                await PersistTransitionAsync(job, expected, ExportJobState.RetryPending, "KickoffRetryScheduled", "Server explicitly did not accept kickoff; a bounded retry was scheduled.", request.CorrelationId, cancellationToken, result.Progress, result.HttpExchanges).ConfigureAwait(false);
                break;
            default:
                job.RecordFailure(result.ErrorCode ?? "KickoffRejected", result.SafeErrorMessage ?? "Kickoff was rejected.", incrementRetry: false);
                await PersistTransitionAsync(job, expected, ExportJobState.FailedPermanent, "KickoffRejected", "Remote export kickoff was rejected.", request.CorrelationId, cancellationToken, result.Progress, result.HttpExchanges).ConfigureAwait(false);
                break;
        }
    }

    private async Task PollAsync(ExportJob job, CancellationToken cancellationToken)
    {
        var exactStatus = new Uri(job.RemoteStatusUrl ?? throw new InvalidOperationException("Cannot poll without Content-Location."), UriKind.Absolute);
        var plugin = pluginCatalog.GetRequired(job.PluginId, job.PluginVersion);
        var token = await tokenProvider.GetAsync(job, plugin, cancellationToken).ConfigureAwait(false);
        var site = await GetSiteContextAsync(job, cancellationToken).ConfigureAwait(false);
        var gate = await siteResilienceGate.TryAcquireAsync(job.SiteId, cancellationToken).ConfigureAwait(false);
        if (!gate.Acquired)
        {
            await ScheduleRetryAsync(job, gate.RetryAtUtc, "SiteRateLimited", "Another control request or site circuit breaker is active.", cancellationToken).ConfigureAwait(false);
            return;
        }

        using var gateLease = gate.Lease!;
        var result = await plugin.PollAsync(exactStatus, site, token, timeProvider, cancellationToken).ConfigureAwait(false);
        if (result.Disposition == PollDisposition.RetryLater)
        {
            siteResilienceGate.ReportTransientFailure(job.SiteId, result.RetryAtUtc);
        }
        else
        {
            siteResilienceGate.ReportSuccess(job.SiteId);
        }
        var expected = job.Version;
        var operation = Guid.NewGuid();
        var correlation = operation.ToString("N");
        if (result.Disposition is not (PollDisposition.Running or PollDisposition.Complete))
        {
            job.RecordRemoteProgress(result.Progress);
        }
        switch (result.Disposition)
        {
            case PollDisposition.Running:
                job.RecordPoll(timeProvider.GetUtcNow(), result.RetryAtUtc ?? DefaultPollAt(job.RetryCount), result.Progress);
                await PersistTransitionAsync(job, expected, ExportJobState.WaitingForPoll, "RemoteExportRunning", "Remote export is still processing.", correlation, cancellationToken, result.Progress, result.HttpExchanges).ConfigureAwait(false);
                break;
            case PollDisposition.Complete when result.ExactManifestJson is not null:
                job.RecordPoll(timeProvider.GetUtcNow(), nextPollAtUtc: null, progress: result.Progress);
                await SaveManifestAsync(job, plugin, result.ExactManifestJson, expected, correlation, cancellationToken, result.Progress, result.HttpExchanges).ConfigureAwait(false);
                break;
            case PollDisposition.AuthenticationRequired:
                tokenProvider.Invalidate(job.CredentialProfileId);
                job.ScheduleRetry(timeProvider.GetUtcNow(), "AuthenticationRefreshRequired", result.SafeErrorMessage ?? "Access token expired.");
                await PersistTransitionAsync(job, expected, ExportJobState.RetryPending, "AuthenticationRefreshScheduled", "Access token will be reacquired before polling.", correlation, cancellationToken, result.Progress, result.HttpExchanges).ConfigureAwait(false);
                break;
            case PollDisposition.RetryLater:
                job.ScheduleRetry(result.RetryAtUtc ?? DefaultPollAt(job.RetryCount), result.ErrorCode ?? "PollRetry", result.SafeErrorMessage ?? "Status check will be retried.");
                await PersistTransitionAsync(job, expected, ExportJobState.RetryPending, "PollRetryScheduled", "A bounded status retry was scheduled.", correlation, cancellationToken, result.Progress, result.HttpExchanges).ConfigureAwait(false);
                break;
            case PollDisposition.Expired:
            case PollDisposition.NotFound:
                job.RecordFailure("RemoteNotFound", result.SafeErrorMessage ?? "Remote job no longer exists.", incrementRetry: false);
                await PersistTransitionAsync(job, expected, ExportJobState.Expired, "RemoteExportExpired", "Remote status URL expired or was not found before local completion.", correlation, cancellationToken, result.Progress, result.HttpExchanges).ConfigureAwait(false);
                break;
            case PollDisposition.AuthorizationDenied:
            case PollDisposition.FailedPermanent:
                job.RecordFailure(result.ErrorCode ?? "RemotePermanent", result.SafeErrorMessage ?? "Status request failed permanently.", incrementRetry: false);
                await PersistTransitionAsync(job, expected, ExportJobState.FailedPermanent, "RemoteStatusFailed", "Remote status request cannot continue automatically.", correlation, cancellationToken, result.Progress, result.HttpExchanges).ConfigureAwait(false);
                break;
            default:
                job.RecordFailure(result.ErrorCode ?? "UnknownRemoteState", result.SafeErrorMessage ?? "Remote state cannot be determined.", incrementRetry: false);
                await PersistTransitionAsync(job, expected, ExportJobState.Unknown, "RemoteStateUnknown", "Operator reconciliation is required.", correlation, cancellationToken, result.Progress, result.HttpExchanges).ConfigureAwait(false);
                break;
        }
    }

    private async Task SaveManifestAsync(
        ExportJob job,
        IVendorPlugin plugin,
        string exactJson,
        long expectedVersion,
        string correlationId,
        CancellationToken cancellationToken,
        string? progress = null,
        IReadOnlyList<HttpExchangeContract>? httpExchanges = null)
    {
        var normalized = plugin.NormalizeManifest(exactJson);
        var vendor = await configurationStore.GetVendorAsync(job.VendorId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Vendor is missing.");
        var paths = storage.GetPaths(vendor.PluginId, job.SiteId, job.Id);
        await storage.EnsureExportDirectoriesAsync(paths, cancellationToken).ConfigureAwait(false);
        await storage.WriteExactManifestAsync(paths, exactJson, cancellationToken).ConfigureAwait(false);
        var manifestHash = HashUtility.Sha256Hex(exactJson);
        var now = timeProvider.GetUtcNow();
        var sequence = 0;
        var files = normalized.Files.Select(contract =>
        {
            sequence++;
            var category = contract.Category switch
            {
                ManifestFileCategory.Output => ExportFileCategory.Output,
                ManifestFileCategory.Deleted => ExportFileCategory.Deleted,
                ManifestFileCategory.Error => ExportFileCategory.Error,
                _ => throw new ArgumentOutOfRangeException(),
            };
            var safeName = ManagedStorage.CreateSafeFileName(sequence, contract.ResourceType);
            var categoryName = category.ToString().ToLowerInvariant();
            return new ExportFile(Guid.NewGuid(), job.Id, category, contract.ResourceType, contract.RemoteUri.AbsoluteUri, sequence, safeName, Path.Combine(categoryName, safeName), now, Path.GetFileName(contract.RemoteUri.LocalPath));
        }).ToArray();

        job.RecordManifest(
            exactJson,
            manifestHash,
            normalized.TransactionTime,
            normalized.RequiresAccessToken,
            files.Count(x => x.Category == ExportFileCategory.Output),
            files.Count(x => x.Category == ExportFileCategory.Deleted),
            files.Count(x => x.Category == ExportFileCategory.Error),
            now);
        var operation = Guid.NewGuid();
        var metadata = BuildEventMetadata(progress, httpExchanges);
        var jobEvent = job.Transition(ExportJobState.ManifestReady, now, "ManifestPersisted", "Completion manifest and all file entries were persisted.", operation, correlationId, metadata);
        await exportStore.SaveManifestAsync(job, jobEvent, files, Audit(job, "ManifestPersisted", "Succeeded", correlationId), expectedVersion, cancellationToken).ConfigureAwait(false);
    }

    private async Task DownloadAsync(ExportJob job, CancellationToken cancellationToken)
    {
        var plugin = pluginCatalog.GetRequired(job.PluginId, job.PluginVersion);
        var result = await downloadCoordinator.ProcessOneAsync(job, plugin, cancellationToken).ConfigureAwait(false);
        if (result.PauseForLowDisk)
        {
            var expected = job.Version;
            job.RecordFailure("LowDiskSpace", result.SafeError ?? "Downloads paused to preserve the configured free-space reserve.", incrementRetry: false);
            await PersistTransitionAsync(job, expected, ExportJobState.Paused, "DownloadsPausedLowDisk", "Local downloads were paused before the disk reserve was breached. The remote export was not cancelled.", Guid.NewGuid().ToString("N"), cancellationToken).ConfigureAwait(false);
        }
        else if (result.PermanentFailure)
        {
            var expected = job.Version;
            job.RecordFailure("FileProcessingFailed", result.SafeError ?? "A file failed.", incrementRetry: false);
            await PersistTransitionAsync(job, expected, ExportJobState.FailedPermanent, "FileProcessingFailed", "One or more files could not be downloaded and validated.", Guid.NewGuid().ToString("N"), cancellationToken).ConfigureAwait(false);
        }
        else if (result.AllValidated)
        {
            await TransitionAsync(job, ExportJobState.Validating, "AllFilesValidated", "All required manifest files are downloaded, checksummed, and validated.", cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task CommitAsync(ExportJob job, CancellationToken cancellationToken)
    {
        var files = await exportStore.GetFilesAsync(job.Id, cancellationToken).ConfigureAwait(false);
        if (files.Count == 0 || files.Any(x => x.Status != ExportFileStatus.Validated || string.IsNullOrWhiteSpace(x.Sha256)))
        {
            var failureExpectedVersion = job.Version;
            job.RecordFailure("CommitInvariantFailed", "Not all manifest files are validated and checksummed.", incrementRetry: false);
            await PersistTransitionAsync(job, failureExpectedVersion, ExportJobState.FailedPermanent, "CommitBlocked", "Local commit was blocked because file invariants failed.", Guid.NewGuid().ToString("N"), cancellationToken).ConfigureAwait(false);
            return;
        }

        var vendor = await configurationStore.GetVendorAsync(job.VendorId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Vendor is missing.");
        var site = await configurationStore.GetSiteAsync(job.SiteId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Site is missing.");
        var paths = storage.GetPaths(vendor.PluginId, site.Id, job.Id);
        var outcomeSummary = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files.Where(x => x.Category == ExportFileCategory.Error))
        {
            var counts = JsonSerializer.Deserialize<Dictionary<string, long>>(file.ValidationSummaryJson, JsonDefaults.Options) ?? [];
            foreach (var pair in counts)
            {
                outcomeSummary[pair.Key] = outcomeSummary.GetValueOrDefault(pair.Key) + pair.Value;
            }
        }

        var commitFiles = files.Select(x => new LocalCommitFile(
            x.Category.ToString(),
            x.ResourceType ?? string.Empty,
            x.RelativeLocalPath,
            x.ActualBytes,
            x.RecordCount,
            x.Sha256!)).ToArray();
        var document = new LocalCommitDocument(
            job.Id,
            vendor.Name,
            site.Name,
            job.ManifestSha256 ?? throw new InvalidOperationException("Manifest hash is missing."),
            job.RemoteTransactionTime,
            timeProvider.GetUtcNow(),
            commitFiles,
            commitFiles.Length,
            commitFiles.Sum(x => x.Bytes),
            commitFiles.Sum(x => x.Records),
            outcomeSummary,
            Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "1.0.0",
            job.PluginVersion);
        await commitService.CreateAsync(paths, document, cancellationToken).ConfigureAwait(false);

        var commitExpectedVersion = job.Version;
        job.RecordFileProgress(files.Count, files.Count, files.Sum(x => x.ActualBytes), files.Sum(x => x.ActualBytes), files.Sum(x => x.RecordCount));
        var serious = files.Any(x => x.HasSeriousOperationOutcome);
        await PersistTransitionAsync(
            job,
            commitExpectedVersion,
            serious ? ExportJobState.HeldForReview : ExportJobState.Committed,
            serious ? "SeriousOperationOutcomeDetected" : "LocalExportCommitted",
            serious ? "Local files are safely committed, but fatal/error OperationOutcome entries require operator review." : "Durable local commit completed.",
            Guid.NewGuid().ToString("N"),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task CompleteOrCleanupAsync(ExportJob job, CancellationToken cancellationToken)
    {
        if (!job.AutomaticCleanupEnabled)
        {
            await TransitionAsync(job, ExportJobState.Completed, "LocalWorkflowCompleted", "Local export completed; automatic remote cleanup is disabled.", cancellationToken).ConfigureAwait(false);
            return;
        }

        var vendor = await configurationStore.GetVendorAsync(job.VendorId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Vendor is missing.");
        var paths = storage.GetPaths(vendor.PluginId, job.SiteId, job.Id);
        if (job.ManifestSha256 is null || !await commitService.VerifyAsync(paths, job.Id, job.ManifestSha256, cancellationToken).ConfigureAwait(false))
        {
            var expected = job.Version;
            job.RecordFailure("LocalCommitVerificationFailed", "Automatic remote cleanup was blocked because local commit verification failed.", incrementRetry: false);
            await PersistTransitionAsync(job, expected, ExportJobState.HeldForReview, "AutomaticCleanupBlocked", "Local commit verification failed; remote data was not deleted.", Guid.NewGuid().ToString("N"), cancellationToken).ConfigureAwait(false);
            return;
        }

        await TransitionAsync(job, ExportJobState.CleanupRequested, "AutomaticCleanupRequested", "Local commit was verified; remote cleanup was queued.", cancellationToken).ConfigureAwait(false);
    }

    private async Task CleanupAsync(ExportJob job, CancellationToken cancellationToken)
    {
        var plugin = pluginCatalog.GetRequired(job.PluginId, job.PluginVersion);
        var token = await tokenProvider.GetAsync(job, plugin, cancellationToken).ConfigureAwait(false);
        var site = await GetSiteContextAsync(job, cancellationToken).ConfigureAwait(false);
        var exactStatus = new Uri(job.RemoteStatusUrl ?? throw new InvalidOperationException("Cleanup target Content-Location is missing."), UriKind.Absolute);

        var vendor = await configurationStore.GetVendorAsync(job.VendorId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Vendor is missing.");
        var paths = storage.GetPaths(vendor.PluginId, job.SiteId, job.Id);
        if (job.ManifestSha256 is null || !await commitService.VerifyAsync(paths, job.Id, job.ManifestSha256, cancellationToken).ConfigureAwait(false))
        {
            var expected = job.Version;
            job.RecordFailure("LocalCommitVerificationFailed", "Remote cleanup was blocked because local commit verification failed.", incrementRetry: false);
            await PersistTransitionAsync(job, expected, ExportJobState.Committed, "RemoteCleanupBlocked", "Remote cleanup was not sent.", Guid.NewGuid().ToString("N"), cancellationToken).ConfigureAwait(false);
            return;
        }

        var gate = await siteResilienceGate.TryAcquireAsync(job.SiteId, cancellationToken).ConfigureAwait(false);
        if (!gate.Acquired)
        {
            await ScheduleRetryAsync(job, gate.RetryAtUtc, "SiteRateLimited", "Another control request or site circuit breaker is active.", cancellationToken).ConfigureAwait(false);
            return;
        }

        using var gateLease = gate.Lease!;
        var cleanup = await plugin.CleanupAsync(exactStatus, site, token, timeProvider, cancellationToken).ConfigureAwait(false);
        if (!cleanup.Succeeded)
        {
            siteResilienceGate.ReportTransientFailure(job.SiteId, cleanup.RetryAtUtc);
            await ScheduleRetryAsync(job, cleanup.RetryAtUtc, "CleanupRetry", cleanup.SafeMessage ?? "Remote cleanup failed transiently.", cancellationToken, httpExchanges: cleanup.HttpExchanges).ConfigureAwait(false);
            return;
        }

        var verification = await plugin.VerifyCleanupAsync(exactStatus, site, token, timeProvider, cancellationToken).ConfigureAwait(false);
        var cleanupExchanges = CombineHttpExchanges(cleanup.HttpExchanges, verification.HttpExchanges);
        if (verification.Disposition == CleanupVerificationDisposition.VerifiedDeleted)
        {
            siteResilienceGate.ReportSuccess(job.SiteId);
            var expected = job.Version;
            job.MarkCleanupVerified();
            await PersistTransitionAsync(job, expected, ExportJobState.Completed, "RemoteCleanupVerified", "Remote status URL returned 404; cleanup is verified.", Guid.NewGuid().ToString("N"), cancellationToken, httpExchanges: cleanupExchanges).ConfigureAwait(false);
        }
        else if (verification.Disposition is CleanupVerificationDisposition.StillPresent or CleanupVerificationDisposition.RetryLater)
        {
            siteResilienceGate.ReportTransientFailure(job.SiteId, verification.RetryAtUtc);
            await ScheduleRetryAsync(job, verification.RetryAtUtc, "CleanupVerificationRetry", "Remote cleanup has not yet been verified.", cancellationToken, httpExchanges: cleanupExchanges).ConfigureAwait(false);
        }
        else
        {
            var expected = job.Version;
            job.RecordFailure("CleanupVerificationUnknown", "Cleanup response could not be interpreted safely.", incrementRetry: false);
            await PersistTransitionAsync(job, expected, ExportJobState.HeldForReview, "CleanupVerificationUnknown", "Local export remains valid; operator review is required before another cleanup attempt.", Guid.NewGuid().ToString("N"), cancellationToken, httpExchanges: cleanupExchanges).ConfigureAwait(false);
        }
    }

    private async Task CancelAsync(ExportJob job, CancellationToken cancellationToken)
    {
        var plugin = pluginCatalog.GetRequired(job.PluginId, job.PluginVersion);
        var token = await tokenProvider.GetAsync(job, plugin, cancellationToken).ConfigureAwait(false);
        var site = await GetSiteContextAsync(job, cancellationToken).ConfigureAwait(false);
        var exactStatus = new Uri(job.RemoteStatusUrl ?? throw new InvalidOperationException("Cancellation target Content-Location is missing."), UriKind.Absolute);
        var gate = await siteResilienceGate.TryAcquireAsync(job.SiteId, cancellationToken).ConfigureAwait(false);
        if (!gate.Acquired)
        {
            await ScheduleRetryAsync(job, gate.RetryAtUtc, "SiteRateLimited", "Another control request or site circuit breaker is active.", cancellationToken).ConfigureAwait(false);
            return;
        }

        using var gateLease = gate.Lease!;
        var result = await plugin.CancelAsync(exactStatus, site, token, timeProvider, cancellationToken).ConfigureAwait(false);
        if (result.Succeeded)
        {
            siteResilienceGate.ReportSuccess(job.SiteId);
            await TransitionAsync(job, ExportJobState.Cancelled, "RemoteExportCancelled", "Remote DELETE request was accepted or the job was already absent.", cancellationToken, httpExchanges: result.HttpExchanges).ConfigureAwait(false);
        }
        else
        {
            siteResilienceGate.ReportTransientFailure(job.SiteId, result.RetryAtUtc);
            await ScheduleRetryAsync(job, result.RetryAtUtc, "CancelRetry", result.SafeMessage ?? "Remote cancellation failed transiently.", cancellationToken, httpExchanges: result.HttpExchanges).ConfigureAwait(false);
        }
    }

    private async Task RouteRetryAsync(ExportJob job, CancellationToken cancellationToken)
    {
        ExportJobState target;
        if (job.CancelRequestedAtUtc is not null)
        {
            target = ExportJobState.CancelRequested;
        }
        else if (job.CleanupRequestedAtUtc is not null && job.CommittedAtUtc is not null)
        {
            target = ExportJobState.CleanupRequested;
        }
        else if (job.ManifestJson is not null)
        {
            target = ExportJobState.QueuedForDownload;
        }
        else if (job.RemoteStatusUrl is not null)
        {
            target = ExportJobState.WaitingForPoll;
        }
        else
        {
            target = ExportJobState.Authenticating;
        }

        await TransitionAsync(job, target, "RetryActivated", "Scheduled retry became due.", cancellationToken).ConfigureAwait(false);
    }

    private async Task ScheduleRetryAsync(
        ExportJob job,
        DateTimeOffset? retryAt,
        string code,
        string message,
        CancellationToken cancellationToken,
        string? progress = null,
        IReadOnlyList<HttpExchangeContract>? httpExchanges = null)
    {
        var expected = job.Version;
        job.ScheduleRetry(retryAt ?? DefaultPollAt(job.RetryCount), code, message);
        await PersistTransitionAsync(job, expected, ExportJobState.RetryPending, "RetryScheduled", "A bounded retry was scheduled without changing local data integrity.", Guid.NewGuid().ToString("N"), cancellationToken, progress, httpExchanges).ConfigureAwait(false);
    }

    private async Task FailOrRetryAsync(
        ExportJob job,
        bool transient,
        string code,
        string message,
        CancellationToken cancellationToken,
        string? progress = null,
        IReadOnlyList<HttpExchangeContract>? httpExchanges = null)
    {
        var expected = job.Version;
        if (transient && job.RetryCount < 5)
        {
            job.ScheduleRetry(DefaultPollAt(job.RetryCount), code, message);
            await PersistTransitionAsync(job, expected, ExportJobState.RetryPending, "TransientFailure", "Transient failure was scheduled for retry.", Guid.NewGuid().ToString("N"), cancellationToken, progress, httpExchanges).ConfigureAwait(false);
        }
        else
        {
            job.RecordFailure(code, message, incrementRetry: false);
            await PersistTransitionAsync(job, expected, ExportJobState.FailedPermanent, "PermanentFailure", "Operation cannot continue automatically.", Guid.NewGuid().ToString("N"), cancellationToken, progress, httpExchanges).ConfigureAwait(false);
        }
    }

    private async Task TransitionAsync(
        ExportJob job,
        ExportJobState target,
        string eventType,
        string message,
        CancellationToken cancellationToken,
        string? progress = null,
        IReadOnlyList<HttpExchangeContract>? httpExchanges = null)
    {
        var expected = job.Version;
        await PersistTransitionAsync(job, expected, target, eventType, message, Guid.NewGuid().ToString("N"), cancellationToken, progress, httpExchanges).ConfigureAwait(false);
    }

    private async Task PersistTransitionAsync(
        ExportJob job,
        long expectedVersion,
        ExportJobState target,
        string eventType,
        string message,
        string correlationId,
        CancellationToken cancellationToken,
        string? progress = null,
        IReadOnlyList<HttpExchangeContract>? httpExchanges = null)
    {
        var operation = Guid.NewGuid();
        var metadata = BuildEventMetadata(progress, httpExchanges);
        var evt = job.Transition(target, timeProvider.GetUtcNow(), eventType, message, operation, correlationId, metadata);
        await exportStore.UpdateJobAsync(job, evt, Audit(job, eventType, "Succeeded", correlationId), expectedVersion, cancellationToken).ConfigureAwait(false);
    }

    private static string BuildEventMetadata(string? progress, IReadOnlyList<HttpExchangeContract>? httpExchanges)
    {
        if (string.IsNullOrWhiteSpace(progress) && httpExchanges is not { Count: > 0 })
        {
            return "{}";
        }

        return JsonSerializer.Serialize(
            new JobEventMetadataContract(progress, httpExchanges ?? []),
            JsonDefaults.Options);
    }

    private static IReadOnlyList<HttpExchangeContract> CombineHttpExchanges(
        IReadOnlyList<HttpExchangeContract>? first,
        IReadOnlyList<HttpExchangeContract>? second) =>
        (first ?? []).Concat(second ?? []).ToArray();

    private async Task RecordUnexpectedFailureAsync(Guid jobId, Exception exception, CancellationToken cancellationToken)
    {
        var current = await exportStore.GetJobAsync(jobId, cancellationToken).ConfigureAwait(false);
        if (current is null || current.IsTerminal || current.State is ExportJobState.Unknown or ExportJobState.Paused or ExportJobState.HeldForReview)
        {
            return;
        }

        if (current.State == ExportJobState.Starting && current.RemoteRequestUrl is not null)
        {
            var expected = current.Version;
            current.RecordFailure("AmbiguousKickoff", SecurityRedactor.Redact(exception.Message), incrementRetry: false);
            await PersistTransitionAsync(current, expected, ExportJobState.Unknown, "KickoffOutcomeUnknown", "An unexpected failure occurred after kickoff dispatch was journaled; automatic duplicate kickoff is blocked.", Guid.NewGuid().ToString("N"), cancellationToken).ConfigureAwait(false);
            return;
        }

        var canRetry = current.RetryCount < 5 && current.State != ExportJobState.Starting;
        try
        {
            await FailOrRetryAsync(current, canRetry, "UnhandledProcessingError", SecurityRedactor.Redact(exception.Message), cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            logger.LogError("Could not transition job {JobId} after an unexpected processing failure.", current.Id);
        }
    }

    private async Task<VendorSiteContext> GetSiteContextAsync(ExportJob job, CancellationToken cancellationToken)
    {
        var site = await configurationStore.GetSiteAsync(job.SiteId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Site is missing.");
        var uri = Guard.AbsoluteHttpsUri(site.FhirBaseUrl, nameof(site.FhirBaseUrl), allowLoopbackHttp: true);
        return new VendorSiteContext(job.VendorId, site.Id, uri, site.TenantIdentifier, site.PracticeCode, "{}");
    }

    private static BulkExportKickoffRequest CreateKickoffRequest(ExportJob job, VendorSiteContext site, Guid operationId)
    {
        var resourceTypes = JsonSerializer.Deserialize<string[]>(job.ResourceTypesJson, JsonDefaults.Options) ?? [];
        var typeFilters = JsonSerializer.Deserialize<string[]>(job.TypeFiltersJson, JsonDefaults.Options) ?? [];
        return new BulkExportKickoffRequest(
            job.Id,
            operationId,
            site,
            job.ExportType switch
            {
                ExportType.System => PluginExportType.System,
                ExportType.Group => PluginExportType.Group,
                ExportType.Patient => PluginExportType.Patient,
                _ => throw new ArgumentOutOfRangeException(nameof(job)),
            },
            job.GroupId,
            job.SinceUtc,
            resourceTypes,
            typeFilters,
            job.OutputFormat,
            job.VendorOptionsJson,
            operationId.ToString("N"));
    }

    private DateTimeOffset DefaultPollAt(int attempt) => RetryAfterParser.ExponentialWithJitter(
        timeProvider.GetUtcNow(),
        attempt,
        TimeSpan.FromSeconds(options.Value.DefaultPollingBackoffSeconds),
        TimeSpan.FromSeconds(options.Value.MaximumPollingIntervalSeconds),
        HashCode.Combine(attempt, timeProvider.GetUtcNow().Ticks));

    private AuditEvent Audit(ExportJob job, string action, string result, string correlationId) =>
        new(timeProvider.GetUtcNow(), identityProvider.GetCurrentIdentity(), action, nameof(ExportJob), job.Id.ToString("D"), result, correlationId);
}
