using System.Text.Json;
using FhirBulkExportManager.Application.Abstractions;
using FhirBulkExportManager.Application.Models;
using FhirBulkExportManager.Domain;
using FhirBulkExportManager.Shared;

namespace FhirBulkExportManager.Application.Services;

public sealed class ExportJobService(
    IExportStore exportStore,
    IConfigurationStore configurationStore,
    IVendorPluginCatalog pluginCatalog,
    IWindowsIdentityProvider identityProvider,
    IWorkSignal workSignal,
    TimeProvider timeProvider)
{
    public async Task<Guid> CreateDraftAsync(CreateExportJobCommand command, CancellationToken cancellationToken)
    {
        var vendor = await configurationStore.GetVendorAsync(command.VendorId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Selected vendor does not exist.");
        var site = await configurationStore.GetSiteAsync(command.SiteId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Selected site does not exist.");
        var credential = await configurationStore.GetCredentialProfileAsync(command.CredentialProfileId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Selected credential profile does not exist.");

        if (!vendor.Enabled || !site.Enabled || site.VendorId != vendor.Id || credential.VendorId != vendor.Id)
        {
            throw new InvalidOperationException("Vendor, site, and credential profile must be enabled and belong to the same vendor.");
        }

        _ = pluginCatalog.GetRequired(vendor.PluginId, vendor.PluginVersion);
        var now = timeProvider.GetUtcNow();
        var job = new ExportJob(
            Guid.NewGuid(),
            command.Name,
            vendor.Id,
            site.Id,
            credential.Id,
            vendor.PluginId,
            vendor.PluginVersion,
            command.ExportType,
            command.Priority,
            command.LocalDestination,
            now,
            command.GroupId,
            command.SinceUtc,
            JsonSerializer.Serialize(command.ResourceTypes, JsonDefaults.Options),
            JsonSerializer.Serialize(command.TypeFilters, JsonDefaults.Options),
            command.VendorOptionsJson,
            command.OutputFormat,
            command.Description,
            command.AutomaticCleanupEnabled);

        var operationId = Guid.NewGuid();
        var correlationId = operationId.ToString("N");
        var createdEvent = new ExportJobEvent(job.Id, ExportJobState.Draft, ExportJobState.Draft, "JobCreated", now, "Export job saved as Draft; no remote export was started.", operationId, correlationId);
        var audit = Audit(now, "JobCreated", job.Id, "Succeeded", correlationId);
        await exportStore.AddJobAsync(job, createdEvent, audit, cancellationToken).ConfigureAwait(false);
        return job.Id;
    }

    public async Task<Guid> ImportExistingAsync(ImportExistingExportCommand command, CancellationToken cancellationToken)
    {
        var vendor = await configurationStore.GetVendorAsync(command.VendorId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Selected vendor does not exist.");
        var site = await configurationStore.GetSiteAsync(command.SiteId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Selected site does not exist.");
        var credential = await configurationStore.GetCredentialProfileAsync(command.CredentialProfileId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Selected credential profile does not exist.");

        if (!vendor.Enabled || !site.Enabled || site.VendorId != vendor.Id || credential.VendorId != vendor.Id)
        {
            throw new InvalidOperationException("Vendor, site, and credential profile must be enabled and belong to the same vendor.");
        }

        if (site.CredentialProfileId != credential.Id)
        {
            throw new InvalidOperationException("The imported export must use the credential profile assigned to the selected connection.");
        }

        _ = pluginCatalog.GetRequired(vendor.PluginId, vendor.PluginVersion);
        var exactContentLocation = Guard.NotNullOrWhiteSpace(command.ExactContentLocation, nameof(command.ExactContentLocation));
        if (exactContentLocation.Length > 4096)
        {
            throw new ArgumentOutOfRangeException(nameof(command.ExactContentLocation), "Content-Location cannot exceed 4,096 characters.");
        }

        var contentLocation = Guard.AbsoluteHttpsUri(
            exactContentLocation,
            nameof(command.ExactContentLocation),
            allowLoopbackHttp: true);
        if (!string.IsNullOrEmpty(contentLocation.UserInfo))
        {
            throw new ArgumentException("Content-Location must not contain embedded credentials.", nameof(command.ExactContentLocation));
        }

        if (!string.IsNullOrEmpty(contentLocation.Fragment))
        {
            throw new ArgumentException("Content-Location must not contain a URI fragment.", nameof(command.ExactContentLocation));
        }

        EnsureSameOrigin(
            contentLocation,
            Guard.AbsoluteHttpsUri(site.FhirBaseUrl, nameof(site.FhirBaseUrl), allowLoopbackHttp: true));

        var existing = await exportStore.GetJobByRemoteStatusUrlAsync(exactContentLocation, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            throw new InvalidOperationException($"This Content-Location is already assigned to job {existing.Id:D} ({existing.State}).");
        }

        var now = timeProvider.GetUtcNow();
        if (command.RemoteStartedAtUtc > now.AddMinutes(5))
        {
            throw new InvalidOperationException("Remote export start time cannot be in the future.");
        }

        var job = new ExportJob(
            Guid.NewGuid(),
            command.Name,
            vendor.Id,
            site.Id,
            credential.Id,
            vendor.PluginId,
            vendor.PluginVersion,
            command.ExportType,
            command.Priority,
            command.LocalDestination,
            now,
            command.GroupId,
            command.SinceUtc,
            JsonSerializer.Serialize(command.ResourceTypes, JsonDefaults.Options),
            JsonSerializer.Serialize(command.TypeFilters, JsonDefaults.Options),
            command.VendorOptionsJson,
            command.OutputFormat,
            command.Description,
            command.AutomaticCleanupEnabled);

        job.ConfirmRemoteJob(
            exactContentLocation,
            now,
            nextPollAtUtc: now,
            remoteStartedAtUtc: command.RemoteStartedAtUtc);
        var operationId = Guid.NewGuid();
        var correlationId = operationId.ToString("N");
        var importedEvent = job.Transition(
            ExportJobState.WaitingForPoll,
            now,
            "ExistingRemoteExportImported",
            "Existing remote export was imported for monitoring; this application did not send a kickoff request.",
            operationId,
            correlationId);
        var auditMetadata = JsonSerializer.Serialize(
            new { source = "external", command.RemoteStartedAtUtc },
            JsonDefaults.Options);
        var audit = new AuditEvent(
            now,
            identityProvider.GetCurrentIdentity(),
            "ExistingRemoteExportImported",
            nameof(ExportJob),
            job.Id.ToString("D"),
            "Succeeded",
            correlationId,
            auditMetadata);
        await exportStore.AddJobAsync(job, importedEvent, audit, cancellationToken).ConfigureAwait(false);
        workSignal.Signal();
        return job.Id;
    }

    public async Task<JobActionResult> ReassignImportedConnectionAsync(
        Guid jobId,
        Guid vendorId,
        Guid siteId,
        Guid credentialProfileId,
        CancellationToken cancellationToken)
    {
        var job = await RequiredJobAsync(jobId, cancellationToken).ConfigureAwait(false);
        var vendor = await configurationStore.GetVendorAsync(vendorId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Selected vendor does not exist.");
        var site = await configurationStore.GetSiteAsync(siteId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Selected site does not exist.");
        var credential = await configurationStore.GetCredentialProfileAsync(credentialProfileId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Selected credential profile does not exist.");

        if (!vendor.Enabled || !site.Enabled || site.VendorId != vendor.Id || credential.VendorId != vendor.Id)
        {
            throw new InvalidOperationException("Vendor, site, and credential profile must be enabled and belong to the same vendor.");
        }

        if (site.CredentialProfileId != credential.Id)
        {
            throw new InvalidOperationException("The imported export must use the credential profile assigned to the selected connection.");
        }

        _ = pluginCatalog.GetRequired(vendor.PluginId, vendor.PluginVersion);
        var exactContentLocation = Guard.AbsoluteHttpsUri(
            job.RemoteStatusUrl ?? throw new InvalidOperationException("The imported export has no Content-Location."),
            nameof(job.RemoteStatusUrl),
            allowLoopbackHttp: true);
        EnsureSameOrigin(
            exactContentLocation,
            Guard.AbsoluteHttpsUri(site.FhirBaseUrl, nameof(site.FhirBaseUrl), allowLoopbackHttp: true));

        var expected = job.Version;
        var previousState = job.State;
        var previousPluginId = job.PluginId;
        var previousSiteId = job.SiteId;
        var previousCredentialProfileId = job.CredentialProfileId;
        var now = timeProvider.GetUtcNow();
        job.ReassignImportedConnection(
            vendor.Id,
            site.Id,
            credential.Id,
            vendor.PluginId,
            vendor.PluginVersion,
            now);

        var operationId = Guid.NewGuid();
        var correlationId = operationId.ToString("N");
        var message = "Imported export was reassigned to the selected connection; no remote kickoff request was sent.";
        var metadata = JsonSerializer.Serialize(
            new
            {
                progress = (string?)null,
                httpExchanges = Array.Empty<object>(),
                connectionChange = new
                {
                    previousPluginId,
                    previousSiteId,
                    previousCredentialProfileId,
                    newPluginId = vendor.PluginId,
                    newSiteId = site.Id,
                    newCredentialProfileId = credential.Id,
                },
            },
            JsonDefaults.Options);
        ExportJobEvent evt;
        if (job.State == ExportJobState.Running)
        {
            evt = new ExportJobEvent(
                job.Id,
                previousState,
                job.State,
                "ImportedConnectionReassigned",
                now,
                message,
                operationId,
                correlationId,
                metadata);
        }
        else
        {
            var target = job.State switch
            {
                ExportJobState.Paused => ExportJobState.WaitingForPoll,
                ExportJobState.FailedTransient => ExportJobState.RetryPending,
                _ => ExportJobState.Running,
            };
            evt = job.Transition(
                target,
                now,
                "ImportedConnectionReassigned",
                message,
                operationId,
                correlationId,
                metadata);
        }

        var audit = Audit(now, "ImportedConnectionReassigned", job.Id, "Succeeded", correlationId);
        await exportStore.UpdateJobAsync(job, evt, audit, expected, cancellationToken).ConfigureAwait(false);
        workSignal.Signal();
        return new JobActionResult(job.Id, job.State, "Imported job connection repaired and an authenticated status check was queued.");
    }

    public Task<JobActionResult> StartAsync(Guid jobId, CancellationToken cancellationToken) =>
        TransitionAsync(jobId, ExportJobState.Draft, ExportJobState.Queued, "ExportStartRequested", "Export was explicitly queued by the operator.", true, cancellationToken);

    public async Task<JobActionResult> GetStatusNowAsync(Guid jobId, CancellationToken cancellationToken)
    {
        var job = await RequiredJobAsync(jobId, cancellationToken).ConfigureAwait(false);
        if (!job.HasConfirmedRemoteJob)
        {
            throw new InvalidOperationException("Status cannot be requested before the exact Content-Location is persisted.");
        }

        var recoveringRemoteStatusFailure = job.State == ExportJobState.FailedPermanent && job.ManifestJson is null;
        if (job.State is not (ExportJobState.WaitingForPoll or ExportJobState.Running or ExportJobState.RetryPending) &&
            !recoveringRemoteStatusFailure)
        {
            throw new InvalidOperationException($"Manual status is not valid while the job is {job.State}.");
        }

        var expected = job.Version;
        var now = timeProvider.GetUtcNow();
        ExportJobEvent evt;
        if (job.State is ExportJobState.WaitingForPoll or ExportJobState.FailedPermanent)
        {
            if (job.State == ExportJobState.FailedPermanent)
            {
                job.ClearFailure();
            }

            evt = job.Transition(ExportJobState.Running, now, "ManualStatusRequested", "Operator requested an immediate status check.", Guid.NewGuid(), Guid.NewGuid().ToString("N"));
        }
        else
        {
            job.RecordPoll(now, now, job.ProgressText);
            evt = new ExportJobEvent(job.Id, job.State, job.State, "ManualStatusRequested", now, "Operator requested an immediate status check.", Guid.NewGuid(), Guid.NewGuid().ToString("N"));
        }

        var audit = Audit(now, "ManualStatusRequested", job.Id, "Succeeded", evt.CorrelationId);
        await exportStore.UpdateJobAsync(job, evt, audit, expected, cancellationToken).ConfigureAwait(false);
        workSignal.Signal();
        return new JobActionResult(job.Id, job.State, "Status check queued.");
    }

    public async Task<JobActionResult> PauseAsync(Guid jobId, CancellationToken cancellationToken)
    {
        var job = await RequiredJobAsync(jobId, cancellationToken).ConfigureAwait(false);
        if (job.State is not (ExportJobState.Queued or ExportJobState.Authenticating or ExportJobState.Running or ExportJobState.WaitingForPoll or ExportJobState.QueuedForDownload or ExportJobState.Downloading))
        {
            throw new InvalidOperationException($"Local processing cannot be paused while the job is {job.State}.");
        }

        return await TransitionLoadedAsync(job, ExportJobState.Paused, "LocalProcessingPaused", "Local polling and downloads were paused. The remote export was not cancelled.", false, cancellationToken).ConfigureAwait(false);
    }

    public async Task<JobActionResult> ResumeAsync(Guid jobId, CancellationToken cancellationToken)
    {
        var job = await RequiredJobAsync(jobId, cancellationToken).ConfigureAwait(false);
        if (job.State != ExportJobState.Paused)
        {
            throw new InvalidOperationException("Only a paused job can be resumed.");
        }

        var target = job.CancelRequestedAtUtc is not null
            ? ExportJobState.CancelRequested
            : job.ManifestJson is not null
            ? ExportJobState.QueuedForDownload
            : job.HasConfirmedRemoteJob ? ExportJobState.WaitingForPoll : ExportJobState.Queued;
        return await TransitionLoadedAsync(job, target, "LocalProcessingResumed", "Local processing was resumed.", true, cancellationToken).ConfigureAwait(false);
    }

    public async Task<JobActionResult> ResumeRemainingDownloadsAsync(Guid jobId, CancellationToken cancellationToken)
    {
        var job = await RequiredJobAsync(jobId, cancellationToken).ConfigureAwait(false);
        if (job.State != ExportJobState.FailedPermanent || job.ManifestJson is null)
        {
            throw new InvalidOperationException("Remaining downloads can be resumed only for a failed job that already has a completion manifest.");
        }

        var files = await exportStore.GetFilesAsync(job.Id, cancellationToken).ConfigureAwait(false);
        if (files.Count == 0)
        {
            throw new InvalidOperationException("The completion manifest has no persisted file entries to resume.");
        }

        var remaining = files.Where(file => file.Status != ExportFileStatus.Validated).ToArray();
        if (remaining.Length == 0)
        {
            throw new InvalidOperationException("All manifest files are already validated. This job does not have any remaining downloads.");
        }

        foreach (var file in remaining.Where(file => file.Status is
            ExportFileStatus.Failed or
            ExportFileStatus.Downloading or
            ExportFileStatus.RetryPending or
            ExportFileStatus.Paused))
        {
            var expectedFileVersion = file.Version;
            file.ResetForDownloadRetry("Operator requested recovery of the unfinished download set.");
            await exportStore.UpdateFileAsync(file, expectedFileVersion, cancellationToken).ConfigureAwait(false);
        }

        var validated = files.Where(file => file.Status == ExportFileStatus.Validated).ToArray();
        var downloaded = files.Where(file => file.Status is
            ExportFileStatus.Downloaded or
            ExportFileStatus.Validating or
            ExportFileStatus.Validated).ToArray();
        var totalBytesKnown = files.All(file =>
            file.ExpectedBytes is not null || file.Status is
                ExportFileStatus.Downloaded or
                ExportFileStatus.Validating or
                ExportFileStatus.Validated);
        var expectedJobVersion = job.Version;
        job.RecordFileProgress(
            downloaded.Length,
            validated.Length,
            downloaded.Sum(file => file.ActualBytes),
            totalBytesKnown ? files.Sum(file => file.ExpectedBytes ?? file.ActualBytes) : 0,
            validated.Sum(file => file.RecordCount));
        job.ClearFailure();

        var now = timeProvider.GetUtcNow();
        var operationId = Guid.NewGuid();
        var message = $"Queued {remaining.Length:N0} unfinished file(s) for download; {validated.Length:N0} already validated file(s) were retained.";
        var metadata = JsonSerializer.Serialize(
            new
            {
                remainingFileCount = remaining.Length,
                retainedValidatedFileCount = validated.Length,
                kickoffRequestSent = false,
                statusRequestSent = false,
            },
            JsonDefaults.Options);
        var evt = job.Transition(
            ExportJobState.QueuedForDownload,
            now,
            "RemainingDownloadsResumeRequested",
            message,
            operationId,
            operationId.ToString("N"),
            metadata);
        await exportStore.UpdateJobAsync(
            job,
            evt,
            Audit(now, "RemainingDownloadsResumeRequested", job.Id, "Succeeded", evt.CorrelationId),
            expectedJobVersion,
            cancellationToken).ConfigureAwait(false);
        workSignal.Signal();
        return new JobActionResult(job.Id, job.State, message);
    }

    public async Task<JobActionResult> RequestRemoteCancelAsync(Guid jobId, string typedConfirmation, CancellationToken cancellationToken)
    {
        RequireConfirmation(typedConfirmation, "CANCEL");
        var job = await RequiredJobAsync(jobId, cancellationToken).ConfigureAwait(false);
        if (!job.HasConfirmedRemoteJob || job.State is not (ExportJobState.Running or ExportJobState.WaitingForPoll or ExportJobState.Paused or ExportJobState.Unknown))
        {
            throw new InvalidOperationException("The remote export cannot be cancelled in its current state.");
        }

        return await TransitionLoadedAsync(job, ExportJobState.CancelRequested, "RemoteCancelRequested", "Operator confirmed remote export cancellation.", true, cancellationToken).ConfigureAwait(false);
    }

    public async Task<JobActionResult> RequestCleanupAsync(Guid jobId, string typedConfirmation, ILocalCommitService commitService, IManagedStorage storage, CancellationToken cancellationToken)
    {
        RequireConfirmation(typedConfirmation, "CLEANUP");
        var job = await RequiredJobAsync(jobId, cancellationToken).ConfigureAwait(false);
        if (job.State != ExportJobState.Committed || !job.HasConfirmedRemoteJob || job.ManifestSha256 is null)
        {
            throw new InvalidOperationException("Cleanup requires a committed job, a manifest hash, and the exact Content-Location.");
        }

        var vendor = await configurationStore.GetVendorAsync(job.VendorId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Vendor is missing.");
        var paths = storage.GetPaths(vendor.PluginId, job.SiteId, job.Id);
        if (!await commitService.VerifyAsync(paths, job.Id, job.ManifestSha256, cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("The local commit document is missing or invalid. Remote cleanup is blocked.");
        }

        return await TransitionLoadedAsync(job, ExportJobState.CleanupRequested, "RemoteCleanupRequested", "Operator confirmed cleanup of the remote export.", true, cancellationToken).ConfigureAwait(false);
    }

    public async Task<JobActionResult> ApprovePartialExportAsync(Guid jobId, string typedConfirmation, CancellationToken cancellationToken)
    {
        RequireConfirmation(typedConfirmation, "APPROVE");
        var job = await RequiredJobAsync(jobId, cancellationToken).ConfigureAwait(false);
        if (job.State != ExportJobState.HeldForReview)
        {
            throw new InvalidOperationException("Only a job held for review can be approved.");
        }

        var expected = job.Version;
        job.ApprovePartialExport();
        var now = timeProvider.GetUtcNow();
        var operation = Guid.NewGuid();
        var evt = job.Transition(ExportJobState.Committed, now, "PartialExportApproved", "Operator approved the partial export after reviewing serious OperationOutcome entries.", operation, operation.ToString("N"));
        await exportStore.UpdateJobAsync(job, evt, Audit(now, "PartialExportApproved", job.Id, "Succeeded", evt.CorrelationId), expected, cancellationToken).ConfigureAwait(false);
        workSignal.Signal();
        return new JobActionResult(job.Id, job.State, "Partial export approved.");
    }

    public async Task<JobActionResult> ArchiveAsync(Guid jobId, CancellationToken cancellationToken)
    {
        var job = await RequiredJobAsync(jobId, cancellationToken).ConfigureAwait(false);
        if (job.State is not (ExportJobState.Draft or ExportJobState.Completed or ExportJobState.Cancelled or ExportJobState.Expired or ExportJobState.FailedPermanent or ExportJobState.Committed or ExportJobState.HeldForReview))
        {
            throw new InvalidOperationException("Only inactive jobs can be archived.");
        }

        return await TransitionLoadedAsync(job, ExportJobState.Archived, "JobArchived", "Job was archived; its history and local files were retained.", false, cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteLocalJobAsync(Guid jobId, bool deleteDownloadedFiles, string typedConfirmation, IManagedStorage storage, CancellationToken cancellationToken)
    {
        RequireConfirmation(typedConfirmation, "DELETE");
        var job = await RequiredJobAsync(jobId, cancellationToken).ConfigureAwait(false);
        if (job.State is not (ExportJobState.Draft or ExportJobState.Archived or ExportJobState.Completed or ExportJobState.Cancelled or ExportJobState.Expired or ExportJobState.FailedPermanent))
        {
            throw new InvalidOperationException("Active or uncommitted jobs cannot be deleted. Pause/cancel or complete the workflow first.");
        }

        var vendor = await configurationStore.GetVendorAsync(job.VendorId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Vendor is missing.");
        if (deleteDownloadedFiles)
        {
            await storage.DeleteExportDataAsync(storage.GetPaths(vendor.PluginId, job.SiteId, job.Id), cancellationToken).ConfigureAwait(false);
        }

        var now = timeProvider.GetUtcNow();
        var correlation = Guid.NewGuid().ToString("N");
        var audit = Audit(now, deleteDownloadedFiles ? "LocalJobAndFilesDeleted" : "LocalJobMetadataDeleted", job.Id, "Succeeded", correlation);
        await exportStore.DeleteJobMetadataAsync(job.Id, audit, cancellationToken).ConfigureAwait(false);
    }

    public Task<PagedResult<ExportJob>> QueryAsync(JobQuery query, CancellationToken cancellationToken) => exportStore.QueryJobsAsync(query, cancellationToken);

    public Task<IReadOnlyList<ExportJobEvent>> GetTimelineAsync(Guid jobId, CancellationToken cancellationToken) => exportStore.GetTimelineAsync(jobId, cancellationToken);

    public Task<IReadOnlyList<ExportFile>> GetFilesAsync(Guid jobId, CancellationToken cancellationToken) => exportStore.GetFilesAsync(jobId, cancellationToken);

    private async Task<JobActionResult> TransitionAsync(Guid jobId, ExportJobState required, ExportJobState target, string eventType, string message, bool signal, CancellationToken cancellationToken)
    {
        var job = await RequiredJobAsync(jobId, cancellationToken).ConfigureAwait(false);
        if (job.State != required)
        {
            throw new InvalidOperationException($"Action requires state {required}; current state is {job.State}.");
        }

        return await TransitionLoadedAsync(job, target, eventType, message, signal, cancellationToken).ConfigureAwait(false);
    }

    private async Task<JobActionResult> TransitionLoadedAsync(ExportJob job, ExportJobState target, string eventType, string message, bool signal, CancellationToken cancellationToken)
    {
        var expected = job.Version;
        var now = timeProvider.GetUtcNow();
        var operation = Guid.NewGuid();
        var evt = job.Transition(target, now, eventType, message, operation, operation.ToString("N"));
        await exportStore.UpdateJobAsync(job, evt, Audit(now, eventType, job.Id, "Succeeded", evt.CorrelationId), expected, cancellationToken).ConfigureAwait(false);
        if (signal)
        {
            workSignal.Signal();
        }

        return new JobActionResult(job.Id, job.State, message);
    }

    private async Task<ExportJob> RequiredJobAsync(Guid jobId, CancellationToken cancellationToken) =>
        await exportStore.GetJobAsync(jobId, cancellationToken).ConfigureAwait(false)
        ?? throw new KeyNotFoundException($"Export job {jobId} was not found.");

    private AuditEvent Audit(DateTimeOffset now, string action, Guid jobId, string result, string correlationId) =>
        new(now, identityProvider.GetCurrentIdentity(), action, nameof(ExportJob), jobId.ToString("D"), result, correlationId);

    private static void RequireConfirmation(string actual, string expected)
    {
        if (!string.Equals(actual?.Trim(), expected, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Type {expected} exactly to confirm this operation.");
        }
    }

    private static void EnsureSameOrigin(Uri contentLocation, Uri siteBaseUri)
    {
        if (!string.Equals(contentLocation.Scheme, siteBaseUri.Scheme, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(contentLocation.Host, siteBaseUri.Host, StringComparison.OrdinalIgnoreCase) ||
            contentLocation.Port != siteBaseUri.Port)
        {
            throw new InvalidOperationException(
                $"Content-Location origin '{contentLocation.GetLeftPart(UriPartial.Authority)}' does not match the selected connection origin '{siteBaseUri.GetLeftPart(UriPartial.Authority)}'. Select the connection that was used to start this export.");
        }
    }
}
