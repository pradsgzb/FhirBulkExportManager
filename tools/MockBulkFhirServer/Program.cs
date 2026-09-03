using System.Collections.Concurrent;
using System.Text;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();
var jobs = new ConcurrentDictionary<string, MockExportJob>(StringComparer.Ordinal);

app.MapPost("/oauth/token", async context =>
{
    if (!context.Request.HasFormContentType)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        await context.Response.WriteAsJsonAsync(OperationOutcome("error", "invalid", "Form content is required."));
        return;
    }

    var form = await context.Request.ReadFormAsync(context.RequestAborted);
    if (string.IsNullOrWhiteSpace(form["client_id"]) || (string.IsNullOrWhiteSpace(form["client_secret"]) && string.IsNullOrWhiteSpace(form["client_assertion"])))
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        await context.Response.WriteAsJsonAsync(new { error = "invalid_client" });
        return;
    }

    await context.Response.WriteAsJsonAsync(new { access_token = "integration-test-token", token_type = "Bearer", expires_in = 300 });
});

app.MapPost("/healthwizz/assertion", async context =>
{
    var form = await context.Request.ReadFormAsync(context.RequestAborted);
    if (string.IsNullOrWhiteSpace(form["client_id"]))
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        await context.Response.WriteAsJsonAsync(new { error = "missing_client_id" });
        return;
    }

    await context.Response.WriteAsJsonAsync(new { client_assertion = "mock-signed-assertion" });
});

app.MapGet("/fhir/metadata", () => Results.Json(new
{
    resourceType = "CapabilityStatement",
    status = "active",
    date = "2026-09-03T00:00:00Z",
    kind = "instance",
    fhirVersion = "4.0.1",
    format = new[] { "application/fhir+json", "application/fhir+ndjson" },
}, contentType: "application/fhir+json"));

app.MapGet("/fhir/$export", (HttpContext context) => Kickoff(context, jobs, "system"));
app.MapGet("/fhir/Patient/$export", (HttpContext context) => Kickoff(context, jobs, "patient"));
app.MapGet("/fhir/Group/{groupId}/$export", (HttpContext context, string groupId) => Kickoff(context, jobs, "group:" + groupId));

app.MapGet("/status/{id}", (HttpContext context, string id) =>
{
    if (!jobs.TryGetValue(id, out var job) || job.Deleted)
    {
        return Results.NotFound(OperationOutcome("error", "not-found", "Remote export does not exist."));
    }

    var poll = job.IncrementPollCount();
    if (job.Scenario == "rate-limit" && poll == 1)
    {
        context.Response.Headers["Retry-After"] = "2";
        return Results.StatusCode(StatusCodes.Status429TooManyRequests);
    }

    if (poll <= job.PollsBeforeComplete)
    {
        context.Response.Headers["Retry-After"] = job.Scenario == "http-date"
            ? DateTimeOffset.UtcNow.AddSeconds(2).ToString("r", System.Globalization.CultureInfo.InvariantCulture)
            : "1";
        context.Response.Headers["X-Progress"] = $"{poll * 30}%";
        return Results.Accepted();
    }

    var origin = $"{context.Request.Scheme}://{context.Request.Host}";
    var errorFiles = job.Scenario is "error-outcome" or "warning-outcome"
        ? new[] { new { type = "OperationOutcome", url = $"{origin}/download/{id}/OperationOutcome.ndjson" } }
        : Array.Empty<object>();
    var manifest = new
    {
        transactionTime = "2026-09-03T00:00:00Z",
        request = job.Scope,
        requiresAccessToken = true,
        output = new object[]
        {
            new { type = "Patient", url = $"{origin}/download/{id}/Patient-1.ndjson" },
            new { type = "Patient", url = $"{origin}/download/{id}/Patient-2.ndjson" },
            new { type = "Observation", url = $"{origin}/download/{id}/Observation.ndjson" },
        },
        deleted = Array.Empty<object>(),
        error = errorFiles,
    };
    return Results.Json(manifest, contentType: "application/fhir+json");
});

app.MapDelete("/status/{id}", (string id) =>
{
    if (jobs.TryGetValue(id, out var job))
    {
        job.Deleted = true;
    }

    return Results.NoContent();
});

