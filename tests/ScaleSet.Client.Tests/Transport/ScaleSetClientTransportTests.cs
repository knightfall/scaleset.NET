using System.Net;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using ScaleSet.Client;
using Xunit;

namespace ScaleSet.Client.Tests.Transport;

public sealed class ScaleSetClientTransportTests
{
    private static readonly GitHubTarget Target = GitHubTarget.Parse("https://github.com/example-org/example-repo");
    private const string Pat = "pat-transport-sentinel-2026";

    [Fact]
    public async Task ActionsRequestMergesBasePathExistingQueriesAndApiVersion()
    {
        var handler = CreateConnectedHandler((_, _) => Task.FromResult(TestResponses.Json(HttpStatusCode.OK, "done")),
            "https://actions.example/tenant?trace=base&api-version=7.0");
        using var httpClient = new HttpClient(handler);
        using var client = CreateClient(httpClient);

        var response = await client.SendActionsAsync(
            HttpMethod.Get,
            "/_apis/runtime/items?lastMessageId=12",
            body: null,
            new Dictionary<string, string> { ["name"] = "build job" },
            expectedStatus: 200,
            TestContext.Current.CancellationToken);

        Assert.Equal("done", Encoding.UTF8.GetString(response));
        var request = handler.Requests.Single(request => request.Uri.Host == "actions.example");
        Assert.Equal("/tenant/_apis/runtime/items", request.Uri.AbsolutePath);
        var query = ParseQuery(request.Uri.Query);
        Assert.Equal("base", query["trace"]);
        Assert.Equal("7.0", query["api-version"]);
        Assert.Equal("12", query["lastMessageId"]);
        Assert.Equal("build job", query["name"]);
    }

    [Fact]
    public async Task RootRelativeActionsRouteIsJoinedToTheDiscoveredServicePath()
    {
        var handler = CreateConnectedHandler((_, _) => Task.FromResult(TestResponses.Json(HttpStatusCode.OK, "{}")),
            "https://actions.example/tenant");
        using var httpClient = new HttpClient(handler);
        using var client = CreateClient(httpClient);

        await client.SendActionsAsync(
            HttpMethod.Get,
            "/_apis/runtime/runnerscalesets",
            body: null,
            query: null,
            expectedStatus: 200,
            TestContext.Current.CancellationToken);

        var request = handler.Requests.Single(request => request.Uri.Host == "actions.example");
        Assert.Equal("/tenant/_apis/runtime/runnerscalesets", request.Uri.AbsolutePath);
    }

    [Fact]
    public async Task ReadOnlyTransientFailureRetriesWithinConfiguredBound()
    {
        var actionCalls = 0;
        var handler = CreateConnectedHandler((_, _) =>
        {
            actionCalls++;
            return Task.FromResult(actionCalls == 1
                ? TestResponses.Json(HttpStatusCode.ServiceUnavailable, "retry")
                : TestResponses.Json(HttpStatusCode.OK, "ok"));
        });
        using var httpClient = new HttpClient(handler);
        using var client = CreateClient(httpClient, new ScaleSetClientOptions
        {
            MaxReadOnlyRetries = 1,
            RetryBaseDelay = TimeSpan.Zero,
            RetryMaxDelay = TimeSpan.Zero,
        });

        var response = await client.SendActionsAsync(
            HttpMethod.Get,
            "/_apis/runtime/items",
            body: null,
            query: null,
            expectedStatus: 200,
            TestContext.Current.CancellationToken);

        Assert.Equal("ok", Encoding.UTF8.GetString(response));
        Assert.Equal(2, actionCalls);
    }

    [Fact]
    public async Task ReadOnlyRetriesExhaustAtConfiguredLimitPlusOneAttempts()
    {
        var actionCalls = 0;
        var handler = CreateConnectedHandler((_, _) =>
        {
            actionCalls++;
            return Task.FromResult(TestResponses.Json(HttpStatusCode.ServiceUnavailable, "still down"));
        });
        using var httpClient = new HttpClient(handler);
        using var client = CreateClient(httpClient, new ScaleSetClientOptions
        {
            MaxReadOnlyRetries = 2,
            RetryBaseDelay = TimeSpan.Zero,
            RetryMaxDelay = TimeSpan.Zero,
        });

        var exception = await Assert.ThrowsAsync<ScaleSetApiException>(() => client.SendActionsAsync(
            HttpMethod.Get,
            "/_apis/runtime/items",
            body: null,
            query: null,
            expectedStatus: 200,
            TestContext.Current.CancellationToken));

        Assert.Equal((int)HttpStatusCode.ServiceUnavailable, exception.StatusCode);
        Assert.Equal(3, actionCalls);
    }

