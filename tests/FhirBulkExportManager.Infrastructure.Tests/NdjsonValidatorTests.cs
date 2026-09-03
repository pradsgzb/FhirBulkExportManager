using System.Text;
using FhirBulkExportManager.Domain;
using FhirBulkExportManager.Infrastructure.Validation;

namespace FhirBulkExportManager.Infrastructure.Tests;

public sealed class NdjsonValidatorTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "fhir-validator-tests-" + Guid.NewGuid().ToString("N"));

    public NdjsonValidatorTests() => Directory.CreateDirectory(directory);

    [Fact]
    public async Task Validates_ndjson_as_a_stream_and_counts_records()
    {
        var path = Path.Combine(directory, "patients.ndjson");
        await File.WriteAllTextAsync(path, "{\"resourceType\":\"Patient\",\"id\":\"1\"}\n{\"resourceType\":\"Patient\",\"id\":\"2\"}\n", Encoding.UTF8);

        var result = await new NdjsonValidator().ValidateAsync(path, "Patient", ExportFileCategory.Output, CancellationToken.None);

        Assert.True(result.IsValid);
        Assert.Equal(2, result.RecordCount);
    }

    [Fact]
    public async Task Rejects_truncated_json_line()
    {
        var path = Path.Combine(directory, "bad.ndjson");
        await File.WriteAllTextAsync(path, "{\"resourceType\":\"Patient\"\n", Encoding.UTF8);

        var result = await new NdjsonValidator().ValidateAsync(path, "Patient", ExportFileCategory.Output, CancellationToken.None);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, x => x.Contains("truncated", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Serious_operation_outcome_is_detected()
    {
        var path = Path.Combine(directory, "error.ndjson");
        await File.WriteAllTextAsync(path, "{\"resourceType\":\"OperationOutcome\",\"issue\":[{\"severity\":\"error\"}]}\n", Encoding.UTF8);

        var result = await new NdjsonValidator().ValidateAsync(path, "OperationOutcome", ExportFileCategory.Error, CancellationToken.None);

        Assert.True(result.IsValid);
        Assert.True(result.HasSeriousOperationOutcome);
        Assert.Equal(1, result.OperationOutcomeSeverityCounts["error"]);
    }

    public void Dispose()
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}

