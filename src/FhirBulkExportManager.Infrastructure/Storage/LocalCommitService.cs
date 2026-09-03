using System.Text.Json;
using FhirBulkExportManager.Application.Abstractions;
using FhirBulkExportManager.Shared;

namespace FhirBulkExportManager.Infrastructure.Storage;

public sealed class LocalCommitService : ILocalCommitService
{
    public async Task<string> CreateAsync(ManagedExportPaths paths, LocalCommitDocument document, CancellationToken cancellationToken)
    {
        if (document.Files.Count != document.TotalFileCount || document.Files.Any(x => string.IsNullOrWhiteSpace(x.Sha256)))
        {
            throw new InvalidOperationException("A local commit requires complete file metadata and checksums.");
        }

        var bytes = JsonSerializer.SerializeToUtf8Bytes(document, JsonDefaults.Options);
        var temporaryPath = Path.Combine(paths.MetadataDirectory, "local-commit.json.tmp");
        var finalPath = Path.Combine(paths.MetadataDirectory, "local-commit.json");
        await ManagedStorage.WriteAtomicAsync(temporaryPath, finalPath, bytes, cancellationToken).ConfigureAwait(false);

        var checksumLines = document.Files
            .OrderBy(x => x.RelativePath, StringComparer.Ordinal)
            .Select(x => $"{x.Sha256}  {x.RelativePath.Replace('\\', '/')}");
        var checksumBytes = System.Text.Encoding.UTF8.GetBytes(string.Join(Environment.NewLine, checksumLines) + Environment.NewLine);
        await ManagedStorage.WriteAtomicAsync(
            Path.Combine(paths.MetadataDirectory, "checksums.sha256.tmp"),
            Path.Combine(paths.MetadataDirectory, "checksums.sha256"),
            checksumBytes,
            cancellationToken).ConfigureAwait(false);
        return finalPath;
    }

    public async Task<bool> VerifyAsync(ManagedExportPaths paths, Guid expectedJobId, string expectedManifestHash, CancellationToken cancellationToken)
    {
        var path = Path.Combine(paths.MetadataDirectory, "local-commit.json");
        if (!File.Exists(path))
        {
            return false;
        }

        try
        {
            await using var stream = File.OpenRead(path);
            var document = await JsonSerializer.DeserializeAsync<LocalCommitDocument>(stream, JsonDefaults.Options, cancellationToken).ConfigureAwait(false);
            if (document is null || document.JobId != expectedJobId || !string.Equals(document.ManifestHash, expectedManifestHash, StringComparison.OrdinalIgnoreCase) || document.Files.Count != document.TotalFileCount)
            {
                return false;
            }

            foreach (var file in document.Files)
            {
                var fullPath = Path.GetFullPath(Path.Combine(paths.Root, file.RelativePath));
                if (!fullPath.StartsWith(Path.GetFullPath(paths.Root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !File.Exists(fullPath))
                {
                    return false;
                }

                var info = new FileInfo(fullPath);
                if (info.Length != file.Bytes)
                {
                    return false;
                }

                var hash = await HashUtility.Sha256FileAsync(fullPath, cancellationToken).ConfigureAwait(false);
                if (!string.Equals(hash, file.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }

            return true;
        }
        catch (JsonException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
    }
}

