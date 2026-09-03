using FhirBulkExportManager.Application.Abstractions;

namespace FhirBulkExportManager.Worker;

public sealed class WorkSignal : IWorkSignal, IDisposable
{
    private readonly SemaphoreSlim semaphore = new(0, 1);

    public void Signal()
    {
        try
        {
            semaphore.Release();
        }
        catch (SemaphoreFullException)
        {
            // A wake-up is already pending; signals are intentionally coalesced.
        }
    }

    public async Task WaitAsync(TimeSpan maximumWait, CancellationToken cancellationToken)
    {
        _ = await semaphore.WaitAsync(maximumWait, cancellationToken).ConfigureAwait(false);
    }

    public void Dispose() => semaphore.Dispose();
}
