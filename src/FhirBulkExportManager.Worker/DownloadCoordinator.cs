using System.Net;
using System.Text.Json;
using FhirBulkExportManager.Application.Abstractions;
using FhirBulkExportManager.Application.Services;
using FhirBulkExportManager.Domain;
using FhirBulkExportManager.PluginContracts;
using FhirBulkExportManager.Shared;

namespace FhirBulkExportManager.Worker;

public sealed record DownloadStepResult(
    bool AllValidated,
    bool PermanentFailure,
    bool PauseForLowDisk,
    bool RetryLater,
    string? SafeError);

public sealed class DownloadCoordinator(
    IExportStore exportStore,
    IConfigurationStore configurationStore,
    IManagedStorage storage,
    IFileDownloadService downloadService,
    INdjsonValidator validator,
    AuthenticationTokenProvider tokenProvider,
    SiteDownloadLimiter siteLimiter,
    ApplicationSettingsService applicationSettings,
    TimeProvider timeProvider)
{
    public async Task<DownloadStepResult> ProcessOneAsync(ExportJob job, IVendorPlugin plugin, CancellationToken cancellationToken)
    {
        var files = await exportStore.GetFilesAsync(job.Id, cancellationToken).ConfigureAwait(false);
        if (files.Any(x => x.Status == ExportFileStatus.Failed))
        {
            return new DownloadStepResult(false, true, false, false, "At least one manifest file requires an operator-requested download recovery.");
        }

        if (files.Count > 0 && files.All(x => x.Status == ExportFileStatus.Validated))
        {
            return new DownloadStepResult(true, false, false, false, null);
        }

        var file = files.FirstOrDefault(x => x.Status is ExportFileStatus.Downloading or ExportFileStatus.Validating or ExportFileStatus.Downloaded or ExportFileStatus.RetryPending or ExportFileStatus.Queued or ExportFileStatus.Pending);
        if (file is null)
        {
            return new DownloadStepResult(false, true, false, false, "No processable file exists, but the manifest is not fully validated.");
        }

        if (file.Status == ExportFileStatus.Downloading)
        {
            var expected = file.Version;
            file.MarkRetry("Recovered an interrupted local file operation after application restart.");
            await exportStore.UpdateFileAsync(file, expected, cancellationToken).ConfigureAwait(false);
        }

        if (file.Status == ExportFileStatus.Pending)
        {
            var expected = file.Version;
            file.Queue();
            await exportStore.UpdateFileAsync(file, expected, cancellationToken).ConfigureAwait(false);
        }

        var vendor = await configurationStore.GetVendorAsync(job.VendorId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Vendor is missing.");
        var paths = storage.GetPaths(vendor.PluginId, job.SiteId, job.Id);
        await storage.EnsureExportDirectoriesAsync(paths, cancellationToken).ConfigureAwait(false);
        var finalPath = storage.GetAbsoluteFilePath(paths, file);
        if (file.Status is ExportFileStatus.Downloaded or ExportFileStatus.Validating)
        {
            var localValidation = await TryCompleteLocalValidationAsync(job, file, finalPath, cancellationToken).ConfigureAwait(false);
            if (localValidation is not null)
            {
                return localValidation;
            }
        }

        using var lease = await siteLimiter.AcquireAsync(job.SiteId, plugin.Metadata.MaximumDownloadsPerSite, cancellationToken).ConfigureAwait(false);
        var exactStatusUri = new Uri(job.RemoteStatusUrl ?? throw new InvalidOperationException("Content-Location is missing."), UriKind.Absolute);
        try
        {
            AccessToken? token = job.RequiresAccessToken
                ? await tokenProvider.GetAsync(job, plugin, cancellationToken).ConfigureAwait(false)
                : null;
            var beforeDownload = file.Version;
            file.BeginDownload(timeProvider.GetUtcNow(), file.ExpectedBytes, file.ETag, file.LastModifiedUtc);
            await exportStore.UpdateFileAsync(file, beforeDownload, cancellationToken).ConfigureAwait(false);
            var result = await downloadService.DownloadAsync(plugin, exactStatusUri, token, file, finalPath, cancellationToken).ConfigureAwait(false);
            await RecordHttpExchangesAsync(
                job,
                file,
                "FileDownloadReceived",
                "Download response headers were received and the response body was preserved in managed storage.",
                result.HttpExchanges,
                cancellationToken).ConfigureAwait(false);
            var beforeComplete = file.Version;
            file.CompleteDownload(timeProvider.GetUtcNow(), result.Bytes, result.Sha256, result.ETag, result.LastModifiedUtc);
            await exportStore.UpdateFileAsync(file, beforeComplete, cancellationToken).ConfigureAwait(false);

            var beforeValidation = file.Version;
            file.BeginValidation();
            await exportStore.UpdateFileAsync(file, beforeValidation, cancellationToken).ConfigureAwait(false);
            var validation = await validator.ValidateAsync(result.TemporaryPath, file.ResourceType, file.Category, cancellationToken).ConfigureAwait(false);
            var beforeValidated = file.Version;
            if (!validation.IsValid)
            {
                file.MarkFailed(string.Join(" | ", validation.Errors.Take(10)));
                await exportStore.UpdateFileAsync(file, beforeValidated, cancellationToken).ConfigureAwait(false);
                return new DownloadStepResult(false, true, false, false, file.LastError);
            }

            File.Move(result.TemporaryPath, finalPath, overwrite: true);
            var summaryJson = JsonSerializer.Serialize(validation.OperationOutcomeSeverityCounts, JsonDefaults.Options);
            file.CompleteValidation(timeProvider.GetUtcNow(), validation.RecordCount, summaryJson, validation.HasSeriousOperationOutcome);
            await exportStore.UpdateFileAsync(file, beforeValidated, cancellationToken).ConfigureAwait(false);
            return new DownloadStepResult(false, false, false, false, null);
        }
        catch (IOException exception) when (exception.Message.StartsWith("Insufficient free disk space", StringComparison.Ordinal))
        {
            var expected = file.Version;
            file.MarkRetry(SecurityRedactor.Redact(exception.Message));
            await exportStore.UpdateFileAsync(file, expected, cancellationToken).ConfigureAwait(false);
            return new DownloadStepResult(false, false, true, false, file.LastError);
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or JsonException)
        {
            if (exception is RecordedHttpExchangeException recorded)
            {
                await RecordHttpExchangesAsync(
                    job,
                    file,
                    "FileDownloadFailed",
                    "Authentication or the file request failed; its sanitized request and available response details were preserved.",
                    recorded.HttpExchanges,
                    cancellationToken).ConfigureAwait(false);
            }

            if (exception is HttpRequestException { StatusCode: HttpStatusCode.Unauthorized })
            {
                tokenProvider.Invalidate(job.CredentialProfileId);
            }

            var failureKind = DownloadRetryPolicy.Classify(exception);
            var automaticResume = applicationSettings.Current.AutomaticallyResumeDownloads;
            var attemptsUsed = Math.Max(file.AttemptCount, job.RetryCount + 1);
            var expected = file.Version;
            if (!DownloadRetryPolicy.ShouldRetry(failureKind, automaticResume, attemptsUsed))
            {
                file.MarkFailed(SecurityRedactor.Redact(exception.Message));
            }
            else
            {
                file.MarkRetry(SecurityRedactor.Redact(exception.Message));
            }

            await exportStore.UpdateFileAsync(file, expected, cancellationToken).ConfigureAwait(false);
            return new DownloadStepResult(
                false,
                file.Status == ExportFileStatus.Failed,
                false,
                file.Status == ExportFileStatus.RetryPending,
                file.LastError);
        }
    }

    private async Task<DownloadStepResult?> TryCompleteLocalValidationAsync(
        ExportJob job,
        ExportFile file,
        string finalPath,
        CancellationToken cancellationToken)
    {
        var partialPath = finalPath + ".partial";
        var validationPath = File.Exists(partialPath)
            ? partialPath
            : File.Exists(finalPath) ? finalPath : null;
        if (validationPath is null)
        {
            var missingExpectedVersion = file.Version;
            file.MarkRetry("The downloaded file was not found in managed storage and will be downloaded again.");
            await exportStore.UpdateFileAsync(file, missingExpectedVersion, cancellationToken).ConfigureAwait(false);
            return null;
        }

        try
        {
            if (file.Status == ExportFileStatus.Downloaded)
            {
                var downloadedExpectedVersion = file.Version;
                file.BeginValidation();
                await exportStore.UpdateFileAsync(file, downloadedExpectedVersion, cancellationToken).ConfigureAwait(false);
            }

            var validation = await validator.ValidateAsync(validationPath, file.ResourceType, file.Category, cancellationToken).ConfigureAwait(false);
            var validationExpectedVersion = file.Version;
            if (!validation.IsValid)
            {
                file.MarkFailed(string.Join(" | ", validation.Errors.Take(10)));
                await exportStore.UpdateFileAsync(file, validationExpectedVersion, cancellationToken).ConfigureAwait(false);
                return new DownloadStepResult(false, true, false, false, file.LastError);
            }

            if (!string.Equals(validationPath, finalPath, StringComparison.Ordinal))
            {
                File.Move(validationPath, finalPath, overwrite: true);
            }

            var summaryJson = JsonSerializer.Serialize(validation.OperationOutcomeSeverityCounts, JsonDefaults.Options);
            file.CompleteValidation(timeProvider.GetUtcNow(), validation.RecordCount, summaryJson, validation.HasSeriousOperationOutcome);
            await exportStore.UpdateFileAsync(file, validationExpectedVersion, cancellationToken).ConfigureAwait(false);
            return new DownloadStepResult(false, false, false, false, null);
        }
        catch (Exception exception) when (exception is IOException or JsonException)
        {
            var failureKind = DownloadRetryPolicy.Classify(exception);
            var attemptsUsed = Math.Max(file.AttemptCount, job.RetryCount + 1);
            var expectedVersion = file.Version;
            if (DownloadRetryPolicy.ShouldRetry(
                failureKind,
                applicationSettings.Current.AutomaticallyResumeDownloads,
                attemptsUsed))
            {
                file.MarkRetry(SecurityRedactor.Redact(exception.Message));
            }
            else
            {
                file.MarkFailed(SecurityRedactor.Redact(exception.Message));
            }

            await exportStore.UpdateFileAsync(file, expectedVersion, cancellationToken).ConfigureAwait(false);
            return new DownloadStepResult(
                false,
                file.Status == ExportFileStatus.Failed,
                false,
                file.Status == ExportFileStatus.RetryPending,
                file.LastError);
        }
    }

    private async Task RecordHttpExchangesAsync(
        ExportJob job,
        ExportFile file,
        string eventType,
        string message,
        IReadOnlyList<HttpExchangeContract>? exchanges,
        CancellationToken cancellationToken)
    {
        if (exchanges is not { Count: > 0 })
        {
            return;
        }

        var operationId = Guid.NewGuid();
        var metadata = JsonSerializer.Serialize(
            new JobEventMetadataContract(null, exchanges),
            JsonDefaults.Options);
        var jobEvent = new ExportJobEvent(
            job.Id,
            job.State,
            job.State,
            eventType,
            timeProvider.GetUtcNow(),
            $"{message} File: {file.SafeLocalFileName}",
            operationId,
            operationId.ToString("N"),
            metadata);
        await exportStore.AddJobEventAsync(jobEvent, cancellationToken).ConfigureAwait(false);
    }
}

internal enum DownloadFailureKind
{
    Permanent,
    Transient,
    Authentication,
}

internal static class DownloadRetryPolicy
{
    private const int MaximumAttemptsWithoutAutomaticResume = 5;

    public static DownloadFailureKind Classify(Exception exception)
    {
        if (exception is IOException)
        {
            return DownloadFailureKind.Transient;
        }

        if (exception is not HttpRequestException httpException)
        {
            return DownloadFailureKind.Permanent;
        }

        if (httpException.StatusCode == HttpStatusCode.Unauthorized)
        {
            return DownloadFailureKind.Authentication;
        }

        var numericStatusCode = (int?)httpException.StatusCode;
        return httpException.StatusCode is null or
            HttpStatusCode.RequestTimeout or
            HttpStatusCode.RequestedRangeNotSatisfiable or
            HttpStatusCode.TooManyRequests ||
            numericStatusCode is >= 200 and < 300 or >= 500
            ? DownloadFailureKind.Transient
            : DownloadFailureKind.Permanent;
    }

    public static bool ShouldRetry(DownloadFailureKind failureKind, bool automaticResume, int attemptsUsed) =>
        failureKind switch
        {
            DownloadFailureKind.Transient => automaticResume || attemptsUsed < MaximumAttemptsWithoutAutomaticResume,
            DownloadFailureKind.Authentication => attemptsUsed < MaximumAttemptsWithoutAutomaticResume,
            _ => false,
        };
}
