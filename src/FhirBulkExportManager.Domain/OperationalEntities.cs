namespace FhirBulkExportManager.Domain;

public sealed class ExportAttempt
{
    private ExportAttempt() { }

    public ExportAttempt(Guid exportJobId, Guid operationId, string operation, DateTimeOffset startedAtUtc)
    {
        Id = Guid.NewGuid();
        ExportJobId = exportJobId;
        OperationId = operationId;
        Operation = operation;
        StartedAtUtc = startedAtUtc;
        Outcome = AttemptOutcome.Started;
    }

    public Guid Id { get; private set; }
    public Guid ExportJobId { get; private set; }
    public Guid OperationId { get; private set; }
    public string Operation { get; private set; } = string.Empty;
    public DateTimeOffset StartedAtUtc { get; private set; }
    public DateTimeOffset? CompletedAtUtc { get; private set; }
    public AttemptOutcome Outcome { get; private set; }
    public int? HttpStatus { get; private set; }
    public string? ErrorCode { get; private set; }

    public void Complete(AttemptOutcome outcome, DateTimeOffset now, int? httpStatus = null, string? errorCode = null)
    {
        Outcome = outcome;
        CompletedAtUtc = now;
        HttpStatus = httpStatus;
        ErrorCode = errorCode;
    }
}

public sealed class DownloadAttempt
{
    private DownloadAttempt() { }

    public DownloadAttempt(Guid exportFileId, int attemptNumber, DateTimeOffset startedAtUtc, long resumeOffset)
    {
        Id = Guid.NewGuid();
        ExportFileId = exportFileId;
        AttemptNumber = attemptNumber;
        StartedAtUtc = startedAtUtc;
        ResumeOffset = resumeOffset;
        Outcome = AttemptOutcome.Started;
    }

    public Guid Id { get; private set; }
    public Guid ExportFileId { get; private set; }
    public int AttemptNumber { get; private set; }
    public DateTimeOffset StartedAtUtc { get; private set; }
    public DateTimeOffset? CompletedAtUtc { get; private set; }
    public long ResumeOffset { get; private set; }
    public long BytesTransferred { get; private set; }
    public AttemptOutcome Outcome { get; private set; }
    public string? ErrorCode { get; private set; }

    public void Complete(AttemptOutcome outcome, DateTimeOffset now, long bytesTransferred, string? errorCode = null)
    {
        Outcome = outcome;
        CompletedAtUtc = now;
        BytesTransferred = bytesTransferred;
        ErrorCode = errorCode;
    }
}

public sealed class AuditEvent
{
    private AuditEvent() { }

    public AuditEvent(DateTimeOffset timestampUtc, string windowsUser, string action, string entityType, string entityId, string result, string correlationId, string safeMetadataJson = "{}")
    {
        Id = Guid.NewGuid();
        TimestampUtc = timestampUtc;
        WindowsUser = windowsUser;
        Action = action;
        EntityType = entityType;
        EntityId = entityId;
        Result = result;
        CorrelationId = correlationId;
        SafeMetadataJson = safeMetadataJson;
    }

    public Guid Id { get; private set; }
    public DateTimeOffset TimestampUtc { get; private set; }
    public string WindowsUser { get; private set; } = string.Empty;
    public string Action { get; private set; } = string.Empty;
    public string EntityType { get; private set; } = string.Empty;
    public string EntityId { get; private set; } = string.Empty;
    public string Result { get; private set; } = string.Empty;
    public string CorrelationId { get; private set; } = string.Empty;
    public string SafeMetadataJson { get; private set; } = "{}";
}

public sealed class PluginInstallation
{
    private PluginInstallation() { }

    public PluginInstallation(string pluginId, string version, string publisher, string installPath, string assemblySha256, DateTimeOffset installedAtUtc, bool trusted)
    {
        Id = Guid.NewGuid();
        PluginId = pluginId;
        Version = version;
        Publisher = publisher;
        InstallPath = installPath;
        AssemblySha256 = assemblySha256;
        InstalledAtUtc = installedAtUtc;
        Trusted = trusted;
    }

    public Guid Id { get; private set; }
    public string PluginId { get; private set; } = string.Empty;
    public string Version { get; private set; } = string.Empty;
    public string Publisher { get; private set; } = string.Empty;
    public string InstallPath { get; private set; } = string.Empty;
    public string AssemblySha256 { get; private set; } = string.Empty;
    public DateTimeOffset InstalledAtUtc { get; private set; }
    public bool Trusted { get; private set; }
}

public sealed class ApplicationSetting
{
    private ApplicationSetting() { }

    public ApplicationSetting(string key, string value, DateTimeOffset updatedAtUtc)
    {
        Key = key;
        Value = value;
        UpdatedAtUtc = updatedAtUtc;
    }

    public string Key { get; private set; } = string.Empty;
    public string Value { get; private set; } = string.Empty;
    public DateTimeOffset UpdatedAtUtc { get; private set; }

    public void Update(string value, DateTimeOffset updatedAtUtc)
    {
        Value = value ?? throw new ArgumentNullException(nameof(value));
        UpdatedAtUtc = updatedAtUtc;
    }
}

public sealed class PluginConfiguration
{
    private PluginConfiguration() { }

    public PluginConfiguration(Guid id, string pluginId, Guid? siteId, string configurationJson, DateTimeOffset updatedAtUtc)
    {
        Id = id == Guid.Empty ? Guid.NewGuid() : id;
        PluginId = pluginId;
        SiteId = siteId;
        ConfigurationJson = configurationJson;
        UpdatedAtUtc = updatedAtUtc;
    }

    public Guid Id { get; private set; }
    public string PluginId { get; private set; } = string.Empty;
    public Guid? SiteId { get; private set; }
    public string ConfigurationJson { get; private set; } = "{}";
    public DateTimeOffset UpdatedAtUtc { get; private set; }

    public void Update(string configurationJson, DateTimeOffset updatedAtUtc)
    {
        ConfigurationJson = string.IsNullOrWhiteSpace(configurationJson) ? "{}" : configurationJson;
        UpdatedAtUtc = updatedAtUtc;
    }
}

public sealed class SchemaVersion
{
    private SchemaVersion() { }

    public SchemaVersion(int version, string hostVersion, DateTimeOffset appliedAtUtc)
    {
        Version = version;
        HostVersion = hostVersion;
        AppliedAtUtc = appliedAtUtc;
    }

    public int Version { get; private set; }
    public string HostVersion { get; private set; } = string.Empty;
    public DateTimeOffset AppliedAtUtc { get; private set; }
}
