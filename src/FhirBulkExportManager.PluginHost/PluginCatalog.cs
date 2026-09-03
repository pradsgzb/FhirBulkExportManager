using FhirBulkExportManager.Application.Abstractions;
using FhirBulkExportManager.PluginContracts;

namespace FhirBulkExportManager.PluginHost;

public sealed class PluginCatalog : IVendorPluginCatalog
{
    private readonly object sync = new();
    private readonly Dictionary<(string Id, string Version), IVendorPlugin> plugins = new();

    public PluginCatalog(IEnumerable<IVendorPlugin> builtInPlugins)
    {
        foreach (var plugin in builtInPlugins)
        {
            Add(plugin);
        }
    }

    public IReadOnlyCollection<PluginMetadata> Available
    {
        get
        {
            lock (sync)
            {
                return plugins.Values.Select(x => x.Metadata).OrderBy(x => x.Name).ThenByDescending(x => x.Version).ToArray();
            }
        }
    }

    public IVendorPlugin GetRequired(string pluginId, string pinnedVersion)
    {
        lock (sync)
        {
            if (plugins.TryGetValue((pluginId, pinnedVersion), out var plugin))
            {
                return plugin;
            }
        }

        throw new InvalidOperationException($"Required plugin '{pluginId}' version '{pinnedVersion}' is not loaded. The job remains unchanged.");
    }

    public void Add(IVendorPlugin plugin)
    {
        var key = (Id: plugin.Metadata.Id, Version: plugin.Metadata.Version.ToString());
        lock (sync)
        {
            if (!plugins.TryAdd(key, plugin))
            {
                throw new InvalidOperationException($"Plugin '{key.Id}' version '{key.Version}' is already loaded.");
            }
        }
    }
}
