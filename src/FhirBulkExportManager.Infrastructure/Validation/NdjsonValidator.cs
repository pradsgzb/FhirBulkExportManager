using System.Text;
using System.Text.Json;
using FhirBulkExportManager.Application.Abstractions;
using FhirBulkExportManager.Domain;

namespace FhirBulkExportManager.Infrastructure.Validation;

public sealed class NdjsonValidator : INdjsonValidator
{
    private const int MaximumReportedErrors = 100;

    public async Task<FileValidationResult> ValidateAsync(string filePath, string? expectedResourceType, ExportFileCategory category, CancellationToken cancellationToken)
    {
        var errors = new List<string>();
        var severities = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase)
        {
            ["fatal"] = 0,
            ["error"] = 0,
            ["warning"] = 0,
            ["information"] = 0,
        };
        long records = 0;
        var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
        try
        {
            await using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var reader = new StreamReader(stream, utf8, detectEncodingFromByteOrderMarks: false, bufferSize: 1024 * 1024, leaveOpen: false);
            while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (records == 0 && line.Length > 0 && line[0] == '\uFEFF')
                {
                    line = line[1..];
                }

                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                records++;
                try
                {
                    using var document = JsonDocument.Parse(line);
                    if (document.RootElement.ValueKind != JsonValueKind.Object)
                    {
                        AddError(errors, $"Line {records} is not a JSON object.");
                        continue;
                    }

                    var resourceType = document.RootElement.TryGetProperty("resourceType", out var resourceTypeElement)
                        ? resourceTypeElement.GetString()
                        : null;
                    if (category != ExportFileCategory.Deleted && string.IsNullOrWhiteSpace(resourceType))
                    {
                        AddError(errors, $"Line {records} has no resourceType.");
                    }
                    else if (category == ExportFileCategory.Output && !string.IsNullOrWhiteSpace(expectedResourceType) && !string.Equals(resourceType, expectedResourceType, StringComparison.Ordinal))
                    {
                        AddError(errors, $"Line {records} resourceType '{resourceType}' does not match manifest type '{expectedResourceType}'.");
                    }

                    if (category == ExportFileCategory.Error && string.Equals(resourceType, "OperationOutcome", StringComparison.Ordinal) && document.RootElement.TryGetProperty("issue", out var issues) && issues.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var issue in issues.EnumerateArray())
                        {
                            if (issue.TryGetProperty("severity", out var severityElement) && severityElement.GetString() is { } severity && severities.ContainsKey(severity))
                            {
                                severities[severity]++;
                            }
                        }
                    }
                }
                catch (JsonException exception)
                {
                    AddError(errors, $"Line {records} is invalid or truncated JSON: {exception.Message}");
                }
            }
        }
        catch (DecoderFallbackException exception)
        {
            AddError(errors, "File is not valid UTF-8: " + exception.Message);
        }

        if (records == 0 && category != ExportFileCategory.Deleted)
        {
            AddError(errors, "File contains no non-empty NDJSON records.");
        }

        var serious = severities["fatal"] > 0 || severities["error"] > 0;
        return new FileValidationResult(errors.Count == 0, records, severities, errors, serious);
    }

    private static void AddError(List<string> errors, string message)
    {
        if (errors.Count < MaximumReportedErrors)
        {
            errors.Add(message);
        }
    }
}
