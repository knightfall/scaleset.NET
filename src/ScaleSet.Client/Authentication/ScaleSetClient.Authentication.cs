using System.Text.Json;

namespace ScaleSet.Client;

public sealed partial class ScaleSetClient
{
    private async Task<AdminServiceSnapshot> GetAdminSnapshotAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();

        var snapshot = Volatile.Read(ref _adminSnapshot);
        if (IsCurrentAdminSnapshot(snapshot))
        {
            return snapshot!;
        }

        await _adminRefreshGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            snapshot = Volatile.Read(ref _adminSnapshot);
            if (IsCurrentAdminSnapshot(snapshot))
            {
                return snapshot!;
            }

            var refreshed = await RefreshAdminSnapshotAsync(cancellationToken).ConfigureAwait(false);
            Volatile.Write(ref _adminSnapshot, refreshed);
            return refreshed;
        }
        finally
        {
            _adminRefreshGate.Release();
        }
    }

    private async Task<AdminServiceSnapshot> RefreshAdminSnapshotAsync(CancellationToken cancellationToken)
    {
        var registrationToken = await GetRunnerRegistrationTokenAsync(cancellationToken).ConfigureAwait(false);
        var discoveryBody = JsonSerializer.SerializeToUtf8Bytes(new
        {
            url = Target.SourceUri.AbsoluteUri,
            runner_event = "register",
        });

        var discovery = await SendGitHubAsync(
            HttpMethod.Post,
            "/actions/runner-registration",
            discoveryBody,
            $"RemoteAuth {registrationToken}",
            "application/json",
            expectedStatus: 0,
            ParseAdminDiscovery,
            retryDiscoveryAuthentication: true,
            cancellationToken).ConfigureAwait(false);
        return new AdminServiceSnapshot(
            discovery.ServiceUri,
            discovery.AdminToken,
            discovery.ExpiresAt,
            Interlocked.Increment(ref _adminGeneration));
    }

    private async Task<string> GetRunnerRegistrationTokenAsync(CancellationToken cancellationToken)
    {
        var authorizationToken = _credentials switch
        {
            PersonalAccessTokenCredentials pat => pat.Token,
            GitHubAppCredentials app => await GetInstallationAccessTokenAsync(app, cancellationToken).ConfigureAwait(false),
            _ => throw new InvalidOperationException("Unsupported ScaleSet credential type."),
        };

        return await SendGitHubAsync(
            HttpMethod.Post,
            Target.RegistrationTokenPath,
            [],
            $"Bearer {authorizationToken}",
            "application/vnd.github.v3+json",
            expectedStatus: 201,
            ParseRegistrationToken,
            retryDiscoveryAuthentication: false,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<string> GetInstallationAccessTokenAsync(
        GitHubAppCredentials credentials,
        CancellationToken cancellationToken)
    {
        var jwt = await credentials.JwtProvider.CreateJwtAsync(
            credentials.ClientId,
            _timeProvider.GetUtcNow(),
            cancellationToken).ConfigureAwait(false);
        if (jwt is null || !ScaleSetCredentials.IsSafeAuthenticationToken(jwt))
        {
            throw new ScaleSetApiException("GitHub App JWT provider returned a token that is unsafe for an HTTP authentication header.");
        }

        var path = $"/app/installations/{credentials.InstallationId}/access_tokens";
        return await SendGitHubAsync(
            HttpMethod.Post,
            path,
            body: null,
            $"Bearer {jwt}",
            "application/vnd.github+json",
            expectedStatus: 201,
            ParseRegistrationToken,
            retryDiscoveryAuthentication: false,
            cancellationToken).ConfigureAwait(false);
    }

    private static string ParseRegistrationToken(byte[] responseBody)
    {
        using var document = JsonDocument.Parse(responseBody);
        return ReadRequiredAuthenticationToken(document.RootElement, "token");
    }

    private static (Uri ServiceUri, string AdminToken, DateTimeOffset ExpiresAt) ParseAdminDiscovery(byte[] responseBody)
    {
        using var document = JsonDocument.Parse(responseBody);
        var serviceUrl = ReadRequiredString(document.RootElement, "url");
        var adminToken = ReadRequiredAuthenticationToken(document.RootElement, "token");
        var serviceUri = ValidateActionsServiceUri(serviceUrl);
        var expiresAt = ReadUnverifiedJwtExpiration(adminToken);
        return (serviceUri, adminToken, expiresAt);
    }

    private static string ReadRequiredString(JsonElement objectValue, string propertyName)
    {
        if (objectValue.ValueKind != JsonValueKind.Object ||
            !objectValue.TryGetProperty(propertyName, out var value) ||
            value.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(value.GetString()))
        {
            throw new JsonException($"The authentication response is missing a non-empty '{propertyName}' string.");
        }

        return value.GetString()!;
    }

    private static string ReadRequiredAuthenticationToken(JsonElement objectValue, string propertyName)
    {
        var token = ReadRequiredString(objectValue, propertyName);
        if (!ScaleSetCredentials.IsSafeAuthenticationToken(token))
        {
            throw new JsonException($"The authentication response contains unsafe characters in '{propertyName}'.");
        }

        return token;
    }

    private static DateTimeOffset ReadUnverifiedJwtExpiration(string token)
    {
        var segments = token.Split('.');
        if (segments.Length != 3 || string.IsNullOrEmpty(segments[1]))
        {
            throw new JsonException("The discovered admin token is not a compact JWT.");
        }

        var payload = segments[1].Replace('-', '+').Replace('_', '/');
        var padding = (4 - (payload.Length % 4)) % 4;
        payload = payload.PadRight(payload.Length + padding, '=');
        byte[] decoded;
        try
        {
            decoded = Convert.FromBase64String(payload);
        }
        catch (FormatException exception)
        {
            throw new JsonException("The discovered admin token has an invalid JWT payload encoding.", exception);
        }

        using var document = JsonDocument.Parse(decoded);
        if (!document.RootElement.TryGetProperty("exp", out var expiration) ||
            expiration.ValueKind != JsonValueKind.Number ||
            !expiration.TryGetInt64(out var unixSeconds))
        {
            throw new JsonException("The discovered admin JWT has no numeric exp claim.");
        }

        try
        {
            return DateTimeOffset.FromUnixTimeSeconds(unixSeconds);
        }
        catch (ArgumentOutOfRangeException exception)
        {
            throw new JsonException("The discovered admin JWT exp claim is outside the supported time range.", exception);
        }
    }

    private static Uri ValidateActionsServiceUri(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            value.Any(char.IsControl) ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Fragment))
        {
            throw new JsonException("The Actions service URL must be an absolute HTTPS URI without credentials or a fragment.");
        }

        return uri;
    }
}
