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
        using var response = await SendWithSecureRedirectsAsync(request, cancellationToken).ConfigureAwait(false);

        var resumed = canResume && response.StatusCode == HttpStatusCode.PartialContent;
        if (resumed && response.Content.Headers.ContentRange?.From != partialLength)
        {
            File.Delete(partialPath);
            throw new IOException("Server returned a Content-Range that does not match the local partial file.");
        }

        if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            File.Delete(partialPath);
            throw new IOException("Server rejected the requested safe download range; the file will be restarted on retry.");
        }

        response.EnsureSuccessStatusCode();
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
            throw new IOException($"Truncated download: received {actualBytes} bytes; expected {expected} bytes.");
        }

        var sha256 = await HashUtility.Sha256FileAsync(partialPath, cancellationToken).ConfigureAwait(false);
        return new DownloadExecutionResult(
            actualBytes,
            sha256,
            partialPath,
            response.Headers.ETag?.ToString(),
            response.Content.Headers.LastModified,
            resumed);
    }

    private async Task<HttpResponseMessage> SendWithSecureRedirectsAsync(HttpRequestMessage initialRequest, CancellationToken cancellationToken)
    {
        var client = httpClientFactory.CreateClient("FhirDownloads");
        HttpRequestMessage current = initialRequest;
        var ownsCurrent = false;
        try
        {
            for (var redirect = 0; redirect <= MaximumRedirects; redirect++)
            {
                EnsureSecureTransport(current.RequestUri ?? throw new InvalidOperationException("Download request URI is missing."));
                var response = await client.SendAsync(current, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
                if (!IsRedirect(response.StatusCode) || response.Headers.Location is null)
                {
                    if (ownsCurrent)
                    {
                        current.Dispose();
                    }

                    return response;
                }

                if (redirect == MaximumRedirects)
                {
                    response.Dispose();
                    throw new HttpRequestException("Maximum safe redirect count exceeded.");
                }

                var nextUri = response.Headers.Location.IsAbsoluteUri
                    ? response.Headers.Location
                    : new Uri(current.RequestUri!, response.Headers.Location);
                EnsureSecureTransport(nextUri);
                var sameOrigin = SameOrigin(current.RequestUri!, nextUri);
                response.Dispose();

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
}
