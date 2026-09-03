using FhirBulkExportManager.Domain;

namespace FhirBulkExportManager.Domain.Tests;

public sealed class CredentialProfileTests
{
    [Fact]
    public void HealthWizz_profile_does_not_require_a_client_secret_reference()
    {
        var profile = new CredentialProfile(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "HealthWizz",
            "OAuth2ClientAssertion",
            "client-for-test",
            null,
            "https://oauth.example.test/oauth/oauth2/token",
            DateTimeOffset.Parse("2026-09-03T00:00:00Z"),
            scope: "system/Patient.read",
            assertionEndpoint: "https://healthwizz.example.test/enterprise/v1.1/oauth/token");

        Assert.Equal(string.Empty, profile.SecretReference);
    }

    [Fact]
    public void Anonymous_profile_does_not_require_oauth_settings()
    {
        var profile = new CredentialProfile(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "Anonymous public access",
            "None",
            null,
            null,
            null,
            DateTimeOffset.Parse("2026-09-03T00:00:00Z"));

        Assert.Equal(string.Empty, profile.ClientId);
        Assert.Equal(string.Empty, profile.SecretReference);
        Assert.Equal(string.Empty, profile.TokenEndpoint);
    }
}
