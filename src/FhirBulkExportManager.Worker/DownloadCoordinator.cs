using System.Text.Json;
using FhirBulkExportManager.Application.Abstractions;
using FhirBulkExportManager.Domain;
using FhirBulkExportManager.PluginContracts;
using FhirBulkExportManager.Shared;

namespace FhirBulkExportManager.Worker;

public sealed record DownloadStepResult(bool AllValidated, bool PermanentFailure, bool PauseForLowDisk, string? SafeError);

public sealed class DownloadCoordinator(
    IExportStore exportStore,
    IConfigurationStore configurationStore,
    IManagedStorage storage,
    IFileDownloadService downloadService,
    INdjsonValidator validator,
    AuthenticationTokenProvider tokenProvider,
    SiteDownloadLimiter siteLimiter,
    TimeProvider timeProvider)
{
    private const int MaximumAttempts = 5;

    public async Task<DownloadStepResult> ProcessOneAsync(ExportJob job, IVendorPlugin plugin, CancellationToken cancellationToken)
    {
        var files = await exportStore.GetFilesAsync(job.Id, cancellationToken).ConfigureAwait(false);
        if (files.Any(x => x.Status == ExportFileStatus.Failed))
        {
            return new DownloadStepResult(false, true, false, "At least one manifest file exhausted its download or validation attempts.");
        }

        if (files.Count > 0 && files.All(x => x.Status == ExportFileStatus.Validated))
        {
            return new DownloadStepResult(true, false, false, null);
        }

        var file = files.FirstOrDefault(x => x.Status is ExportFileStatus.Downloading or ExportFileStatus.Validating or ExportFileStatus.RetryPending or ExportFileStatus.Queued or ExportFileStatus.Pending);
        if (file is null)
        {
            return new DownloadStepResult(false, true, false, "No processable file exists, but the manifest is not fully validated.");
        }

        if (file.Status is ExportFileStatus.Downloading or ExportFileStatus.Validating)
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

        using var lease = await siteLimiter.AcquireAsync(job.SiteId, plugin.Metadata.MaximumDownloadsPerSite, cancellationToken).ConfigureAwait(false);
        var vendor = await configurationStore.GetVendorAsync(job.VendorId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Vendor is missing.");
        var paths = storage.GetPaths(vendor.PluginId, job.SiteId, job.Id);
        await storage.EnsureExportDirectoriesAsync(paths, cancellationToken).ConfigureAwait(false);
        var finalPath = storage.GetAbsoluteFilePath(paths, file);
        var exactStatusUri = new Uri(job.RemoteStatusUrl ?? throw new InvalidOperationException("Content-Location is missing."), UriKind.Absolute);
        AccessToken? token = job.RequiresAccessToken ? await tokenProvider.GetAsync(job, plugin, cancellationToken).ConfigureAwait(false) : null;

        var beforeDownload = file.Version;
        file.BeginDownload(timeProvider.GetUtcNow(), file.ExpectedBytes, file.ETag, file.LastModifiedUtc);
        await exportStore.UpdateFileAsync(file, beforeDownload, cancellationToken).ConfigureAwait(false);
        try
        {
            var result = await downloadService.DownloadAsync(plugin, exactStatusUri, token, file, finalPath, cancellationToken).ConfigureAwait(false);
            await RecordHttpExchangesAsync(
                job,
                file,
                "FileDownloadReceived",
                "Download response headers were received and the response body was preserved in managed storage.",
                result.HttpExchanges,
                cancellationToken).ConfigureAwait(false);
            var beforeComplete = file.Version;
            file.CompleteDownload(timeProvider.GetUtcNow(), result.Bytes, result.Sha256);
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
                return new DownloadStepResult(false, true, false, file.LastError);
            }

            File.Move(result.TemporaryPath, finalPath, overwrite: true);
            var summaryJson = JsonSerializer.Serialize(validation.OperationOutcomeSeverityCounts, JsonDefaults.Options);
            file.CompleteValidation(timeProvider.GetUtcNow(), validation.RecordCount, summaryJson, validation.HasSeriousOperationOutcome);
            await exportStore.UpdateFileAsync(file, beforeValidated, cancellationToken).ConfigureAwait(false);
            return new DownloadStepResult(false, false, false, null);
        }
        catch (IOException exception) when (exception.Message.StartsWith("Insufficient free disk space", StringComparison.Ordinal))
        {
            var expected = file.Version;
            file.MarkRetry(SecurityRedactor.Redact(exception.Message));
            await exportStore.UpdateFileAsync(file, expected, cancellationToken).ConfigureAwait(false);
            return new DownloadStepResult(false, false, true, file.LastError);
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or JsonException)
        {
            if (exception is RecordedHttpExchangeException recorded)
            {
                await RecordHttpExchangesAsync(
                    job,
                    file,
                    "FileDownloadFailed",
                    "The file request failed; its sanitized request and available response details were preserved.",
                    recorded.HttpExchanges,
                    cancellationToken).ConfigureAwait(false);
            }

            var expected = file.Version;
            if (file.AttemptCount >= MaximumAttempts)
            {
                file.MarkFailed(SecurityRedactor.Redact(exception.Message));
            }
            else
            {
                file.MarkRetry(SecurityRedactor.Redact(exception.Message));
            }

            await exportStore.UpdateFileAsync(file, expected, cancellationToken).ConfigureAwait(false);
            return new DownloadStepResult(false, file.Status == ExportFileStatus.Failed, false, file.LastError);
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