    [Fact]
    public async Task MutationIsNotRetriedAfterTransientFailure()
    {
        var actionCalls = 0;
        var handler = CreateConnectedHandler((_, _) =>
        {
            actionCalls++;
            return Task.FromResult(TestResponses.Json(HttpStatusCode.ServiceUnavailable, "do not replay"));
        });
        using var httpClient = new HttpClient(handler);
        using var client = CreateClient(httpClient, new ScaleSetClientOptions
        {
            MaxReadOnlyRetries = 4,
            RetryBaseDelay = TimeSpan.Zero,
            RetryMaxDelay = TimeSpan.Zero,
        });

        var exception = await Assert.ThrowsAsync<ScaleSetApiException>(() => client.SendActionsAsync(
            HttpMethod.Post,
            "/_apis/runtime/runnerscalesets",
            Encoding.UTF8.GetBytes("{}"),
            query: null,
            expectedStatus: 200,
            TestContext.Current.CancellationToken));

        Assert.Equal((int)HttpStatusCode.ServiceUnavailable, exception.StatusCode);
        Assert.Equal(1, actionCalls);
    }

    [Fact]
    public async Task DiscoveryRetriesOnlyItsUnauthorizedAndForbiddenResponses()
    {
        var discoveryCalls = 0;
        var handler = new RecordingHttpMessageHandler((request, _) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/runners/registration-token", StringComparison.Ordinal))
            {
                return Task.FromResult(TestResponses.Json(HttpStatusCode.Created, "{\"token\":\"registration-token\"}"));
            }

            if (request.RequestUri.AbsolutePath == "/actions/runner-registration")
            {
                discoveryCalls++;
                if (discoveryCalls == 1)
                {
                    return Task.FromResult(TestResponses.Json(HttpStatusCode.Unauthorized, "{}"));
                }

                if (discoveryCalls == 2)
                {
                    return Task.FromResult(TestResponses.Json(HttpStatusCode.Forbidden, "{}"));
                }

                return Task.FromResult(TestResponses.Json(
                    HttpStatusCode.OK,
                    CreateDiscoveryResponse("https://actions.example/tenant", DateTimeOffset.UtcNow.AddMinutes(10))));
            }

            return Task.FromResult(TestResponses.Json(HttpStatusCode.OK, "ok"));
        });
        using var httpClient = new HttpClient(handler);
        using var client = CreateClient(httpClient, new ScaleSetClientOptions
        {
            MaxRegistrationDiscoveryAuthRetries = 2,
            RetryBaseDelay = TimeSpan.Zero,
            RetryMaxDelay = TimeSpan.Zero,
        });

        await client.SendActionsAsync(
            HttpMethod.Get,
            "/_apis/runtime/items",
            body: null,
            query: null,
            expectedStatus: 200,
            TestContext.Current.CancellationToken);

