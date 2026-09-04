namespace FhirBulkExportManager.Application.Configuration;

public enum DateDisplayMode
{
    Utc,
    Local,
}

public sealed record ApplicationPreferences
{
    public DateDisplayMode DateMode { get; init; } = DateDisplayMode.Utc;
    public bool AutoRefreshEnabled { get; init; }
    public int AutoRefreshIntervalSeconds { get; init; } = 30;
    public bool AutomaticallyResumeDownloads { get; init; } = true;
    public bool MinimizeToTray { get; init; } = true;
    public bool ShowNetworkAccessHintOnBadRequest { get; init; } = true;

    public ApplicationPreferences Normalize()
    {
        var mode = Enum.IsDefined(DateMode) ? DateMode : DateDisplayMode.Utc;
        return this with
        {
            DateMode = mode,
            AutoRefreshIntervalSeconds = Math.Clamp(AutoRefreshIntervalSeconds, 5, 300),
        };
    }
}
