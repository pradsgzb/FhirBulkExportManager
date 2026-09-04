using FhirBulkExportManager.Application.Configuration;
using FhirBulkExportManager.Infrastructure.Storage;

namespace FhirBulkExportManager.Infrastructure.Tests;

public sealed class ManagedDataRootMigrationServiceTests
{
    [Fact]
    public async Task Same_volume_move_preserves_files_and_retains_recovery_marker_until_cleanup()
    {
        var testRoot = CreateTestRoot();
        var source = Path.Combine(testRoot, "source");
        var target = Path.Combine(testRoot, "target");
        Directory.CreateDirectory(Path.Combine(source, "vendor", "job"));
        var expected = new byte[] { 0, 1, 2, 3, 255 };
        await File.WriteAllBytesAsync(Path.Combine(source, "vendor", "job", "data.ndjson"), expected);
        var service = new ManagedDataRootMigrationService();
        var request = new DataRootMigrationRequest(Guid.NewGuid(), source, target);

        try
        {
            service.ValidateMove(request);
            var result = await service.MigrateAsync(request, CancellationToken.None);

            Assert.True(result.SourceExisted);
            Assert.True(result.UsedAtomicMove);
            Assert.Equal(1, result.FilesMoved);
            Assert.False(Directory.Exists(source));
            Assert.Equal(expected, await File.ReadAllBytesAsync(Path.Combine(target, "vendor", "job", "data.ndjson")));
            Assert.True(File.Exists(Path.Combine(target, ".fhir-data-root-migration.json")));

            service.CleanupCompletedMigrationArtifacts(target);
            Assert.False(File.Exists(Path.Combine(target, ".fhir-data-root-migration.json")));
        }
        finally
        {
            if (Directory.Exists(testRoot))
            {
                Directory.Delete(testRoot, recursive: true);
            }
        }
    }

    [Fact]
    public void Existing_destination_content_is_never_merged_or_overwritten()
    {
        var testRoot = CreateTestRoot();
        var source = Path.Combine(testRoot, "source");
        var target = Path.Combine(testRoot, "target");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(source, "source.txt"), "source");
        File.WriteAllText(Path.Combine(target, "existing.txt"), "destination");
        var service = new ManagedDataRootMigrationService();

        try
        {
            var exception = Assert.Throws<InvalidOperationException>(() =>
                service.ValidateMove(new DataRootMigrationRequest(Guid.NewGuid(), source, target)));
            Assert.Contains("not empty", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal("destination", File.ReadAllText(Path.Combine(target, "existing.txt")));
        }
        finally
        {
            Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public void Source_and_destination_cannot_contain_one_another()
    {
        var testRoot = CreateTestRoot();
        var source = Path.Combine(testRoot, "source");
        var target = Path.Combine(source, "nested-target");
        Directory.CreateDirectory(source);
        var service = new ManagedDataRootMigrationService();

        try
        {
            var exception = Assert.Throws<InvalidOperationException>(() =>
                service.ValidateMove(new DataRootMigrationRequest(Guid.NewGuid(), source, target)));
            Assert.Contains("cannot contain one another", exception.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(testRoot, recursive: true);
        }
    }

    private static string CreateTestRoot()
    {
        var path = Path.Combine(Path.GetTempPath(), "FhirBulkExportManager.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
