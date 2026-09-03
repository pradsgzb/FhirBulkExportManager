using System.Security.Cryptography;
using System.Text.Json;
using FhirBulkExportManager.Application.Abstractions;
using FhirBulkExportManager.Domain;
using FhirBulkExportManager.Shared;

namespace FhirBulkExportManager.Application.Services;

public sealed class PasswordProtectionService(
    IApplicationSettingsStore settingsStore,
    IWindowsIdentityProvider identityProvider,
    TimeProvider timeProvider)
{
    private const string PasswordVerifierKey = "security.password-verifier.v1";
    private const int SaltSize = 32;
    private const int HashSize = 32;
    private const int Iterations = 600_000;
    private readonly SemaphoreSlim gate = new(1, 1);

    public async Task<bool> IsConfiguredAsync(CancellationToken cancellationToken) =>
        !string.IsNullOrWhiteSpace(await settingsStore.GetValueAsync(PasswordVerifierKey, cancellationToken).ConfigureAwait(false));

    public async Task<bool> VerifyAsync(string? password, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(password))
        {
            return false;
        }

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var verifier = await ReadVerifierAsync(cancellationToken).ConfigureAwait(false);
            if (verifier is null)
            {
                return false;
            }

            var salt = Convert.FromBase64String(verifier.SaltBase64);
            var expected = Convert.FromBase64String(verifier.HashBase64);
            byte[]? actual = null;
            try
            {
                actual = Rfc2898DeriveBytes.Pbkdf2(
                    password,
                    salt,
                    verifier.Iterations,
                    HashAlgorithmName.SHA256,
                    expected.Length);
                return CryptographicOperations.FixedTimeEquals(actual, expected);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(salt);
                CryptographicOperations.ZeroMemory(expected);
                if (actual is not null)
                {
                    CryptographicOperations.ZeroMemory(actual);
                }
            }
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task SetOrChangeAsync(string? currentPassword, string newPassword, CancellationToken cancellationToken)
    {
        ValidateNewPassword(newPassword);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var existing = await ReadVerifierAsync(cancellationToken).ConfigureAwait(false);
            if (existing is not null && !Verify(existing, currentPassword))
            {
                throw new InvalidOperationException("The current application password is incorrect.");
            }

            var salt = RandomNumberGenerator.GetBytes(SaltSize);
            byte[]? hash = null;
            try
            {
                hash = Rfc2898DeriveBytes.Pbkdf2(newPassword, salt, Iterations, HashAlgorithmName.SHA256, HashSize);
                var verifier = new PasswordVerifier(
                    1,
                    "PBKDF2-HMAC-SHA256",
                    Iterations,
                    Convert.ToBase64String(salt),
                    Convert.ToBase64String(hash));
                await settingsStore.SetValueAsync(
                    PasswordVerifierKey,
                    JsonSerializer.Serialize(verifier, JsonDefaults.Options),
                    Audit(existing is null ? "ApplicationPasswordConfigured" : "ApplicationPasswordChanged"),
                    cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(salt);
                if (hash is not null)
                {
                    CryptographicOperations.ZeroMemory(hash);
                }
            }
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task RemoveAsync(string currentPassword, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var existing = await ReadVerifierAsync(cancellationToken).ConfigureAwait(false);
            if (existing is null)
            {
                return;
            }

            if (!Verify(existing, currentPassword))
            {
                throw new InvalidOperationException("The current application password is incorrect.");
            }

            await settingsStore.DeleteValueAsync(
                PasswordVerifierKey,
                Audit("ApplicationPasswordRemoved"),
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task ResetAsync(CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await settingsStore.DeleteValueAsync(
                PasswordVerifierKey,
                Audit("ApplicationPasswordResetFromCommandLine"),
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<PasswordVerifier?> ReadVerifierAsync(CancellationToken cancellationToken)
    {
        var json = await settingsStore.GetValueAsync(PasswordVerifierKey, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            var verifier = JsonSerializer.Deserialize<PasswordVerifier>(json, JsonDefaults.Options)
                ?? throw InvalidVerifier();
            if (verifier.Version != 1 ||
                !string.Equals(verifier.Algorithm, "PBKDF2-HMAC-SHA256", StringComparison.Ordinal) ||
                verifier.Iterations is < 100_000 or > 2_000_000 ||
                string.IsNullOrWhiteSpace(verifier.SaltBase64) ||
                string.IsNullOrWhiteSpace(verifier.HashBase64))
            {
                throw InvalidVerifier();
            }

            byte[]? salt = null;
            byte[]? hash = null;
            try
            {
                salt = Convert.FromBase64String(verifier.SaltBase64);
                hash = Convert.FromBase64String(verifier.HashBase64);
                if (salt.Length != SaltSize || hash.Length != HashSize)
                {
                    throw InvalidVerifier();
                }
            }
            finally
            {
                if (salt is not null)
                {
                    CryptographicOperations.ZeroMemory(salt);
                }

                if (hash is not null)
                {
                    CryptographicOperations.ZeroMemory(hash);
                }
            }

            return verifier;
        }
        catch (Exception exception) when (exception is JsonException or FormatException)
        {
            throw InvalidVerifier(exception);
        }
    }

    private static bool Verify(PasswordVerifier verifier, string? password)
    {
        if (string.IsNullOrEmpty(password))
        {
            return false;
        }

        var salt = Convert.FromBase64String(verifier.SaltBase64);
        var expected = Convert.FromBase64String(verifier.HashBase64);
        byte[]? actual = null;
        try
        {
            actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, verifier.Iterations, HashAlgorithmName.SHA256, expected.Length);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(salt);
            CryptographicOperations.ZeroMemory(expected);
            if (actual is not null)
            {
                CryptographicOperations.ZeroMemory(actual);
            }
        }
    }

    private static void ValidateNewPassword(string password)
    {
        ArgumentNullException.ThrowIfNull(password);
        if (password.Length is < 10 or > 256)
        {
            throw new InvalidOperationException("The application password must be between 10 and 256 characters.");
        }
    }

    private AuditEvent Audit(string action)
    {
        var correlationId = Guid.NewGuid().ToString("N");
        return new AuditEvent(
            timeProvider.GetUtcNow(),
            identityProvider.GetCurrentIdentity(),
            action,
            nameof(ApplicationSetting),
            PasswordVerifierKey,
            "Succeeded",
            correlationId);
    }

    private static InvalidOperationException InvalidVerifier(Exception? innerException = null) =>
        new("The stored application password verifier is invalid. Start the app once with --reset-password to recover.", innerException);

    private sealed record PasswordVerifier(
        int Version,
        string Algorithm,
        int Iterations,
        string SaltBase64,
        string HashBase64);
}
