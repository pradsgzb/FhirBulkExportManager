using FhirBulkExportManager.Application.Abstractions;
using FhirBulkExportManager.Application.Configuration;
using FhirBulkExportManager.Application.Services;
using FhirBulkExportManager.Domain;
using Microsoft.Extensions.Options;

namespace FhirBulkExportManager.Application.Tests;

public sealed class RuntimeConfigurationServiceTests
{
    [Fact]
    public async Task Persisted_configuration_is_applied_to_runtime_options_on_startup()
    {
        var store = new MemorySettingsStore();
        var initialDataRoot = TestPath("initial");
        var nextDataRoot = TestPath("next");
        var sourceProcessing = new ProcessingOptions
        {
            MaxConcurrentExports = 1,
            PerSiteFileDownloadLimit = 1,
        };
        var sourceStorage = new StorageOptions
        {
            DataRoot = initialDataRoot,
            MinimumFreeDiskSpaceGB = 10,
        };
        var writer = CreateService(store, sourceProcessing, sourceStorage);
        await writer.LoadAsync(CancellationToken.None);
        var expectedWorkerLimit = Math.Min(2, Environment.ProcessorCount);
        await writer.SaveForNextRestartAsync(
            new RuntimeConfiguration
            {
                MaxConcurrentExports = 2,
                PerSiteFileDownloadLimit = 3,
                MinimumFreeDiskSpaceGB = 25,
                DataRoot = nextDataRoot,
            },
            CancellationToken.None);

        var runtimeProcessing = new ProcessingOptions
        {
            MaxConcurrentExports = 1,
            PerSiteFileDownloadLimit = 1,
        };
        var runtimeStorage = new StorageOptions
        {
            DataRoot = initialDataRoot,
            MinimumFreeDiskSpaceGB = 10,
        };
        var reader = CreateService(store, runtimeProcessing, runtimeStorage);
        var loaded = await reader.LoadAsync(CancellationToken.None);
        reader.ApplyAtStartup(loaded);

        Assert.Equal(expectedWorkerLimit, runtimeProcessing.MaxConcurrentExports);
        Assert.Equal(3, runtimeProcessing.PerSiteFileDownloadLimit);
        Assert.Equal(25, runtimeStorage.MinimumFreeDiskSpaceGB);
        Assert.Equal(RuntimeConfiguration.NormalizePath(nextDataRoot), runtimeStorage.DataRoot);
        Assert.Equal(reader.Current, reader.Applied);
    }

    [Fact]
    public async Task Worker_limit_is_safely_capped_at_detected_logical_processors()
    {
        var store = new MemorySettingsStore();
        var service = CreateService(
            store,
            new ProcessingOptions(),
            new StorageOptions { DataRoot = TestPath("workers") });
        await service.LoadAsync(CancellationToken.None);

        var saved = await service.SaveForNextRestartAsync(
            service.Current with { MaxConcurrentExports = int.MaxValue },
            CancellationToken.None);

        Assert.Equal(Environment.ProcessorCount, saved.MaxConcurrentExports);
        Assert.Equal(saved, service.Current);
    }

    [Fact]
    public async Task Pending_data_move_is_durable_but_cannot_be_applied_before_migration()
    {
        var store = new MemorySettingsStore();
        var service = CreateService(
            store,
            new ProcessingOptions(),
            new StorageOptions { DataRoot = TestPath("source") });
        await service.LoadAsync(CancellationToken.None);
        var migration = new DataRootMigrationRequest(
            Guid.NewGuid(),
            service.Applied.DataRoot,
            TestPath("destination"));
        var staged = service.Applied with
        {
            DataRoot = migration.TargetDataRoot,
            PendingDataRootMigration = migration,
        };

        var saved = await service.SaveForNextRestartAsync(staged, CancellationToken.None);

        Assert.NotNull(saved.PendingDataRootMigration);
        Assert.Single(store.Values);
        Assert.Equal("RuntimeConfigurationStaged", Assert.Single(store.Audits).Action);
        Assert.Throws<InvalidOperationException>(() => service.ApplyAtStartup(saved));

        var completed = await service.CompleteDataRootMigrationAsync(CancellationToken.None);
        Assert.Null(completed.PendingDataRootMigration);
        service.ApplyAtStartup(completed);
        Assert.Equal(completed.DataRoot, service.Applied.DataRoot);
    }

    [Fact]
    public async Task Invalid_saved_configuration_falls_back_without_blocking_startup()
    {
        var store = new MemorySettingsStore();
        store.Values["runtime.configuration.v1"] = "{ invalid json";
        var defaults = new StorageOptions { DataRoot = TestPath("fallback") };
        var service = CreateService(store, new ProcessingOptions(), defaults);

        var loaded = await service.LoadAsync(CancellationToken.None);
        service.ApplyAtStartup(loaded);

        Assert.Equal(RuntimeConfiguration.NormalizePath(defaults.DataRoot), service.Applied.DataRoot);
        Assert.NotNull(service.LoadWarning);
    }

    private static RuntimeConfigurationService CreateService(
        IApplicationSettingsStore store,
        ProcessingOptions processing,
        StorageOptions storage) =>
        new(
            store,
            new TestIdentityProvider(),
            Options.Create(processing),
            Options.Create(storage),
            TimeProvider.System);

    private static string TestPath(string name) =>
        Path.Combine(Path.GetTempPath(), "FhirBulkExportManager.Tests", Guid.NewGuid().ToString("N"), name);

    private sealed class MemorySettingsStore : IApplicationSettingsStore
    {
        public Dictionary<string, string> Values { get; } = new(StringComparer.Ordinal);
        public List<AuditEvent> Audits { get; } = [];

        public Task<string?> GetValueAsync(string key, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Values.GetValueOrDefault(key));
        }

        public Task SetValueAsync(string key, string value, AuditEvent auditEvent, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Values[key] = value;
            Audits.Add(auditEvent);
            return Task.CompletedTask;
        }

        public Task DeleteValueAsync(string key, AuditEvent auditEvent, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Values.Remove(key);
            Audits.Add(auditEvent);
            return Task.CompletedTask;
        }
    }

    private sealed class TestIdentityProvider : IWindowsIdentityProvider
    {
        public string GetCurrentIdentity() => "runtime-settings-test";
    }
}
