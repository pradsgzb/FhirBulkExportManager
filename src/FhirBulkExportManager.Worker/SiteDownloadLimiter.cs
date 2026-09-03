using System.Collections.Concurrent;
using FhirBulkExportManager.Application.Configuration;
using Microsoft.Extensions.Options;

namespace FhirBulkExportManager.Worker;

public sealed class SiteDownloadLimiter(IOptions<ProcessingOptions> options)
{
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> siteSemaphores = new();
    private readonly ConcurrentDictionary<(Guid SiteId, int PluginLimit), SemaphoreSlim> pluginSemaphores = new();

    public async Task<IDisposable> AcquireAsync(Guid siteId, int pluginLimit, CancellationToken cancellationToken)
    {
        var configured = ConcurrencyLimits.EffectiveSiteDownloadLimit(options.Value.PerSiteFileDownloadLimit, pluginLimit: null);
        var effectivePluginLimit = Math.Clamp(pluginLimit, 1, 4);
        var siteSemaphore = siteSemaphores.GetOrAdd(siteId, _ => new SemaphoreSlim(configured, configured));
        var pluginSemaphore = pluginSemaphores.GetOrAdd((siteId, effectivePluginLimit), static key => new SemaphoreSlim(key.PluginLimit, key.PluginLimit));
        await siteSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await pluginSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            siteSemaphore.Release();
            throw;
        }

        return new Releaser(siteSemaphore, pluginSemaphore);
    }

    private sealed class Releaser(SemaphoreSlim siteSemaphore, SemaphoreSlim pluginSemaphore) : IDisposable
    {
        private int disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0)
            {
                pluginSemaphore.Release();
                siteSemaphore.Release();
            }
        }
    }
}
