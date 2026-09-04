using System.Text.Json;
using FhirBulkExportManager.Application.Abstractions;
using FhirBulkExportManager.Application.Configuration;
using FhirBulkExportManager.Shared;

namespace FhirBulkExportManager.Infrastructure.Storage;

public sealed class ManagedDataRootMigrationService : IManagedDataRootMigrationService
{
    private const string JournalFileName = ".fhir-data-root-migration.json";
    private const string StagingDirectoryName = ".fhir-data-root-migration-staging";

    public bool HasContent(string dataRoot)
    {
        var path = RuntimeConfiguration.NormalizePath(dataRoot);
        return Directory.Exists(path) && Directory.EnumerateFileSystemEntries(path).Any();
    }

    public void ValidateMove(DataRootMigrationRequest request)
    {
        var normalized = Normalize(request);
        EnsurePathsAreIndependent(normalized.SourceDataRoot, normalized.TargetDataRoot);
        RejectReparsePoint(normalized.SourceDataRoot, "source");
        RejectReparsePoint(normalized.TargetDataRoot, "destination");

        if (!Directory.Exists(normalized.TargetDataRoot) ||
            !Directory.EnumerateFileSystemEntries(normalized.TargetDataRoot).Any())
        {
            return;
        }

        if (!JournalMatches(normalized.TargetDataRoot, normalized))
        {
            throw new InvalidOperationException(
                "The new managed data root is not empty. Select an empty folder so existing data is never merged or overwritten.");
        }
    }

