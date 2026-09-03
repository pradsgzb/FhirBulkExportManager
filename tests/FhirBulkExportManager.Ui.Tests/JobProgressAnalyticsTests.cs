using System.Text.Json;
using FhirBulkExportManager.App.Wpf;
using FhirBulkExportManager.Domain;
using FhirBulkExportManager.PluginContracts;
using FhirBulkExportManager.Shared;

namespace FhirBulkExportManager.Ui.Tests;

public sealed class JobProgressAnalyticsTests
{
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse(
        "2026-09-03T12:00:00Z",
        System.Globalization.CultureInfo.InvariantCulture);

    [Fact]
    public void Percentage_history_produces_an_average_rate_and_estimate()
    {
        var job = CreateJob("40% Completed");
        var events = new[]
        {
            ProgressEvent("10% Completed", Start),
            ProgressEvent("40% Completed", Start.AddMinutes(1)),
        };

        var result = JobProgressAnalytics.Calculate(job, events, [], Start.AddMinutes(1));

        Assert.Equal("40% Completed", result.LatestServerProgress);
        Assert.Contains("30.00 percentage points/min", result.AverageExportSpeedText, StringComparison.Ordinal);
        Assert.Contains("2m", result.EstimatedCompletionText, StringComparison.Ordinal);
        Assert.Equal(0d, result.FileCompletionPercent);
    }

    [Fact]
    public void Count_history_and_completed_downloads_produce_job_heuristics()
    {
        var job = CreateJob("Searched 700 of 1000 patients");
        var file = new ExportFile(
            Guid.NewGuid(), job.Id, ExportFileCategory.Output, "Patient",
            "https://example.test/file.ndjson", 1, "0001-Patient.ndjson", "output/0001-Patient.ndjson", Start);
        file.Queue();
        file.BeginDownload(Start, 1024 * 1024, null, null);
        file.CompleteDownload(Start.AddSeconds(2), 1024 * 1024, new string('a', 64));
        file.BeginValidation();
        file.CompleteValidation(Start.AddSeconds(3), 10);
        var events = new[]
        {
            ProgressEvent("Searched 100 of 1000 patients", Start),
            ProgressEvent("Searched 700 of 1000 patients", Start.AddMinutes(1)),
        };

        var result = JobProgressAnalytics.Calculate(job, events, [file], Start.AddMinutes(1));

        Assert.Contains("10.0 patients/sec", result.AverageExportSpeedText, StringComparison.Ordinal);
        Assert.Contains("30s", result.EstimatedCompletionText, StringComparison.Ordinal);
        Assert.Equal("1 / 1", result.FileCompletionText);
        Assert.Equal(100d, result.FileCompletionPercent);
        Assert.Contains("512.0 KB/s", result.AverageDownloadSpeedText, StringComparison.Ordinal);
    }

    private static ExportJob CreateJob(string progress)
    {
        var job = new ExportJob(
            Guid.NewGuid(), "Analytics", Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            "test", "1.0.0", ExportType.System, JobPriority.Normal, "C:\\Exports", Start);
        job.ConfirmRemoteJob("https://example.test/status/1", Start, Start.AddMinutes(2));
        job.RecordRemoteProgress(progress);
        return job;
    }

    private static ExportJobEvent ProgressEvent(string progress, DateTimeOffset timestamp)
    {
        var metadata = JsonSerializer.Serialize(new JobEventMetadataContract(progress, []), JsonDefaults.Options);
        return new ExportJobEvent(
            Guid.NewGuid(), ExportJobState.Running, ExportJobState.WaitingForPoll,
            "RemoteExportRunning", timestamp, "Running", Guid.NewGuid(), Guid.NewGuid().ToString("N"), metadata);
    }
}
