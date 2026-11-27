using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Text.Json;

namespace ScaleSet.Client;

public sealed partial class ScaleSetClient
{
    private static readonly TimeSpan InstallationRefreshWindow = TimeSpan.FromMinutes(1);
    private readonly SemaphoreSlim _installationRefreshGate = new(1, 1);
    private InstallationAccessToken? _installationAccessToken;

    private sealed class InstallationAccessToken(string value, DateTimeOffset? expiresAt)
    {
        public string Value { get; } = value;
        public DateTimeOffset? ExpiresAt { get; } = expiresAt;
    }
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
            installationToken: null,
            deadline: null,
            elapsedBudget: null,
            cancellationToken).ConfigureAwait(false);
        return new AdminServiceSnapshot(
            discovery.ServiceUri,
            discovery.AdminToken,
            discovery.ExpiresAt,
            Interlocked.Increment(ref _adminGeneration));
    }

    private Task<string> GetRunnerRegistrationTokenAsync(CancellationToken cancellationToken) =>
        WithGitHubAccessTokenAsync((authorizationToken, installationToken, deadline) => SendGitHubAsync(
            HttpMethod.Post,
            Target.RegistrationTokenPath,
            [],
            $"Bearer {authorizationToken}",
            "application/vnd.github.v3+json",
            expectedStatus: 201,
            ParseRegistrationToken,
            retryDiscoveryAuthentication: false,
            installationToken,
            deadline,
            elapsedBudget: null,
            cancellationToken), cancellationToken);

    private async Task<T> WithGitHubAccessTokenAsync<T>(
        Func<string, InstallationAccessToken?, BoundedDeadlineScope?, Task<T>> send, CancellationToken cancellationToken)
    {
        if (_credentials is PersonalAccessTokenCredentials pat)
        {
            return await send(pat.Token, null, null).ConfigureAwait(false);
        }

        if (_credentials is not GitHubAppCredentials app)
        {
            throw new InvalidOperationException("Unsupported ScaleSet credential type.");
        }

        var retryOptions = ResolveRetryOptions(protectMutation: false, out _);
        using BoundedDeadlineScope? deadline = BoundedDeadlineScope.Create(retryOptions.Deadline, _timeProvider);
        InstallationAccessToken token = await GetInstallationAccessTokenAsync(app, deadline, elapsedBudget: null, cancellationToken)
            .ConfigureAwait(false);
        return await send(token.Value, token, deadline).ConfigureAwait(false);
    }

    private async Task<InstallationAccessToken> GetInstallationAccessTokenAsync(
        GitHubAppCredentials credentials,
        BoundedDeadlineScope? deadline,
        ElapsedRequestBudget? elapsedBudget,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        CancellationToken elapsedToken = elapsedBudget?.Token ?? CancellationToken.None;
        ThrowIfInstallationAuthCanceled(deadline, elapsedToken, cancellationToken);
        InstallationAccessToken? token = Volatile.Read(ref _installationAccessToken);
        if (IsCurrentInstallationToken(token))
        {
            return token;
        }

        using CancellationTokenSource? linked = deadline is not null || elapsedToken.CanBeCanceled
            ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline?.Token ?? CancellationToken.None, elapsedToken)
            : null;
        CancellationToken blockingToken = linked?.Token ?? cancellationToken;
        bool gateHeld = false;
        try
        {
            await _installationRefreshGate.WaitAsync(blockingToken).ConfigureAwait(false);
            gateHeld = true;
            ThrowIfDisposed();
            ThrowIfInstallationAuthCanceled(deadline, elapsedToken, cancellationToken);
            token = Volatile.Read(ref _installationAccessToken);
            if (IsCurrentInstallationToken(token))
            {
                return token;
            }

            var jwt = await credentials.JwtProvider.CreateJwtAsync(credentials.ClientId, _timeProvider.GetUtcNow(), blockingToken)
                .ConfigureAwait(false);
            ThrowIfInstallationAuthCanceled(deadline, elapsedToken, cancellationToken);
            token = await MintInstallationAccessTokenAsync(credentials, jwt, deadline, elapsedBudget, cancellationToken).ConfigureAwait(false);
            Volatile.Write(ref _installationAccessToken, IsCurrentInstallationToken(token) ? token : null);
            ThrowIfInstallationAuthCanceled(deadline, elapsedToken, cancellationToken);
            return token;
        }
        catch (OperationCanceledException)
        {
            ThrowIfInstallationAuthCanceled(deadline, elapsedToken, cancellationToken);
            throw;
        }
        finally
        {
            if (gateHeld)
            {
                _installationRefreshGate.Release();
            }
        }
    }

    private static void ThrowIfInstallationAuthCanceled(
        BoundedDeadlineScope? deadline, CancellationToken elapsedToken, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        deadline?.ThrowIfExpired(cancellationToken);
        elapsedToken.ThrowIfCancellationRequested();
    }

    private bool IsCurrentInstallationToken([NotNullWhen(true)] InstallationAccessToken? token) =>
        token?.ExpiresAt is { } expiration && expiration - _timeProvider.GetUtcNow() > InstallationRefreshWindow;

    private async Task<InstallationAccessToken> MintInstallationAccessTokenAsync(
        GitHubAppCredentials credentials,
        string? jwt,
        BoundedDeadlineScope? deadline,
        ElapsedRequestBudget? elapsedBudget,
        CancellationToken cancellationToken)
    {
        if (jwt is null || !ScaleSetCredentials.IsSafeAuthenticationToken(jwt))
        {
            throw new ScaleSetApiException("GitHub App JWT provider returned a token that is unsafe for an HTTP authentication header.");
        }

        var path = $"/app/installations/{credentials.InstallationId}/access_tokens";
        using IDisposable mutation = ScaleSetRequestRetry.SuppressMutationReplay();
        return await SendGitHubAsync(
            HttpMethod.Post,
            path,
            body: null,
            $"Bearer {jwt}",
            "application/vnd.github+json",
            expectedStatus: 201,
            ParseInstallationAccessToken,
            retryDiscoveryAuthentication: false,
            installationToken: null,
            deadline,
            elapsedBudget,
            cancellationToken).ConfigureAwait(false);
    }

    private static InstallationAccessToken ParseInstallationAccessToken(byte[] responseBody)
    {
        using var document = JsonDocument.Parse(responseBody);
        JsonElement root = document.RootElement;
        string value = ReadRequiredAuthenticationToken(root, "token");
        if (!root.TryGetProperty("expires_at", out JsonElement expiry))
        {
            return new InstallationAccessToken(value, null);
        }

        if (expiry.ValueKind != JsonValueKind.String || !expiry.TryGetDateTimeOffset(out var expiresAt))
        {
            throw new JsonException("The GitHub App installation token response contains an invalid expiration.");
        }

        return new InstallationAccessToken(value, expiresAt);
    }

    private static string ParseRegistrationToken(byte[] responseBody)
    {
        using var document = JsonDocument.Parse(responseBody);
        return ReadRequiredAuthenticationToken(document.RootElement, "token");
    }

    private (Uri ServiceUri, string AdminToken, DateTimeOffset ExpiresAt) ParseAdminDiscovery(byte[] responseBody)
    {
        using var document = JsonDocument.Parse(responseBody);
        var serviceUrl = ReadRequiredString(document.RootElement, "url");
        var adminToken = ReadRequiredAuthenticationToken(document.RootElement, "token");
        if (!TryValidateDiscoveredEndpoint(serviceUrl, out var serviceUri))
        {
            throw new JsonException("The Actions service URL is not an eligible HTTPS endpoint.");
        }
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

    internal bool TryValidateDiscoveredEndpoint(string? value, [NotNullWhen(true)] out Uri? uri)
    {
        return Uri.TryCreate(value, UriKind.Absolute, out uri)
            && uri.Scheme == Uri.UriSchemeHttps
            && !value!.Any(char.IsControl)
            && !string.IsNullOrEmpty(uri.Host)
            && string.IsNullOrEmpty(uri.UserInfo)
            && string.IsNullOrEmpty(uri.Fragment)
            && IsGitHubActionsOrigin(uri)
            && (_options.IsTrustedDiscoveredEndpoint?.Invoke(uri) ?? true);
    }

    private bool IsGitHubActionsOrigin(Uri uri)
    {
        if (uri.HostNameType != UriHostNameType.Dns || uri.Port != 443)
        {
            return false;
        }

        string host = uri.IdnHost.TrimEnd('.');
        string targetHost = Target.SourceUri.IdnHost;
        string suffix = targetHost.EndsWith(".ghe.com", StringComparison.OrdinalIgnoreCase)
            ? $".actions.{targetHost}"
            : ".actions.githubusercontent.com";
        return host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase);
    }
}
