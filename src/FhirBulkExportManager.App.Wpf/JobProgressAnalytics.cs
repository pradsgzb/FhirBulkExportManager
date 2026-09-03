using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using FhirBulkExportManager.Domain;
using FhirBulkExportManager.PluginContracts;
using FhirBulkExportManager.Shared;

namespace FhirBulkExportManager.App.Wpf;

public sealed record JobDashboardSnapshot(
    string LatestServerProgress,
    string FileCompletionText,
    double FileCompletionPercent,
    string DataDownloadedText,
    string AverageExportSpeedText,
    string AverageDownloadSpeedText,
    string EstimatedCompletionText,
    string ElapsedText,
    string NextCheckText)
{
    public static JobDashboardSnapshot Empty { get; } = new(
        "No X-Progress received yet",
        "0 / 0",
        0,
        "0 B",
        "Not enough progress history",
        "No completed downloads",
        "Not enough data",
        "—",
        "—");
}

public static class JobProgressAnalytics
{
    private static readonly Regex PercentPattern = new(
        @"(?<value>\d+(?:\.\d+)?)\s*%",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex CountPattern = new(
        @"(?<current>[\d,]+)\s+of\s+(?<total>[\d,]+)(?:\s+(?<unit>[A-Za-z]+))?",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    public static JobDashboardSnapshot Calculate(
        ExportJob? job,
        IReadOnlyCollection<ExportJobEvent> events,
        IReadOnlyCollection<ExportFile> files,
        DateTimeOffset now)
    {
        if (job is null)
        {
            return JobDashboardSnapshot.Empty;
        }

        var samples = ReadProgressSamples(events);
        var latestProgress = !string.IsNullOrWhiteSpace(job.ProgressText)
            ? job.ProgressText
            : samples.OrderByDescending(sample => sample.TimestampUtc).FirstOrDefault()?.Text;
        latestProgress = string.IsNullOrWhiteSpace(latestProgress) ? "No X-Progress received yet" : latestProgress;

        var (exportSpeed, eta, _) = CalculateExportRate(job, latestProgress, samples);
        var totalFiles = files.Count > 0
            ? files.Count
            : job.OutputFileCount + job.DeletedFileCount + job.ErrorFileCount;
        var downloadedFromFiles = files.Count(file => file.Status is ExportFileStatus.Downloaded or ExportFileStatus.Validating or ExportFileStatus.Validated);
        var downloadedFiles = Math.Min(totalFiles, Math.Max(job.DownloadedFileCount, downloadedFromFiles));
        var filePercent = totalFiles <= 0 ? 0 : downloadedFiles * 100d / totalFiles;
        var downloadedBytes = Math.Max(
            job.DownloadedBytes,
            files.Where(file => file.Status is ExportFileStatus.Downloaded or ExportFileStatus.Validating or ExportFileStatus.Validated)
                .Sum(file => file.ActualBytes));
        var knownTotalBytes = job.TotalBytes > 0
            ? job.TotalBytes
            : files.Where(file => file.ExpectedBytes is > 0).Sum(file => file.ExpectedBytes!.Value);
        var dataText = knownTotalBytes > 0
            ? $"{FormatBytes(downloadedBytes)} / {FormatBytes(knownTotalBytes)}"
            : FormatBytes(downloadedBytes);

        var completedTransfers = files
            .Where(file => file.ActualBytes > 0 &&
                file.DownloadStartedAtUtc is { } started &&
                file.DownloadCompletedAtUtc is { } completed &&
                completed > started)
            .ToArray();
        var transferSeconds = completedTransfers.Sum(file =>
            (file.DownloadCompletedAtUtc!.Value - file.DownloadStartedAtUtc!.Value).TotalSeconds);
        var transferBytes = completedTransfers.Sum(file => file.ActualBytes);
        var downloadSpeed = transferSeconds > 0
            ? $"{FormatBytes(transferBytes / transferSeconds)}/s average"
            : "No completed downloads";

        var startedAt = job.StartedAtUtc ?? job.CreatedAtUtc;
        var elapsedEnd = job.CompletedAtUtc ?? now;
        var elapsed = elapsedEnd > startedAt ? FormatDuration(elapsedEnd - startedAt) : "Less than a second";

        return new JobDashboardSnapshot(
            latestProgress,
            $"{downloadedFiles:N0} / {totalFiles:N0}",
            Math.Clamp(filePercent, 0, 100),
            dataText,
            exportSpeed,
            downloadSpeed,
            eta,
            elapsed,
            DateTimeDisplayPolicy.Format(job.NextPollAtUtc));
    }

    private static (string Speed, string Eta, double Percent) CalculateExportRate(
        ExportJob job,
        string latestProgress,
        IReadOnlyList<ProgressSample> samples)
    {
        var latestCount = CountPattern.Match(latestProgress);
        if (latestCount.Success &&
            TryLong(latestCount.Groups["current"].Value, out var current) &&
            TryLong(latestCount.Groups["total"].Value, out var total) && total > 0)
        {
            var unit = string.IsNullOrWhiteSpace(latestCount.Groups["unit"].Value)
                ? "items"
                : latestCount.Groups["unit"].Value.ToLowerInvariant();
            var countSamples = samples
                .Select(sample => ToCountSample(sample, total))
                .Where(sample => sample is not null)
                .Select(sample => sample!.Value)
                .OrderBy(sample => sample.TimestampUtc)
                .ToArray();
            var rate = Rate(countSamples.Select(sample => (sample.TimestampUtc, (double)sample.Value)).ToArray(), job.StartedAtUtc);
            var speed = rate > 0 ? FormatCountRate(rate, unit) : "Not enough count history";
            var remaining = Math.Max(0, total - current);
            var eta = rate > 0
                ? remaining == 0 ? "Server reports search complete" : $"≈ {FormatDuration(TimeSpan.FromSeconds(remaining / rate))} remaining"
                : "Not enough data";
            return (speed, eta, Math.Clamp(current * 100d / total, 0, 100));
        }

        var latestPercent = PercentPattern.Match(latestProgress);
        var currentPercent = latestPercent.Success &&
            double.TryParse(latestPercent.Groups["value"].Value, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsedPercent)
            ? parsedPercent
            : 0;
        var percentSamples = samples
            .Select(ToPercentSample)
            .Where(sample => sample is not null)
            .Select(sample => sample!.Value)
            .OrderBy(sample => sample.TimestampUtc)
            .ToArray();
        var percentRatePerSecond = Rate(percentSamples.Select(sample => (sample.TimestampUtc, sample.Value)).ToArray(), job.StartedAtUtc);
        var percentSpeed = percentRatePerSecond > 0
            ? $"{percentRatePerSecond * 60:N2} percentage points/min average"
            : "Not enough percentage history";
        var percentRemaining = Math.Max(0, 100 - currentPercent);
        var percentEta = percentRatePerSecond > 0
            ? percentRemaining == 0 ? "Server reports export complete" : $"≈ {FormatDuration(TimeSpan.FromSeconds(percentRemaining / percentRatePerSecond))} remaining"
            : "Not enough data";
        return (percentSpeed, percentEta, Math.Clamp(currentPercent, 0, 100));
    }

    private static IReadOnlyList<ProgressSample> ReadProgressSamples(IEnumerable<ExportJobEvent> events)
    {
        var result = new List<ProgressSample>();
        foreach (var jobEvent in events)
        {
            if (string.IsNullOrWhiteSpace(jobEvent.MetadataJson) || jobEvent.MetadataJson == "{}")
            {
                continue;
            }

            try
            {
                var metadata = JsonSerializer.Deserialize<JobEventMetadataContract>(jobEvent.MetadataJson, JsonDefaults.Options);
                if (!string.IsNullOrWhiteSpace(metadata?.Progress))
                {
                    result.Add(new ProgressSample(jobEvent.TimestampUtc, metadata.Progress));
                }
            }
            catch (JsonException)
            {
                // Invalid legacy metadata remains visible in history but cannot contribute to analytics.
            }
        }

        return result;
    }

    private static (DateTimeOffset TimestampUtc, long Value)? ToCountSample(ProgressSample sample, long requiredTotal)
    {
        var match = CountPattern.Match(sample.Text);
        return match.Success &&
               TryLong(match.Groups["current"].Value, out var current) &&
               TryLong(match.Groups["total"].Value, out var total) &&
               total == requiredTotal
            ? (sample.TimestampUtc, current)
            : null;
    }

    private static (DateTimeOffset TimestampUtc, double Value)? ToPercentSample(ProgressSample sample)
    {
        var match = PercentPattern.Match(sample.Text);
        return match.Success && double.TryParse(match.Groups["value"].Value, NumberStyles.Number, CultureInfo.InvariantCulture, out var value)
            ? (sample.TimestampUtc, value)
            : null;
    }

    private static double Rate(
        IReadOnlyList<(DateTimeOffset TimestampUtc, double Value)> orderedSamples,
        DateTimeOffset? startedAtUtc)
    {
        var useful = orderedSamples
            .Where((sample, index) => index == 0 || sample.Value != orderedSamples[index - 1].Value)
            .ToArray();
        if (useful.Length >= 2)
        {
            var seconds = (useful[^1].TimestampUtc - useful[0].TimestampUtc).TotalSeconds;
            var delta = useful[^1].Value - useful[0].Value;
            return seconds > 0 && delta > 0 ? delta / seconds : 0;
        }

        if (useful.Length == 1 && startedAtUtc is { } started && useful[0].TimestampUtc > started && useful[0].Value > 0)
        {
            return useful[0].Value / (useful[0].TimestampUtc - started).TotalSeconds;
        }

        return 0;
    }

    private static string FormatCountRate(double perSecond, string unit) => perSecond switch
    {
        >= 1 => $"{perSecond:N1} {unit}/sec average",
        > 0 => $"{perSecond * 60:N1} {unit}/min average",
        _ => "Not enough count history",
    };

    private static bool TryLong(string value, out long result) =>
        long.TryParse(value.Replace(",", string.Empty, StringComparison.Ordinal), NumberStyles.Integer, CultureInfo.InvariantCulture, out result);

    private static string FormatBytes(double bytes) => bytes switch
    {
        >= 1024d * 1024d * 1024d => $"{bytes / (1024d * 1024d * 1024d):N2} GB",
        >= 1024d * 1024d => $"{bytes / (1024d * 1024d):N2} MB",
        >= 1024d => $"{bytes / 1024d:N1} KB",
        _ => $"{bytes:N0} B",
    };

    private static string FormatDuration(TimeSpan duration)
    {
        if (duration.TotalDays >= 1)
        {
            return $"{(int)duration.TotalDays}d {duration.Hours}h";
        }

        if (duration.TotalHours >= 1)
        {
            return $"{(int)duration.TotalHours}h {duration.Minutes}m";
        }

        if (duration.TotalMinutes >= 1)
        {
            return $"{(int)duration.TotalMinutes}m {duration.Seconds}s";
        }

        return $"{Math.Max(0, (int)Math.Ceiling(duration.TotalSeconds))}s";
    }

    private sealed record ProgressSample(DateTimeOffset TimestampUtc, string Text);
}
