using FhirBulkExportManager.Shared;

namespace FhirBulkExportManager.Domain;

public sealed class ExportFile
{
    private ExportFile()
    {
    }

    public ExportFile(
        Guid id,
        Guid exportJobId,
        ExportFileCategory category,
        string? resourceType,
        string remoteUrl,
        int sequence,
        string safeLocalFileName,
        string relativeLocalPath,
        DateTimeOffset createdAtUtc,
        string? originalSuggestedName = null)
    {
        Id = id == Guid.Empty ? Guid.NewGuid() : id;
        ExportJobId = exportJobId;
        Category = category;
        ResourceType = resourceType?.Trim();
        RemoteUrl = Guard.NotNullOrWhiteSpace(remoteUrl, nameof(remoteUrl));
        Sequence = sequence;
        SafeLocalFileName = Guard.NotNullOrWhiteSpace(safeLocalFileName, nameof(safeLocalFileName));
        RelativeLocalPath = Guard.NotNullOrWhiteSpace(relativeLocalPath, nameof(relativeLocalPath));
        OriginalSuggestedName = originalSuggestedName?.Trim();
        Status = ExportFileStatus.Pending;
        CreatedAtUtc = createdAtUtc;
        Version = 1;
    }

    public Guid Id { get; private set; }
    public Guid ExportJobId { get; private set; }
    public ExportFileCategory Category { get; private set; }
    public string? ResourceType { get; private set; }
    public string RemoteUrl { get; private set; } = string.Empty;
    public int Sequence { get; private set; }
    public string? OriginalSuggestedName { get; private set; }
    public string SafeLocalFileName { get; private set; } = string.Empty;
    public string RelativeLocalPath { get; private set; } = string.Empty;
    public ExportFileStatus Status { get; private set; }
    public int AttemptCount { get; private set; }
    public long? ExpectedBytes { get; private set; }
    public long ActualBytes { get; private set; }
    public long RecordCount { get; private set; }
    public string? Sha256 { get; private set; }
    public string? ETag { get; private set; }
    public DateTimeOffset? LastModifiedUtc { get; private set; }
    public DateTimeOffset CreatedAtUtc { get; private set; }
    public DateTimeOffset? DownloadStartedAtUtc { get; private set; }
    public DateTimeOffset? DownloadCompletedAtUtc { get; private set; }
    public DateTimeOffset? ValidatedAtUtc { get; private set; }
    public string? LastError { get; private set; }
    public string ValidationSummaryJson { get; private set; } = "{}";
    public bool HasSeriousOperationOutcome { get; private set; }
    public long Version { get; private set; }

    public void Queue()
    {
        EnsureStatus(ExportFileStatus.Pending, ExportFileStatus.RetryPending, ExportFileStatus.Paused);
        Status = ExportFileStatus.Queued;
        Version++;
    }

    public void BeginDownload(DateTimeOffset now, long? expectedBytes, string? etag, DateTimeOffset? lastModifiedUtc)
    {
        EnsureStatus(ExportFileStatus.Queued, ExportFileStatus.RetryPending);
        Status = ExportFileStatus.Downloading;
        AttemptCount++;
        DownloadStartedAtUtc = now;
        ExpectedBytes = expectedBytes;
        ETag = etag;
        LastModifiedUtc = lastModifiedUtc;
        LastError = null;
        Version++;
    }

    public void CompleteDownload(
        DateTimeOffset now,
        long actualBytes,
        string sha256,
        string? etag = null,
        DateTimeOffset? lastModifiedUtc = null)
    {
        EnsureStatus(ExportFileStatus.Downloading);
        if (ExpectedBytes is { } expected && expected != actualBytes)
        {
            throw new InvalidOperationException($"Downloaded length {actualBytes} does not match expected length {expected}.");
        }

        ActualBytes = actualBytes;
        Sha256 = Guard.NotNullOrWhiteSpace(sha256, nameof(sha256));
        ETag = etag ?? ETag;
        LastModifiedUtc = lastModifiedUtc ?? LastModifiedUtc;
        DownloadCompletedAtUtc = now;
        Status = ExportFileStatus.Downloaded;
        Version++;
    }

    public void BeginValidation()
    {
        EnsureStatus(ExportFileStatus.Downloaded);
        Status = ExportFileStatus.Validating;
        Version++;
    }

    public void CompleteValidation(DateTimeOffset now, long recordCount, string validationSummaryJson = "{}", bool hasSeriousOperationOutcome = false)
    {
        EnsureStatus(ExportFileStatus.Validating);
        RecordCount = recordCount;
        ValidationSummaryJson = validationSummaryJson;
        HasSeriousOperationOutcome = hasSeriousOperationOutcome;
        ValidatedAtUtc = now;
        Status = ExportFileStatus.Validated;
        Version++;
    }

    public void MarkRetry(string safeError)
    {
        LastError = SecurityRedactor.Redact(safeError);
        Status = ExportFileStatus.RetryPending;
        Version++;
    }

    public void MarkFailed(string safeError)
    {
        LastError = SecurityRedactor.Redact(safeError);
        Status = ExportFileStatus.Failed;
        Version++;
    }

    public void ResetForDownloadRetry(string reason)
    {
        if (Status == ExportFileStatus.Validated)
        {
            throw new InvalidOperationException("A validated file cannot be reset for download.");
        }

        LastError = SecurityRedactor.Redact(reason);
        Status = ExportFileStatus.RetryPending;
        AttemptCount = 0;
        ActualBytes = 0;
        RecordCount = 0;
        Sha256 = null;
        DownloadCompletedAtUtc = null;
        ValidatedAtUtc = null;
        ValidationSummaryJson = "{}";
        HasSeriousOperationOutcome = false;
        Version++;
    }

    private void EnsureStatus(params ExportFileStatus[] allowed)
    {
        if (!allowed.Contains(Status))
        {
            throw new InvalidOperationException($"File operation is invalid while status is {Status}.");
        }
    }
}