    public async Task<DataRootMigrationResult> MigrateAsync(
        DataRootMigrationRequest request,
        CancellationToken cancellationToken)
    {
        var normalized = Normalize(request);
        ValidateMove(normalized);
        cancellationToken.ThrowIfCancellationRequested();

        var sourceExists = Directory.Exists(normalized.SourceDataRoot);
        if (!sourceExists)
        {
            if (!Directory.Exists(normalized.TargetDataRoot))
            {
                Directory.CreateDirectory(normalized.TargetDataRoot);
            }

            if (Directory.EnumerateFileSystemEntries(normalized.TargetDataRoot).Any() &&
                !JournalMatches(normalized.TargetDataRoot, normalized))
            {
                throw new InvalidOperationException(
                    "The original managed data root is missing and the destination cannot be verified as a completed migration.");
            }

            return new DataRootMigrationResult(false, false, 0, 0);
        }

        ValidateTreeHasNoReparsePoints(normalized.SourceDataRoot);
        var sourceFiles = Directory.EnumerateFiles(normalized.SourceDataRoot, "*", SearchOption.AllDirectories)
            .Where(path => !IsMigrationArtifact(normalized.SourceDataRoot, path))
            .ToArray();
        var totalBytes = sourceFiles.Sum(path => new FileInfo(path).Length);

        if (SameStorageRoot(normalized.SourceDataRoot, normalized.TargetDataRoot) &&
            (!Directory.Exists(normalized.TargetDataRoot) ||
             !Directory.EnumerateFileSystemEntries(normalized.TargetDataRoot).Any()))
        {
            WriteJournal(normalized.SourceDataRoot, normalized);
            if (Directory.Exists(normalized.TargetDataRoot))
            {
                Directory.Delete(normalized.TargetDataRoot, recursive: false);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(normalized.TargetDataRoot)
                ?? throw new InvalidOperationException("The destination data root has no parent directory."));
            Directory.Move(normalized.SourceDataRoot, normalized.TargetDataRoot);
            return new DataRootMigrationResult(true, true, sourceFiles.LongLength, totalBytes);
        }

        Directory.CreateDirectory(normalized.TargetDataRoot);
        if (!JournalMatches(normalized.TargetDataRoot, normalized))
        {
            WriteJournal(normalized.TargetDataRoot, normalized);
        }

        var stagingRoot = Path.Combine(normalized.TargetDataRoot, StagingDirectoryName);
        Directory.CreateDirectory(stagingRoot);
        long copiedFiles = 0;
        long copiedBytes = 0;
        foreach (var sourceFile in sourceFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relativePath = Path.GetRelativePath(normalized.SourceDataRoot, sourceFile);
            var destinationFile = SafeCombine(normalized.TargetDataRoot, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(destinationFile)!);

            if (File.Exists(destinationFile))
            {
                await VerifyFilesMatchAsync(sourceFile, destinationFile, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                var temporaryFile = SafeCombine(stagingRoot, relativePath + ".partial");
                Directory.CreateDirectory(Path.GetDirectoryName(temporaryFile)!);
                if (File.Exists(temporaryFile))
                {
                    File.Delete(temporaryFile);
                }

                await CopyAndVerifyAsync(sourceFile, temporaryFile, cancellationToken).ConfigureAwait(false);
                File.Move(temporaryFile, destinationFile, overwrite: false);
                File.SetLastWriteTimeUtc(destinationFile, File.GetLastWriteTimeUtc(sourceFile));
            }

            copiedFiles++;
            copiedBytes += new FileInfo(sourceFile).Length;
        }

        await VerifyDestinationContainsOnlySourceFilesAsync(
            normalized.SourceDataRoot,
            normalized.TargetDataRoot,
            cancellationToken).ConfigureAwait(false);
        if (Directory.Exists(stagingRoot))
        {
            Directory.Delete(stagingRoot, recursive: true);
        }

        Directory.Delete(normalized.SourceDataRoot, recursive: true);
        return new DataRootMigrationResult(true, false, copiedFiles, copiedBytes);
    }

    public void CleanupCompletedMigrationArtifacts(string dataRoot)
    {
        var normalizedRoot = RuntimeConfiguration.NormalizePath(dataRoot);
        var journal = Path.Combine(normalizedRoot, JournalFileName);
        if (File.Exists(journal))
        {
            File.Delete(journal);
        }

        var staging = Path.Combine(normalizedRoot, StagingDirectoryName);
        if (Directory.Exists(staging) && !Directory.EnumerateFileSystemEntries(staging).Any())
        {
            Directory.Delete(staging, recursive: false);
        }
    }

    private static async Task CopyAndVerifyAsync(
        string source,
        string destination,
        CancellationToken cancellationToken)
    {
        await using (var input = new FileStream(
            source,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan))
        await using (var output = new FileStream(
            destination,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            1024 * 1024,
            FileOptions.Asynchronous | FileOptions.WriteThrough))
        {
            await input.CopyToAsync(output, 1024 * 1024, cancellationToken).ConfigureAwait(false);
            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            output.Flush(flushToDisk: true);
        }

        await VerifyFilesMatchAsync(source, destination, cancellationToken).ConfigureAwait(false);
    }

    private static async Task VerifyFilesMatchAsync(
        string source,
        string destination,
        CancellationToken cancellationToken)
    {
        if (new FileInfo(source).Length != new FileInfo(destination).Length)
        {
            throw new IOException($"Managed-data migration verification failed for '{Path.GetFileName(source)}'.");
        }

        var sourceHash = await HashUtility.Sha256FileAsync(source, cancellationToken).ConfigureAwait(false);
        var destinationHash = await HashUtility.Sha256FileAsync(destination, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(sourceHash, destinationHash, StringComparison.Ordinal))
        {
            throw new IOException($"Managed-data migration checksum verification failed for '{Path.GetFileName(source)}'.");
        }
    }

    private static async Task VerifyDestinationContainsOnlySourceFilesAsync(
        string sourceRoot,
        string destinationRoot,
        CancellationToken cancellationToken)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var sourceRelativeFiles = Directory.EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories)
            .Where(path => !IsMigrationArtifact(sourceRoot, path))
            .Select(path => Path.GetRelativePath(sourceRoot, path))
            .ToHashSet(comparison);
        var destinationFiles = Directory.EnumerateFiles(destinationRoot, "*", SearchOption.AllDirectories)
            .Where(path => !IsMigrationArtifact(destinationRoot, path));
        foreach (var destinationFile in destinationFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relativePath = Path.GetRelativePath(destinationRoot, destinationFile);
            if (!sourceRelativeFiles.Contains(relativePath))
            {
                throw new InvalidOperationException(
                    $"Unexpected file '{relativePath}' appeared in the migration destination; the original data was retained.");
            }
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }

