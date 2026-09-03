using FhirBulkExportManager.Shared;

namespace FhirBulkExportManager.Domain;

public sealed class ExportJob
{
    private static readonly IReadOnlyDictionary<ExportJobState, HashSet<ExportJobState>> LegalTransitions =
        new Dictionary<ExportJobState, HashSet<ExportJobState>>
        {
            [ExportJobState.Draft] = [ExportJobState.Queued, ExportJobState.WaitingForPoll, ExportJobState.Archived],
            [ExportJobState.Queued] = [ExportJobState.Authenticating, ExportJobState.Paused, ExportJobState.FailedPermanent],
            [ExportJobState.Authenticating] = [ExportJobState.Starting, ExportJobState.RetryPending, ExportJobState.FailedPermanent, ExportJobState.Paused],
            [ExportJobState.Starting] = [ExportJobState.Running, ExportJobState.WaitingForPoll, ExportJobState.Unknown, ExportJobState.RetryPending, ExportJobState.FailedPermanent],
            [ExportJobState.Running] = [ExportJobState.WaitingForPoll, ExportJobState.ManifestReady, ExportJobState.Paused, ExportJobState.CancelRequested, ExportJobState.RetryPending, ExportJobState.FailedPermanent, ExportJobState.Expired, ExportJobState.Unknown],
            [ExportJobState.WaitingForPoll] = [ExportJobState.Running, ExportJobState.ManifestReady, ExportJobState.Paused, ExportJobState.CancelRequested, ExportJobState.RetryPending, ExportJobState.Expired, ExportJobState.Unknown],
            [ExportJobState.Paused] = [ExportJobState.Queued, ExportJobState.WaitingForPoll, ExportJobState.QueuedForDownload, ExportJobState.CancelRequested, ExportJobState.Archived],
            [ExportJobState.ManifestReady] = [ExportJobState.QueuedForDownload, ExportJobState.HeldForReview, ExportJobState.FailedPermanent],
            [ExportJobState.QueuedForDownload] = [ExportJobState.Downloading, ExportJobState.Paused, ExportJobState.RetryPending],
            [ExportJobState.Downloading] = [ExportJobState.Validating, ExportJobState.Paused, ExportJobState.RetryPending, ExportJobState.FailedPermanent],
            [ExportJobState.Validating] = [ExportJobState.Committed, ExportJobState.HeldForReview, ExportJobState.FailedPermanent],
            [ExportJobState.HeldForReview] = [ExportJobState.QueuedForDownload, ExportJobState.Committed, ExportJobState.Archived],
            [ExportJobState.Committed] = [ExportJobState.CleanupRequested, ExportJobState.Completed, ExportJobState.HeldForReview, ExportJobState.Archived],
            [ExportJobState.CleanupRequested] = [ExportJobState.Completed, ExportJobState.RetryPending, ExportJobState.Committed, ExportJobState.HeldForReview],
            [ExportJobState.CancelRequested] = [ExportJobState.Cancelled, ExportJobState.RetryPending, ExportJobState.Unknown],
            [ExportJobState.RetryPending] = [ExportJobState.Authenticating, ExportJobState.Starting, ExportJobState.Running, ExportJobState.WaitingForPoll, ExportJobState.QueuedForDownload, ExportJobState.CleanupRequested, ExportJobState.FailedPermanent, ExportJobState.Paused],
            [ExportJobState.FailedTransient] = [ExportJobState.RetryPending, ExportJobState.Paused, ExportJobState.FailedPermanent],
            [ExportJobState.Unknown] = [ExportJobState.Running, ExportJobState.WaitingForPoll, ExportJobState.ManifestReady, ExportJobState.CancelRequested, ExportJobState.FailedPermanent, ExportJobState.Expired],
            [ExportJobState.Expired] = [ExportJobState.Archived],
            [ExportJobState.Cancelled] = [ExportJobState.Archived],
            [ExportJobState.FailedPermanent] = [ExportJobState.Archived],
            [ExportJobState.Completed] = [ExportJobState.Archived],
            [ExportJobState.Archived] = [],
        };

