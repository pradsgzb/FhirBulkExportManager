using FhirBulkExportManager.Domain;

namespace FhirBulkExportManager.Application.Models;

public sealed record CreateExportJobCommand(
    string Name,
    string? Description,
    Guid VendorId,
    Guid SiteId,
    Guid CredentialProfileId,
    ExportType ExportType,
    string? GroupId,
    DateTimeOffset? SinceUtc,
    IReadOnlyList<string> ResourceTypes,
    IReadOnlyList<string> TypeFilters,
    string OutputFormat,
    string VendorOptionsJson,
    string LocalDestination,
    bool AutomaticCleanupEnabled,
    JobPriority Priority);

public sealed record JobActionResult(Guid JobId, ExportJobState State, string Message);

