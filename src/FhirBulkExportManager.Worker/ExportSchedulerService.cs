using System.Collections.Concurrent;
using System.Threading.Channels;
using FhirBulkExportManager.Application.Abstractions;
using FhirBulkExportManager.Application.Configuration;
using FhirBulkExportManager.Domain;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FhirBulkExportManager.Worker;

public sealed class ExportSchedulerService(
    IExportStore exportStore,
    ExportJobProcessor processor,
    IWorkSignal workSignal,
    IOptions<ProcessingOptions> options,
    TimeProvider timeProvider,
    ILogger<ExportSchedulerService> logger) : BackgroundService
{
    private readonly string owner = $"{Environment.MachineName}:{Environment.ProcessId}:{Guid.NewGuid():N}";
    private readonly ConcurrentDictionary<Guid, byte> scheduled = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var workerCount = ConcurrencyLimits.EffectiveExportLimit(options.Value.MaxConcurrentExports, Environment.ProcessorCount);
        var channel = Channel.CreateBounded<Guid>(new BoundedChannelOptions(workerCount * 4)
        {
            SingleWriter = true,
            SingleReader = false,
            FullMode = BoundedChannelFullMode.Wait,
        });

        await exportStore.ClearExpiredLeasesAsync(timeProvider.GetUtcNow(), stoppingToken).ConfigureAwait(false);
        var producer = ProduceAsync(channel.Writer, workerCount, stoppingToken);
        var workers = Enumerable.Range(0, workerCount).Select(index => ConsumeAsync(index, channel.Reader, stoppingToken)).ToArray();
        try
        {
            await Task.WhenAll(workers.Prepend(producer)).ConfigureAwait(false);
        }
        finally
        {
            channel.Writer.TryComplete();
        }
    }

    private async Task ProduceAsync(ChannelWriter<Guid> writer, int workerCount, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var candidates = await exportStore.GetRunnableJobsAsync(timeProvider.GetUtcNow(), workerCount * 8, cancellationToken).ConfigureAwait(false);
                if (!options.Value.AutomaticPollingEnabled)
                {
                    candidates = candidates.Where(x => x.State != ExportJobState.WaitingForPoll).ToArray();
                }
                foreach (var job in FairSiteSelector.Select(candidates, workerCount * 4))
                {
                    if (scheduled.TryAdd(job.Id, 0))
                    {
                        await writer.WriteAsync(job.Id, cancellationToken).ConfigureAwait(false);
                    }
                }

                await workSignal.WaitAsync(TimeSpan.FromSeconds(options.Value.SchedulerWakeIntervalSeconds), cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            writer.TryComplete();
        }
    }

    private async Task ConsumeAsync(int workerIndex, ChannelReader<Guid> reader, CancellationToken cancellationToken)
    {
        var workerOwner = owner + ":" + workerIndex.ToString(System.Globalization.CultureInfo.InvariantCulture);
        await foreach (var jobId in reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            CancellationTokenSource? operationCancellation = null;
            Task? heartbeat = null;
            try
            {
                var acquired = await exportStore.TryAcquireLeaseAsync(jobId, workerOwner, timeProvider.GetUtcNow(), TimeSpan.FromSeconds(options.Value.LeaseDurationSeconds), cancellationToken).ConfigureAwait(false);
                if (acquired)
                {
                    operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    heartbeat = MaintainLeaseAsync(jobId, workerOwner, operationCancellation, cancellationToken);
                    await processor.ProcessAsync(jobId, operationCancellation.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Worker {WorkerIndex} failed while processing job {JobId}", workerIndex, jobId);
            }
            finally
            {
                if (operationCancellation is not null)
                {
                    await operationCancellation.CancelAsync().ConfigureAwait(false);
                    if (heartbeat is not null)
                    {
                        try
                        {
                            await heartbeat.ConfigureAwait(false);
                        }
                        catch (OperationCanceledException)
                        {
                        }
                    }

                    operationCancellation.Dispose();
                }

                try
                {
                    await exportStore.ReleaseLeaseAsync(jobId, workerOwner, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    logger.LogWarning(exception, "Could not release lease for job {JobId}; lease expiry will recover it.", jobId);
                }

                scheduled.TryRemove(jobId, out _);
                workSignal.Signal();
            }
        }
    }

    private async Task MaintainLeaseAsync(Guid jobId, string workerOwner, CancellationTokenSource operationCancellation, CancellationToken serviceCancellation)
    {
        var duration = TimeSpan.FromSeconds(options.Value.LeaseDurationSeconds);
        var interval = TimeSpan.FromSeconds(Math.Max(5, options.Value.LeaseDurationSeconds / 3));
        using var timer = new PeriodicTimer(interval, timeProvider);
        try
        {
            while (await timer.WaitForNextTickAsync(operationCancellation.Token).ConfigureAwait(false))
            {
                var renewed = await exportStore.TryAcquireLeaseAsync(jobId, workerOwner, timeProvider.GetUtcNow(), duration, serviceCancellation).ConfigureAwait(false);
                if (!renewed)
                {
                    await operationCancellation.CancelAsync().ConfigureAwait(false);
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (operationCancellation.IsCancellationRequested)
        {
        }
    }
}
