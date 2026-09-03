using FhirBulkExportManager.Shared;

namespace FhirBulkExportManager.Domain;

public sealed class Vendor
{
    private Vendor()
    {
    }

    public Vendor(Guid id, string name, string pluginId, string pluginVersion, DateTimeOffset createdAtUtc)
    {
        Id = id == Guid.Empty ? Guid.NewGuid() : id;
        Name = Guard.NotNullOrWhiteSpace(name, nameof(name));
        PluginId = Guard.NotNullOrWhiteSpace(pluginId, nameof(pluginId));
        PluginVersion = Guard.NotNullOrWhiteSpace(pluginVersion, nameof(pluginVersion));
        Enabled = true;
        CreatedAtUtc = createdAtUtc;
        UpdatedAtUtc = createdAtUtc;
    }

    public Guid Id { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public string PluginId { get; private set; } = string.Empty;
    public string PluginVersion { get; private set; } = string.Empty;
    public bool Enabled { get; private set; }
    public DateTimeOffset CreatedAtUtc { get; private set; }
    public DateTimeOffset UpdatedAtUtc { get; private set; }

    public void SetEnabled(bool enabled, DateTimeOffset now)
    {
        Enabled = enabled;
        UpdatedAtUtc = now;
    }
}

