using FhirBulkExportManager.Domain;
using FhirBulkExportManager.Shared;

namespace FhirBulkExportManager.Domain.Tests;

public sealed class ExportJobStateMachineTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 3, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void New_job_is_a_draft_and_has_no_remote_job()
    {
        var job = CreateJob();

        Assert.Equal(ExportJobState.Draft, job.State);
        Assert.False(job.HasConfirmedRemoteJob);
        Assert.Null(job.RemoteStatusUrl);
    }

    [Fact]
    public void Illegal_transition_is_rejected_by_domain()
    {
        var job = CreateJob();

        var exception = Assert.Throws<InvalidOperationException>(() => Transition(job, ExportJobState.Starting));

        Assert.Contains("Draft -> Starting", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Content_location_is_preserved_as_the_exact_opaque_url()
    {
        var job = CreateJob();
        Transition(job, ExportJobState.Queued);
        Transition(job, ExportJobState.Authenticating);
        Transition(job, ExportJobState.Starting);
        const string exact = "https://vendor.example/status?job_id=A%2FB&file=x%20y";

        job.ConfirmRemoteJob(exact, Now, Now.AddMinutes(2));

        Assert.Equal(exact, job.RemoteStatusUrl);
        Assert.True(job.HasConfirmedRemoteJob);
    }

    [Fact]
    public void Polling_without_content_location_is_blocked()
    {
        var job = CreateJob();

        Assert.Throws<InvalidOperationException>(() => job.RecordPoll(Now, Now.AddMinutes(1), "10%"));
    }

    [Fact]
    public void Existing_remote_export_can_be_imported_without_a_kickoff_request()
    {
        var job = CreateJob();
        var remoteStartedAtUtc = Now.AddMinutes(-15);
        const string exact = "https://vendor.example/status?job_id=A%2FB&opaque=x%20y";

        job.ConfirmRemoteJob(exact, Now, Now, remoteStartedAtUtc: remoteStartedAtUtc);
        Transition(job, ExportJobState.WaitingForPoll);

        Assert.Equal(ExportJobState.WaitingForPoll, job.State);
        Assert.Equal(exact, job.RemoteStatusUrl);
        Assert.Equal(remoteStartedAtUtc, job.StartedAtUtc);
        Assert.Equal(Now, job.NextPollAtUtc);
        Assert.Null(job.RemoteRequestUrl);
    }

    [Fact]
    public void Imported_export_connection_can_be_reassigned_without_changing_content_location()
    {
        var job = CreateJob();
        const string exact = "https://fhir.example.test/status?job_id=opaque";
        job.ConfirmRemoteJob(exact, Now, Now);
        Transition(job, ExportJobState.WaitingForPoll);
        Transition(job, ExportJobState.Running);
        job.ScheduleRetry(Now, "Unauthorized", "Old connection was rejected.");
        Transition(job, ExportJobState.RetryPending);
        var newVendorId = Guid.NewGuid();
        var newSiteId = Guid.NewGuid();
        var newCredentialId = Guid.NewGuid();

        job.ReassignImportedConnection(
            newVendorId,
            newSiteId,
            newCredentialId,
            "eclinicalworks",
            "1.0.0",
            Now.AddMinutes(1));

        Assert.Equal(newVendorId, job.VendorId);
        Assert.Equal(newSiteId, job.SiteId);
        Assert.Equal(newCredentialId, job.CredentialProfileId);
        Assert.Equal("eclinicalworks", job.PluginId);
        Assert.Equal(exact, job.RemoteStatusUrl);
        Assert.Null(job.RemoteRequestUrl);
        Assert.Null(job.LastErrorCode);
        Assert.Equal(0, job.RetryCount);
        Assert.Equal(Now.AddMinutes(1), job.NextPollAtUtc);
    }

    [Fact]
    public void Locally_started_export_connection_cannot_be_reassigned()
    {
        var job = CreateJob();
        job.RecordKickoffRequest("https://fhir.example.test/Group/group-1/$export");
        job.ConfirmRemoteJob("https://fhir.example.test/status?job_id=opaque", Now, Now);

        var exception = Assert.Throws<InvalidOperationException>(() => job.ReassignImportedConnection(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            "eclinicalworks",
            "1.0.0",
            Now));

        Assert.Contains("externally started export", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Waiting_for_poll_without_content_location_is_rejected()
    {
        var job = CreateJob();

        var exception = Assert.Throws<InvalidOperationException>(() => Transition(job, ExportJobState.WaitingForPoll));

        Assert.Contains("Content-Location", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Failed_permanent_is_operator_recoverable_for_a_confirmed_remote_job()
    {
        var job = CreateJob();
        Transition(job, ExportJobState.Queued);
        Transition(job, ExportJobState.Authenticating);
        Transition(job, ExportJobState.Starting);
        job.ConfirmRemoteJob("https://vendor.example/status?job=opaque", Now, Now);
        Transition(job, ExportJobState.WaitingForPoll);
        Transition(job, ExportJobState.Running);
        Transition(job, ExportJobState.FailedPermanent);

        Assert.False(job.IsTerminal);

        job.ClearFailure();
        Transition(job, ExportJobState.Running);

        Assert.Equal(ExportJobState.Running, job.State);
        Assert.True(job.HasConfirmedRemoteJob);
    }

    [Fact]
    public void Local_pause_is_not_remote_cancellation()
    {
        var job = CreateJob();
        Transition(job, ExportJobState.Queued);
        Transition(job, ExportJobState.Paused);

        Assert.Equal(ExportJobState.Paused, job.State);
        Assert.False(job.HasConfirmedRemoteJob);
    }

    [Fact]
    public void Retry_after_supports_seconds_and_http_dates()
    {
        Assert.Equal(Now.AddSeconds(120), RetryAfterParser.Parse("120", Now));
        Assert.Equal(new DateTimeOffset(2026, 9, 3, 12, 5, 0, TimeSpan.Zero), RetryAfterParser.Parse("Thu, 03 Sep 2026 12:05:00 GMT", Now));
    }

    private static ExportJob CreateJob() => new(
        Guid.NewGuid(), "Test Export", Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
        "test-plugin", "1.0.0", ExportType.Group, JobPriority.Normal, "C:\\Exports", Now, "group-1");

    private static void Transition(ExportJob job, ExportJobState target)
    {
        var operation = Guid.NewGuid();
        _ = job.Transition(target, Now, "Test", "Test transition", operation, operation.ToString("N"));
    }
}
