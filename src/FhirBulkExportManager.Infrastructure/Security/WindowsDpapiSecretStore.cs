using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using FhirBulkExportManager.Application.Abstractions;
using FhirBulkExportManager.Shared;

namespace FhirBulkExportManager.Infrastructure.Security;

[SupportedOSPlatform("windows")]
public sealed class WindowsDpapiSecretStore : ISecretStore
{
    private readonly string root;

    public WindowsDpapiSecretStore()
    {
        root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FhirBulkExportManager",
            "Secrets");
        Directory.CreateDirectory(root);
    }

    public async Task<string> SaveAsync(string purpose, ReadOnlyMemory<char> secret, CancellationToken cancellationToken)
    {
        var safePurpose = Guard.NotNullOrWhiteSpace(purpose, nameof(purpose));
        if (secret.IsEmpty)
        {
            throw new ArgumentException("Secret cannot be empty.", nameof(secret));
        }

        var id = Guid.NewGuid().ToString("N");
        var path = Path.Combine(root, id + ".bin");
        var plaintext = new byte[Encoding.UTF8.GetByteCount(secret.Span)];
        _ = Encoding.UTF8.GetBytes(secret.Span, plaintext);
        try
        {
            var entropy = SHA256.HashData(Encoding.UTF8.GetBytes("FhirBulkExportManager:" + safePurpose));
            var protectedBytes = ProtectedData.Protect(plaintext, entropy, DataProtectionScope.CurrentUser);
            await File.WriteAllBytesAsync(path, protectedBytes, cancellationToken).ConfigureAwait(false);
            return "dpapi-user:" + id + ":" + Convert.ToHexString(entropy).ToLowerInvariant();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    public async Task<string?> GetAsync(string secretReference, CancellationToken cancellationToken)
    {
        var components = secretReference.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (components.Length != 3 || components[0] != "dpapi-user" || components[1].Length != 32)
        {
            throw new InvalidOperationException("Secret reference is invalid.");
        }

        var path = Path.Combine(root, components[1] + ".bin");
        if (!File.Exists(path))
        {
            return null;
        }

        var protectedBytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        var entropy = Convert.FromHexString(components[2]);
        var plaintext = ProtectedData.Unprotect(protectedBytes, entropy, DataProtectionScope.CurrentUser);
        try
        {
            return Encoding.UTF8.GetString(plaintext);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    public Task DeleteAsync(string secretReference, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var components = secretReference.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (components.Length != 3 || components[0] != "dpapi-user" || components[1].Length != 32)
        {
            throw new InvalidOperationException("Secret reference is invalid.");
        }

        var path = Path.Combine(root, components[1] + ".bin");
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        return Task.CompletedTask;
    }
}