    private ExportJob()
    {
    }

    public ExportJob(
        Guid id,
        string name,
        Guid vendorId,
        Guid siteId,
        Guid credentialProfileId,
        string pluginId,
        string pluginVersion,
        ExportType exportType,
        JobPriority priority,
        string localDestination,
        DateTimeOffset createdAtUtc,
        string? groupId = null,
        DateTimeOffset? sinceUtc = null,
        string resourceTypesJson = "[]",
        string typeFiltersJson = "[]",
        string vendorOptionsJson = "{}",
        string outputFormat = "application/fhir+ndjson",
        string? description = null,
        bool automaticCleanupEnabled = false)
    {
        Id = id == Guid.Empty ? Guid.NewGuid() : id;
        Name = Guard.NotNullOrWhiteSpace(name, nameof(name));
        VendorId = vendorId;
        SiteId = siteId;
        CredentialProfileId = credentialProfileId;
        PluginId = Guard.NotNullOrWhiteSpace(pluginId, nameof(pluginId));
        PluginVersion = Guard.NotNullOrWhiteSpace(pluginVersion, nameof(pluginVersion));
        ExportType = exportType;
        Priority = priority;
        LocalDestination = Guard.NotNullOrWhiteSpace(localDestination, nameof(localDestination));
        GroupId = groupId?.Trim();
        if (exportType == ExportType.Group && string.IsNullOrWhiteSpace(GroupId))
        {
            throw new ArgumentException("Group ID is required for a Group export.", nameof(groupId));
        }

        SinceUtc = sinceUtc;
        ResourceTypesJson = resourceTypesJson;
        TypeFiltersJson = typeFiltersJson;
        VendorOptionsJson = vendorOptionsJson;
        OutputFormat = outputFormat;
        Description = description?.Trim();
        AutomaticCleanupEnabled = automaticCleanupEnabled;
        State = ExportJobState.Draft;
        CreatedAtUtc = createdAtUtc;
        Version = 1;
    }

