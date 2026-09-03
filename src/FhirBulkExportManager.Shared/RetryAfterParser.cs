using System.Globalization;
using System.Net.Http.Headers;

namespace FhirBulkExportManager.Shared;

public static class RetryAfterParser
{
    public static DateTimeOffset? Parse(RetryConditionHeaderValue? value, DateTimeOffset now)
    {
        if (value?.Delta is { } delta)
        {
            return now.Add(delta < TimeSpan.Zero ? TimeSpan.Zero : delta);
        }

        return value?.Date is { } date ? (date < now ? now : date) : null;
    }

    public static DateTimeOffset? Parse(string? value, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) && seconds >= 0)
        {
            return now.AddSeconds(seconds);
        }

        if (DateTimeOffset.TryParseExact(
                value,
                "r",
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var date))
        {
            return date < now ? now : date;
        }

        return null;
    }

    public static DateTimeOffset ExponentialWithJitter(
        DateTimeOffset now,
        int attempt,
        TimeSpan initial,
        TimeSpan maximum,
        int jitterSeed)
    {
        var safeAttempt = Math.Clamp(attempt, 0, 30);
        var exponential = initial.TotalMilliseconds * Math.Pow(2, safeAttempt);
        var capped = Math.Min(exponential, maximum.TotalMilliseconds);
        var random = new Random(jitterSeed);
        var jitterFactor = 0.8 + (random.NextDouble() * 0.4);
        return now.AddMilliseconds(capped * jitterFactor);
    }
}

