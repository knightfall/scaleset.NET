using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ScaleSet.Client;
using Xunit;

namespace ScaleSet.Client.Tests.Authentication;

public sealed class GitHubAppSigningTests
{
    private static readonly string[] ExpectedPemBootstrapPaths =
    [
        "/app/installations/42/access_tokens",
        "/repos/example-org/example-repo/actions/runners/registration-token",
        "/actions/runner-registration",
        "/_apis/runtime/runnerscalesets/7",
    ];

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FromGitHubAppPemFormatsProduceVerifiableRs256JwtWithExpectedClaims(bool usePkcs8Pem)
    {
        using RSA key = RSA.Create(2048);
        string privateKeyPem = usePkcs8Pem ? key.ExportPkcs8PrivateKeyPem() : key.ExportRSAPrivateKeyPem();
        byte[] publicKey = key.ExportSubjectPublicKeyInfo();
        DateTimeOffset now = new(2026, 2, 3, 4, 5, 6, TimeSpan.Zero);
        TimeProvider clock = new FixedTimeProvider(now);
        ScaleSetCredentials credentials = ScaleSetCredentials.FromGitHubApp("app-client-123", 42, privateKeyPem);

        Assert.DoesNotContain(privateKeyPem, credentials.ToString());
        Assert.Contains("REDACTED", credentials.ToString(), StringComparison.Ordinal);

        using ClientFixture fixture = new(credentials, clock);
        Assert.Empty(fixture.Handler.Requests);

        RunnerScaleSet? result = await fixture.Client.GetScaleSetAsync(7, TestContext.Current.CancellationToken);

        Assert.Null(result);
        RecordedRequest installationTokenRequest = Assert.Single(
            fixture.Handler.Requests,
            request => request.Uri.AbsolutePath == "/app/installations/42/access_tokens");
        Assert.Equal(HttpMethod.Post, installationTokenRequest.Method);
        Assert.StartsWith("Bearer ", installationTokenRequest.Authorization, StringComparison.Ordinal);

        string compactJwt = installationTokenRequest.Authorization!["Bearer ".Length..];
        string[] segments = compactJwt.Split('.');
        Assert.Equal(3, segments.Length);

        using JsonDocument header = JsonDocument.Parse(DecodeBase64Url(segments[0]));
        using JsonDocument payload = JsonDocument.Parse(DecodeBase64Url(segments[1]));
        Assert.Equal("RS256", header.RootElement.GetProperty("alg").GetString());
        Assert.Equal("JWT", header.RootElement.GetProperty("typ").GetString());
        Assert.Equal("app-client-123", payload.RootElement.GetProperty("iss").GetString());
        long expectedIssuedAt = now.AddMinutes(-1).ToUnixTimeSeconds();
        Assert.Equal(expectedIssuedAt, payload.RootElement.GetProperty("iat").GetInt64());
        Assert.Equal(expectedIssuedAt + 540, payload.RootElement.GetProperty("exp").GetInt64());

        using RSA verifier = RSA.Create();
        verifier.ImportSubjectPublicKeyInfo(publicKey, out int bytesRead);
        Assert.Equal(publicKey.Length, bytesRead);
        byte[] signingInput = Encoding.ASCII.GetBytes($"{segments[0]}.{segments[1]}");
        Assert.True(verifier.VerifyData(
            signingInput,
            DecodeBase64Url(segments[2]),
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1));

        Assert.Equal(ExpectedPemBootstrapPaths, fixture.Handler.Requests.Select(request => request.Uri.AbsolutePath));
    }

    [Fact]
    public async Task FromGitHubAppUsesAsyncSignerArgumentsAndReturnedTokenInInstallationRequest()
    {
        const string appJwt = "remote-signer-generated-jwt";
        DateTimeOffset now = new(2026, 3, 4, 5, 6, 7, TimeSpan.Zero);
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        RecordingJwtProvider signer = new(appJwt);
        ScaleSetCredentials credentials = ScaleSetCredentials.FromGitHubApp("remote-app-client", 73, signer);
        using ClientFixture fixture = new(credentials, new FixedTimeProvider(now));

        Assert.Empty(signer.Calls);
        Assert.Empty(fixture.Handler.Requests);

        _ = await fixture.Client.GetScaleSetAsync(8, cancellationToken);

        SignerCall call = Assert.Single(signer.Calls);
        Assert.Equal("remote-app-client", call.ClientId);
        Assert.Equal(now, call.Now);
        Assert.Equal(cancellationToken, call.CancellationToken);
        RecordedRequest installationRequest = Assert.Single(
            fixture.Handler.Requests,
            request => request.Uri.AbsolutePath == "/app/installations/73/access_tokens");
        Assert.Equal($"Bearer {appJwt}", installationRequest.Authorization);
        Assert.Equal(HttpMethod.Post, installationRequest.Method);
    }