    private static void ValidateTreeHasNoReparsePoints(string root)
    {
        RejectReparsePoint(root, "source");
        foreach (var entry in Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories))
        {
            if ((File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidOperationException(
                    $"Managed-data migration cannot follow the reparse point '{Path.GetRelativePath(root, entry)}'.");
            }
        }
    }

    private static void RejectReparsePoint(string path, string description)
    {
        if ((Directory.Exists(path) || File.Exists(path)) &&
            (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidOperationException($"The managed-data migration {description} cannot be a reparse point.");
        }
    }

    private static void EnsurePathsAreIndependent(string source, string target)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var sourcePrefix = source + Path.DirectorySeparatorChar;
        var targetPrefix = target + Path.DirectorySeparatorChar;
        if (target.StartsWith(sourcePrefix, comparison) || source.StartsWith(targetPrefix, comparison))
        {
            throw new InvalidOperationException("The old and new managed data roots cannot contain one another.");
        }
    }

    private static bool SameStorageRoot(string first, string second) =>
        string.Equals(
            Path.GetPathRoot(first),
            Path.GetPathRoot(second),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static DataRootMigrationRequest Normalize(DataRootMigrationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.MigrationId == Guid.Empty)
        {
            throw new InvalidOperationException("The managed-data migration identifier is invalid.");
        }

        var source = RuntimeConfiguration.NormalizePath(request.SourceDataRoot);
        var target = RuntimeConfiguration.NormalizePath(request.TargetDataRoot);
        if (RuntimeConfiguration.PathsEqual(source, target))
        {
            throw new InvalidOperationException("The old and new managed data roots are the same folder.");
        }

        return request with { SourceDataRoot = source, TargetDataRoot = target };
    }

    private static string SafeCombine(string root, string relativePath)
    {
        var candidate = Path.GetFullPath(Path.Combine(root, relativePath));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!candidate.StartsWith(root + Path.DirectorySeparatorChar, comparison))
        {
            throw new InvalidOperationException("A managed-data migration path escaped its destination root.");
        }

        return candidate;
    }

    private static bool IsMigrationArtifact(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        return string.Equals(relative, JournalFileName, StringComparison.Ordinal) ||
               relative.StartsWith(StagingDirectoryName + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }

    private static bool JournalMatches(string root, DataRootMigrationRequest request)
    {
        var path = Path.Combine(root, JournalFileName);
        if (!File.Exists(path))
        {
            return false;
        }

        try
        {
            var journal = JsonSerializer.Deserialize<MigrationJournal>(File.ReadAllText(path), JsonDefaults.Options);
            return journal is not null &&
                   journal.MigrationId == request.MigrationId &&
                   RuntimeConfiguration.PathsEqual(journal.SourceDataRoot, request.SourceDataRoot) &&
                   RuntimeConfiguration.PathsEqual(journal.TargetDataRoot, request.TargetDataRoot);
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    private static void WriteJournal(string root, DataRootMigrationRequest request)
    {
        Directory.CreateDirectory(root);
        var journal = new MigrationJournal(
            request.MigrationId,
            request.SourceDataRoot,
            request.TargetDataRoot,
            DateTimeOffset.UtcNow);
        File.WriteAllText(
            Path.Combine(root, JournalFileName),
            JsonSerializer.Serialize(journal, JsonDefaults.Options));
    }

    private sealed record MigrationJournal(
        Guid MigrationId,
        string SourceDataRoot,
        string TargetDataRoot,
        DateTimeOffset CreatedAtUtc);
}
