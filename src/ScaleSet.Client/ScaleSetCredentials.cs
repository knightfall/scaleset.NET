namespace ScaleSet.Client;

/// <summary>Asynchronous signer seam for GitHub App JWTs.</summary>
/// <remarks>
/// Implementations must return an RS256 JWT with <c>iss</c> set to
/// <c>clientId</c>, <c>iat</c> set to the supplied time minus one minute,
/// and <c>exp</c> set to <c>iat</c> plus nine minutes.
/// </remarks>
public interface IGitHubAppJwtProvider
{
    /// <summary>Creates a short-lived GitHub App JWT.</summary>
    /// <param name="clientId">The GitHub App client ID to place in the issuer claim.</param>
    /// <param name="now">The UTC time supplied by the client clock.</param>
    /// <param name="cancellationToken">Token used to cancel signing.</param>
    /// <returns>A compact RS256-signed JWT.</returns>
    ValueTask<string> CreateJwtAsync(
        string clientId,
        DateTimeOffset now,
        CancellationToken cancellationToken);
}

/// <summary>Exactly one authentication mode used by a <see cref="ScaleSetClient"/>.</summary>
public abstract class ScaleSetCredentials
{
    private protected ScaleSetCredentials()
    {
    }

    /// <summary>Creates credentials using a personal access token.</summary>
    /// <param name="token">GitHub personal access token.</param>
    /// <returns>PAT credentials.</returns>
    /// <exception cref="ArgumentException">The token is empty or contains characters unsafe in an HTTP authentication header.</exception>
    public static ScaleSetCredentials FromPersonalAccessToken(string token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        if (!IsSafeAuthenticationToken(token))
        {
            throw new ArgumentException("A personal access token contains characters that are unsafe in an HTTP authentication header.", nameof(token));
        }

        return new PersonalAccessTokenCredentials(token);
    }

    /// <summary>Creates GitHub App credentials from an unencrypted PEM RSA private key.</summary>
    /// <param name="clientId">GitHub App client ID.</param>
    /// <param name="installationId">GitHub App installation identifier.</param>
    /// <param name="privateKeyPem">PKCS#1 or PKCS#8 PEM-encoded RSA private key.</param>
    /// <returns>GitHub App credentials.</returns>
    public static ScaleSetCredentials FromGitHubApp(
        string clientId,
        long installationId,
        string privateKeyPem)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(installationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(privateKeyPem);
        RsaPemGitHubAppJwtProvider.ValidatePrivateKey(privateKeyPem);
        return new GitHubAppCredentials(clientId, installationId, new RsaPemGitHubAppJwtProvider(privateKeyPem));
    }

    /// <summary>Creates GitHub App credentials using a replaceable asynchronous JWT provider.</summary>
    /// <param name="clientId">GitHub App client ID.</param>
    /// <param name="installationId">GitHub App installation identifier.</param>
    /// <param name="jwtProvider">Provider that signs JWTs locally or through a remote signer.</param>
    /// <returns>GitHub App credentials.</returns>
    public static ScaleSetCredentials FromGitHubApp(
        string clientId,
        long installationId,
        IGitHubAppJwtProvider jwtProvider)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(installationId);
        ArgumentNullException.ThrowIfNull(jwtProvider);
        return new GitHubAppCredentials(clientId, installationId, jwtProvider);
    }

    /// <summary>Returns a diagnostic label without revealing credential values.</summary>
    public override string ToString() => GetType().Name + " { Secret = [REDACTED] }";

    internal static bool IsSafeAuthenticationToken(string token)
    {
        if (token.Length == 0)
        {
            return false;
        }

        var paddingStart = token.IndexOf('=');
        var valueLength = paddingStart < 0 ? token.Length : paddingStart;
        if (valueLength == 0 || token.Length - valueLength > 2)
        {
            return false;
        }

        for (var index = 0; index < valueLength; index++)
        {
            var character = token[index];
            if (!char.IsAsciiLetterOrDigit(character) && character is not ('-' or '.' or '_' or '~' or '+' or '/'))
            {
                return false;
            }
        }

        return paddingStart < 0 || token.AsSpan(paddingStart).IndexOfAnyExcept('=') < 0;
    }
}

internal sealed class PersonalAccessTokenCredentials(string token) : ScaleSetCredentials
{
    internal string Token { get; } = token;
}

internal sealed class GitHubAppCredentials(
    string clientId,
    long installationId,
    IGitHubAppJwtProvider jwtProvider) : ScaleSetCredentials
{
    internal string ClientId { get; } = clientId;

    internal long InstallationId { get; } = installationId;

    internal IGitHubAppJwtProvider JwtProvider { get; } = jwtProvider;

    public override string ToString() =>
        $"GitHubAppCredentials {{ ClientId = {ClientId}, InstallationId = {InstallationId}, PrivateKey = [REDACTED] }}";
}
