namespace FhirBulkExportManager.Shared;

public static class Guard
{
    public static string NotNullOrWhiteSpace(string? value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Value cannot be null, empty, or whitespace.", parameterName);
        }

        return value.Trim();
    }

    public static Uri AbsoluteHttpsUri(string value, string parameterName, bool allowLoopbackHttp = false)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            throw new ArgumentException("Value must be an absolute URI.", parameterName);
        }

        var allowed = uri.Scheme == Uri.UriSchemeHttps ||
            (allowLoopbackHttp && uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback);
        if (!allowed)
        {
            throw new ArgumentException("HTTPS is required (HTTP is allowed only for loopback test endpoints).", parameterName);
        }

        return uri;
    }

    public static int InRange(int value, int minimum, int maximum, string parameterName)
    {
        if (value < minimum || value > maximum)
        {
            throw new ArgumentOutOfRangeException(parameterName, value, $"Value must be from {minimum} through {maximum}.");
        }

        return value;
    }
}