    [Fact]
    public async Task FromGitHubAppCanceledAsyncSigningStopsBeforeAnyHttpRequest()
    {
        CancellableJwtProvider signer = new();
        ScaleSetCredentials credentials = ScaleSetCredentials.FromGitHubApp("cancel-app", 9, signer);
        DateTimeOffset now = new(2026, 4, 5, 6, 7, 8, TimeSpan.Zero);
        using ClientFixture fixture = new(credentials, new FixedTimeProvider(now));
        using CancellationTokenSource cancellation = new();

        Task<RunnerScaleSet?> operation = fixture.Client.GetScaleSetAsync(8, cancellation.Token);
        SignerCall call = await signer.Started.Task.WaitAsync(
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);

        Assert.Equal("cancel-app", call.ClientId);
        Assert.Equal(cancellation.Token, call.CancellationToken);
        Assert.Empty(fixture.Handler.Requests);
    }

    [Fact]
    public void FromPersonalAccessTokenValidatesInputAndRedactsDiagnosticString()
    {
        const string pat = "ghp-test-secret-value";
        ScaleSetCredentials credentials = ScaleSetCredentials.FromPersonalAccessToken(pat);

        Assert.DoesNotContain(pat, credentials.ToString(), StringComparison.Ordinal);
        Assert.Contains("REDACTED", credentials.ToString(), StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => ScaleSetCredentials.FromPersonalAccessToken(string.Empty));
        Assert.Throws<ArgumentException>(() => ScaleSetCredentials.FromPersonalAccessToken("   "));
        Assert.Throws<ArgumentException>(() => ScaleSetCredentials.FromPersonalAccessToken("token\nwith-line-break"));
        Assert.Throws<ArgumentNullException>(() => ScaleSetCredentials.FromPersonalAccessToken(null!));
    }

    [Theory]
    [InlineData("https://github.com/enterprises/acme")]
    [InlineData("https://acme.ghe.com/enterprises/acme")]
    public void ClientRejectsEnterpriseTargetsWithGitHubAppCredentials(string url)
    {
        RecordingJwtProvider signer = new("synthetic-jwt");
        ScaleSetCredentials credentials = ScaleSetCredentials.FromGitHubApp("app", 1, signer);

        ArgumentException failure = Assert.Throws<ArgumentException>("credentials", () => new ScaleSetClient(GitHubTarget.Parse(url), credentials));

        Assert.Contains("manage_runners:enterprise", failure.Message, StringComparison.Ordinal);
        Assert.Empty(signer.Calls);
    }

    [Fact]
    public void FromGitHubAppValidatesRequiredFieldsInstallationAndPrivatePem()
    {
        using RSA key = RSA.Create(2048);
        string privateKeyPem = key.ExportPkcs8PrivateKeyPem();

        Assert.Throws<ArgumentNullException>(() => ScaleSetCredentials.FromGitHubApp(null!, 1, privateKeyPem));
        Assert.Throws<ArgumentException>(() => ScaleSetCredentials.FromGitHubApp(" ", 1, privateKeyPem));
        Assert.Throws<ArgumentOutOfRangeException>(() => ScaleSetCredentials.FromGitHubApp("app", 0, privateKeyPem));
        Assert.Throws<ArgumentOutOfRangeException>(() => ScaleSetCredentials.FromGitHubApp("app", -1, privateKeyPem));
        Assert.Throws<ArgumentNullException>(() => ScaleSetCredentials.FromGitHubApp("app", 1, (string)null!));
        Assert.Throws<ArgumentException>(() => ScaleSetCredentials.FromGitHubApp("app", 1, "  "));
        Assert.Throws<ArgumentException>(() => ScaleSetCredentials.FromGitHubApp("app", 1, "not-a-private-key"));
    }

    [Fact]
    public void FromGitHubAppRejectsPublicOnlyPemAndNullCustomSigner()
    {
        using RSA key = RSA.Create(2048);
        string publicKeyPem = key.ExportSubjectPublicKeyInfoPem();

        Exception exception = Assert.ThrowsAny<Exception>(() => ScaleSetCredentials.FromGitHubApp("app", 1, publicKeyPem));
        Assert.True(exception is ArgumentException or CryptographicException);
        Assert.Throws<ArgumentNullException>(() => ScaleSetCredentials.FromGitHubApp(
            "app",
            1,
            (IGitHubAppJwtProvider)null!));
    }

