using FhirBulkExportManager.Domain;
using FhirBulkExportManager.PluginContracts;

namespace FhirBulkExportManager.Application.Abstractions;

public interface IWorkSignal
{
    void Signal();
    Task WaitAsync(TimeSpan maximumWait, CancellationToken cancellationToken);
}

public interface ISecretStore
{
    Task<string> SaveAsync(string purpose, ReadOnlyMemory<char> secret, CancellationToken cancellationToken);
    Task<string?> GetAsync(string secretReference, CancellationToken cancellationToken);
    Task DeleteAsync(string secretReference, CancellationToken cancellationToken);
}

public interface IWindowsIdentityProvider
{
    string GetCurrentIdentity();
}

public interface IVendorPluginCatalog
{
    IReadOnlyCollection<PluginMetadata> Available { get; }
    IVendorPlugin GetRequired(string pluginId, string pinnedVersion);
}

public sealed record FileValidationResult(
    bool IsValid,
    long RecordCount,
    IReadOnlyDictionary<string, long> OperationOutcomeSeverityCounts,
    IReadOnlyList<string> Errors,
    bool HasSeriousOperationOutcome);

public interface INdjsonValidator
{
    Task<FileValidationResult> ValidateAsync(string filePath, string? expectedResourceType, ExportFileCategory category, CancellationToken cancellationToken);
}

public sealed record ManagedExportPaths(
    string Root,
    string ManifestDirectory,
    string OutputDirectory,
    string DeletedDirectory,
    string ErrorDirectory,
    string MetadataDirectory);

public interface IManagedStorage
{
    ManagedExportPaths GetPaths(string vendorKey, Guid siteId, Guid jobId);
    Task EnsureExportDirectoriesAsync(ManagedExportPaths paths, CancellationToken cancellationToken);
    string GetAbsoluteFilePath(ManagedExportPaths paths, ExportFile file);
    Task EnsureCapacityAsync(string path, long knownRemainingBytes, CancellationToken cancellationToken);
    Task WriteExactManifestAsync(ManagedExportPaths paths, string exactJson, CancellationToken cancellationToken);
    Task DeleteExportDataAsync(ManagedExportPaths paths, CancellationToken cancellationToken);
}

public sealed record LocalCommitFile(
    string Category,
    string ResourceType,
    string RelativePath,
    long Bytes,
    long Records,
    string Sha256);

public sealed record LocalCommitDocument(
    Guid JobId,
    string Vendor,
    string Site,
    string ManifestHash,
    string? TransactionTime,
    DateTimeOffset CompletionTimeUtc,
    IReadOnlyList<LocalCommitFile> Files,
    int TotalFileCount,
    long TotalBytes,
    long TotalResources,
    IReadOnlyDictionary<string, long> OperationOutcomeSummary,
    string ApplicationVersion,
    string PluginVersion);

public interface ILocalCommitService
{
    Task<string> CreateAsync(ManagedExportPaths paths, LocalCommitDocument document, CancellationToken cancellationToken);
    Task<bool> VerifyAsync(ManagedExportPaths paths, Guid expectedJobId, string expectedManifestHash, CancellationToken cancellationToken);
}

public sealed record DownloadExecutionResult(long Bytes, string Sha256, string TemporaryPath, string? ETag, DateTimeOffset? LastModifiedUtc, bool Resumed);

public interface IFileDownloadService
{
    Task<DownloadExecutionResult> DownloadAsync(
        IVendorPlugin plugin,
        Uri exactStatusUri,
        AccessToken? token,
        ExportFile file,
        string finalPath,
        CancellationToken cancellationToken);
}

public interface IDiagnosticBundleService
{
    Task<string> CreateAsync(string destinationDirectory, CancellationToken cancellationToken);
}