        Assert.Equal(3, discoveryCalls);
    }

    [Fact]
    public async Task DiscoveryAuthenticationRetriesExhaustAtConfiguredLimitPlusOneAttempts()
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
                return Task.FromResult(TestResponses.Json(HttpStatusCode.Unauthorized, "unauthorized"));
            }

            return Task.FromResult(TestResponses.Json(HttpStatusCode.OK, "unexpected"));
        });
        using var httpClient = new HttpClient(handler);
        using var client = CreateClient(httpClient, new ScaleSetClientOptions
        {
            MaxRegistrationDiscoveryAuthRetries = 1,
            RetryBaseDelay = TimeSpan.Zero,
            RetryMaxDelay = TimeSpan.Zero,
        });

        var exception = await Assert.ThrowsAsync<ScaleSetApiException>(() => client.SendActionsAsync(
            HttpMethod.Get,
            "/_apis/runtime/items",
            body: null,
            query: null,
            expectedStatus: 200,
            TestContext.Current.CancellationToken));

        Assert.Equal((int)HttpStatusCode.Unauthorized, exception.StatusCode);
        Assert.Equal(2, discoveryCalls);
    }

    [Fact]
    public async Task DiscoveryDoesNotReplayServerFailure()
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
                return Task.FromResult(TestResponses.Json(HttpStatusCode.ServiceUnavailable, "server error"));
            }

            return Task.FromResult(TestResponses.Json(HttpStatusCode.OK, "unexpected"));
        });
        using var httpClient = new HttpClient(handler);
        using var client = CreateClient(httpClient, new ScaleSetClientOptions
        {
            MaxRegistrationDiscoveryAuthRetries = 3,
            RetryBaseDelay = TimeSpan.Zero,
            RetryMaxDelay = TimeSpan.Zero,
        });

        var exception = await Assert.ThrowsAsync<ScaleSetApiException>(() => client.SendActionsAsync(
            HttpMethod.Get,
            "/_apis/runtime/items",
            body: null,
            query: null,
            expectedStatus: 200,
            TestContext.Current.CancellationToken));

        Assert.Equal((int)HttpStatusCode.ServiceUnavailable, exception.StatusCode);
        Assert.Equal(1, discoveryCalls);
    }

    [Fact]
    public async Task QueueRequestPreservesReturnedQueryAndUsesPerRequestBearer()
    {
        using var httpClient = new HttpClient(new RecordingHttpMessageHandler((_, _) =>
            Task.FromResult(TestResponses.Json(HttpStatusCode.OK, "payload"))));
        using var client = CreateClient(httpClient);
        var queueUrl = new Uri("https://queue.example/poll?cursor=old&sig=queue-secret");

        var response = await client.SendQueueAsync(
            HttpMethod.Get,
            queueUrl,
            "queue-secret",
            body: null,
            new Dictionary<string, string> { ["X-ScaleSetMaxCapacity"] = "8" },
            allowAccepted: true,
            TestContext.Current.CancellationToken);

        Assert.Equal("payload", Encoding.UTF8.GetString(response!));
        Assert.Null(httpClient.DefaultRequestHeaders.Authorization);
    }

    [Fact]
    public async Task QueueUnauthorizedIsClassifiedWithoutTransportRetry()
    {
        const string pathSecret = "opaque-queue-route-secret";
        var handler = new RecordingHttpMessageHandler((_, _) =>
            Task.FromResult(TestResponses.Json(HttpStatusCode.Unauthorized, "{\"message\":\"queue-secret\"}")));
        using var httpClient = new HttpClient(handler);
        using var client = CreateClient(httpClient);

        var exception = await Assert.ThrowsAsync<ScaleSetApiException>(() => client.SendQueueAsync(
            HttpMethod.Get,
            new Uri($"https://queue.example/{pathSecret}/poll?sig=queue"),
            "queue-token",
            body: null,
            headers: null,
            allowAccepted: true,
            TestContext.Current.CancellationToken));

        Assert.Equal((int)HttpStatusCode.Unauthorized, exception.StatusCode);
        Assert.Equal(ScaleSetDomainError.MessageQueueTokenExpired, exception.DomainError);
        Assert.Equal("https://queue.example/", exception.SafeUri);
        Assert.DoesNotContain(pathSecret, exception.SafeUri, StringComparison.Ordinal);
        Assert.DoesNotContain("sig=", exception.SafeUri, StringComparison.Ordinal);
        Assert.DoesNotContain("queue-secret", exception.BodyPreview, StringComparison.Ordinal);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task QueueTransportFailureRedactsPathFromSafeUri()
    {
        const string pathSecret = "opaque-queue-route-secret";
        var handler = new RecordingHttpMessageHandler((_, _) =>
            Task.FromException<HttpResponseMessage>(new HttpRequestException("transport failure")));
        using var httpClient = new HttpClient(handler);
        using var client = CreateClient(httpClient);

        var exception = await Assert.ThrowsAsync<ScaleSetApiException>(() => client.SendQueueAsync(
            HttpMethod.Get,
            new Uri($"https://queue.example/{pathSecret}/poll?sig=queue"),
            "queue-token",
            body: null,
            headers: null,
            allowAccepted: true,
            TestContext.Current.CancellationToken));

        Assert.Null(exception.StatusCode);
        Assert.Equal("https://queue.example/", exception.SafeUri);
        Assert.DoesNotContain(pathSecret, exception.SafeUri, StringComparison.Ordinal);
        Assert.DoesNotContain("sig=", exception.SafeUri, StringComparison.Ordinal);
    }

    [Fact]
    public async Task QueueResponseDecodeFailureRedactsPathFromSafeUri()
    {
        const string pathSecret = "opaque-queue-route-secret";
        var handler = new RecordingHttpMessageHandler((_, _) =>
            Task.FromResult(TestResponses.Json(HttpStatusCode.OK, "invalid response")));
        using var httpClient = new HttpClient(handler);
        using var client = CreateClient(httpClient);

        var exception = await Assert.ThrowsAsync<ScaleSetApiException>(() => client.SendQueueAsync<string>(
            HttpMethod.Get,
            new Uri($"https://queue.example/{pathSecret}/poll?sessionId=unused"),
            "queue-token",
            body: null,
            headers: null,
            allowAccepted: true,
            TestContext.Current.CancellationToken,
            static _ => throw new System.Text.Json.JsonException("invalid message")));

        Assert.Equal((int)HttpStatusCode.OK, exception.StatusCode);
        Assert.Equal("https://queue.example/", exception.SafeUri);
        Assert.DoesNotContain(pathSecret, exception.SafeUri, StringComparison.Ordinal);
        Assert.DoesNotContain("sessionId=", exception.SafeUri, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData((int)HttpStatusCode.TooManyRequests)]
    [InlineData((int)HttpStatusCode.ServiceUnavailable)]
    public async Task QueueTransientFailureIsNotRetried(int statusCode)
    {
        var handler = new RecordingHttpMessageHandler((_, _) =>
            Task.FromResult(TestResponses.Json((HttpStatusCode)statusCode, "transient queue failure")));
        using var httpClient = new HttpClient(handler);
        using var client = CreateClient(httpClient, new ScaleSetClientOptions
        {
            MaxReadOnlyRetries = 4,
            RetryBaseDelay = TimeSpan.Zero,
            RetryMaxDelay = TimeSpan.Zero,
        });

        var exception = await Assert.ThrowsAsync<ScaleSetApiException>(() => client.SendQueueAsync(
            HttpMethod.Get,
            new Uri("https://queue.example/poll"),
            "queue-token",
            body: null,
            headers: null,
            allowAccepted: true,
            TestContext.Current.CancellationToken));

        Assert.Equal(statusCode, exception.StatusCode);
        Assert.Single(handler.Requests);
    }

    [Theory]
    [InlineData("AgentExistsException", 409, ScaleSetDomainError.AgentExists)]
    [InlineData("AgentNotFoundException", 404, ScaleSetDomainError.AgentNotFound)]
    [InlineData("JobStillRunningException", 409, ScaleSetDomainError.JobStillRunning)]
    public async Task ResponsePreservesHttpStatusAndRecognizedDomainError(
        string typeName,
        int statusCode,
        ScaleSetDomainError domainError)
    {
        var handler = CreateConnectedHandler((_, _) => Task.FromResult(TestResponses.Json(
            (HttpStatusCode)statusCode,
            $"{{\"typeName\":\"{typeName}\",\"message\":\"safe detail\"}}")));
        using var httpClient = new HttpClient(handler);
        using var client = CreateClient(httpClient);

        var exception = await Assert.ThrowsAsync<ScaleSetApiException>(() => client.SendActionsAsync(
            HttpMethod.Get,
            "/_apis/runtime/runner",
            body: null,
            query: null,
            expectedStatus: 200,
            TestContext.Current.CancellationToken));

        Assert.Equal(statusCode, exception.StatusCode);
        Assert.Equal(domainError, exception.DomainError);
    }

    [Fact]
    public async Task EmptyAndPlainErrorBodiesKeepStatusWithoutUnboundedPreview()
    {
        var handler = CreateConnectedHandler((request, _) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/empty", StringComparison.Ordinal))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest));
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent("plain upstream error", Encoding.UTF8, "text/plain"),
            });
        });
        using var httpClient = new HttpClient(handler);
        using var client = CreateClient(httpClient);

        var empty = await Assert.ThrowsAsync<ScaleSetApiException>(() => client.SendActionsAsync(
            HttpMethod.Get,
            "/_apis/runtime/empty",
            body: null,
            query: null,
            expectedStatus: 200,
            TestContext.Current.CancellationToken));
        var plain = await Assert.ThrowsAsync<ScaleSetApiException>(() => client.SendActionsAsync(
            HttpMethod.Get,
            "/_apis/runtime/plain",
            body: null,
            query: null,
            expectedStatus: 200,
            TestContext.Current.CancellationToken));

        Assert.Equal((int)HttpStatusCode.BadRequest, empty.StatusCode);
        Assert.Null(empty.BodyPreview);
        Assert.Equal((int)HttpStatusCode.BadRequest, plain.StatusCode);
        Assert.Equal("plain upstream error", plain.BodyPreview);
    }

    [Fact]
    public async Task ErrorIncludesCorrelationAndSafeUriWithBoundedSecretFreePreview()
    {
        var body = "{\"message\":\"" + Pat + "\",\"token\":\"" + Pat + "\",\"padding\":\"" + new string('x', 5000) + "\"}";
        var handler = CreateConnectedHandler((_, _) =>
        {
            var response = TestResponses.Json(HttpStatusCode.InternalServerError, body);
            response.Headers.TryAddWithoutValidation("ActivityId", Pat);
            response.Headers.TryAddWithoutValidation("X-GitHub-Request-Id", "request-correlation");
            return Task.FromResult(response);
        });
        using var httpClient = new HttpClient(handler);
        using var client = CreateClient(httpClient);

        var exception = await Assert.ThrowsAsync<ScaleSetApiException>(() => client.SendActionsAsync(
            HttpMethod.Get,
            "/_apis/runtime/items?continuation=private-query-value",
            body: null,
            query: null,
            expectedStatus: 200,
            TestContext.Current.CancellationToken));

        Assert.Equal((int)HttpStatusCode.InternalServerError, exception.StatusCode);
        Assert.Equal("GET", exception.Method);
        Assert.Equal("[REDACTED]", exception.ActivityId);
        Assert.Equal("request-correlation", exception.GitHubRequestId);
        Assert.DoesNotContain("continuation=", exception.SafeUri, StringComparison.Ordinal);
        Assert.DoesNotContain(Pat, exception.BodyPreview, StringComparison.Ordinal);
        Assert.True(exception.BodyPreview!.Length <= 1024);
    }

    [Fact]
    public async Task MalformedTruncatedQuotedTokenFieldIsRedacted()
    {
        const string hiddenToken = "malformed-token-sentinel";
        var handler = CreateConnectedHandler((_, _) => Task.FromResult(TestResponses.Json(
            HttpStatusCode.BadGateway,
            "{\"token\":\"" + hiddenToken)));
        using var httpClient = new HttpClient(handler);
        using var client = CreateClient(httpClient);

        var exception = await Assert.ThrowsAsync<ScaleSetApiException>(() => client.SendActionsAsync(
            HttpMethod.Get,
            "/_apis/runtime/error",
            body: null,
            query: null,
            expectedStatus: 200,
            TestContext.Current.CancellationToken));

        Assert.DoesNotContain(hiddenToken, exception.BodyPreview, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", exception.BodyPreview, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ErrorPreviewRedactsUnknownQueueUriUserInfoAndSignedQuery()
    {
        const string userInfo = "unknown-userinfo-sentinel";
        const string signature = "unknown-signed-query-sentinel";
        var handler = CreateConnectedHandler((_, _) => Task.FromResult(TestResponses.Json(
            HttpStatusCode.BadGateway,
            System.Text.Json.JsonSerializer.Serialize(new
            {
                message = $"queue URL https://{userInfo}@queue.example/poll?sig={signature}",
                messageQueueUrl = $"https://queue.example/poll?sig={signature}",
            }))));
        using var httpClient = new HttpClient(handler);
        using var client = CreateClient(httpClient);

        var exception = await Assert.ThrowsAsync<ScaleSetApiException>(() => client.SendActionsAsync(
            HttpMethod.Get,
            "/_apis/runtime/queue-error",
            body: null,
            query: null,
            expectedStatus: 200,
            TestContext.Current.CancellationToken));

        Assert.DoesNotContain(userInfo, exception.BodyPreview, StringComparison.Ordinal);
        Assert.DoesNotContain(signature, exception.BodyPreview, StringComparison.Ordinal);
        Assert.DoesNotContain("queue.example", exception.BodyPreview, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnsafeRegistrationTokenIsRejectedWithSafeResponseMetadata()
    {
        const string unsafeToken = "registration-token\r\nInjected: true";
        var handler = new RecordingHttpMessageHandler((request, _) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/runners/registration-token", StringComparison.Ordinal))
            {
                return Task.FromResult(TestResponses.Json(
                    HttpStatusCode.Created,
                    System.Text.Json.JsonSerializer.Serialize(new { token = unsafeToken })));
            }

            return Task.FromResult(TestResponses.Json(HttpStatusCode.OK, "unexpected"));
        });
        using var httpClient = new HttpClient(handler);
        using var client = CreateClient(httpClient);

        var exception = await Assert.ThrowsAsync<ScaleSetApiException>(() => client.SendActionsAsync(
            HttpMethod.Get,
            "/_apis/runtime/never-reached",
            body: null,
            query: null,
            expectedStatus: 200,
            TestContext.Current.CancellationToken));

        Assert.Equal((int)HttpStatusCode.Created, exception.StatusCode);
        Assert.DoesNotContain(unsafeToken, exception.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("Injected", exception.BodyPreview, StringComparison.Ordinal);
        Assert.DoesNotContain(handler.Requests, request => request.Uri.Host == "actions.example");
    }

    [Fact]
    public async Task UnsafeDiscoveredAdminTokenIsRejectedWithoutExposingItsValue()
    {
        var unsafeToken = TestResponses.CreateAdminToken(DateTimeOffset.UtcNow.AddMinutes(10)) + "\r\nInjected: true";
        var handler = new RecordingHttpMessageHandler((request, _) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/runners/registration-token", StringComparison.Ordinal))
            {
                return Task.FromResult(TestResponses.Json(HttpStatusCode.Created, "{\"token\":\"registration-token\"}"));
            }

            if (path == "/actions/runner-registration")
            {
                return Task.FromResult(TestResponses.Json(HttpStatusCode.OK, System.Text.Json.JsonSerializer.Serialize(new
                {
                    url = "https://actions.example/tenant",
                    token = unsafeToken,
                })));
            }

            return Task.FromResult(TestResponses.Json(HttpStatusCode.OK, "unexpected"));
        });
        using var httpClient = new HttpClient(handler);
        using var client = CreateClient(httpClient);

        var exception = await Assert.ThrowsAsync<ScaleSetApiException>(() => client.SendActionsAsync(
            HttpMethod.Get,
            "/_apis/runtime/never-reached",
            body: null,
            query: null,
            expectedStatus: 200,
            TestContext.Current.CancellationToken));

        Assert.Equal((int)HttpStatusCode.OK, exception.StatusCode);
        Assert.DoesNotContain("Injected", exception.BodyPreview, StringComparison.Ordinal);
        Assert.DoesNotContain(unsafeToken, exception.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(handler.Requests, request => request.Uri.Host == "actions.example");
    }

    [Fact]
    public async Task QueueRejectsUnsafeBearerBeforeSendingRequest()
    {
        var handler = new RecordingHttpMessageHandler((_, _) =>
            Task.FromResult(TestResponses.Json(HttpStatusCode.OK, "unexpected")));
        using var httpClient = new HttpClient(handler);
        using var client = CreateClient(httpClient);

        await Assert.ThrowsAsync<ArgumentException>(() => client.SendQueueAsync(
            HttpMethod.Get,
            new Uri("https://queue.example/poll"),
            "queue-token\r\nInjected: true",
            body: null,
            headers: null,
            allowAccepted: true,
            TestContext.Current.CancellationToken));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task MalformedResponseDecodeKeepsStatusAndCorrelationWithoutInnerParserDetails()
    {
        var handler = CreateConnectedHandler((_, _) =>
        {
            var response = TestResponses.Json(HttpStatusCode.OK, "{bad-json");
            response.Headers.TryAddWithoutValidation("ActivityId", "activity-123");
            response.Headers.TryAddWithoutValidation("X-GitHub-Request-Id", "request-456");
            return Task.FromResult(response);
        });
        using var httpClient = new HttpClient(handler);
        using var client = CreateClient(httpClient);

        var exception = await Assert.ThrowsAsync<ScaleSetApiException>(() => client.SendActionsAsync<byte[]>(
            HttpMethod.Get,
            "/_apis/runtime/decode",
            body: null,
            query: null,
            expectedStatus: 200,
            TestContext.Current.CancellationToken,
            _ => throw new System.Text.Json.JsonException("raw parser detail")));

        Assert.Equal((int)HttpStatusCode.OK, exception.StatusCode);
        Assert.Equal("GET", exception.Method);
        Assert.Equal("activity-123", exception.ActivityId);
        Assert.Equal("request-456", exception.GitHubRequestId);
        Assert.Null(exception.InnerException);
        Assert.DoesNotContain("raw parser detail", exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task CallerCancellationRemainsCancellation()
    {
        var enteredAction = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = CreateConnectedHandler(async (request, token) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/warmup", StringComparison.Ordinal))
            {
                return TestResponses.Json(HttpStatusCode.OK, "ready");
            }

            enteredAction.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return TestResponses.Json(HttpStatusCode.OK, "unreachable");
        });
        using var httpClient = new HttpClient(handler);
        using var client = CreateClient(httpClient);
        await client.SendActionsAsync(
            HttpMethod.Get,
            "/_apis/runtime/warmup",
            body: null,
            query: null,
            expectedStatus: 200,
            TestContext.Current.CancellationToken);
        using var cancellation = new CancellationTokenSource();

        var pending = client.SendActionsAsync(
            HttpMethod.Get,
            "/_apis/runtime/cancel",
            body: null,
            query: null,
            expectedStatus: 200,
            cancellation.Token);
        await enteredAction.Task;
        cancellation.Cancel();

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);

        Assert.IsNotType<ScaleSetApiException>(exception);
    }

    [Fact]
    public async Task ControlRequestTimeoutUsesConfiguredTimerBudget()
    {
        var enteredAction = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = CreateConnectedHandler(async (_, token) =>
        {
            enteredAction.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return TestResponses.Json(HttpStatusCode.OK, "unreachable");
        });
        using var httpClient = new HttpClient(handler);
        using var client = CreateClient(httpClient, new ScaleSetClientOptions
        {
            ControlRequestTimeout = TimeSpan.FromMilliseconds(25),
            PollRequestTimeout = TimeSpan.FromSeconds(51),
            MaxReadOnlyRetries = 0,
        });

        var stopwatch = Stopwatch.StartNew();
        var exception = await Assert.ThrowsAsync<ScaleSetApiException>(() => client.SendActionsAsync(
            HttpMethod.Get,
            "/_apis/runtime/timeout",
            body: null,
            query: null,
            expectedStatus: 200,
            TestContext.Current.CancellationToken));
        stopwatch.Stop();

        Assert.Null(exception.StatusCode);
        Assert.Equal("GET", exception.Method);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void OwnedClientHandlerDisablesRedirects()
    {
        using var client = new ScaleSetClient(Target, ScaleSetCredentials.FromPersonalAccessToken(Pat));
        var handlerField = typeof(ScaleSetClient).GetField("_ownedHandler", BindingFlags.Instance | BindingFlags.NonPublic);
        var handler = Assert.IsType<HttpClientHandler>(handlerField!.GetValue(client));

        Assert.False(handler.AllowAutoRedirect);
    }

    [Fact]
    public async Task CallerHttpClientRemainsUsableAfterClientDisposal()
    {
        var handler = new RecordingHttpMessageHandler((_, _) =>
            Task.FromResult(TestResponses.Json(HttpStatusCode.OK, "caller still owns this")));
        using var httpClient = new HttpClient(handler);
        var client = CreateClient(httpClient);

        client.Dispose();
        using var response = await httpClient.GetAsync("https://example.test/health", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task RequestAndResponseContentAreDisposedAfterSuccess()
    {
        var responseStream = new DisposalTrackingStream(Encoding.UTF8.GetBytes("ok"));
        var handler = CreateConnectedHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(responseStream),
        }));
        using var httpClient = new HttpClient(handler);
        using var client = CreateClient(httpClient);

        var result = await client.SendActionsAsync(
            HttpMethod.Get,
            "/_apis/runtime/dispose",
            body: null,
            query: null,
            expectedStatus: 200,
            TestContext.Current.CancellationToken);

        Assert.Equal("ok", Encoding.UTF8.GetString(result));
        Assert.True(responseStream.IsDisposed);
    }

    [Fact]
    public async Task ResponseReadFailuresAreWrappedAndResponseStreamIsDisposed()
    {
        var responseStream = new ThrowingReadStream();
        var handler = CreateConnectedHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            Content = new StreamContent(responseStream),
        }));
        using var httpClient = new HttpClient(handler);
        using var client = CreateClient(httpClient);

        var exception = await Assert.ThrowsAsync<ScaleSetApiException>(() => client.SendActionsAsync(
            HttpMethod.Get,
            "/_apis/runtime/bad-error-body",
            body: null,
            query: null,
            expectedStatus: 200,
            TestContext.Current.CancellationToken));

        Assert.Equal((int)HttpStatusCode.InternalServerError, exception.StatusCode);
        Assert.Equal("GET", exception.Method);
        Assert.Null(exception.InnerException);
        Assert.True(responseStream.IsDisposed);
    }

    [Fact]
    public async Task SuccessResponseReadFailuresAreWrappedAndResponseStreamIsDisposed()
    {
        var responseStream = new ThrowingReadStream();
        var handler = CreateConnectedHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(responseStream),
        }));
        using var httpClient = new HttpClient(handler);
        using var client = CreateClient(httpClient);

        var exception = await Assert.ThrowsAsync<ScaleSetApiException>(() => client.SendActionsAsync(
            HttpMethod.Get,
            "/_apis/runtime/bad-success-body",
            body: null,
            query: null,
            expectedStatus: 200,
            TestContext.Current.CancellationToken));

        Assert.Equal((int)HttpStatusCode.OK, exception.StatusCode);
        Assert.Equal("GET", exception.Method);
        Assert.Null(exception.InnerException);
        Assert.True(responseStream.IsDisposed);
    }

    [Fact]
    public async Task RequestContentIsDisposedAfterSendCompletes()
    {
        HttpContent? actionContent = null;
        var handler = new RecordingHttpMessageHandler((request, _) =>
        {
            if (request.RequestUri!.Host == "actions.example")
            {
                actionContent = request.Content;
            }

            var path = request.RequestUri.AbsolutePath;
            if (path.EndsWith("/runners/registration-token", StringComparison.Ordinal))
            {
                return Task.FromResult(TestResponses.Json(HttpStatusCode.Created, "{\"token\":\"registration-token\"}"));
            }

            if (path == "/actions/runner-registration")
            {
                return Task.FromResult(TestResponses.Json(
                    HttpStatusCode.OK,
                    CreateDiscoveryResponse("https://actions.example/tenant", DateTimeOffset.UtcNow.AddMinutes(10))));
            }

            return Task.FromResult(TestResponses.Json(HttpStatusCode.OK, "ok"));
        });
        using var httpClient = new HttpClient(handler);
        using var client = CreateClient(httpClient);

        await client.SendActionsAsync(
            HttpMethod.Post,
            "/_apis/runtime/request-content",
            Encoding.UTF8.GetBytes("{}"),
            query: null,
            expectedStatus: 200,
            TestContext.Current.CancellationToken);

        Assert.NotNull(actionContent);
        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            actionContent!.ReadAsByteArrayAsync(TestContext.Current.CancellationToken));
    }

    private static ScaleSetClient CreateClient(
        HttpClient httpClient,
        ScaleSetClientOptions? options = null,
        TimeProvider? timeProvider = null) =>
        new(Target, ScaleSetCredentials.FromPersonalAccessToken(Pat), options, httpClient, timeProvider: timeProvider);

    private static RecordingHttpMessageHandler CreateConnectedHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> actionsHandler,
        string serviceUrl = "https://actions.example/tenant") =>
        new(async (request, cancellationToken) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/runners/registration-token", StringComparison.Ordinal))
            {
                return TestResponses.Json(HttpStatusCode.Created, "{\"token\":\"registration-token\"}");
            }

            if (path == "/actions/runner-registration")
            {
                return TestResponses.Json(HttpStatusCode.OK, CreateDiscoveryResponse(serviceUrl, DateTimeOffset.UtcNow.AddMinutes(10)));
            }

            return await actionsHandler(request, cancellationToken).ConfigureAwait(false);
        });

    private static string CreateDiscoveryResponse(string serviceUrl, DateTimeOffset expiresAt) =>
        System.Text.Json.JsonSerializer.Serialize(new
        {
            url = serviceUrl,
            token = TestResponses.CreateAdminToken(expiresAt),
        });

    private static Dictionary<string, string> ParseQuery(string query)
    {
        return query.TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Split('=', 2))
            .ToDictionary(
                part => WebUtility.UrlDecode(part[0])!,
                part => WebUtility.UrlDecode(part.Length > 1 ? part[1] : string.Empty)!,
                StringComparer.Ordinal);
    }

    private sealed class DisposalTrackingStream(byte[] bytes) : MemoryStream(bytes)
    {
        internal bool IsDisposed { get; private set; }

        protected override void Dispose(bool disposing)
        {
            IsDisposed = true;
            base.Dispose(disposing);
        }
    }

    private sealed class ThrowingReadStream : MemoryStream
    {
        internal bool IsDisposed { get; private set; }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            throw new IOException("simulated stream read failure");
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            throw new IOException("simulated stream read failure");
        }

        protected override void Dispose(bool disposing)
        {
            IsDisposed = true;
            base.Dispose(disposing);
        }
    }
}
