namespace FhirBulkExportManager.Application.Configuration;

public sealed record DataRootMigrationRequest(
    Guid MigrationId,
    string SourceDataRoot,
    string TargetDataRoot);

public sealed record RuntimeConfiguration
{
    public const int CurrentVersion = 1;

    public int Version { get; init; } = CurrentVersion;
    public int MaxConcurrentExports { get; init; }
    public int PerSiteFileDownloadLimit { get; init; }
    public int MinimumFreeDiskSpaceGB { get; init; }
    public string DataRoot { get; init; } = string.Empty;
    public DataRootMigrationRequest? PendingDataRootMigration { get; init; }

    public static RuntimeConfiguration FromDeploymentOptions(
        ProcessingOptions processing,
        StorageOptions storage) =>
        new()
        {
            MaxConcurrentExports = processing.MaxConcurrentExports,
            PerSiteFileDownloadLimit = processing.PerSiteFileDownloadLimit,
            MinimumFreeDiskSpaceGB = storage.MinimumFreeDiskSpaceGB,
            DataRoot = storage.DataRoot,
        };

    public RuntimeConfiguration NormalizeAndValidate(int logicalProcessorCount)
    {
        if (Version != CurrentVersion)
        {
            throw new InvalidOperationException($"Runtime configuration version {Version} is not supported.");
        }

        if (logicalProcessorCount < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(logicalProcessorCount));
        }

        if (MaxConcurrentExports < 1)
        {
            throw new InvalidOperationException("The configured export worker limit must be at least 1.");
        }

        if (PerSiteFileDownloadLimit is < 1 or > 4)
        {
            throw new InvalidOperationException("The per-site file download limit must be between 1 and 4.");
        }

        if (MinimumFreeDiskSpaceGB is < 1 or > 1024)
        {
            throw new InvalidOperationException("The minimum free disk reserve must be between 1 and 1,024 GB.");
        }

        if (string.IsNullOrWhiteSpace(DataRoot))
        {
            throw new InvalidOperationException("The managed data root is required.");
        }

        var normalizedDataRoot = NormalizePath(DataRoot);
        DataRootMigrationRequest? normalizedMigration = null;
        if (PendingDataRootMigration is { } migration)
        {
            if (migration.MigrationId == Guid.Empty)
            {
                throw new InvalidOperationException("The pending managed-data migration identifier is invalid.");
            }

            var source = NormalizePath(migration.SourceDataRoot);
            var target = NormalizePath(migration.TargetDataRoot);
            if (PathsEqual(source, target) || !PathsEqual(target, normalizedDataRoot))
            {
                throw new InvalidOperationException("The pending managed-data migration paths are inconsistent.");
            }

            normalizedMigration = migration with
            {
                SourceDataRoot = source,
                TargetDataRoot = target,
            };
        }

        return this with
        {
            MaxConcurrentExports = Math.Min(MaxConcurrentExports, logicalProcessorCount),
            DataRoot = normalizedDataRoot,
            PendingDataRootMigration = normalizedMigration,
        };
    }

    public RuntimeConfiguration WithoutPendingMigration() => this with { PendingDataRootMigration = null };

    public static string NormalizePath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(Environment.ExpandEnvironmentVariables(path.Trim()));
        var normalized = Path.TrimEndingDirectorySeparator(fullPath);
        var storageRoot = Path.GetPathRoot(fullPath);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!string.IsNullOrWhiteSpace(storageRoot) &&
            string.Equals(normalized, Path.TrimEndingDirectorySeparator(storageRoot), comparison))
        {
            throw new InvalidOperationException("The managed data root cannot be an entire drive, volume, or filesystem root.");
        }

        return normalized;
    }

    public static bool PathsEqual(string first, string second) =>
        string.Equals(
            NormalizePath(first),
            NormalizePath(second),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}
