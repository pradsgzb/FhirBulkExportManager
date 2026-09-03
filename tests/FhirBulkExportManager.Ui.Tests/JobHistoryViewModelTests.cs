using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using FhirBulkExportManager.App.Wpf;
using FhirBulkExportManager.Domain;
using FhirBulkExportManager.PluginContracts;
using FhirBulkExportManager.Shared;

namespace FhirBulkExportManager.Ui.Tests;

public sealed class JobHistoryViewModelTests
{
    [Fact]
    public void History_item_exposes_progress_headers_and_bodies()
    {
        var now = DateTimeOffset.Parse("2026-09-03T12:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
        var exchange = new HttpExchangeContract(
            "Bulk export status",
            now,
            now.AddMilliseconds(125),
            "GET",
            "https://example.test/status?job=123",
            [new HttpHeaderContract("Authorization", ["[REDACTED]"])],
            null,
            202,
            "Accepted",
            [new HttpHeaderContract("X-Progress", ["17% Completed"])],
            "status body");
        var metadata = JsonSerializer.Serialize(
            new JobEventMetadataContract("17% Completed", [exchange]),
            JsonDefaults.Options);
        var jobEvent = new ExportJobEvent(
            Guid.NewGuid(),
            ExportJobState.Running,
            ExportJobState.WaitingForPoll,
            "RemoteExportRunning",
            now,
            "Remote export is still processing.",
            Guid.NewGuid(),
            "correlation",
            metadata);

        var item = JobStatusHistoryItem.Create(jobEvent);

        Assert.Equal("17% Completed", item.Progress);
        var http = Assert.Single(item.HttpExchanges);
        Assert.Contains("X-Progress: 17% Completed", http.ResponseHeadersText, StringComparison.Ordinal);
        Assert.Equal("status body", http.ResponseBodyText);
    }

    [Fact]
    public void Capture_redacts_reusable_credentials_but_keeps_header_names()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://example.test/token?access_token=uri-secret")
        {
            Content = new StringContent("client_assertion=jwt-secret&scope=system%2FPatient.read", Encoding.UTF8, "application/x-www-form-urlencoded"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "header-secret");
        using var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"access_token\":\"body-secret\",\"expires_in\":299}"),
        };

        var exchange = HttpExchangeCapture.Create(
            "OAuth token",
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            request,
            response,
            "client_assertion=jwt-secret&scope=system%2FPatient.read",
            "{\"access_token\":\"body-secret\",\"expires_in\":299}");
        var json = JsonSerializer.Serialize(exchange);

        Assert.Contains("Authorization", json, StringComparison.Ordinal);
        Assert.Contains("REDACTED", json, StringComparison.Ordinal);
        Assert.DoesNotContain("header-secret", json, StringComparison.Ordinal);
        Assert.DoesNotContain("uri-secret", json, StringComparison.Ordinal);
        Assert.DoesNotContain("jwt-secret", json, StringComparison.Ordinal);
        Assert.DoesNotContain("body-secret", json, StringComparison.Ordinal);
    }
}