    private static byte[] DecodeBase64Url(string value)
    {
        string padded = value.Replace('-', '+').Replace('_', '/');
        padded = padded.PadRight(padded.Length + ((4 - (padded.Length % 4)) % 4), '=');
        return Convert.FromBase64String(padded);
    }

    private sealed class ClientFixture : IDisposable
    {
        private readonly HttpClient _httpClient;

        public ClientFixture(ScaleSetCredentials credentials, TimeProvider timeProvider)
        {
            Handler = new RecordingHandler();
            _httpClient = new HttpClient(Handler);
            Client = new ScaleSetClient(
                GitHubTarget.Parse("https://github.com/example-org/example-repo"),
                credentials,
                httpClient: _httpClient,
                timeProvider: timeProvider);
        }

        public RecordingHandler Handler { get; }

        public ScaleSetClient Client { get; }

        public void Dispose()
        {
            Client.Dispose();
            _httpClient.Dispose();
        }
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private const string ActionsServiceHost = "actions.example.test";
        private const string DiscoveryJwt = "eyJhbGciOiJub25lIiwidHlwIjoiSldUIn0.eyJleHAiOjQxMDI0NDQ4MDB9.";
        private readonly List<RecordedRequest> _requests = [];

        public IReadOnlyList<RecordedRequest> Requests => _requests;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Uri requestUri = request.RequestUri ?? throw new InvalidOperationException("Request URI is missing.");
            string body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            _requests.Add(new RecordedRequest(
                request.Method,
                requestUri,
                request.Headers.Authorization?.ToString(),
                body));

            if (string.Equals(requestUri.Host, "api.github.com", StringComparison.OrdinalIgnoreCase)
                && requestUri.AbsolutePath.StartsWith("/app/installations/", StringComparison.Ordinal))
            {
                return JsonResponse(HttpStatusCode.Created, "{\"token\":\"synthetic-installation-token\",\"expires_at\":\"2099-01-01T00:00:00Z\"}");
            }

            if (string.Equals(requestUri.Host, "api.github.com", StringComparison.OrdinalIgnoreCase)
                && requestUri.AbsolutePath.EndsWith("/actions/runners/registration-token", StringComparison.Ordinal))
            {
                return JsonResponse(HttpStatusCode.Created, "{\"token\":\"synthetic-registration-token\",\"expires_at\":\"2099-01-01T00:00:00Z\"}");
            }

            if (string.Equals(requestUri.Host, "api.github.com", StringComparison.OrdinalIgnoreCase)
                && string.Equals(requestUri.AbsolutePath, "/actions/runner-registration", StringComparison.Ordinal))
            {
                return JsonResponse(HttpStatusCode.OK, $"{{\"url\":\"https://{ActionsServiceHost}\",\"token\":\"{DiscoveryJwt}\"}}");
            }

            if (string.Equals(requestUri.Host, ActionsServiceHost, StringComparison.OrdinalIgnoreCase))
            {
                return JsonResponse(HttpStatusCode.OK, "null");
            }

            throw new InvalidOperationException("Unexpected request in the local GitHub App test handler.");
        }

        private static HttpResponseMessage JsonResponse(HttpStatusCode statusCode, string body) =>
            new(statusCode)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed record RecordedRequest(HttpMethod Method, Uri Uri, string? Authorization, string Body);

    private sealed record SignerCall(string ClientId, DateTimeOffset Now, CancellationToken CancellationToken);

    private sealed class RecordingJwtProvider(string token) : IGitHubAppJwtProvider
    {
        private readonly List<SignerCall> _calls = [];

        public IReadOnlyList<SignerCall> Calls => _calls;

        public ValueTask<string> CreateJwtAsync(string clientId, DateTimeOffset now, CancellationToken cancellationToken)
        {
            _calls.Add(new SignerCall(clientId, now, cancellationToken));
            return ValueTask.FromResult(token);
        }
    }

    private sealed class CancellableJwtProvider : IGitHubAppJwtProvider
    {
        public TaskCompletionSource<SignerCall> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask<string> CreateJwtAsync(
            string clientId,
            DateTimeOffset now,
            CancellationToken cancellationToken)
        {
            Started.TrySetResult(new SignerCall(clientId, now, cancellationToken));
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            return "unreachable";
        }
    }
}
