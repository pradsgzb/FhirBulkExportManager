using System.Collections.Concurrent;
using FhirBulkExportManager.Application.Configuration;
using Microsoft.Extensions.Options;

namespace FhirBulkExportManager.Worker;

public sealed record SiteGateAcquisition(IDisposable? Lease, DateTimeOffset? RetryAtUtc)
{
    public bool Acquired => Lease is not null;
}

public sealed class SiteResilienceGate(TimeProvider timeProvider, IOptions<HttpOptions> options)
{
    private readonly ConcurrentDictionary<Guid, SiteState> states = new();

    public async Task<SiteGateAcquisition> TryAcquireAsync(Guid siteId, CancellationToken cancellationToken)
    {
        var state = states.GetOrAdd(siteId, static _ => new SiteState());
        DateTimeOffset blockedUntil;
        lock (state.Sync)
        {
            blockedUntil = state.BlockedUntilUtc;
        }

        var now = timeProvider.GetUtcNow();
        if (blockedUntil > now)
        {
            return new SiteGateAcquisition(null, blockedUntil);
        }

        if (!await state.Semaphore.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return new SiteGateAcquisition(null, now.AddSeconds(1));
        }

        return new SiteGateAcquisition(new Lease(state), null);
    }

    public void ReportSuccess(Guid siteId)
    {
        var state = states.GetOrAdd(siteId, static _ => new SiteState());
        lock (state.Sync)
        {
            state.ConsecutiveTransientFailures = 0;
            state.BlockedUntilUtc = timeProvider.GetUtcNow().AddMilliseconds(250);
        }
    }

    public void ReportTransientFailure(Guid siteId, DateTimeOffset? serverRetryAtUtc)
    {
        var state = states.GetOrAdd(siteId, static _ => new SiteState());
        lock (state.Sync)
        {
            state.ConsecutiveTransientFailures++;
            var now = timeProvider.GetUtcNow();
            var requested = serverRetryAtUtc ?? now.AddSeconds(Math.Min(300, 5 * state.ConsecutiveTransientFailures));
            if (state.ConsecutiveTransientFailures >= 5)
            {
                requested = requested > now.AddSeconds(options.Value.CircuitBreakSeconds)
                    ? requested
                    : now.AddSeconds(options.Value.CircuitBreakSeconds);
            }

            state.BlockedUntilUtc = requested;
        }
    }

    private sealed class SiteState
    {
        public object Sync { get; } = new();
        public SemaphoreSlim Semaphore { get; } = new(1, 1);
        public int ConsecutiveTransientFailures { get; set; }
        public DateTimeOffset BlockedUntilUtc { get; set; }
    }

    private sealed class Lease(SiteState state) : IDisposable
    {
        private int disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0)
            {
                state.Semaphore.Release();
            }
        }
    }
}