    public Guid Id { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public string? Description { get; private set; }
    public Guid VendorId { get; private set; }
    public Guid SiteId { get; private set; }
    public Guid CredentialProfileId { get; private set; }
    public string PluginId { get; private set; } = string.Empty;
    public string PluginVersion { get; private set; } = string.Empty;
    public ExportType ExportType { get; private set; }
    public JobPriority Priority { get; private set; }
    public string? GroupId { get; private set; }
    public DateTimeOffset? SinceUtc { get; private set; }
    public string ResourceTypesJson { get; private set; } = "[]";
    public string TypeFiltersJson { get; private set; } = "[]";
    public string VendorOptionsJson { get; private set; } = "{}";
    public string OutputFormat { get; private set; } = "application/fhir+ndjson";
    public string LocalDestination { get; private set; } = string.Empty;
    public bool AutomaticCleanupEnabled { get; private set; }
    public bool PartialExportApproved { get; private set; }
    public ExportJobState State { get; private set; }
    public string? RemoteRequestUrl { get; private set; }
    public string? RemoteStatusUrl { get; private set; }
    public string? RemoteTransactionTime { get; private set; }
    public DateTimeOffset? RemoteExpiresAtUtc { get; private set; }
    public DateTimeOffset CreatedAtUtc { get; private set; }
    public DateTimeOffset? QueuedAtUtc { get; private set; }
    public DateTimeOffset? StartedAtUtc { get; private set; }
    public DateTimeOffset? LastPolledAtUtc { get; private set; }
    public DateTimeOffset? NextPollAtUtc { get; private set; }
    public DateTimeOffset? ManifestReceivedAtUtc { get; private set; }
    public DateTimeOffset? CommittedAtUtc { get; private set; }
    public DateTimeOffset? CleanupRequestedAtUtc { get; private set; }
    public DateTimeOffset? CancelRequestedAtUtc { get; private set; }
    public DateTimeOffset? CompletedAtUtc { get; private set; }
    public string? ProgressText { get; private set; }
    public string? ManifestJson { get; private set; }
    public string? ManifestSha256 { get; private set; }
    public bool RequiresAccessToken { get; private set; }
    public int OutputFileCount { get; private set; }
    public int DeletedFileCount { get; private set; }
    public int ErrorFileCount { get; private set; }
    public int DownloadedFileCount { get; private set; }
    public int ValidatedFileCount { get; private set; }
    public long TotalBytes { get; private set; }
    public long DownloadedBytes { get; private set; }
    public long TotalRecords { get; private set; }
    public int RetryCount { get; private set; }
    public string? LastErrorCode { get; private set; }
    public string? LastErrorMessage { get; private set; }
    public string? LeaseOwner { get; private set; }
    public DateTimeOffset? LeaseAcquiredAtUtc { get; private set; }
    public DateTimeOffset? LeaseExpiresAtUtc { get; private set; }
    public bool RemoteCleanupVerified { get; private set; }
    public long Version { get; private set; }

    public bool HasConfirmedRemoteJob => !string.IsNullOrWhiteSpace(RemoteStatusUrl);
    public bool IsTerminal => State is ExportJobState.Completed or ExportJobState.Cancelled or ExportJobState.Expired or ExportJobState.FailedPermanent or ExportJobState.Archived;

    public ExportJobEvent Transition(
        ExportJobState target,
        DateTimeOffset now,
        string eventType,
        string message,
        Guid operationId,
        string correlationId,
        string metadataJson = "{}")
    {
        if (target == State || !LegalTransitions.TryGetValue(State, out var targets) || !targets.Contains(target))
        {
            throw new InvalidOperationException($"Illegal export job transition: {State} -> {target}.");
        }

        if (target == ExportJobState.WaitingForPoll && !HasConfirmedRemoteJob)
        {
            throw new InvalidOperationException("Cannot wait for polling before the exact Content-Location is persisted.");
        }

        var previous = State;
        State = target;
        Version++;
        ApplyTransitionTimestamps(target, now);
        return new ExportJobEvent(Id, previous, target, eventType, now, message, operationId, correlationId, metadataJson);
    }

    public void RecordKickoffRequest(string requestUrl)
    {
        RemoteRequestUrl = Guard.NotNullOrWhiteSpace(requestUrl, nameof(requestUrl));
        Version++;
    }

    public void ConfirmRemoteJob(
        string exactContentLocation,
        DateTimeOffset now,
        DateTimeOffset? nextPollAtUtc,
        string? progress = null,
        DateTimeOffset? remoteStartedAtUtc = null)
    {
        if (!Uri.TryCreate(exactContentLocation, UriKind.Absolute, out _))
        {
            throw new ArgumentException("Content-Location must be an absolute URI.", nameof(exactContentLocation));
        }

        RemoteStatusUrl = exactContentLocation;
        if (remoteStartedAtUtc > now.AddMinutes(5))
        {
            throw new ArgumentOutOfRangeException(nameof(remoteStartedAtUtc), "Remote start time cannot be in the future.");
        }

        StartedAtUtc ??= remoteStartedAtUtc ?? now;
        NextPollAtUtc = nextPollAtUtc;
        if (!string.IsNullOrWhiteSpace(progress))
        {
            ProgressText = progress;
        }
        Version++;
    }

    public void RecordPoll(DateTimeOffset now, DateTimeOffset? nextPollAtUtc, string? progress)
    {
        if (!HasConfirmedRemoteJob)
        {
            throw new InvalidOperationException("Cannot poll without the exact persisted Content-Location.");
        }

        LastPolledAtUtc = now;
        NextPollAtUtc = nextPollAtUtc;
        if (!string.IsNullOrWhiteSpace(progress))
        {
            ProgressText = progress;
        }
        Version++;
    }

    public void RecordRemoteProgress(string? progress)
    {
        if (string.IsNullOrWhiteSpace(progress))
        {
            return;
        }

        ProgressText = progress;
        Version++;
    }

    public void RecordManifest(
        string exactJson,
        string sha256,
        string? transactionTime,
        bool requiresAccessToken,
        int outputFiles,
        int deletedFiles,
        int errorFiles,
        DateTimeOffset now)
    {
        ManifestJson = Guard.NotNullOrWhiteSpace(exactJson, nameof(exactJson));
        ManifestSha256 = Guard.NotNullOrWhiteSpace(sha256, nameof(sha256));
        RemoteTransactionTime = transactionTime;
        RequiresAccessToken = requiresAccessToken;
        OutputFileCount = outputFiles;
        DeletedFileCount = deletedFiles;
        ErrorFileCount = errorFiles;
        ManifestReceivedAtUtc = now;
        NextPollAtUtc = null;
        Version++;
    }

    public void RecordFileProgress(int downloadedFiles, int validatedFiles, long downloadedBytes, long totalBytes, long totalRecords)
    {
        DownloadedFileCount = downloadedFiles;
        ValidatedFileCount = validatedFiles;
        DownloadedBytes = downloadedBytes;
        TotalBytes = totalBytes;
        TotalRecords = totalRecords;
        Version++;
    }

    public void RecordFailure(string code, string safeMessage, bool incrementRetry)
    {
        LastErrorCode = Guard.NotNullOrWhiteSpace(code, nameof(code));
        LastErrorMessage = SecurityRedactor.Redact(safeMessage);
        if (incrementRetry)
        {
            RetryCount++;
        }

        Version++;
    }

    public void ScheduleRetry(DateTimeOffset retryAtUtc, string code, string safeMessage)
    {
        NextPollAtUtc = retryAtUtc;
        RecordFailure(code, safeMessage, incrementRetry: true);
    }

    public void ClearKickoffJournalAfterConfirmedRejection()
    {
        if (HasConfirmedRemoteJob)
        {
            throw new InvalidOperationException("Cannot clear kickoff request after a remote job was confirmed.");
        }

        RemoteRequestUrl = null;
        Version++;
    }

    public void ClearFailure()
    {
        LastErrorCode = null;
        LastErrorMessage = null;
        Version++;
    }

    public bool TryAcquireLease(string owner, DateTimeOffset now, TimeSpan duration)
    {
        if (LeaseExpiresAtUtc is { } expiry && expiry > now && !string.Equals(LeaseOwner, owner, StringComparison.Ordinal))
        {
            return false;
        }

        LeaseOwner = Guard.NotNullOrWhiteSpace(owner, nameof(owner));
        LeaseAcquiredAtUtc = now;
        LeaseExpiresAtUtc = now.Add(duration);
        Version++;
        return true;
    }

    public void RenewLease(string owner, DateTimeOffset now, TimeSpan duration)
    {
        if (!string.Equals(LeaseOwner, owner, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Only the lease owner can renew a job lease.");
        }

        LeaseExpiresAtUtc = now.Add(duration);
        Version++;
    }

    public void ReleaseLease(string owner)
    {
        if (!string.Equals(LeaseOwner, owner, StringComparison.Ordinal))
        {
            return;
        }

        LeaseOwner = null;
        LeaseAcquiredAtUtc = null;
        LeaseExpiresAtUtc = null;
        Version++;
    }

    public void ApprovePartialExport()
    {
        PartialExportApproved = true;
        Version++;
    }

    public void MarkCleanupVerified()
    {
        if (State != ExportJobState.CleanupRequested)
        {
            throw new InvalidOperationException("Cleanup can be verified only after it was requested.");
        }

        RemoteCleanupVerified = true;
        Version++;
    }

    private void ApplyTransitionTimestamps(ExportJobState target, DateTimeOffset now)
    {
        switch (target)
        {
            case ExportJobState.Queued:
                QueuedAtUtc ??= now;
                break;
            case ExportJobState.Starting:
                StartedAtUtc ??= now;
                break;
            case ExportJobState.Committed:
                CommittedAtUtc = now;
                break;
            case ExportJobState.CleanupRequested:
                CleanupRequestedAtUtc = now;
                break;
            case ExportJobState.CancelRequested:
                CancelRequestedAtUtc = now;
                break;
            case ExportJobState.Completed:
                CompletedAtUtc = now;
                break;
        }
    }
}
