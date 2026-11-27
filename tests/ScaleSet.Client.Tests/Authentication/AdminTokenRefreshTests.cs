using System.Net;
using ScaleSet.Client;
using ScaleSet.Client.Tests.Transport;
using Xunit;

namespace ScaleSet.Client.Tests.Authentication;

public sealed class AdminTokenRefreshTests
{
    private static readonly GitHubTarget Target = GitHubTarget.Parse("https://github.com/example-org/example-repo");

    [Fact]
    public async Task AdminSnapshotRefreshesAtSixtySecondBoundaryAndPublishesUrlWithToken()
    {
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var registrationCalls = 0;
        var discoveryCalls = 0;
        var issuedTokens = new List<string>();
        var actionRequests = new List<RecordedRequest>();
        var handler = new RecordingHttpMessageHandler((request, _) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/runners/registration-token", StringComparison.Ordinal))
            {
                registrationCalls++;
                return Task.FromResult(TestResponses.Json(HttpStatusCode.Created, "{\"token\":\"registration-token\"}"));
            }

            if (path == "/actions/runner-registration")
            {
                discoveryCalls++;
                var host = $"https://actions{discoveryCalls}-fixture.actions.githubusercontent.com/tenant";
                var token = TestResponses.CreateAdminToken(clock.GetUtcNow().AddSeconds(120)) + $"-{discoveryCalls}";
                issuedTokens.Add(token);
                return Task.FromResult(TestResponses.Json(HttpStatusCode.OK, $"{{\"url\":\"{host}\",\"token\":\"{token}\"}}"));
            }

            actionRequests.Add(new RecordedRequest(
                request.Method.Method,
                request.RequestUri,
                request.Headers.Authorization?.ToString(),
                request.Content?.Headers.ContentType?.ToString(),
                request.Headers.UserAgent.ToString(),
                Array.Empty<byte>()));
            return Task.FromResult(TestResponses.Json(HttpStatusCode.OK, "ok"));
        });
        using var httpClient = new HttpClient(handler);
        using var client = new ScaleSetClient(
            Target,
            ScaleSetCredentials.FromPersonalAccessToken("synthetic-pat"),
            httpClient: httpClient,
            timeProvider: clock);

        await SendReadAsync(client, "first");
        clock.Advance(TimeSpan.FromSeconds(59));
        await SendReadAsync(client, "just-before-boundary");
        clock.Advance(TimeSpan.FromSeconds(1));
        await SendReadAsync(client, "at-boundary");
        clock.Advance(TimeSpan.FromTicks(1));
        await SendReadAsync(client, "below-boundary");

