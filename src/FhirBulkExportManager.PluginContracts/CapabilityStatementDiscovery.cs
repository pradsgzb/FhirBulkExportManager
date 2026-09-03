using System.Text.Json;

namespace FhirBulkExportManager.PluginContracts;

public static class CapabilityStatementDiscovery
{
    public static string? FindOAuthTokenEndpoint(string? capabilityStatementJson)
    {
        if (string.IsNullOrWhiteSpace(capabilityStatementJson))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(capabilityStatementJson);
            if (!document.RootElement.TryGetProperty("rest", out var rest) || rest.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            foreach (var restEntry in rest.EnumerateArray())
            {
                if (restEntry.ValueKind != JsonValueKind.Object ||
                    !restEntry.TryGetProperty("security", out var security) ||
                    security.ValueKind != JsonValueKind.Object ||
                    !security.TryGetProperty("extension", out var extensions) ||
                    extensions.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                var endpoint = FindTokenEndpoint(extensions, insideOAuthUrisExtension: false);
                if (endpoint is not null)
                {
                    return endpoint;
                }
            }
        }
        catch (JsonException)
        {
            return null;
        }

        return null;
    }

    private static string? FindTokenEndpoint(JsonElement extensions, bool insideOAuthUrisExtension)
    {
        foreach (var extension in extensions.EnumerateArray())
        {
            if (extension.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var extensionUrl = extension.TryGetProperty("url", out var urlElement) && urlElement.ValueKind == JsonValueKind.String
                ? urlElement.GetString()
                : null;
            var isOAuthUrisExtension = insideOAuthUrisExtension || IsOAuthUrisExtension(extensionUrl);
            if (IsTokenExtension(extensionUrl) && TryReadEndpoint(extension, out var endpoint))
            {
                return endpoint;
            }

            if (extension.TryGetProperty("extension", out var nested) && nested.ValueKind == JsonValueKind.Array)
            {
                var nestedEndpoint = FindTokenEndpoint(nested, isOAuthUrisExtension);
                if (nestedEndpoint is not null)
                {
                    return nestedEndpoint;
                }
            }
        }

        return null;
    }

    private static bool TryReadEndpoint(JsonElement extension, out string? endpoint)
    {
        endpoint = null;
        foreach (var propertyName in new[] { "valueUri", "valueUrl" })
        {
            if (!extension.TryGetProperty(propertyName, out var value) || value.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var candidate = value.GetString()?.Trim();
            if (Uri.TryCreate(candidate, UriKind.Absolute, out var uri) &&
                uri.Scheme is "https" or "http" &&
                string.IsNullOrEmpty(uri.UserInfo) &&
                string.IsNullOrEmpty(uri.Fragment))
            {
                endpoint = candidate;
                return true;
            }
        }

        return false;
    }

    private static bool IsOAuthUrisExtension(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.EndsWith("/oauth-uris", StringComparison.OrdinalIgnoreCase);

    private static bool IsTokenExtension(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        (value.Equals("token", StringComparison.OrdinalIgnoreCase) ||
         value.EndsWith("/token", StringComparison.OrdinalIgnoreCase));
}
