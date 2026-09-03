using System.Collections.Concurrent;
using FhirBulkExportManager.Application.Abstractions;
using FhirBulkExportManager.Domain;
using FhirBulkExportManager.PluginContracts;

namespace FhirBulkExportManager.Worker;

public sealed class AuthenticationTokenProvider(
    IConfigurationStore configurationStore,
    ISecretStore secretStore,
    TimeProvider timeProvider)
{
    private readonly ConcurrentDictionary<Guid, AccessToken> cache = new();
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> locks = new();

    public async Task<AccessToken> GetAsync(ExportJob job, IVendorPlugin plugin, CancellationToken cancellationToken)
    {
        if (cache.TryGetValue(job.CredentialProfileId, out var existing) && !existing.NeedsRefresh(timeProvider.GetUtcNow(), TimeSpan.FromMinutes(1)))
        {
            return existing;
        }

        var gate = locks.GetOrAdd(job.CredentialProfileId, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (cache.TryGetValue(job.CredentialProfileId, out existing) && !existing.NeedsRefresh(timeProvider.GetUtcNow(), TimeSpan.FromMinutes(1)))
            {
                return existing;
            }

            var profile = await configurationStore.GetCredentialProfileAsync(job.CredentialProfileId, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Credential profile is missing.");
            var clientSecret = await secretStore.GetAsync(profile.SecretReference, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Protected client secret is missing or cannot be decrypted for the current Windows user.");
            var context = new AuthenticationContext(
                profile.ClientId,
                profile.TokenEndpoint,
                profile.AssertionEndpoint,
                profile.Scope,
                profile.Audience,
                profile.AdditionalConfigurationJson,
                new SecretMaterial(clientSecret, new Dictionary<string, string>(StringComparer.Ordinal)));
            var token = await plugin.AuthenticateAsync(context, timeProvider, cancellationToken).ConfigureAwait(false);
            cache[job.CredentialProfileId] = token;
            return token;
        }
        finally
        {
            gate.Release();
        }
    }

    public void Invalidate(Guid credentialProfileId) => cache.TryRemove(credentialProfileId, out _);
}

