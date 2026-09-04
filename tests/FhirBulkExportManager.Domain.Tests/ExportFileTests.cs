using FhirBulkExportManager.Domain;

namespace FhirBulkExportManager.Domain.Tests;

public sealed class ExportFileTests
{
    [Fact]
    public void Truncated_download_cannot_be_marked_complete()
    {
        var now = DateTimeOffset.Parse("2026-09-03T00:00:00Z");
        var file = new ExportFile(Guid.NewGuid(), Guid.NewGuid(), ExportFileCategory.Output, "Patient", "https://example.test/patient.ndjson", 1, "000001_Patient.ndjson", "output/000001_Patient.ndjson", now);
        file.Queue();
        file.BeginDownload(now, expectedBytes: 100, etag: null, lastModifiedUtc: null);

        Assert.Throws<InvalidOperationException>(() => file.CompleteDownload(now, actualBytes: 50, new string('a', 64)));
        Assert.Equal(ExportFileStatus.Downloading, file.Status);
    }

    [Fact]
    public void Downloaded_is_distinct_from_validated()
    {
        var now = DateTimeOffset.Parse("2026-09-03T00:00:00Z");
        var file = new ExportFile(Guid.NewGuid(), Guid.NewGuid(), ExportFileCategory.Output, "Patient", "https://example.test/patient.ndjson", 1, "000001_Patient.ndjson", "output/000001_Patient.ndjson", now);
        file.Queue();
        file.BeginDownload(now, expectedBytes: 10, etag: null, lastModifiedUtc: null);
        file.CompleteDownload(now, 10, new string('b', 64));

        Assert.Equal(ExportFileStatus.Downloaded, file.Status);
        Assert.Null(file.ValidatedAtUtc);
    }

    [Fact]
    public void Operator_retry_resets_only_an_unfinished_file_attempt_window()
    {
        var now = DateTimeOffset.Parse("2026-09-03T00:00:00Z");
        var file = new ExportFile(Guid.NewGuid(), Guid.NewGuid(), ExportFileCategory.Output, "Observation", "https://example.test/observation.ndjson", 1, "000001_Observation.ndjson", "output/000001_Observation.ndjson", now);
        file.Queue();
        file.BeginDownload(now, expectedBytes: 100, etag: "\"stable-etag\"", lastModifiedUtc: now.AddHours(-1));
        file.MarkFailed("VPN disconnected.");

        file.ResetForDownloadRetry("Operator requested recovery.");

        Assert.Equal(ExportFileStatus.RetryPending, file.Status);
        Assert.Equal(0, file.AttemptCount);
        Assert.Equal(100, file.ExpectedBytes);
        Assert.Equal("\"stable-etag\"", file.ETag);
        Assert.Equal(now.AddHours(-1), file.LastModifiedUtc);
        Assert.Contains("Operator requested recovery", file.LastError, StringComparison.Ordinal);
    }

    [Fact]
    public void Validated_file_cannot_be_reset_for_download()
    {
        var now = DateTimeOffset.Parse("2026-09-03T00:00:00Z");
        var file = new ExportFile(Guid.NewGuid(), Guid.NewGuid(), ExportFileCategory.Output, "Patient", "https://example.test/patient.ndjson", 1, "000001_Patient.ndjson", "output/000001_Patient.ndjson", now);
        file.Queue();
        file.BeginDownload(now, expectedBytes: 10, etag: null, lastModifiedUtc: null);
        file.CompleteDownload(now, 10, new string('a', 64));
        file.BeginValidation();
        file.CompleteValidation(now, 1);

        Assert.Throws<InvalidOperationException>(() => file.ResetForDownloadRetry("Must remain complete."));
        Assert.Equal(ExportFileStatus.Validated, file.Status);
    }
}
