using System.Text.Json;
using FhirBulkExportManager.Application.Abstractions;
using FhirBulkExportManager.Application.Configuration;
using FhirBulkExportManager.Domain;
using FhirBulkExportManager.Shared;
using Microsoft.Extensions.Options;

namespace FhirBulkExportManager.Application.Services;

public sealed class RuntimeConfigurationService
{
    private const string RuntimeConfigurationKey = "runtime.configuration.v1";
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly IApplicationSettingsStore settingsStore;
    private readonly IWindowsIdentityProvider identityProvider;
    private readonly IOptions<ProcessingOptions> processingOptions;
    private readonly IOptions<StorageOptions> storageOptions;
    private readonly TimeProvider timeProvider;
    private readonly RuntimeConfiguration deploymentDefaults;
    private bool loaded;

    public RuntimeConfigurationService(
        IApplicationSettingsStore settingsStore,
        IWindowsIdentityProvider identityProvider,
        IOptions<ProcessingOptions> processingOptions,
        IOptions<StorageOptions> storageOptions,
        TimeProvider timeProvider)
    {
        this.settingsStore = settingsStore;
        this.identityProvider = identityProvider;
        this.processingOptions = processingOptions;
        this.storageOptions = storageOptions;
        this.timeProvider = timeProvider;
        deploymentDefaults = RuntimeConfiguration
            .FromDeploymentOptions(processingOptions.Value, storageOptions.Value)
            .NormalizeAndValidate(Environment.ProcessorCount);
        Current = deploymentDefaults;
        Applied = deploymentDefaults;
    }

    public RuntimeConfiguration Current { get; private set; }
    public RuntimeConfiguration Applied { get; private set; }
    public RuntimeConfiguration DeploymentDefaults => deploymentDefaults;
    public string? LoadWarning { get; private set; }

    public async Task<RuntimeConfiguration> LoadAsync(CancellationToken cancellationToken)
    {
        if (loaded)
        {
            return Current;
        }

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (loaded)
            {
                return Current;
            }

            var json = await settingsStore.GetValueAsync(RuntimeConfigurationKey, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(json))
            {
                Current = deploymentDefaults;
            }
            else
            {
                try
                {
                    var saved = JsonSerializer.Deserialize<RuntimeConfiguration>(json, JsonDefaults.Options)
                        ?? throw new InvalidOperationException("Saved runtime configuration is empty.");
                    Current = saved.NormalizeAndValidate(Environment.ProcessorCount);
                    if (saved.MaxConcurrentExports > Environment.ProcessorCount)
                    {
                        LoadWarning = $"The saved worker limit exceeded this machine's {Environment.ProcessorCount:N0} logical processors and was reduced safely.";
                    }
                }
                catch (Exception exception) when (exception is JsonException or InvalidOperationException or ArgumentException or NotSupportedException or IOException or UnauthorizedAccessException)
                {
                    Current = deploymentDefaults;
                    LoadWarning = "Saved runtime and storage options were invalid, so deployment defaults were loaded. Apply the options again to replace the invalid values.";
                }
            }

            Applied = deploymentDefaults;
            loaded = true;
            return Current;
        }
        finally
        {
            gate.Release();
        }
    }

    public void ApplyAtStartup(RuntimeConfiguration configuration)
    {
        var normalized = configuration.NormalizeAndValidate(Environment.ProcessorCount);
        if (normalized.PendingDataRootMigration is not null)
        {
            throw new InvalidOperationException("Managed-data migration must complete before runtime settings can be applied.");
        }

        processingOptions.Value.MaxConcurrentExports = normalized.MaxConcurrentExports;
        processingOptions.Value.PerSiteFileDownloadLimit = normalized.PerSiteFileDownloadLimit;
        storageOptions.Value.MinimumFreeDiskSpaceGB = normalized.MinimumFreeDiskSpaceGB;
        storageOptions.Value.DataRoot = normalized.DataRoot;
        Current = normalized;
        Applied = normalized;
        loaded = true;
    }

    public async Task<RuntimeConfiguration> SaveForNextRestartAsync(
        RuntimeConfiguration configuration,
        CancellationToken cancellationToken)
    {
        var normalized = configuration.NormalizeAndValidate(Environment.ProcessorCount);
        await PersistAsync(normalized, "RuntimeConfigurationStaged", cancellationToken).ConfigureAwait(false);
        return normalized;
    }

    public async Task<RuntimeConfiguration> CompleteDataRootMigrationAsync(CancellationToken cancellationToken)
    {
        var current = await LoadAsync(cancellationToken).ConfigureAwait(false);
        if (current.PendingDataRootMigration is null)
        {
            return current;
        }

        var completed = current.WithoutPendingMigration();
        await PersistAsync(completed, "ManagedDataRootMigrationCompleted", cancellationToken).ConfigureAwait(false);
        return completed;
    }

    private async Task PersistAsync(
        RuntimeConfiguration configuration,
        string action,
        CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var now = timeProvider.GetUtcNow();
            var audit = new AuditEvent(
                now,
                identityProvider.GetCurrentIdentity(),
                action,
                nameof(ApplicationSetting),
                RuntimeConfigurationKey,
                "Succeeded",
                Guid.NewGuid().ToString("N"));
            await settingsStore.SetValueAsync(
                RuntimeConfigurationKey,
                JsonSerializer.Serialize(configuration, JsonDefaults.Options),
                audit,
                cancellationToken).ConfigureAwait(false);
            Current = configuration;
            LoadWarning = null;
            loaded = true;
        }
        finally
        {
            gate.Release();
        }
    }
}
