using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ScaleSet.Client;

internal sealed class RsaPemGitHubAppJwtProvider(string privateKeyPem) : IGitHubAppJwtProvider
{
    public ValueTask<string> CreateJwtAsync(
        string clientId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);

        using var rsa = RSA.Create();
        rsa.ImportFromPem(privateKeyPem);

        var issuedAt = now.ToUniversalTime().AddMinutes(-1);
        var expiresAt = issuedAt.AddMinutes(9);
        var header = SerializeBase64Url(new { alg = "RS256", typ = "JWT" });
        var payload = SerializeBase64Url(new
        {
            iat = issuedAt.ToUnixTimeSeconds(),
            exp = expiresAt.ToUnixTimeSeconds(),
            iss = clientId,
        });
        var signingInput = header + "." + payload;
        var signature = rsa.SignData(
            Encoding.ASCII.GetBytes(signingInput),
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);

        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(signingInput + "." + EncodeBase64Url(signature));
    }

    internal static void ValidatePrivateKey(string privateKeyPem)
    {
        using var rsa = RSA.Create();
        rsa.ImportFromPem(privateKeyPem);
        _ = rsa.SignData([], HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
    }

    private static string SerializeBase64Url<T>(T value)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(value);
        return EncodeBase64Url(json);
    }

    private static string EncodeBase64Url(ReadOnlySpan<byte> bytes) =>
        Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
}
