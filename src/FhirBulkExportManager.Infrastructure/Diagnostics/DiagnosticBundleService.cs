using System.IO.Compression;
using System.Reflection;
using System.Text.Json;
using FhirBulkExportManager.Application.Abstractions;
using FhirBulkExportManager.Application.Configuration;
using FhirBulkExportManager.Domain;
using FhirBulkExportManager.Shared;
using Microsoft.Extensions.Options;

namespace FhirBulkExportManager.Infrastructure.Diagnostics;

public sealed class DiagnosticBundleService(
    IExportStore exportStore,
    IVendorPluginCatalog pluginCatalog,
    IOptions<ProcessingOptions> processingOptions,
    IOptions<StorageOptions> storageOptions,
    TimeProvider timeProvider) : IDiagnosticBundleService
{
    public async Task<string> CreateAsync(string destinationDirectory, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(destinationDirectory);
        var jobs = await exportStore.QueryJobsAsync(new JobQuery(Take: 500), cancellationToken).ConfigureAwait(false);
        var stateCounts = jobs.Items.GroupBy(x => x.State).ToDictionary(x => x.Key.ToString(), x => x.Count());
        var payload = new
        {
            CreatedAtUtc = timeProvider.GetUtcNow(),
            ApplicationVersion = Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "unknown",
            OperatingSystem = Environment.OSVersion.VersionString,
            Environment.ProcessorCount,
            Processing = new
            {
                processingOptions.Value.MaxConcurrentExports,
                processingOptions.Value.EffectiveMaxConcurrentExports,
                processingOptions.Value.PerSiteFileDownloadLimit,
                processingOptions.Value.AutomaticPollingEnabled,
            },
            Storage = new
            {
                DataRoot = "[REDACTED LOCAL PATH]",
                storageOptions.Value.MinimumFreeDiskSpaceGB,
            },
            Plugins = pluginCatalog.Available.Select(x => new { x.Id, Version = x.Version.ToString(), x.Publisher }).ToArray(),
            JobStateCounts = stateCounts,
        };

        var zipPath = Path.Combine(destinationDirectory, $"diagnostics-{timeProvider.GetUtcNow():yyyyMMdd-HHmmss}.zip");
        await using var stream = new FileStream(zipPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true);
        var entry = archive.CreateEntry("diagnostics.json", CompressionLevel.Optimal);
        await using var entryStream = entry.Open();
        await JsonSerializer.SerializeAsync(entryStream, payload, JsonDefaults.Options, cancellationToken).ConfigureAwait(false);
        return zipPath;
    }
}

