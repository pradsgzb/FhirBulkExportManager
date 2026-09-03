using FhirBulkExportManager.Domain;

namespace FhirBulkExportManager.Application.Abstractions;

public sealed record JobQuery(
    string? Search = null,
    IReadOnlyCollection<ExportJobState>? States = null,
    Guid? VendorId = null,
    Guid? SiteId = null,
    DateTimeOffset? CreatedFromUtc = null,
    DateTimeOffset? CreatedToUtc = null,
    int Skip = 0,
    int Take = 100);

public sealed record PagedResult<T>(IReadOnlyList<T> Items, int TotalCount);

public interface IExportStore
{
    Task AddJobAsync(ExportJob job, ExportJobEvent createdEvent, AuditEvent auditEvent, CancellationToken cancellationToken);

    Task<ExportJob?> GetJobAsync(Guid jobId, CancellationToken cancellationToken);

    Task<PagedResult<ExportJob>> QueryJobsAsync(JobQuery query, CancellationToken cancellationToken);

    Task<IReadOnlyList<ExportJob>> GetRunnableJobsAsync(DateTimeOffset now, int take, CancellationToken cancellationToken);

    Task UpdateJobAsync(ExportJob job, ExportJobEvent jobEvent, AuditEvent auditEvent, long expectedVersion, CancellationToken cancellationToken);

    Task SaveManifestAsync(ExportJob job, ExportJobEvent jobEvent, IReadOnlyCollection<ExportFile> files, AuditEvent auditEvent, long expectedVersion, CancellationToken cancellationToken);

    Task<IReadOnlyList<ExportFile>> GetFilesAsync(Guid jobId, CancellationToken cancellationToken);

    Task<ExportFile?> GetFileAsync(Guid fileId, CancellationToken cancellationToken);

    Task UpdateFileAsync(ExportFile file, long expectedVersion, CancellationToken cancellationToken);

    Task AddJobEventAsync(ExportJobEvent jobEvent, CancellationToken cancellationToken);

    Task<IReadOnlyList<ExportJobEvent>> GetTimelineAsync(Guid jobId, CancellationToken cancellationToken);

    Task<bool> TryAcquireLeaseAsync(Guid jobId, string owner, DateTimeOffset now, TimeSpan duration, CancellationToken cancellationToken);

    Task ReleaseLeaseAsync(Guid jobId, string owner, CancellationToken cancellationToken);

    Task ClearExpiredLeasesAsync(DateTimeOffset now, CancellationToken cancellationToken);

    Task DeleteJobMetadataAsync(Guid jobId, AuditEvent auditEvent, CancellationToken cancellationToken);
}

public interface IConfigurationStore
{
    Task<IReadOnlyList<Vendor>> GetVendorsAsync(bool enabledOnly, CancellationToken cancellationToken);
    Task<Vendor?> GetVendorAsync(Guid id, CancellationToken cancellationToken);
    Task AddVendorAsync(Vendor vendor, AuditEvent auditEvent, CancellationToken cancellationToken);
    Task<IReadOnlyList<Site>> GetSitesAsync(Guid? vendorId, bool enabledOnly, CancellationToken cancellationToken);
    Task<Site?> GetSiteAsync(Guid id, CancellationToken cancellationToken);
    Task AddSiteAsync(Site site, AuditEvent auditEvent, CancellationToken cancellationToken);
    Task<CredentialProfile?> GetCredentialProfileAsync(Guid id, CancellationToken cancellationToken);
    Task<IReadOnlyList<CredentialProfile>> GetCredentialProfilesAsync(Guid? vendorId, CancellationToken cancellationToken);
    Task AddCredentialProfileAsync(CredentialProfile profile, AuditEvent auditEvent, CancellationToken cancellationToken);
}

public interface IDatabaseLifecycle
{
    Task InitializeAsync(CancellationToken cancellationToken);
    Task<string> BackupAsync(string destinationDirectory, CancellationToken cancellationToken);
    Task<int> GetSchemaVersionAsync(CancellationToken cancellationToken);
}
