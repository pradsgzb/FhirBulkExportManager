using System.ComponentModel.DataAnnotations;

namespace FhirBulkExportManager.Application.Configuration;

public sealed class ProcessingOptions
{
    public const string SectionName = "Processing";

    [Range(1, int.MaxValue)]
    public int MaxConcurrentExports { get; set; } = Math.Max(1, Math.Min(4, Environment.ProcessorCount));

    [Range(1, 4)]
    public int PerSiteFileDownloadLimit { get; set; } = 4;

    public bool AutomaticPollingEnabled { get; set; } = true;

    [Range(5, 3600)]
    public int DefaultPollingBackoffSeconds { get; set; } = 30;

    [Range(30, 86400)]
    public int MaximumPollingIntervalSeconds { get; set; } = 3600;

    [Range(15, 3600)]
    public int LeaseDurationSeconds { get; set; } = 120;

    [Range(1, 600)]
    public int SchedulerWakeIntervalSeconds { get; set; } = 5;

    public int EffectiveMaxConcurrentExports => ConcurrencyLimits.EffectiveExportLimit(MaxConcurrentExports, Environment.ProcessorCount);
}

public sealed class HttpOptions
{
    public const string SectionName = "Http";

    [Range(1, 300)]
    public int ConnectionTimeoutSeconds { get; set; } = 30;

    [Range(10, 1800)]
    public int RequestTimeoutSeconds { get; set; } = 120;

    [Range(0, 10)]
    public int RetryCount { get; set; } = 4;

    [Range(10, 600)]
    public int CircuitBreakSeconds { get; set; } = 60;
}

public sealed class StorageOptions
{
    public const string SectionName = "Storage";

    [Required]
    public string DataRoot { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FhirBulkExportManager", "Data");

    [Range(1, 1024)]
    public int MinimumFreeDiskSpaceGB { get; set; } = 10;
}

public sealed class PluginOptions
{
    public const string SectionName = "Plugins";

    [Required]
    public string Directory { get; set; } = Path.Combine(AppContext.BaseDirectory, "Plugins");

    public bool AllowUnsignedPlugins { get; set; }

    public List<string> TrustedAssemblySha256 { get; set; } = [];
}

public static class ConcurrencyLimits
{
    public static int EffectiveExportLimit(int configured, int processorCount)
    {
        if (configured < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(configured), "At least one export worker is required.");
        }

        if (processorCount < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(processorCount));
        }

        return Math.Min(configured, processorCount);
    }

    public static int EffectiveSiteDownloadLimit(int configured, int? pluginLimit)
    {
        if (configured is < 1 or > 4)
        {
            throw new ArgumentOutOfRangeException(nameof(configured), "Per-site downloads must be between 1 and 4.");
        }

        var vendorLimit = pluginLimit is null ? 4 : Math.Clamp(pluginLimit.Value, 1, 4);
        return Math.Min(configured, vendorLimit);
    }
}

