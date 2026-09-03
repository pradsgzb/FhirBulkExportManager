using System.Collections.Concurrent;
using System.Net;
using FhirBulkExportManager.Application.Abstractions;
using FhirBulkExportManager.Domain;
using FhirBulkExportManager.PluginContracts;

namespace FhirBulkExportManager.Worker;

public sealed class AuthenticationTokenProvider(
    IConfigurationStore configurationStore,
    ISecretStore secretStore,
    TimeProvider timeProvider,
    ITransientNotificationService? notificationService = null)
{
    private readonly ConcurrentDictionary<Guid, AccessToken> cache = new();
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> locks = new();

    public async Task<AccessToken> GetAsync(
        ExportJob job,
        IVendorPlugin plugin,
        CancellationToken cancellationToken,
        bool forceRefresh = false)
    {
        if (!forceRefresh &&
            cache.TryGetValue(job.CredentialProfileId, out var existing) &&
            !existing.NeedsRefresh(timeProvider.GetUtcNow(), TimeSpan.FromMinutes(1)))
        {
            return existing with { HttpExchanges = null };
        }

        var gate = locks.GetOrAdd(job.CredentialProfileId, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (forceRefresh)
            {
                cache.TryRemove(job.CredentialProfileId, out _);
            }
            else if (cache.TryGetValue(job.CredentialProfileId, out existing) &&
                !existing.NeedsRefresh(timeProvider.GetUtcNow(), TimeSpan.FromMinutes(1)))
            {
                return existing with { HttpExchanges = null };
            }

            var profile = await configurationStore.GetCredentialProfileAsync(job.CredentialProfileId, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Credential profile is missing.");
            var clientSecret = string.Empty;
            if (!string.IsNullOrWhiteSpace(profile.SecretReference))
            {
                clientSecret = await secretStore.GetAsync(profile.SecretReference, cancellationToken).ConfigureAwait(false)
                    ?? throw new InvalidOperationException("Protected client secret is missing or cannot be decrypted for the current Windows user.");
            }

            var context = new AuthenticationContext(
                profile.ClientId,
                profile.TokenEndpoint,
                profile.AssertionEndpoint,
                profile.Scope,
                profile.Audience,
                profile.AdditionalConfigurationJson,
                new SecretMaterial(clientSecret, new Dictionary<string, string>(StringComparer.Ordinal)));
            AccessToken token;
            try
            {
                token = await plugin.AuthenticateAsync(context, timeProvider, cancellationToken).ConfigureAwait(false);
            }
            catch (RecordedHttpExchangeException exception) when (exception.StatusCode == HttpStatusCode.BadRequest)
            {
                notificationService?.ShowNetworkAccessHint("requesting an OAuth access token");
                throw;
            }
            ValidateToken(profile, token);
            cache[job.CredentialProfileId] = token with { HttpExchanges = null };
            return token;
        }
        finally
        {
            gate.Release();
        }
    }

    public void Invalidate(Guid credentialProfileId) => cache.TryRemove(credentialProfileId, out _);

    private void ValidateToken(CredentialProfile profile, AccessToken token)
    {
        var anonymous = string.Equals(profile.AuthenticationType, "None", StringComparison.OrdinalIgnoreCase);
        if (!anonymous && string.IsNullOrWhiteSpace(token.Value))
        {
            throw InvalidToken(
                "The authenticated vendor returned an empty access token; the protected FHIR request was not sent.",
                token);
        }

        if (string.IsNullOrWhiteSpace(token.TokenType))
        {
            throw InvalidToken("The authentication response did not provide a token type.", token);
        }

        if (!anonymous && token.ExpiresAtUtc <= timeProvider.GetUtcNow())
        {
            throw InvalidToken("The authentication response contained an access token that was already expired.", token);
        }
    }

    private static RecordedHttpExchangeException InvalidToken(string message, AccessToken token) =>
        new(message, null, token.HttpExchanges ?? []);
}