app.MapGet("/download/{id}/{fileName}", async (HttpContext context, string id, string fileName) =>
{
    if (!jobs.TryGetValue(id, out var job) || job.Deleted)
    {
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        return;
    }

    var content = fileName switch
    {
        "Patient-1.ndjson" => "{\"resourceType\":\"Patient\",\"id\":\"example-1\"}\n",
        "Patient-2.ndjson" => "{\"resourceType\":\"Patient\",\"id\":\"example-2\"}\n",
        "Observation.ndjson" => "{\"resourceType\":\"Observation\",\"id\":\"example-observation\",\"status\":\"final\",\"code\":{\"text\":\"Mock\"}}\n",
        "OperationOutcome.ndjson" when job.Scenario == "error-outcome" => "{\"resourceType\":\"OperationOutcome\",\"issue\":[{\"severity\":\"error\",\"code\":\"processing\",\"diagnostics\":\"Mock partial export error\"}]}\n",
        "OperationOutcome.ndjson" => "{\"resourceType\":\"OperationOutcome\",\"issue\":[{\"severity\":\"warning\",\"code\":\"informational\",\"diagnostics\":\"Mock warning\"}]}\n",
        _ => string.Empty,
    };
    if (content.Length == 0)
    {
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        return;
    }

    var bytes = Encoding.UTF8.GetBytes(content);
    var etag = $"\"{Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes))[..16]}\"";
    context.Response.Headers["ETag"] = etag;
    context.Response.Headers["Last-Modified"] = "Thu, 03 Sep 2026 00:00:00 GMT";
    context.Response.ContentType = "application/fhir+ndjson";
    var start = 0;
    if (context.Request.Headers["Range"].Count > 0 && TryReadRange(context.Request.Headers["Range"].ToString(), bytes.Length, out var rangeStart))
    {
        start = rangeStart;
        context.Response.StatusCode = StatusCodes.Status206PartialContent;
        context.Response.Headers["Content-Range"] = $"bytes {start}-{bytes.Length - 1}/{bytes.Length}";
    }

    context.Response.ContentLength = bytes.Length - start;
    await context.Response.Body.WriteAsync(bytes.AsMemory(start), context.RequestAborted);
});

app.MapGet("/health", () => Results.Ok(new { status = "ready", activeJobs = jobs.Count(x => !x.Value.Deleted) }));

app.Run();

static IResult Kickoff(HttpContext context, ConcurrentDictionary<string, MockExportJob> jobs, string scope)
{
    if (!context.Request.Headers.TryGetValue("Prefer", out var prefer) || !prefer.ToString().Contains("respond-async", StringComparison.OrdinalIgnoreCase))
    {
        return Results.BadRequest(OperationOutcome("error", "required", "Prefer: respond-async is required."));
    }

    var id = Guid.NewGuid().ToString("N");
    var scenario = context.Request.Query["scenario"].FirstOrDefault() ?? "normal";
    var polls = int.TryParse(context.Request.Query["polls"], out var configuredPolls) ? Math.Clamp(configuredPolls, 0, 10) : 2;
    jobs[id] = new MockExportJob(scope, scenario, polls);
    var statusUrl = $"{context.Request.Scheme}://{context.Request.Host}/status/{id}";
    context.Response.Headers["Location"] = statusUrl;
    context.Response.Headers["Content-Location"] = statusUrl;
    context.Response.Headers["Retry-After"] = "1";
    return Results.Accepted();
}

static object OperationOutcome(string severity, string code, string diagnostics) => new
{
    resourceType = "OperationOutcome",
    issue = new[] { new { severity, code, diagnostics } },
};

static bool TryReadRange(string value, int length, out int start)
{
    start = 0;
    if (!value.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase))
    {
        return false;
    }

    var first = value[6..].Split('-', 2)[0];
    return int.TryParse(first, out start) && start >= 0 && start < length;
}

public sealed class MockExportJob(string scope, string scenario, int pollsBeforeComplete)
{
    private int pollCount;

    public string Scope { get; } = scope;
    public string Scenario { get; } = scenario;
    public int PollsBeforeComplete { get; } = pollsBeforeComplete;
    public bool Deleted { get; set; }

    public int IncrementPollCount() => Interlocked.Increment(ref pollCount);
}

public partial class Program
{
}
