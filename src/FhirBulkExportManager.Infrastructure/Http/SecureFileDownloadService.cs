using System.Net;
using System.Net.Http.Headers;
using FhirBulkExportManager.Application.Abstractions;
using FhirBulkExportManager.Domain;
using FhirBulkExportManager.PluginContracts;
using FhirBulkExportManager.Shared;

namespace FhirBulkExportManager.Infrastructure.Http;

public sealed class SecureFileDownloadService(IHttpClientFactory httpClientFactory, IManagedStorage storage) : IFileDownloadService
{
    private const int MaximumRedirects = 5;

    public async Task<DownloadExecutionResult> DownloadAsync(
        IVendorPlugin plugin,
        Uri exactStatusUri,
        AccessToken? token,
        ExportFile file,
        string finalPath,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(finalPath)!);
        var partialPath = finalPath + ".partial";
        var partialLength = File.Exists(partialPath) ? new FileInfo(partialPath).Length : 0;
        var canResume = partialLength > 0 && (!string.IsNullOrWhiteSpace(file.ETag) || file.LastModifiedUtc is not null);
        EntityTagHeaderValue? etag = null;
        if (canResume && !string.IsNullOrWhiteSpace(file.ETag) && !EntityTagHeaderValue.TryParse(file.ETag, out etag))
        {
            canResume = false;
        }

        using var request = plugin.CreateDownloadRequest(
            new Uri(file.RemoteUrl, UriKind.Absolute),
            exactStatusUri,
            token,
            canResume ? partialLength : null,
            etag,
            canResume ? file.LastModifiedUtc : null);
        using var downloadResponse = await SendWithSecureRedirectsAsync(request, cancellationToken).ConfigureAwait(false);
        var response = downloadResponse.Response;

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await ReadRedirectBodyAsync(response.Content, cancellationToken).ConfigureAwait(false);
            var message = $"FHIR file download returned {(int)response.StatusCode} {response.ReasonPhrase}.";
            var failedExchanges = downloadResponse.RedirectExchanges
                .Append(HttpExchangeCapture.Create(
                    "FHIR file download",
                    downloadResponse.StartedAtUtc,
                    DateTimeOffset.UtcNow,
                    downloadResponse.Request,
                    response,
                    responseBody: errorBody,
                    failure: message))
                .ToArray();
            if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
            {
                File.Delete(partialPath);
            }

