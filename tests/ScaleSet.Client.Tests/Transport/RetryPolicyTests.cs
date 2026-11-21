using System.Net;
using System.Security.Authentication;
using System.Text;
using ScaleSet.Client;
using Xunit;

namespace ScaleSet.Client.Tests.Transport;

public sealed class RetryPolicyTests
{
    private static readonly GitHubTarget Target = GitHubTarget.Parse("https://github.com/example-org/example-repo");
    private static readonly DateTimeOffset BaseNow = new(2026, 11, 21, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task MaxElapsedSkipsWaitThatWouldExceedBudgetAndPreservesLastRateLimitException()
    {
        VirtualTimeProvider clock = new(BaseNow);
        int calls = 0;
        using HttpClient httpClient = new(CreateConnectedHandler((_, _) =>
        {
            calls++;
            HttpResponseMessage response = TestResponses.Json(HttpStatusCode.TooManyRequests, "{}");
            response.Headers.TryAddWithoutValidation("Retry-After", "12");
            return Task.FromResult(response);
        }));
        using ScaleSetClient client = CreateClient(
            httpClient,
            new ScaleSetRetryOptions
            {
                MaxRetries = 4,
                WaitMin = TimeSpan.FromSeconds(1),
                WaitMax = TimeSpan.FromSeconds(30),
                MaxElapsed = TimeSpan.FromSeconds(10),
                MaxRetryAfterDelay = null,
            },
            clock);

        ScaleSetRateLimitException exception = await Assert.ThrowsAsync<ScaleSetRateLimitException>(
            () => client.GetScaleSetAsync(1, TestContext.Current.CancellationToken));

        Assert.Equal(1, calls);
        Assert.Equal(TimeSpan.FromSeconds(12), exception.RetryAfter);
        Assert.Equal(429, exception.StatusCode);
    }

    [Fact]
    public async Task MaxElapsedThrowsMaxElapsedExceededExceptionWhenBudgetExpiresMidAttempt()
    {
        VirtualTimeProvider clock = new(BaseNow);
        TaskCompletionSource attemptStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using HttpClient httpClient = new(CreateConnectedHandler(async (_, cancellationToken) =>
        {
            attemptStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return TestResponses.Json(HttpStatusCode.OK, "{}");
        }));
        using ScaleSetClient client = CreateClient(
            httpClient,
            new ScaleSetRetryOptions
            {
                MaxRetries = 2,
                MaxElapsed = TimeSpan.FromSeconds(5),
            },
            clock);

        Task<RunnerScaleSet?> pending = client.GetScaleSetAsync(1, TestContext.Current.CancellationToken);
        await attemptStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        _ = await clock.WaitForActiveTimerAsync(delay => delay == TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        clock.Advance(TimeSpan.FromSeconds(5));

        ScaleSetMaxElapsedExceededException exception = await Assert.ThrowsAsync<ScaleSetMaxElapsedExceededException>(() => pending);
        Assert.Equal(TimeSpan.FromSeconds(5), exception.MaxElapsed);
        Assert.Null(exception.Response);
    }

    [Fact]
    public async Task MaxElapsedThrowsWithPartialSnapshotWhenBudgetExpiresMidBodyRead()
    {
        VirtualTimeProvider clock = new(BaseNow);
        byte[] prefix = Encoding.UTF8.GetBytes("{\"id\":42");
        TaskCompletionSource stalled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using HttpClient httpClient = new(CreateConnectedHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new PartialReadStream(prefix, stalled)),
        })));
        using ScaleSetClient client = CreateClient(
            httpClient,
            new ScaleSetRetryOptions
            {
                MaxRetries = 2,
                MaxElapsed = TimeSpan.FromSeconds(4),
            },
            clock);

        Task<RunnerScaleSet?> pending = client.GetScaleSetAsync(42, TestContext.Current.CancellationToken);
        await stalled.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        _ = await clock.WaitForActiveTimerAsync(delay => delay == TimeSpan.FromSeconds(4), TestContext.Current.CancellationToken);
        clock.Advance(TimeSpan.FromSeconds(4));

        ScaleSetMaxElapsedExceededException exception = await Assert.ThrowsAsync<ScaleSetMaxElapsedExceededException>(() => pending);
        Assert.Equal(200, exception.StatusCode);
        Assert.Equal(TimeSpan.FromSeconds(4), exception.MaxElapsed);
        Assert.NotNull(exception.Response);
        Assert.True(exception.Response.IsBodyIncomplete);
        Assert.Equal(prefix, exception.Response.Body.ToArray());
    }

    [Fact]
    public async Task CallerCancellationRemainsOperationCanceledEvenWhenMaxElapsedIsConfigured()
    {
        VirtualTimeProvider clock = new(BaseNow);
        TaskCompletionSource attemptStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using HttpClient httpClient = new(CreateConnectedHandler(async (_, cancellationToken) =>
        {
            attemptStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return TestResponses.Json(HttpStatusCode.OK, "{}");
        }));
        using ScaleSetClient client = CreateClient(
            httpClient,
            new ScaleSetRetryOptions
            {
                MaxRetries = 2,
                MaxElapsed = TimeSpan.FromSeconds(10),
            },
            clock);
        using CancellationTokenSource callerCts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);

        Task<RunnerScaleSet?> pending = client.GetScaleSetAsync(1, callerCts.Token);
        await attemptStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        callerCts.Cancel();

        OperationCanceledException canceled = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.IsNotType<ScaleSetMaxElapsedExceededException>(canceled);
    }

    [Fact]
    public async Task DeadlineSkipsWaitThatWouldReachDeadlineAndPreservesLastResponse()
    {
        VirtualTimeProvider clock = new(BaseNow);
        int calls = 0;
        using HttpClient httpClient = new(CreateConnectedHandler((_, _) =>
        {
            calls++;
            HttpResponseMessage response = TestResponses.Json(HttpStatusCode.ServiceUnavailable, "{\"message\":\"busy\"}");
            response.Headers.TryAddWithoutValidation("Retry-After", "10");
            return Task.FromResult(response);
        }));
        using ScaleSetClient client = CreateClient(
            httpClient,
            new ScaleSetRetryOptions
            {
                MaxRetries = 4,
                Deadline = BaseNow.AddSeconds(10),
            },
            clock);

        ScaleSetApiException exception = await Assert.ThrowsAsync<ScaleSetApiException>(
            () => client.GetScaleSetAsync(1, TestContext.Current.CancellationToken));

        Assert.Equal(1, calls);
        Assert.Equal(503, exception.StatusCode);
    }

    [Fact]
    public async Task ScaleSetRequestRetryScopesPerCallSupportsNestingAndCoversMessageSessions()
    {
        VirtualTimeProvider clock = new(BaseNow);
        int calls = 0;
        using HttpClient httpClient = new(new RecordingHttpMessageHandler((request, _) =>
        {
            string path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/runners/registration-token", StringComparison.Ordinal))
            {
                return Task.FromResult(TestResponses.Json(HttpStatusCode.Created, "{\"token\":\"registration-token\"}"));
            }

            if (path == "/actions/runner-registration")
            {
                string token = TestResponses.CreateAdminToken(BaseNow.AddMinutes(10));
                return Task.FromResult(TestResponses.Json(HttpStatusCode.OK, $"{{\"url\":\"https://actions.example/tenant\",\"token\":\"{token}\"}}"));
            }

            if (path.EndsWith("/sessions", StringComparison.Ordinal))
            {
                return Task.FromResult(TestResponses.Json(
                    HttpStatusCode.OK,
                    "{\"sessionId\":\"2c581b9c-5c84-44d0-85bd-d6d780de6124\",\"ownerName\":\"host\",\"messageQueueUrl\":\"https://queue.example/poll\",\"messageQueueAccessToken\":\"qtok\"}"));
            }

            calls++;
            if (path.EndsWith("/acquirejobs", StringComparison.Ordinal))
            {
                return Task.FromResult(TestResponses.Json(HttpStatusCode.TooManyRequests, "{}"));
            }

            return Task.FromResult(calls <= 2
                ? TestResponses.Json(HttpStatusCode.ServiceUnavailable, "retry")
                : TestResponses.Json(HttpStatusCode.OK, "{\"id\":7}"));
        }));
        using ScaleSetClient client = CreateClient(
            httpClient,
            new ScaleSetRetryOptions
            {
                MaxRetries = 3,
                WaitMin = TimeSpan.Zero,
                WaitMax = TimeSpan.Zero,
            },
            clock);

        using (ScaleSetRequestRetry.Configure(r => r with { MaxRetries = 0 }))
        {
            ScaleSetApiException fastFail = await Assert.ThrowsAsync<ScaleSetApiException>(
                () => client.GetScaleSetAsync(7, TestContext.Current.CancellationToken));
            Assert.Equal(503, fastFail.StatusCode);
            Assert.Equal(1, calls);

            using (ScaleSetRequestRetry.Configure(r => r with { MaxRetries = 2 }))
            {
                RunnerScaleSet? recovered = await client.GetScaleSetAsync(7, TestContext.Current.CancellationToken);
                Assert.Equal(7, recovered?.Id);
                Assert.Equal(3, calls);
            }

            // Verify outer scope was restored on inner dispose while client's own policy remained untouched.
            calls = 0;
            await Assert.ThrowsAsync<ScaleSetApiException>(
                () => client.GetScaleSetAsync(7, TestContext.Current.CancellationToken));
            Assert.Equal(1, calls);
        }

        // Verify per-call override works on MessageSessionClient without mutating shared client options.
        MessageSessionClient session = await client.CreateMessageSessionAsync(7, "host", TestContext.Current.CancellationToken);
        int acquireBefore = calls;
        using (ScaleSetRequestRetry.Configure(r => r with
        {
            OnRateLimit = (_, _) => ValueTask.FromResult(ScaleSetRateLimitDecision.RetryWithBackoff),
            MaxRetries = 1,
        }))
        {
            await Assert.ThrowsAsync<ScaleSetRateLimitException>(
                () => session.AcquireJobsAsync([100], TestContext.Current.CancellationToken));
            Assert.Equal(acquireBefore + 2, calls);
        }
    }

    [Fact]
    public async Task DisabledPolicyPreventsRegistrationDiscoveryAuthRetries()
    {
        int discoveryCalls = 0;
        using HttpClient httpClient = new(new RecordingHttpMessageHandler((request, _) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/runners/registration-token", StringComparison.Ordinal))
            {
                return Task.FromResult(TestResponses.Json(HttpStatusCode.Created, "{\"token\":\"registration-token\"}"));
            }

            discoveryCalls++;
            return Task.FromResult(TestResponses.Json(HttpStatusCode.Unauthorized, "unauthorized"));
        }));
        using ScaleSetClient client = CreateClient(httpClient, ScaleSetRetryOptions.Disabled);

        ScaleSetApiException exception = await Assert.ThrowsAsync<ScaleSetApiException>(
            () => client.GetScaleSetAsync(1, TestContext.Current.CancellationToken));

        Assert.Equal(401, exception.StatusCode);
        Assert.Equal(1, discoveryCalls);
    }

    [Fact]
    public async Task NotImplemented501AndCertificateErrorsAreNeverRetried()
    {
        int notImplementedCalls = 0;
        using HttpClient notImplementedHttp = new(CreateConnectedHandler((_, _) =>
        {
            notImplementedCalls++;
            return Task.FromResult(TestResponses.Json(HttpStatusCode.NotImplemented, "not implemented"));
        }));
        using ScaleSetClient notImplementedClient = CreateClient(
            notImplementedHttp,
            ScaleSetRetryOptions.CreateUpstreamDefault() with { WaitMin = TimeSpan.Zero, WaitMax = TimeSpan.Zero });

        ScaleSetApiException notImplEx = await Assert.ThrowsAsync<ScaleSetApiException>(
            () => notImplementedClient.GetScaleSetAsync(1, TestContext.Current.CancellationToken));
        Assert.Equal(501, notImplEx.StatusCode);
        Assert.Equal(1, notImplementedCalls);

        int secureConnCalls = 0;
        using HttpClient secureConnHttp = new(CreateConnectedHandler((_, _) =>
        {
            secureConnCalls++;
            return Task.FromException<HttpResponseMessage>(
                new HttpRequestException(HttpRequestError.SecureConnectionError, "TLS verification failed"));
        }));
        using ScaleSetClient secureConnClient = CreateClient(
            secureConnHttp,
            ScaleSetRetryOptions.CreateUpstreamDefault() with { WaitMin = TimeSpan.Zero, WaitMax = TimeSpan.Zero });

        await Assert.ThrowsAsync<ScaleSetApiException>(
            () => secureConnClient.GetScaleSetAsync(1, TestContext.Current.CancellationToken));
        Assert.Equal(1, secureConnCalls);

        int authExCalls = 0;
        using HttpClient authExHttp = new(CreateConnectedHandler((_, _) =>
        {
            authExCalls++;
            return Task.FromException<HttpResponseMessage>(
                new HttpRequestException("TLS failed", new AuthenticationException("untrusted root")));
        }));
        using ScaleSetClient authExClient = CreateClient(
            authExHttp,
            ScaleSetRetryOptions.CreateUpstreamDefault() with { WaitMin = TimeSpan.Zero, WaitMax = TimeSpan.Zero });

        await Assert.ThrowsAsync<ScaleSetApiException>(
            () => authExClient.GetScaleSetAsync(1, TestContext.Current.CancellationToken));
        Assert.Equal(1, authExCalls);
    }

    [Fact]
    public async Task UpstreamDefaultPolicyRetriesReplayablePostRequests()
    {
        int calls = 0;
        using HttpClient httpClient = new(CreateConnectedHandler((_, _) =>
        {
            calls++;
            return Task.FromResult(calls == 1
                ? TestResponses.Json(HttpStatusCode.ServiceUnavailable, "retry post")
                : TestResponses.Json(HttpStatusCode.OK, "{\"id\":77,\"name\":\"linux\"}"));
        }));
        using ScaleSetClient client = CreateClient(
            httpClient,
            ScaleSetRetryOptions.CreateUpstreamDefault() with { WaitMin = TimeSpan.Zero, WaitMax = TimeSpan.Zero });

        RunnerScaleSet created = await client.CreateScaleSetAsync(
            new RunnerScaleSet { Name = "linux", RunnerGroupId = 1 },
            TestContext.Current.CancellationToken);

        Assert.Equal(77, created.Id);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task ConcurrentCallsWithDifferentRequestRetryScopesRemainIsolated()
    {
        int fastCalls = 0;
        int retryCalls = 0;
        TaskCompletionSource bothEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int entered = 0;
        using HttpClient httpClient = new(CreateConnectedHandler(async (request, _) =>
        {
            string path = request.RequestUri!.AbsolutePath;
            if (Interlocked.Increment(ref entered) == 2)
            {
                bothEntered.TrySetResult();
            }

            await bothEntered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

            if (path.EndsWith("/1", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref fastCalls);
                return TestResponses.Json(HttpStatusCode.ServiceUnavailable, "fast");
            }

            int attempt = Interlocked.Increment(ref retryCalls);
            return attempt == 1
                ? TestResponses.Json(HttpStatusCode.ServiceUnavailable, "retry once")
                : TestResponses.Json(HttpStatusCode.OK, "{\"id\":2}");
        }));
        using ScaleSetClient client = CreateClient(
            httpClient,
            new ScaleSetRetryOptions
            {
                MaxRetries = 3,
                WaitMin = TimeSpan.Zero,
                WaitMax = TimeSpan.Zero,
            });

        Task<ScaleSetApiException> fastTask = Task.Run(async () =>
        {
            using (ScaleSetRequestRetry.Configure(r => r with { MaxRetries = 0 }))
            {
                return await Assert.ThrowsAsync<ScaleSetApiException>(
                    () => client.GetScaleSetAsync(1, TestContext.Current.CancellationToken));
            }
        });
        Task<RunnerScaleSet?> retryTask = Task.Run(async () =>
        {
            using (ScaleSetRequestRetry.Configure(r => r with { MaxRetries = 2 }))
            {
                return await client.GetScaleSetAsync(2, TestContext.Current.CancellationToken);
            }
        });

        await Task.WhenAll(fastTask, retryTask);
        Assert.Equal(503, (await fastTask).StatusCode);
        Assert.Equal(2, (await retryTask)?.Id);
        Assert.Equal(1, Volatile.Read(ref fastCalls));
        Assert.Equal(2, Volatile.Read(ref retryCalls));
    }

    [Fact]
    public async Task MaxElapsedAndDeadlineCancelAsyncShouldRetryAndOnRateLimitHooks()
    {
        VirtualTimeProvider clock = new(BaseNow);
        TaskCompletionSource shouldRetryEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using HttpClient shouldRetryHttp = new(CreateConnectedHandler((_, _) =>
            Task.FromResult(TestResponses.Json(HttpStatusCode.ServiceUnavailable, "slow hook"))));
        using ScaleSetClient shouldRetryClient = CreateClient(
            shouldRetryHttp,
            new ScaleSetRetryOptions
            {
                MaxRetries = 2,
                MaxElapsed = TimeSpan.FromSeconds(5),
                ShouldRetry = async (_, token) =>
                {
                    shouldRetryEntered.TrySetResult();
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                    return true;
                },
            },
            clock);

        Task<ScaleSetMaxElapsedExceededException> shouldRetryTask = Assert.ThrowsAsync<ScaleSetMaxElapsedExceededException>(
            () => shouldRetryClient.GetScaleSetAsync(1, TestContext.Current.CancellationToken));
        await shouldRetryEntered.Task.WaitAsync(TestContext.Current.CancellationToken);
        clock.Advance(TimeSpan.FromSeconds(5));
        ScaleSetMaxElapsedExceededException shouldRetryEx = await shouldRetryTask;
        Assert.Equal(503, shouldRetryEx.StatusCode);
        Assert.NotNull(shouldRetryEx.Response);

        TaskCompletionSource onRateLimitMaxElapsedEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using HttpClient rateLimitMaxElapsedHttp = new(CreateConnectedHandler((_, _) =>
            Task.FromResult(TestResponses.Json(HttpStatusCode.TooManyRequests, "{}"))));
        using ScaleSetClient rateLimitMaxElapsedClient = CreateClient(
            rateLimitMaxElapsedHttp,
            new ScaleSetRetryOptions
            {
                MaxRetries = 2,
                MaxElapsed = TimeSpan.FromSeconds(5),
                OnRateLimit = async (_, token) =>
                {
                    onRateLimitMaxElapsedEntered.TrySetResult();
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                    return ScaleSetRateLimitDecision.RetryWithBackoff;
                },
            },
            clock);

        Task<ScaleSetMaxElapsedExceededException> rateLimitMaxElapsedTask = Assert.ThrowsAsync<ScaleSetMaxElapsedExceededException>(
            () => rateLimitMaxElapsedClient.GetScaleSetAsync(2, TestContext.Current.CancellationToken));
        await onRateLimitMaxElapsedEntered.Task.WaitAsync(TestContext.Current.CancellationToken);
        clock.Advance(TimeSpan.FromSeconds(5));
        ScaleSetMaxElapsedExceededException rateLimitMaxElapsedEx = await rateLimitMaxElapsedTask;
        Assert.Equal(429, rateLimitMaxElapsedEx.StatusCode);
        Assert.NotNull(rateLimitMaxElapsedEx.Response);

        TaskCompletionSource onRateLimitDeadlineEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using HttpClient rateLimitDeadlineHttp = new(CreateConnectedHandler((_, _) =>
            Task.FromResult(TestResponses.Json(HttpStatusCode.TooManyRequests, "{}"))));
        DateTimeOffset deadlineAt = clock.GetUtcNow().AddSeconds(3);
        using ScaleSetClient rateLimitDeadlineClient = CreateClient(
            rateLimitDeadlineHttp,
            new ScaleSetRetryOptions
            {
                MaxRetries = 2,
                Deadline = deadlineAt,
                OnRateLimit = async (_, token) =>
                {
                    onRateLimitDeadlineEntered.TrySetResult();
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                    return ScaleSetRateLimitDecision.RetryWithBackoff;
                },
            },
            clock);

        Task<OperationCanceledException> rateLimitDeadlineTask = Assert.ThrowsAsync<OperationCanceledException>(
            () => rateLimitDeadlineClient.GetScaleSetAsync(3, TestContext.Current.CancellationToken));
        await onRateLimitDeadlineEntered.Task.WaitAsync(TestContext.Current.CancellationToken);
        clock.Advance(TimeSpan.FromSeconds(3));
        OperationCanceledException deadlineEx = await rateLimitDeadlineTask;
        Assert.IsNotType<ScaleSetMaxElapsedExceededException>(deadlineEx);
        Assert.NotEqual(TestContext.Current.CancellationToken, deadlineEx.CancellationToken);

        TaskCompletionSource onRateLimitCallerEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using CancellationTokenSource callerCts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        using HttpClient rateLimitCallerHttp = new(CreateConnectedHandler((_, _) =>
            Task.FromResult(TestResponses.Json(HttpStatusCode.TooManyRequests, "{}"))));
        using ScaleSetClient rateLimitCallerClient = CreateClient(
            rateLimitCallerHttp,
            new ScaleSetRetryOptions
            {
                MaxRetries = 2,
                MaxElapsed = TimeSpan.FromSeconds(30),
                OnRateLimit = async (_, token) =>
                {
                    onRateLimitCallerEntered.TrySetResult();
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                    return ScaleSetRateLimitDecision.RetryWithBackoff;
                },
            },
            clock);

        Task<OperationCanceledException> rateLimitCallerTask = Assert.ThrowsAsync<OperationCanceledException>(
            () => rateLimitCallerClient.GetScaleSetAsync(4, callerCts.Token));
        await onRateLimitCallerEntered.Task.WaitAsync(TestContext.Current.CancellationToken);
        callerCts.Cancel();
        OperationCanceledException callerEx = await rateLimitCallerTask;
        Assert.Equal(callerCts.Token, callerEx.CancellationToken);
    }

    [Fact]
    public async Task ExplicitRetryPolicySharesMaxRetriesAcrossDiscoveryAuthAndTransientFailures()
    {
        ScaleSetRetryOptions policy = ScaleSetRetryOptions.CreateUpstreamDefault() with
        {
            MaxRetries = 1,
            WaitMin = TimeSpan.Zero,
            WaitMax = TimeSpan.Zero,
        };

        int authThenTransientCalls = 0;
        using HttpClient authThenTransientHttp = new(new RecordingHttpMessageHandler((request, _) =>
        {
            string path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/runners/registration-token", StringComparison.Ordinal))
            {
                return Task.FromResult(TestResponses.Json(HttpStatusCode.Created, "{\"token\":\"registration-token\"}"));
            }

            if (path == "/actions/runner-registration")
            {
                authThenTransientCalls++;
                return Task.FromResult(authThenTransientCalls == 1
                    ? TestResponses.Json(HttpStatusCode.Unauthorized, "{\"message\":\"stale registration\"}")
                    : TestResponses.Json(HttpStatusCode.ServiceUnavailable, "{\"message\":\"service unavailable\"}"));
            }

            return Task.FromResult(TestResponses.Json(HttpStatusCode.OK, "{}"));
        }));
        using ScaleSetClient authThenTransientClient = CreateClient(authThenTransientHttp, policy);

        ScaleSetApiException authThenTransientEx = await Assert.ThrowsAsync<ScaleSetApiException>(
            () => authThenTransientClient.GetScaleSetAsync(1, TestContext.Current.CancellationToken));
        Assert.Equal(503, authThenTransientEx.StatusCode);
        Assert.Equal(2, authThenTransientCalls);

        int transientThenAuthCalls = 0;
        using HttpClient transientThenAuthHttp = new(new RecordingHttpMessageHandler((request, _) =>
        {
            string path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/runners/registration-token", StringComparison.Ordinal))
            {
                return Task.FromResult(TestResponses.Json(HttpStatusCode.Created, "{\"token\":\"registration-token\"}"));
            }

            if (path == "/actions/runner-registration")
            {
                transientThenAuthCalls++;
                return Task.FromResult(transientThenAuthCalls == 1
                    ? TestResponses.Json(HttpStatusCode.ServiceUnavailable, "{\"message\":\"service unavailable\"}")
                    : TestResponses.Json(HttpStatusCode.Unauthorized, "{\"message\":\"stale registration\"}"));
            }

            return Task.FromResult(TestResponses.Json(HttpStatusCode.OK, "{}"));
        }));
        using ScaleSetClient transientThenAuthClient = CreateClient(transientThenAuthHttp, policy);

        ScaleSetApiException transientThenAuthEx = await Assert.ThrowsAsync<ScaleSetApiException>(
            () => transientThenAuthClient.GetScaleSetAsync(2, TestContext.Current.CancellationToken));
        Assert.Equal(401, transientThenAuthEx.StatusCode);
        Assert.Equal(2, transientThenAuthCalls);
    }

    private static ScaleSetClient CreateClient(
        HttpClient httpClient,
        ScaleSetRetryOptions retry,
        TimeProvider? timeProvider = null) =>
        new(
            Target,
            ScaleSetCredentials.FromPersonalAccessToken("synthetic-pat"),
            new ScaleSetClientOptions { Retry = retry },
            httpClient,
            timeProvider: timeProvider);

    private static RecordingHttpMessageHandler CreateConnectedHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> actionsHandler) =>
        TestResponses.CreateConnectedHandler(BaseNow.AddMinutes(10), actionsHandler);
}
