using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using FhirBulkExportManager.Shared;

namespace FhirBulkExportManager.PluginContracts;

public static class HttpExchangeCapture
{
    private const string Redacted = "[REDACTED]";
    private static readonly HashSet<string> SensitiveNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "authorization",
        "proxy-authorization",
        "cookie",
        "set-cookie",
        "x-api-key",
        "api-key",
        "apikey",
        "client-secret",
        "client_secret",
        "client-assertion",
        "client_assertion",
        "access-token",
        "access_token",
        "refresh-token",
        "refresh_token",
        "id-token",
        "id_token",
        "password",
        "secret",
        "token",
    };

    public static HttpExchangeContract Create(
        string operation,
        DateTimeOffset startedAtUtc,
        DateTimeOffset completedAtUtc,
        HttpRequestMessage request,
        HttpResponseMessage? response = null,
        string? requestBody = null,
        string? responseBody = null,
        string? responseBodyStorageReference = null,
        string? failure = null)
    {
        return new HttpExchangeContract(
            operation,
            startedAtUtc,
            completedAtUtc,
            request.Method.Method,
            SanitizeUri(request.RequestUri),
            CaptureHeaders(request.Headers, request.Content?.Headers),
            SanitizeBody(requestBody, request.Content?.Headers.ContentType?.MediaType),
            response is null ? null : (int)response.StatusCode,
            response is null ? null : HttpStatusText.CanonicalReasonPhrase(response.StatusCode),
            response is null ? [] : CaptureHeaders(response.Headers, response.Content.Headers, response.TrailingHeaders),
            SanitizeBody(responseBody, response?.Content.Headers.ContentType?.MediaType),
            responseBodyStorageReference,
            string.IsNullOrWhiteSpace(failure) ? null : SecurityRedactor.Redact(failure));
    }

    public static string? SanitizeBody(string? body, string? mediaType = null)
    {
        if (string.IsNullOrEmpty(body))
        {
            return body;
        }

        var trimmed = body.Trim();
        if (LooksLikeJwt(trimmed))
        {
            return "[REDACTED JWT]";
        }

        if (IsJson(mediaType, trimmed))
        {
            try
            {
                var node = JsonNode.Parse(body);
                if (node is not null)
                {
                    if (node is JsonValue valueNode &&
                        valueNode.TryGetValue<string>(out var stringValue) &&
                        !string.IsNullOrEmpty(stringValue) &&
                        LooksLikeJwt(stringValue))
                    {
                        return JsonSerializer.Serialize("[REDACTED JWT]", JsonDefaults.Options);
                    }

                    RedactJson(node);
                    return node.ToJsonString(JsonDefaults.Options);
                }
            }
            catch (JsonException)
            {
                // Preserve malformed/non-JSON diagnostic text after pattern redaction.
            }
        }

        if (IsFormUrlEncoded(mediaType, trimmed))
        {
            return string.Join("&", trimmed.Split('&', StringSplitOptions.RemoveEmptyEntries).Select(SanitizeFormField));
        }

        return SecurityRedactor.Redact(body);
    }

    private static IReadOnlyList<HttpHeaderContract> CaptureHeaders(
        HttpHeaders headers,
        HttpHeaders? contentHeaders,
        HttpHeaders? trailingHeaders = null)
    {
        var captured = new List<HttpHeaderContract>();
        AddHeaders(captured, headers);
        if (contentHeaders is not null)
        {
            AddHeaders(captured, contentHeaders);
        }

        if (trailingHeaders is not null)
        {
            AddHeaders(captured, trailingHeaders);
        }

        return captured;
    }

    private static void AddHeaders(List<HttpHeaderContract> target, HttpHeaders source)
    {
        foreach (var header in source)
        {
            var values = IsSensitiveName(header.Key)
                ? new[] { Redacted }
                : header.Value.Select(value => SanitizeHeaderValue(header.Key, value)).ToArray();
            target.Add(new HttpHeaderContract(header.Key, values));
        }
    }

    private static string SanitizeHeaderValue(string name, string value)
    {
        if (name.Equals("Location", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("Content-Location", StringComparison.OrdinalIgnoreCase))
        {
            return Uri.TryCreate(value, UriKind.Absolute, out var uri) ? SanitizeUri(uri) : SecurityRedactor.Redact(value);
        }

        return SecurityRedactor.Redact(value);
    }

    private static string SanitizeUri(Uri? uri)
    {
        if (uri is null)
        {
            return string.Empty;
        }

        if (!uri.IsAbsoluteUri)
        {
            return SecurityRedactor.Redact(uri.ToString());
        }

        if (string.IsNullOrEmpty(uri.Query))
        {
            return uri.AbsoluteUri;
        }

        var builder = new UriBuilder(uri);
        var fields = uri.Query.TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(SanitizeQueryField);
        builder.Query = string.Join("&", fields);
        return builder.Uri.AbsoluteUri;
    }

    private static string SanitizeQueryField(string field)
    {
        var separator = field.IndexOf('=');
        if (separator < 0)
        {
            return field;
        }

        var encodedName = field[..separator];
        var name = Uri.UnescapeDataString(encodedName.Replace('+', ' '));
        return IsSensitiveName(name) ? encodedName + "=" + Uri.EscapeDataString(Redacted) : field;
    }

    private static string SanitizeFormField(string field)
    {
        var separator = field.IndexOf('=');
        if (separator < 0)
        {
            return SecurityRedactor.Redact(field);
        }

        var encodedName = field[..separator];
        var name = Uri.UnescapeDataString(encodedName.Replace('+', ' '));
        return IsSensitiveName(name)
            ? encodedName + "=" + Uri.EscapeDataString(Redacted)
            : encodedName + "=" + Uri.EscapeDataString(SecurityRedactor.Redact(Uri.UnescapeDataString(field[(separator + 1)..].Replace('+', ' '))));
    }

    private static void RedactJson(JsonNode node)
    {
        if (node is JsonObject jsonObject)
        {
            foreach (var property in jsonObject.ToList())
            {
                if (IsSensitiveName(property.Key))
                {
                    jsonObject[property.Key] = Redacted;
                }
                else if (property.Value is JsonValue valueNode &&
                    valueNode.TryGetValue<string>(out var stringValue) &&
                    !string.IsNullOrEmpty(stringValue) &&
                    LooksLikeJwt(stringValue))
                {
                    jsonObject[property.Key] = "[REDACTED JWT]";
                }
                else if (property.Value is not null)
                {
                    RedactJson(property.Value);
                }
            }
        }
        else if (node is JsonArray jsonArray)
        {
            for (var index = 0; index < jsonArray.Count; index++)
            {
                var item = jsonArray[index];
                if (item is JsonValue valueNode &&
                    valueNode.TryGetValue<string>(out var stringValue) &&
                    !string.IsNullOrEmpty(stringValue) &&
                    LooksLikeJwt(stringValue))
                {
                    jsonArray[index] = "[REDACTED JWT]";
                }
                else if (item is not null)
                {
                    RedactJson(item);
                }
            }
        }
    }

    private static bool IsSensitiveName(string name)
    {
        var normalized = name.Replace("-", string.Empty, StringComparison.Ordinal)
            .Replace("_", string.Empty, StringComparison.Ordinal)
            .Replace(".", string.Empty, StringComparison.Ordinal);
        return SensitiveNames.Contains(name) ||
            normalized.Contains("password", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("secret", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("assertion", StringComparison.OrdinalIgnoreCase) ||
            normalized.EndsWith("token", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals("authorization", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("cookie", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals("apikey", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsJson(string? mediaType, string value) =>
        mediaType?.Contains("json", StringComparison.OrdinalIgnoreCase) == true ||
        value.StartsWith('{') || value.StartsWith('[') || value.StartsWith('"');

    private static bool IsFormUrlEncoded(string? mediaType, string value) =>
        mediaType?.Equals("application/x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase) == true ||
        string.IsNullOrWhiteSpace(mediaType) && value.Contains('=') && value.Contains('&');

    private static bool LooksLikeJwt(string value)
    {
        var parts = value.Split('.');
        return parts.Length == 3 && parts.All(part => part.Length >= 8 && part.All(character => char.IsLetterOrDigit(character) || character is '-' or '_' or '='));
    }
}