            throw new RecordedHttpExchangeException(message, response.StatusCode, failedExchanges);
        }

        var resumed = canResume && response.StatusCode == HttpStatusCode.PartialContent;
        if (resumed && response.Content.Headers.ContentRange?.From != partialLength)
        {
            var message = "Server returned a Content-Range that does not match the local partial file.";
            File.Delete(partialPath);
            var failedExchanges = downloadResponse.RedirectExchanges
                .Append(HttpExchangeCapture.Create(
                    "FHIR file download",
                    downloadResponse.StartedAtUtc,
                    DateTimeOffset.UtcNow,
                    downloadResponse.Request,
                    response,
                    failure: message))
                .ToArray();
            throw new RecordedHttpExchangeException(message, response.StatusCode, failedExchanges);
        }

        var mode = resumed ? FileMode.Append : FileMode.Create;
        var expectedTotal = response.Content.Headers.ContentRange?.Length ??
            (response.Content.Headers.ContentLength is { } length ? length + (resumed ? partialLength : 0) : (long?)null);
        var remaining = response.Content.Headers.ContentLength ?? 0;
        await storage.EnsureCapacityAsync(finalPath, remaining, cancellationToken).ConfigureAwait(false);

        await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
        await using (var destination = new FileStream(partialPath, mode, FileAccess.Write, FileShare.Read, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.WriteThrough))
        {
            var buffer = new byte[1024 * 1024];
            int read;
            while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                await storage.EnsureCapacityAsync(finalPath, read, cancellationToken).ConfigureAwait(false);
                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            }

            await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
            destination.Flush(flushToDisk: true);
        }

        var actualBytes = new FileInfo(partialPath).Length;
        if (expectedTotal is { } expected && actualBytes != expected)
        {
            var message = $"Truncated download: received {actualBytes} bytes; expected {expected} bytes.";
            var failedExchanges = downloadResponse.RedirectExchanges
                .Append(HttpExchangeCapture.Create(
                    "FHIR file download",
                    downloadResponse.StartedAtUtc,
                    DateTimeOffset.UtcNow,
                    downloadResponse.Request,
                    response,
                    responseBodyStorageReference: partialPath,
                    failure: message))
                .ToArray();
            throw new RecordedHttpExchangeException(message, response.StatusCode, failedExchanges);
        }

        var sha256 = await HashUtility.Sha256FileAsync(partialPath, cancellationToken).ConfigureAwait(false);
        var exchanges = downloadResponse.RedirectExchanges
            .Append(HttpExchangeCapture.Create(
                "FHIR file download",
                downloadResponse.StartedAtUtc,
                DateTimeOffset.UtcNow,
                downloadResponse.Request,
                response,
                responseBodyStorageReference: finalPath))
            .ToArray();
        return new DownloadExecutionResult(
            actualBytes,
            sha256,
            partialPath,
            response.Headers.ETag?.ToString(),
            response.Content.Headers.LastModified,
            resumed,
            exchanges);
    }

    private async Task<CapturedDownloadResponse> SendWithSecureRedirectsAsync(
        HttpRequestMessage initialRequest,
        CancellationToken cancellationToken)
    {
        var client = httpClientFactory.CreateClient("FhirDownloads");
        HttpRequestMessage current = initialRequest;
        var ownsCurrent = false;
        var exchanges = new List<HttpExchangeContract>();
        try
        {
            for (var redirect = 0; redirect <= MaximumRedirects; redirect++)
            {
                var startedAtUtc = DateTimeOffset.UtcNow;
                try
                {
                    EnsureSecureTransport(current.RequestUri ?? throw new InvalidOperationException("Download request URI is missing."));
                }
                catch (InvalidOperationException exception)
                {
                    exchanges.Add(HttpExchangeCapture.Create(
                        "FHIR file download blocked",
                        startedAtUtc,
                        DateTimeOffset.UtcNow,
                        current,
                        failure: exception.Message));
                    throw new RecordedHttpExchangeException(exception.Message, null, exchanges.ToArray(), exception);
                }

                HttpResponseMessage response;
                try
                {
                    response = await client.SendAsync(current, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException)
                {
                    exchanges.Add(HttpExchangeCapture.Create(
                        "FHIR file download",
                        startedAtUtc,
                        DateTimeOffset.UtcNow,
                        current,
                        failure: exception.Message));
                    throw new RecordedHttpExchangeException(
                        SecurityRedactor.Redact(exception.Message),
                        (exception as HttpRequestException)?.StatusCode,
                        exchanges.ToArray(),
                        exception);
                }

                if (!IsRedirect(response.StatusCode) || response.Headers.Location is null)
                {
                    return new CapturedDownloadResponse(
                        response,
                        current,
                        ownsCurrent,
                        startedAtUtc,
                        exchanges.ToArray());
                }

                using (response)
                {
                    var redirectBody = await ReadRedirectBodyAsync(response.Content, cancellationToken).ConfigureAwait(false);
                    exchanges.Add(HttpExchangeCapture.Create(
                        "FHIR download redirect",
                        startedAtUtc,
                        DateTimeOffset.UtcNow,
                        current,
                        response,
                        responseBody: redirectBody));

                    if (redirect == MaximumRedirects)
                    {
                        throw new RecordedHttpExchangeException("Maximum safe redirect count exceeded.", response.StatusCode, exchanges.ToArray());
                    }

                    var nextUri = response.Headers.Location.IsAbsoluteUri
                        ? response.Headers.Location
                        : new Uri(current.RequestUri!, response.Headers.Location);
                    try
                    {
                        EnsureSecureTransport(nextUri);
                    }
                    catch (InvalidOperationException exception)
                    {
                        throw new RecordedHttpExchangeException(exception.Message, response.StatusCode, exchanges.ToArray(), exception);
                    }

                    var sameOrigin = SameOrigin(current.RequestUri!, nextUri);

                    var next = new HttpRequestMessage(HttpMethod.Get, nextUri);
                    foreach (var header in current.Headers)
                    {
                        if (header.Key.Equals("Authorization", StringComparison.OrdinalIgnoreCase) && !sameOrigin)
                        {
                            continue;
                        }

                        next.Headers.TryAddWithoutValidation(header.Key, header.Value);
                    }

                    if (ownsCurrent)
                    {
                        current.Dispose();
                    }

                    current = next;
                    ownsCurrent = true;
                }
            }

            throw new InvalidOperationException("Redirect processing exited unexpectedly.");
        }
        catch
        {
            if (ownsCurrent)
            {
                current.Dispose();
            }

            throw;
        }
    }

    private static async Task<string> ReadRedirectBodyAsync(HttpContent content, CancellationToken cancellationToken)
    {
        const int maximumBytes = 2 * 1024 * 1024;
        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream(capacity: 64 * 1024);
        var chunk = new byte[64 * 1024];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > maximumBytes)
            {
                return "[BODY OMITTED: redirect response exceeded the safe 2 MiB capture limit]";
            }

            await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }

        return System.Text.Encoding.UTF8.GetString(buffer.GetBuffer(), 0, checked((int)buffer.Length));
    }

    private static bool IsRedirect(HttpStatusCode statusCode) => statusCode is
        HttpStatusCode.MovedPermanently or
        HttpStatusCode.Redirect or
        HttpStatusCode.RedirectMethod or
        HttpStatusCode.TemporaryRedirect or
        HttpStatusCode.PermanentRedirect;

    private static bool SameOrigin(Uri first, Uri second) =>
        string.Equals(first.Scheme, second.Scheme, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(first.Host, second.Host, StringComparison.OrdinalIgnoreCase) &&
        first.Port == second.Port;

    private static void EnsureSecureTransport(Uri uri)
    {
        if (uri.Scheme != Uri.UriSchemeHttps && !(uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback))
        {
            throw new InvalidOperationException("FHIR downloads and redirects require HTTPS; HTTP is permitted only for loopback integration tests.");
        }
    }

    private sealed class CapturedDownloadResponse(
        HttpResponseMessage response,
        HttpRequestMessage request,
        bool ownsRequest,
        DateTimeOffset startedAtUtc,
        IReadOnlyList<HttpExchangeContract> redirectExchanges) : IDisposable
    {
        public HttpResponseMessage Response { get; } = response;
        public HttpRequestMessage Request { get; } = request;
        public DateTimeOffset StartedAtUtc { get; } = startedAtUtc;
        public IReadOnlyList<HttpExchangeContract> RedirectExchanges { get; } = redirectExchanges;

        public void Dispose()
        {
            Response.Dispose();
            if (ownsRequest)
            {
                Request.Dispose();
            }
        }
    }
}
