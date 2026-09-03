using System.Globalization;
using System.Net;

namespace FhirBulkExportManager.PluginContracts;

public static class HttpStatusText
{
    public static string Format(HttpStatusCode statusCode) =>
        Format((int)statusCode);

    public static string Format(int statusCode)
    {
        var numericStatus = statusCode.ToString(CultureInfo.InvariantCulture);
        if (!Enum.IsDefined((HttpStatusCode)statusCode))
        {
            return numericStatus;
        }

        using var response = new HttpResponseMessage((HttpStatusCode)statusCode);
        return string.IsNullOrWhiteSpace(response.ReasonPhrase)
            ? numericStatus
            : numericStatus + " " + response.ReasonPhrase;
    }

    public static string? CanonicalReasonPhrase(HttpStatusCode statusCode)
    {
        if (!Enum.IsDefined(statusCode))
        {
            return null;
        }

        using var response = new HttpResponseMessage(statusCode);
        return response.ReasonPhrase;
    }
}
