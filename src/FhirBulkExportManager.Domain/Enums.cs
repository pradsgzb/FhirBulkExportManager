namespace FhirBulkExportManager.Domain;

public enum ExportJobState
{
    Draft,
    Queued,
    Authenticating,
    Starting,
    Running,
    WaitingForPoll,
    Paused,
    ManifestReady,
    QueuedForDownload,
    Downloading,
    Validating,
    HeldForReview,
    Committed,
    CleanupRequested,
    Completed,
    CancelRequested,
    Cancelled,
    RetryPending,
    FailedTransient,
    FailedPermanent,
    Expired,
    Unknown,
    Archived,
}

public enum ExportType
{
    System,
    Group,
    Patient,
}

public enum JobPriority
{
    Low = 0,
    Normal = 10,
    High = 20,
    Critical = 30,
}

public enum ExportFileCategory
{
    Output,
    Deleted,
    Error,
}

public enum ExportFileStatus
{
    Pending,
    Queued,
    Downloading,
    Paused,
    RetryPending,
    Downloaded,
    Validating,
    Validated,
    Failed,
}

public enum AttemptOutcome
{
    Started,
    Succeeded,
    TransientFailure,
    PermanentFailure,
    Cancelled,
    Ambiguous,
}

