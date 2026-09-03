using System.Text;
using FhirBulkExportManager.Application.Abstractions;
using FhirBulkExportManager.Infrastructure.Storage;
using FhirBulkExportManager.Shared;

namespace FhirBulkExportManager.Infrastructure.Tests;

public sealed class LocalCommitServiceTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "fhir-commit-tests-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Creates_and_revalidates_atomic_local_commit()
    {
        var output = Path.Combine(root, "output");
        var metadata = Path.Combine(root, "metadata");
        Directory.CreateDirectory(output);
        Directory.CreateDirectory(metadata);
        var filePath = Path.Combine(output, "000001_Patient.ndjson");
        await File.WriteAllTextAsync(filePath, "{\"resourceType\":\"Patient\"}\n", Encoding.UTF8);
        var hash = await HashUtility.Sha256FileAsync(filePath, CancellationToken.None);
        var paths = new ManagedExportPaths(root, Path.Combine(root, "manifest"), output, Path.Combine(root, "deleted"), Path.Combine(root, "error"), metadata);
        var jobId = Guid.NewGuid();
        var document = new LocalCommitDocument(jobId, "Vendor", "Site", new string('c', 64), null, DateTimeOffset.Parse("2026-09-03T00:00:00Z"),
            [new LocalCommitFile("Output", "Patient", "output/000001_Patient.ndjson", new FileInfo(filePath).Length, 1, hash)], 1, new FileInfo(filePath).Length, 1,
            new Dictionary<string, long>(), "1.0.0", "1.0.0");
        var service = new LocalCommitService();

        var commitPath = await service.CreateAsync(paths, document, CancellationToken.None);
        var verified = await service.VerifyAsync(paths, jobId, document.ManifestHash, CancellationToken.None);

        Assert.True(File.Exists(commitPath));
        Assert.True(verified);
    }

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}

