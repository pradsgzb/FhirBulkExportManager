using System.Security.Cryptography;
using System.Text;

namespace FhirBulkExportManager.Shared;

public static class HashUtility
{
    public static string Sha256Hex(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    public static string Sha256Hex(string value) => Sha256Hex(Encoding.UTF8.GetBytes(value));

    public static async Task<string> Sha256FileAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var digest = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(digest).ToLowerInvariant();
    }
}

