using FhirBulkExportManager.Application.Abstractions;
using FhirBulkExportManager.Application.Services;
using FhirBulkExportManager.Domain;

namespace FhirBulkExportManager.Application.Tests;

public sealed class ExportJobStatusRecoveryTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-03T12:30:36Z", System.Globalization.CultureInfo.InvariantCulture);

    [Fact]
    public async Task Manual_status_recovers_failed_permanent_job_without_starting_a_new_export()
    {
        var job = new ExportJob(
            Guid.NewGuid(),
            "Recover status",
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            "test-plugin",
            "1.0.0",
            ExportType.Group,
            JobPriority.Normal,
            "C:\\Exports",
            Now,
            "group-1");
        job.ConfirmRemoteJob("https://vendor.example/status?job=opaque", Now, Now);
        Transition(job, ExportJobState.WaitingForPoll);
        Transition(job, ExportJobState.Running);
        job.RecordFailure("RemoteRejected", "Status request returned 400.", incrementRetry: false);
        Transition(job, ExportJobState.FailedPermanent);
        var store = new RecordingExportStore(job);
        var signal = new RecordingWorkSignal();
        var service = new ExportJobService(
            store,
            null!,
            null!,
            new TestIdentityProvider(),
            signal,
            new FixedTimeProvider(Now.AddMinutes(5)));

        var result = await service.GetStatusNowAsync(job.Id, CancellationToken.None);

        Assert.Equal(ExportJobState.Running, result.State);
        Assert.Equal(ExportJobState.Running, job.State);
        Assert.Null(job.LastErrorCode);
        Assert.Null(job.LastErrorMessage);
        Assert.Equal("https://vendor.example/status?job=opaque", job.RemoteStatusUrl);
        Assert.Null(job.RemoteRequestUrl);
        Assert.Equal(1, signal.SignalCount);
        Assert.NotNull(store.LastEvent);
        Assert.Equal(ExportJobState.FailedPermanent, store.LastEvent.PreviousState);
        Assert.Equal(ExportJobState.Running, store.LastEvent.NewState);
        Assert.Equal("ManualStatusRequested", store.LastEvent.EventType);
    }

    private static void Transition(ExportJob job, ExportJobState state)
    {
        var operationId = Guid.NewGuid();
        _ = job.Transition(state, Now, "Test", "Test", operationId, operationId.ToString("N"));
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class TestIdentityProvider : IWindowsIdentityProvider
    {
        public string GetCurrentIdentity() => "TEST\\operator";
    }

    private sealed class RecordingWorkSignal : IWorkSignal
    {
        public int SignalCount { get; private set; }

        public void Signal() => SignalCount++;

        public Task WaitAsync(TimeSpan maximumWait, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class RecordingExportStore(ExportJob job) : IExportStore
    {
        public ExportJobEvent? LastEvent { get; private set; }

        public Task<ExportJob?> GetJobAsync(Guid jobId, CancellationToken cancellationToken) =>
            Task.FromResult<ExportJob?>(jobId == job.Id ? job : null);

        public Task UpdateJobAsync(ExportJob updatedJob, ExportJobEvent jobEvent, AuditEvent auditEvent, long expectedVersion, CancellationToken cancellationToken)
        {
            Assert.Same(job, updatedJob);
            Assert.True(expectedVersion < updatedJob.Version);
            LastEvent = jobEvent;
            return Task.CompletedTask;
        }

        public Task AddJobAsync(ExportJob exportJob, ExportJobEvent createdEvent, AuditEvent auditEvent, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ExportJob?> GetJobByRemoteStatusUrlAsync(string exactContentLocation, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<PagedResult<ExportJob>> QueryJobsAsync(JobQuery query, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<ExportJob>> GetRunnableJobsAsync(DateTimeOffset now, int take, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task SaveManifestAsync(ExportJob exportJob, ExportJobEvent jobEvent, IReadOnlyCollection<ExportFile> files, AuditEvent auditEvent, long expectedVersion, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<ExportFile>> GetFilesAsync(Guid jobId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ExportFile?> GetFileAsync(Guid fileId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task UpdateFileAsync(ExportFile file, long expectedVersion, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task AddJobEventAsync(ExportJobEvent jobEvent, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<ExportJobEvent>> GetTimelineAsync(Guid jobId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> TryAcquireLeaseAsync(Guid jobId, string owner, DateTimeOffset now, TimeSpan duration, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task ReleaseLeaseAsync(Guid jobId, string owner, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task ClearExpiredLeasesAsync(DateTimeOffset now, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task DeleteJobMetadataAsync(Guid jobId, AuditEvent auditEvent, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
