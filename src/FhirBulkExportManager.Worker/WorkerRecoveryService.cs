using FhirBulkExportManager.Application.Abstractions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FhirBulkExportManager.Worker;

public sealed class WorkerRecoveryService(
    IDatabaseLifecycle databaseLifecycle,
    IExportStore exportStore,
    IWorkSignal workSignal,
    TimeProvider timeProvider,
    ILogger<WorkerRecoveryService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await databaseLifecycle.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await exportStore.ClearExpiredLeasesAsync(timeProvider.GetUtcNow(), cancellationToken).ConfigureAwait(false);
        logger.LogInformation("Database schema {SchemaVersion} is ready; non-terminal started jobs will be recovered. Draft jobs remain Draft.", await databaseLifecycle.GetSchemaVersionAsync(cancellationToken).ConfigureAwait(false));
        workSignal.Signal();
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

