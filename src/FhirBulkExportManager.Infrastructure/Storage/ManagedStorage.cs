using System.Text;
using FhirBulkExportManager.Application.Abstractions;
using FhirBulkExportManager.Application.Configuration;
using FhirBulkExportManager.Domain;
using FhirBulkExportManager.Shared;
using Microsoft.Extensions.Options;

namespace FhirBulkExportManager.Infrastructure.Storage;

public sealed class ManagedStorage(IOptions<StorageOptions> options) : IManagedStorage
{
    public ManagedExportPaths GetPaths(string vendorKey, Guid siteId, Guid jobId)
    {
        var safeVendor = SanitizeSegment(vendorKey);
        var root = Path.GetFullPath(Path.Combine(options.Value.DataRoot, "Vendors", safeVendor, "Sites", siteId.ToString("D"), "Exports", jobId.ToString("D")));
        EnsureUnderDataRoot(root);
        return new ManagedExportPaths(
            root,
            Path.Combine(root, "manifest"),
            Path.Combine(root, "output"),
            Path.Combine(root, "deleted"),
            Path.Combine(root, "error"),
            Path.Combine(root, "metadata"));
    }

    public Task EnsureExportDirectoriesAsync(ManagedExportPaths paths, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        foreach (var directory in new[] { paths.Root, paths.ManifestDirectory, paths.OutputDirectory, paths.DeletedDirectory, paths.ErrorDirectory, paths.MetadataDirectory })
        {
            EnsureUnderDataRoot(directory);
            Directory.CreateDirectory(directory);
        }

        return Task.CompletedTask;
    }

    public string GetAbsoluteFilePath(ManagedExportPaths paths, ExportFile file)
    {
        var categoryDirectory = file.Category switch
        {
            ExportFileCategory.Output => paths.OutputDirectory,
            ExportFileCategory.Deleted => paths.DeletedDirectory,
            ExportFileCategory.Error => paths.ErrorDirectory,
            _ => throw new ArgumentOutOfRangeException(nameof(file)),
        };
        var candidate = Path.GetFullPath(Path.Combine(categoryDirectory, file.SafeLocalFileName));
        if (!candidate.StartsWith(Path.GetFullPath(categoryDirectory) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Resolved file path escaped the managed category directory.");
        }

        EnsureUnderDataRoot(candidate);
        return candidate;
    }

    public Task EnsureCapacityAsync(string path, long knownRemainingBytes, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var root = Path.GetPathRoot(Path.GetFullPath(path)) ?? throw new InvalidOperationException("Unable to determine the destination drive.");
        var drive = new DriveInfo(root);
        var reserve = (long)options.Value.MinimumFreeDiskSpaceGB * 1024 * 1024 * 1024;
        if (drive.AvailableFreeSpace - Math.Max(0, knownRemainingBytes) < reserve)
        {
            throw new IOException($"Insufficient free disk space. The configured reserve is {options.Value.MinimumFreeDiskSpaceGB} GB.");
        }

        return Task.CompletedTask;
    }

    public async Task WriteExactManifestAsync(ManagedExportPaths paths, string exactJson, CancellationToken cancellationToken)
    {
        await EnsureExportDirectoriesAsync(paths, cancellationToken).ConfigureAwait(false);
        var finalPath = Path.Combine(paths.ManifestDirectory, "manifest.json");
        var temporaryPath = finalPath + ".tmp";
        await WriteAtomicAsync(temporaryPath, finalPath, Encoding.UTF8.GetBytes(exactJson), cancellationToken).ConfigureAwait(false);
    }

    public Task DeleteExportDataAsync(ManagedExportPaths paths, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureUnderDataRoot(paths.Root);
        if (Directory.Exists(paths.Root))
        {
            Directory.Delete(paths.Root, recursive: true);
        }

        return Task.CompletedTask;
    }

    internal static async Task WriteAtomicAsync(string temporaryPath, string finalPath, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken)
    {
        await using (var stream = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough))
        {
            await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            stream.Flush(flushToDisk: true);
        }

        File.Move(temporaryPath, finalPath, overwrite: true);
    }

    public static string CreateSafeFileName(int sequence, string? resourceType)
    {
        var safeType = SanitizeSegment(string.IsNullOrWhiteSpace(resourceType) ? "Unknown" : resourceType);
        return $"{sequence:D6}_{safeType}.ndjson";
    }

    private static string SanitizeSegment(string value)
    {
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            builder.Append(invalid.Contains(character) || character is '.' or '/' or '\\' ? '_' : character);
        }

        var candidate = builder.ToString().Trim(' ', '.');
        if (candidate.Length > 80)
        {
            candidate = candidate[..80];
        }

        string[] reserved = ["CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"];
        if (string.IsNullOrWhiteSpace(candidate) || reserved.Contains(candidate, StringComparer.OrdinalIgnoreCase))
        {
            candidate = "safe_" + HashUtility.Sha256Hex(value)[..12];
        }

        return candidate;
    }

    private void EnsureUnderDataRoot(string candidate)
    {
        var root = Path.GetFullPath(options.Value.DataRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(candidate);
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase) && !string.Equals(full.TrimEnd(Path.DirectorySeparatorChar), root.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Path is outside the configured managed data root.");
        }
    }
}
