using FhirBulkExportManager.Shared;

namespace FhirBulkExportManager.Domain;

public sealed class Site
{
    private Site()
    {
    }

    public Site(
        Guid id,
        Guid vendorId,
        string name,
        Uri fhirBaseUrl,
        Guid credentialProfileId,
        DateTimeOffset createdAtUtc,
        string? description = null,
        string? tenantIdentifier = null,
        string? practiceCode = null)
    {
        Id = id == Guid.Empty ? Guid.NewGuid() : id;
        VendorId = vendorId;
        Name = Guard.NotNullOrWhiteSpace(name, nameof(name));
        FhirBaseUrl = fhirBaseUrl.AbsoluteUri.TrimEnd('/');
        CredentialProfileId = credentialProfileId;
        Description = description?.Trim();
        TenantIdentifier = tenantIdentifier?.Trim();
        PracticeCode = practiceCode?.Trim();
        Enabled = true;
        CreatedAtUtc = createdAtUtc;
        UpdatedAtUtc = createdAtUtc;
    }

    public Guid Id { get; private set; }
    public Guid VendorId { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public string? Description { get; private set; }
    public string FhirBaseUrl { get; private set; } = string.Empty;
    public string? TenantIdentifier { get; private set; }
    public string? PracticeCode { get; private set; }
    public Guid CredentialProfileId { get; private set; }
    public bool Enabled { get; private set; }
    public DateTimeOffset CreatedAtUtc { get; private set; }
    public DateTimeOffset UpdatedAtUtc { get; private set; }
}

