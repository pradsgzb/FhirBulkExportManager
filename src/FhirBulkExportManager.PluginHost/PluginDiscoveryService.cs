using System.Reflection;
using System.Text.Json;
using FhirBulkExportManager.Application.Configuration;
using FhirBulkExportManager.PluginContracts;
using FhirBulkExportManager.Shared;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FhirBulkExportManager.PluginHost;

public sealed class PluginDiscoveryService(
    PluginCatalog catalog,
    IOptions<PluginOptions> options,
    ILogger<PluginDiscoveryService> logger) : IHostedService
{
    private static readonly Version HostVersion = new(1, 0, 0);
    private readonly List<PluginLoadContext> loadContexts = [];

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var directory = Path.GetFullPath(options.Value.Directory);
        if (!Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
            return;
        }

        foreach (var manifestPath in Directory.EnumerateFiles(directory, "plugin.json", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await LoadPluginAsync(manifestPath, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogError("Plugin manifest {ManifestFile} was rejected: {Reason}", Path.GetFileName(manifestPath), SecurityRedactor.Redact(exception.Message));
            }
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private async Task LoadPluginAsync(string manifestPath, CancellationToken cancellationToken)
    {
        await using var manifestStream = File.OpenRead(manifestPath);
        var manifest = await JsonSerializer.DeserializeAsync<PluginManifest>(manifestStream, JsonDefaults.Options, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Plugin manifest is empty.");
        if (!manifest.Enabled)
        {
            return;
        }

        ValidateCompatibility(manifest);
        var pluginRoot = Path.GetDirectoryName(Path.GetFullPath(manifestPath))!;
        var assemblyPath = Path.GetFullPath(Path.Combine(pluginRoot, manifest.EntryAssembly));
        if (!assemblyPath.StartsWith(pluginRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !File.Exists(assemblyPath))
        {
            throw new InvalidOperationException("Plugin entry assembly is missing or outside its plugin directory.");
        }

        var actualHash = await HashUtility.Sha256FileAsync(assemblyPath, cancellationToken).ConfigureAwait(false);
        var trustedHashes = options.Value.TrustedAssemblySha256.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var hashMatchesManifest = !string.IsNullOrWhiteSpace(manifest.Sha256) && string.Equals(actualHash, manifest.Sha256, StringComparison.OrdinalIgnoreCase);
        if (!options.Value.AllowUnsignedPlugins && !hashMatchesManifest && !trustedHashes.Contains(actualHash))
        {
            throw new InvalidOperationException("Plugin assembly is not allowlisted and its manifest SHA-256 is absent or mismatched.");
        }

        var loadContext = new PluginLoadContext(assemblyPath);
        var assembly = loadContext.LoadFromAssemblyPath(assemblyPath);
        var type = assembly.GetTypes().SingleOrDefault(x => typeof(IVendorPlugin).IsAssignableFrom(x) && x is { IsClass: true, IsAbstract: false });
        if (type is null || type.GetConstructor(Type.EmptyTypes) is null)
        {
            throw new InvalidOperationException("External plugin must expose exactly one public parameterless IVendorPlugin implementation.");
        }

        var plugin = (IVendorPlugin)Activator.CreateInstance(type)!;
        if (!string.Equals(plugin.Metadata.Id, manifest.Id, StringComparison.Ordinal) || plugin.Metadata.Version != Version.Parse(manifest.Version))
        {
            throw new InvalidOperationException("Plugin metadata does not match plugin.json.");
        }

        catalog.Add(plugin);
        loadContexts.Add(loadContext);
        logger.LogInformation("Loaded trusted plugin {PluginId} version {PluginVersion} from {PluginDirectory}", manifest.Id, manifest.Version, pluginRoot);
    }

    private static void ValidateCompatibility(PluginManifest manifest)
    {
        var minimum = Version.Parse(manifest.MinimumHostVersion);
        var maximum = string.IsNullOrWhiteSpace(manifest.MaximumHostVersion) ? null : Version.Parse(manifest.MaximumHostVersion);
        if (HostVersion < minimum || (maximum is not null && HostVersion > maximum))
        {
            throw new InvalidOperationException($"Plugin supports host versions {minimum} through {maximum?.ToString() ?? "unbounded"}; current host is {HostVersion}.");
        }
    }
}
