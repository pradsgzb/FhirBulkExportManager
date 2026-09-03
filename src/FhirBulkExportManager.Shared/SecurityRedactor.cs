using System.Text.RegularExpressions;

namespace FhirBulkExportManager.Shared;

public static partial class SecurityRedactor
{
    public static string Redact(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var result = BearerRegex().Replace(value, "$1[REDACTED]");
        result = SecretRegex().Replace(result, "$1[REDACTED]");
        return AssertionRegex().Replace(result, "$1[REDACTED]");
    }

    [GeneratedRegex("(?i)(authorization\\s*[:=]\\s*bearer\\s+)[^\\s,;]+", RegexOptions.CultureInvariant)]
    private static partial Regex BearerRegex();

    [GeneratedRegex("(?i)((?:client_?secret|password|refresh_?token)\\s*[:=]\\s*)[^\\s,;&]+", RegexOptions.CultureInvariant)]
    private static partial Regex SecretRegex();

    [GeneratedRegex("(?i)((?:client_?assertion|assertion)\\s*[:=]\\s*)[^\\s,;&]+", RegexOptions.CultureInvariant)]
    private static partial Regex AssertionRegex();
}

