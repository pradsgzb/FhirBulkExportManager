using System.Text.Json;
using FhirBulkExportManager.Domain;
using FhirBulkExportManager.PluginContracts;
using FhirBulkExportManager.Shared;

namespace FhirBulkExportManager.App.Wpf;

public sealed class JobStatusHistoryItem
{
    private JobStatusHistoryItem(
        ExportJobEvent jobEvent,
        string? progress,
        IReadOnlyList<HttpExchangeViewItem> httpExchanges)
    {
        JobEvent = jobEvent;
        Progress = progress ?? string.Empty;
        HttpExchanges = httpExchanges;
    }

    public ExportJobEvent JobEvent { get; }
    public DateTimeOffset TimestampUtc => JobEvent.TimestampUtc;
    public string EventType => JobEvent.EventType;
    public string StateChange => JobEvent.PreviousState == JobEvent.NewState
        ? JobEvent.NewState.ToString()
        : $"{JobEvent.PreviousState} → {JobEvent.NewState}";
    public string Message => JobEvent.Message;
    public string Progress { get; }
    public string RequestCount => HttpExchanges.Count == 0 ? "" : HttpExchanges.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
    public IReadOnlyList<HttpExchangeViewItem> HttpExchanges { get; }

    public static JobStatusHistoryItem Create(ExportJobEvent jobEvent)
    {
        if (string.IsNullOrWhiteSpace(jobEvent.MetadataJson) || jobEvent.MetadataJson == "{}")
        {
            return new JobStatusHistoryItem(jobEvent, string.Empty, []);
        }

        try
        {
            var metadata = JsonSerializer.Deserialize<JobEventMetadataContract>(jobEvent.MetadataJson, JsonDefaults.Options);
            return new JobStatusHistoryItem(
                jobEvent,
                metadata?.Progress ?? string.Empty,
                metadata?.HttpExchanges?.Select(exchange => new HttpExchangeViewItem(exchange)).ToArray() ?? []);
        }
        catch (JsonException)
        {
            return new JobStatusHistoryItem(jobEvent, string.Empty, []);
        }
    }
}

public sealed class HttpExchangeViewItem
{
    public HttpExchangeViewItem(HttpExchangeContract exchange)
    {
        Exchange = exchange;
        RequestHeadersText = FormatHeaders(exchange.RequestHeaders);
        ResponseHeadersText = FormatHeaders(exchange.ResponseHeaders);
        RequestBodyText = string.IsNullOrEmpty(exchange.RequestBody) ? "(no request body)" : exchange.RequestBody;
        ResponseBodyText = FormatResponseBody(exchange);
        RequestText = JoinHttpMessage(RequestLine, RequestHeadersText, RequestBodyText);
        ResponseText = JoinHttpMessage(ResponseStatus, ResponseHeadersText, ResponseBodyText);
        var sections = new List<string>
        {
            "Operation: " + Exchange.Operation,
            "Timing: " + Timing,
        };
        if (!string.IsNullOrWhiteSpace(Failure))
        {
            sections.Add(Failure);
        }

        sections.AddRange([string.Empty, "REQUEST", RequestText, string.Empty, "RESPONSE", ResponseText]);
        AllText = string.Join(Environment.NewLine, sections);
    }

    public HttpExchangeContract Exchange { get; }
    public string Summary => $"{Exchange.Operation} · {Exchange.RequestMethod} · {ResponseStatus}";
    public string RequestLine => $"{Exchange.RequestMethod} {Exchange.RequestUri}";
    public string ResponseStatus => Exchange.ResponseStatusCode is { } statusCode
        ? "HTTP " + HttpStatusText.Format(statusCode)
        : "No HTTP response";
    public string Timing => $"{Exchange.StartedAtUtc:u} · {Math.Max(0, (Exchange.CompletedAtUtc - Exchange.StartedAtUtc).TotalMilliseconds):N0} ms";
    public string Failure => string.IsNullOrWhiteSpace(Exchange.Failure) ? string.Empty : "Failure: " + Exchange.Failure;
    public string RequestHeadersText { get; }
    public string ResponseHeadersText { get; }
    public string RequestBodyText { get; }
    public string ResponseBodyText { get; }
    public string RequestText { get; }
    public string ResponseText { get; }
    public string AllText { get; }

    private static string JoinHttpMessage(string firstLine, string headers, string body) =>
        string.Join(Environment.NewLine, firstLine, headers, string.Empty, body);

    private static string FormatHeaders(IReadOnlyList<HttpHeaderContract> headers)
    {
        if (headers.Count == 0)
        {
            return "(no headers)";
        }

        return string.Join(
            Environment.NewLine,
            headers.SelectMany(header => header.Values.Count == 0
                ? new[] { header.Name + ":" }
                : header.Values.Select(value => header.Name + ": " + value)));
    }

    private static string FormatResponseBody(HttpExchangeContract exchange)
    {
        if (!string.IsNullOrEmpty(exchange.ResponseBody))
        {
            return exchange.ResponseBody;
        }

        return string.IsNullOrWhiteSpace(exchange.ResponseBodyStorageReference)
            ? "(no response body)"
            : "The response body is preserved as the managed download file:" + Environment.NewLine + exchange.ResponseBodyStorageReference;
    }
}
