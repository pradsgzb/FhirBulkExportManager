using FhirBulkExportManager.Shared;

namespace FhirBulkExportManager.Domain;

public sealed class CredentialProfile
{
    private CredentialProfile()
    {
    }

    public CredentialProfile(
        Guid id,
        Guid vendorId,
        string name,
        string authenticationType,
        string? clientId,
        string? secretReference,
        string? tokenEndpoint,
        DateTimeOffset createdAtUtc,
        string? scope = null,
        string? audience = null,
        string? assertionEndpoint = null,
        string additionalConfigurationJson = "{}")
    {
        Id = id == Guid.Empty ? Guid.NewGuid() : id;
        VendorId = vendorId;
        Name = Guard.NotNullOrWhiteSpace(name, nameof(name));
        AuthenticationType = Guard.NotNullOrWhiteSpace(authenticationType, nameof(authenticationType));
        var anonymous = string.Equals(AuthenticationType, "None", StringComparison.OrdinalIgnoreCase);
        ClientId = anonymous ? clientId?.Trim() ?? string.Empty : Guard.NotNullOrWhiteSpace(clientId, nameof(clientId));
        SecretReference = secretReference?.Trim() ?? string.Empty;
        TokenEndpoint = anonymous ? tokenEndpoint?.Trim() ?? string.Empty : Guard.NotNullOrWhiteSpace(tokenEndpoint, nameof(tokenEndpoint));
        Scope = scope?.Trim();
        Audience = audience?.Trim();
        AssertionEndpoint = assertionEndpoint?.Trim();
        AdditionalConfigurationJson = string.IsNullOrWhiteSpace(additionalConfigurationJson) ? "{}" : additionalConfigurationJson;
        CreatedAtUtc = createdAtUtc;
        UpdatedAtUtc = createdAtUtc;
    }

    public Guid Id { get; private set; }
    public Guid VendorId { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public string AuthenticationType { get; private set; } = string.Empty;
    public string ClientId { get; private set; } = string.Empty;
    public string SecretReference { get; private set; } = string.Empty;
    public string? Scope { get; private set; }
    public string? Audience { get; private set; }
    public string TokenEndpoint { get; private set; } = string.Empty;
    public string? AssertionEndpoint { get; private set; }
    public string AdditionalConfigurationJson { get; private set; } = "{}";
    public DateTimeOffset CreatedAtUtc { get; private set; }
    public DateTimeOffset UpdatedAtUtc { get; private set; }

    public void Update(
        string name,
        string authenticationType,
        string? clientId,
        string? secretReference,
        string? tokenEndpoint,
        DateTimeOffset now,
        string? scope = null,
        string? audience = null,
        string? assertionEndpoint = null,
        string additionalConfigurationJson = "{}")
    {
        Name = Guard.NotNullOrWhiteSpace(name, nameof(name));
        AuthenticationType = Guard.NotNullOrWhiteSpace(authenticationType, nameof(authenticationType));
        var anonymous = string.Equals(AuthenticationType, "None", StringComparison.OrdinalIgnoreCase);
        ClientId = anonymous ? clientId?.Trim() ?? string.Empty : Guard.NotNullOrWhiteSpace(clientId, nameof(clientId));
        SecretReference = anonymous ? string.Empty : secretReference?.Trim() ?? string.Empty;
        TokenEndpoint = anonymous ? tokenEndpoint?.Trim() ?? string.Empty : Guard.NotNullOrWhiteSpace(tokenEndpoint, nameof(tokenEndpoint));
        Scope = scope?.Trim();
        Audience = audience?.Trim();
        AssertionEndpoint = assertionEndpoint?.Trim();
        AdditionalConfigurationJson = string.IsNullOrWhiteSpace(additionalConfigurationJson) ? "{}" : additionalConfigurationJson;
        UpdatedAtUtc = now;
    }
}