        Assert.Equal(2, registrationCalls);
        Assert.Equal(2, discoveryCalls);
        Assert.Equal(4, actionRequests.Count);
        Assert.Equal("actions1-fixture.actions.githubusercontent.com", actionRequests[0].Uri.Host);
        Assert.Equal("Bearer " + issuedTokens[0], actionRequests[0].Authorization);
        Assert.Equal("actions1-fixture.actions.githubusercontent.com", actionRequests[1].Uri.Host);
        Assert.Equal("Bearer " + issuedTokens[0], actionRequests[1].Authorization);
        Assert.Equal("actions1-fixture.actions.githubusercontent.com", actionRequests[2].Uri.Host);
        Assert.Equal("Bearer " + issuedTokens[0], actionRequests[2].Authorization);
        Assert.Equal("actions2-fixture.actions.githubusercontent.com", actionRequests[3].Uri.Host);
        Assert.Equal("Bearer " + issuedTokens[1], actionRequests[3].Authorization);
    }

    [Fact]
    public async Task ConcurrentAdminRefreshCoalescesAndSharesOneSnapshot()
    {
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var discoveryEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseDiscovery = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var registrationCalls = 0;
        var discoveryCalls = 0;
        var handler = new RecordingHttpMessageHandler(async (request, cancellationToken) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/runners/registration-token", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref registrationCalls);
                return TestResponses.Json(HttpStatusCode.Created, "{\"token\":\"registration-token\"}");
            }

            if (path == "/actions/runner-registration")
            {
                var attempt = Interlocked.Increment(ref discoveryCalls);
                discoveryEntered.TrySetResult();
                await releaseDiscovery.Task.WaitAsync(cancellationToken);
                var token = TestResponses.CreateAdminToken(clock.GetUtcNow().AddMinutes(10));
                return TestResponses.Json(HttpStatusCode.OK, $"{{\"url\":\"https://actions{attempt}-fixture.actions.githubusercontent.com/tenant\",\"token\":\"{token}\"}}");
            }

            return TestResponses.Json(HttpStatusCode.OK, "ok");
        });
        using var httpClient = new HttpClient(handler);
        using var client = new ScaleSetClient(
            Target,
            ScaleSetCredentials.FromPersonalAccessToken("synthetic-pat"),
            httpClient: httpClient,
            timeProvider: clock);

        var requests = Enumerable.Range(0, 8)
            .Select(index => SendReadAsync(client, $"parallel-{index}"))
            .ToArray();
        await discoveryEntered.Task;
        releaseDiscovery.SetResult();
        await Task.WhenAll(requests);

        Assert.Equal(1, registrationCalls);
        Assert.Equal(1, discoveryCalls);
        Assert.Equal(8, handler.Requests.Count(request => request.Uri.Host == "actions1-fixture.actions.githubusercontent.com"));
    }

    [Fact]
    public async Task NearExpiryFreshTokenServesCurrentCallAndRefreshesOnNextCall()
    {
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var discoveryCalls = 0;
        var issuedTokens = new List<string>();
        var handler = new RecordingHttpMessageHandler((request, _) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/runners/registration-token", StringComparison.Ordinal))
            {
                return Task.FromResult(TestResponses.Json(HttpStatusCode.Created, "{\"token\":\"registration-token\"}"));
            }

            if (path == "/actions/runner-registration")
            {
                discoveryCalls++;
                var expiresAt = clock.GetUtcNow().AddSeconds(discoveryCalls == 1 ? 30 : 600);
                var token = TestResponses.CreateAdminToken(expiresAt) + $"-{discoveryCalls}";
                issuedTokens.Add(token);
                return Task.FromResult(TestResponses.Json(
                    HttpStatusCode.OK,
                    $"{{\"url\":\"https://actions{discoveryCalls}-fixture.actions.githubusercontent.com/tenant\",\"token\":\"{token}\"}}"));
            }

            return Task.FromResult(TestResponses.Json(HttpStatusCode.OK, "ok"));
        });
        using var httpClient = new HttpClient(handler);
        using var client = new ScaleSetClient(
            Target,
            ScaleSetCredentials.FromPersonalAccessToken("synthetic-pat"),
            httpClient: httpClient,
            timeProvider: clock);

        await SendReadAsync(client, "near-expiry-first-call");
        await SendReadAsync(client, "near-expiry-next-call");

        var actionRequests = handler.Requests.Where(request => request.Uri.Host.StartsWith("actions", StringComparison.Ordinal)).ToArray();
        Assert.Equal(2, discoveryCalls);
        Assert.Equal(2, actionRequests.Length);
        Assert.Equal("actions1-fixture.actions.githubusercontent.com", actionRequests[0].Uri.Host);
        Assert.Equal("Bearer " + issuedTokens[0], actionRequests[0].Authorization);
        Assert.Equal("actions2-fixture.actions.githubusercontent.com", actionRequests[1].Uri.Host);
        Assert.Equal("Bearer " + issuedTokens[1], actionRequests[1].Authorization);
    }

    [Fact]
    public async Task FailedAdminRefreshCanBeRetriedOnTheNextCall()
    {
        var discoveryCalls = 0;
        var handler = new RecordingHttpMessageHandler((request, _) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/runners/registration-token", StringComparison.Ordinal))
            {
                return Task.FromResult(TestResponses.Json(HttpStatusCode.Created, "{\"token\":\"registration-token\"}"));
            }

            if (path == "/actions/runner-registration")
            {
                discoveryCalls++;
                if (discoveryCalls == 1)
                {
                    return Task.FromResult(TestResponses.Json(HttpStatusCode.InternalServerError, "temporary"));
                }

                var token = TestResponses.CreateAdminToken(DateTimeOffset.UtcNow.AddMinutes(10));
                return Task.FromResult(TestResponses.Json(HttpStatusCode.OK, $"{{\"url\":\"https://actions-fixture.actions.githubusercontent.com/tenant\",\"token\":\"{token}\"}}"));
            }

            return Task.FromResult(TestResponses.Json(HttpStatusCode.OK, "ok"));
        });
        using var httpClient = new HttpClient(handler);
        using var client = new ScaleSetClient(
            Target,
            ScaleSetCredentials.FromPersonalAccessToken("synthetic-pat"),
            httpClient: httpClient);

        var failure = await Assert.ThrowsAsync<ScaleSetApiException>(() => SendReadAsync(client, "first"));
        Assert.Equal((int)HttpStatusCode.InternalServerError, failure.StatusCode);
        await SendReadAsync(client, "second");

        Assert.Equal(2, discoveryCalls);
    }

    [Theory]
    [InlineData("https://github.com/enterprises/acme", "api.github.com", "/enterprises/acme/actions/runners/registration-token")]
    [InlineData("https://acme.ghe.com/enterprises/acme", "api.acme.ghe.com", "/enterprises/acme/actions/runners/registration-token")]
    [InlineData("https://acme.ghe.com/org", "api.acme.ghe.com", "/orgs/org/actions/runners/registration-token")]
    public async Task PatAuthenticationSupportsEnterpriseAndHostedGheTargets(
        string targetUrl,
        string expectedApiHost,
        string expectedRegistrationPath)
    {
        const string pat = "pat-ghec-sentinel";
        var target = GitHubTarget.Parse(targetUrl);
        string? discoveryBody = null;
        string? registrationAuth = null;
        string? discoveryAuth = null;
        var handler = new RecordingHttpMessageHandler(async (request, cancellationToken) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (string.Equals(request.RequestUri.Host, expectedApiHost, StringComparison.OrdinalIgnoreCase)
                && string.Equals(path, expectedRegistrationPath, StringComparison.Ordinal))
            {
                registrationAuth = request.Headers.Authorization?.ToString();
                return TestResponses.Json(HttpStatusCode.Created, "{\"token\":\"registration-token\"}");
            }

            if (string.Equals(request.RequestUri.Host, expectedApiHost, StringComparison.OrdinalIgnoreCase)
                && path == "/actions/runner-registration")
            {
                discoveryAuth = request.Headers.Authorization?.ToString();
                discoveryBody = request.Content is null
                    ? string.Empty
                    : await request.Content.ReadAsStringAsync(cancellationToken);
                string actionsHost = target.SourceUri.Host.EndsWith(".ghe.com", StringComparison.Ordinal)
                    ? $"actions-fixture.actions.{target.SourceUri.Host}"
                    : "actions-fixture.actions.githubusercontent.com";
                var token = TestResponses.CreateAdminToken(DateTimeOffset.UtcNow.AddMinutes(10));
                return TestResponses.Json(HttpStatusCode.OK, $"{{\"url\":\"https://{actionsHost}/tenant\",\"token\":\"{token}\"}}");
            }

            return TestResponses.Json(HttpStatusCode.OK, "ok");
        });
        using var httpClient = new HttpClient(handler);
        using var client = new ScaleSetClient(
            target,
            ScaleSetCredentials.FromPersonalAccessToken(pat),
            httpClient: httpClient);

        await SendReadAsync(client, "ghec-test");

        Assert.Equal($"Bearer {pat}", registrationAuth);
        Assert.Equal("RemoteAuth registration-token", discoveryAuth);
        Assert.NotNull(discoveryBody);
        using var discoveryJson = System.Text.Json.JsonDocument.Parse(discoveryBody);
        Assert.Equal(target.SourceUri.AbsoluteUri, discoveryJson.RootElement.GetProperty("url").GetString());
    }

    private static Task<byte[]> SendReadAsync(ScaleSetClient client, string name) =>
        client.SendActionsAsync(
            HttpMethod.Get,
            "/_apis/runtime/test",
            body: null,
            query: new Dictionary<string, string> { ["name"] = name },
            expectedStatus: 200,
            TestContext.Current.CancellationToken);
}
