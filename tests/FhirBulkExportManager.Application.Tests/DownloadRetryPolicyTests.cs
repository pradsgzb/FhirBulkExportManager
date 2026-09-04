using System.Net;
using FhirBulkExportManager.Worker;

namespace FhirBulkExportManager.Application.Tests;

public sealed class DownloadRetryPolicyTests
{
    [Fact]
    public void Automatic_resume_keeps_transient_downloads_recoverable_after_bounded_attempt_limit()
    {
        Assert.True(DownloadRetryPolicy.ShouldRetry(DownloadFailureKind.Transient, automaticResume: true, attemptsUsed: 500));
    }

    [Theory]
    [InlineData(4, true)]
    [InlineData(5, false)]
    [InlineData(50, false)]
    public void Disabled_automatic_resume_uses_bounded_attempts(int attemptsUsed, bool expected)
    {
        Assert.Equal(expected, DownloadRetryPolicy.ShouldRetry(DownloadFailureKind.Transient, automaticResume: false, attemptsUsed));
    }

    [Fact]
    public void Permanent_failure_is_never_automatically_retried()
    {
        Assert.False(DownloadRetryPolicy.ShouldRetry(DownloadFailureKind.Permanent, automaticResume: true, attemptsUsed: 1));
    }

    [Theory]
    [InlineData(4, true)]
    [InlineData(5, false)]
    public void Authentication_refresh_failures_remain_bounded(int attemptsUsed, bool expected)
    {
        Assert.Equal(expected, DownloadRetryPolicy.ShouldRetry(DownloadFailureKind.Authentication, automaticResume: true, attemptsUsed));
    }

    [Theory]
    [InlineData(null, "Transient")]
    [InlineData(HttpStatusCode.OK, "Transient")]
    [InlineData(HttpStatusCode.Unauthorized, "Authentication")]
    [InlineData(HttpStatusCode.RequestedRangeNotSatisfiable, "Transient")]
    [InlineData(HttpStatusCode.TooManyRequests, "Transient")]
    [InlineData(HttpStatusCode.ServiceUnavailable, "Transient")]
    [InlineData(HttpStatusCode.BadRequest, "Permanent")]
    [InlineData(HttpStatusCode.Forbidden, "Permanent")]
    public void Http_download_failures_are_classified_for_safe_retry(HttpStatusCode? statusCode, string expected)
    {
        var exception = new HttpRequestException("Download failed.", null, statusCode);

        Assert.Equal(Enum.Parse<DownloadFailureKind>(expected), DownloadRetryPolicy.Classify(exception));
    }
}
