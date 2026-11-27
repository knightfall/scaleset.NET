using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using ScaleSet.Client.Tests.Transport;
using Xunit;

namespace ScaleSet.Client.Tests.Authentication;

public sealed class GitHubAppAuthenticationBudgetTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(10);

    [Theory]
    [InlineData("sign", "deadline", 1, 0, 0)]
    [InlineData("sign", "caller", 1, 0, 0)]
    [InlineData("mint headers", "deadline", 1, 1, 0)]
    [InlineData("mint headers", "caller", 1, 1, 0)]
    [InlineData("mint headers", "both", 1, 1, 0)]
    [InlineData("mint body", "deadline", 1, 1, 0)]
    [InlineData("protected", "deadline", 1, 1, 1)]
    [InlineData("protected", "caller", 1, 1, 1)]
    [InlineData("refresh sign", "deadline", 2, 1, 1)]
    [InlineData("refresh mint", "deadline", 2, 2, 1)]
    [InlineData("refresh mint", "caller", 2, 2, 1)]
    [InlineData("refresh mint", "elapsed", 2, 2, 1)]
    [InlineData("pat", "deadline", 0, 0, 1)]
    public async Task RegistrationPhasesPreserveCancellationAndTransportClassification(
        string phase, string cause, int expectedSigns, int expectedMints, int expectedProtectedPosts)
    {
        VirtualTimeProvider clock = new(Now);
        CancellationToken testToken = TestContext.Current.CancellationToken;
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(testToken);
        TaskCompletionSource blocked = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource resume = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var spans = new ConcurrentQueue<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == ScaleSetClientTelemetry.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activity => spans.Enqueue(activity),
        };
        ActivitySource.AddActivityListener(listener);
        using var trace = new Activity(nameof(RegistrationPhasesPreserveCancellationAndTransportClassification))
            .SetIdFormat(ActivityIdFormat.W3C).Start();

        var signer = new ScriptedSigner(async (number, token) =>
        {
            if ((phase == "sign" && number == 1) || (phase == "refresh sign" && number == 2))
            {
                await BlockAsync(token);
            }

            return "synthetic-jwt";
        });
        int mints = 0;
        int protectedPosts = 0;
        bool refresh = phase.StartsWith("refresh ", StringComparison.Ordinal);
        using var handler = new RecordingHttpMessageHandler(async (request, token) =>
        {
            string path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/access_tokens", StringComparison.Ordinal))
            {
                int number = Interlocked.Increment(ref mints);
                Activity.Current?.SetTag("test.phase", $"mint-{number}");
                if ((phase == "mint headers" && number == 1) || (phase == "refresh mint" && number == 2))
                {
                    await BlockAsync(token);
                }

                if (phase == "mint body")
                {
                    return new HttpResponseMessage(HttpStatusCode.Created)
                    {
                        Content = new StreamContent(new PartialReadStream("{"u8.ToArray(), blocked)),
                    };
                }

                return InstallationResponse(number, Now.AddHours(1));
            }

            Assert.EndsWith("/registration-token", path, StringComparison.Ordinal);
            int sent = Interlocked.Increment(ref protectedPosts);
            Activity.Current?.SetTag("test.phase", "registration");
            Assert.Equal(phase == "pat" ? "synthetic-pat" : $"installation-{sent}", request.Headers.Authorization?.Parameter);
            if (refresh)
            {
                if (cause == "elapsed")
                {
                    // Spend time in the original HTTP request before the replacement mint starts.
                    clock.Advance(TimeSpan.FromSeconds(3));
                }
                return TestResponses.Json(HttpStatusCode.Unauthorized, "{}");
            }

            await BlockAsync(token);
            throw new InvalidOperationException("The test never completes a protected request.");
        });
        using var http = new HttpClient(handler);
        using var client = CreateClient(http, signer, clock, new ScaleSetRetryOptions
        {
            Deadline = Now + (cause == "elapsed" ? TimeSpan.FromMinutes(1) : Budget),
            MaxElapsed = cause == "elapsed" ? Budget : null,
            MaxRetries = 1,
            WaitMin = TimeSpan.Zero,
            WaitMax = TimeSpan.Zero,
        }, usePat: phase == "pat");

        Task<RunnerScaleSet?> operation = client.GetScaleSetAsync(7, caller.Token);
        await blocked.Task.WaitAsync(TimeSpan.FromSeconds(5), testToken);
        if (cause == "elapsed")
        {
            clock.Advance(Budget - TimeSpan.FromSeconds(3));
            ScaleSetMaxElapsedExceededException elapsed = await Assert.ThrowsAsync<ScaleSetMaxElapsedExceededException>(() => operation);
            Assert.Equal(Budget, elapsed.MaxElapsed);
            Assert.EndsWith("/registration-token", elapsed.SafeUri, StringComparison.Ordinal);
        }
        else
        {
            if (cause is "deadline" or "both")
            {
                clock.Advance(Budget);
            }
            if (cause is "caller" or "both")
            {
                await caller.CancelAsync();
            }
            resume.TrySetResult();
            var cancellation = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
            if (cause == "deadline")
            {
                Assert.Contains("deadline", cancellation.Message, StringComparison.OrdinalIgnoreCase);
                Assert.True(cancellation.CancellationToken.IsCancellationRequested);
                Assert.NotEqual(caller.Token, cancellation.CancellationToken);
            }
            else
            {
                Assert.Equal(caller.Token, cancellation.CancellationToken);
            }
        }

        Assert.Equal(expectedSigns, signer.Calls);
        Assert.Equal(expectedMints, mints);
        Assert.Equal(expectedProtectedPosts, protectedPosts);
        Assert.Equal(expectedMints + expectedProtectedPosts, handler.Requests.Count);
        Activity[] requestSpans = spans.Where(activity => activity.TraceId == trace.TraceId).ToArray();
        Assert.Equal(handler.Requests.Count, requestSpans.Length);
        string? interruptedPhase = phase switch
        {
            "sign" or "refresh sign" => null,
            "mint headers" or "mint body" => "mint-1",
            "refresh mint" => "mint-2",
            _ => "registration",
        };
        if (interruptedPhase is not null)
        {
            Activity interrupted = Assert.Single(requestSpans, activity => Equals(activity.GetTagItem("test.phase"), interruptedPhase));
            Assert.Equal(ActivityStatusCode.Error, interrupted.Status);
            Assert.Equal(cause switch
            {
                "deadline" => "deadline_exceeded",
                "elapsed" => "max_elapsed_exceeded",
                _ => "caller_canceled",
            }, interrupted.GetTagItem("error.type"));
        }
        else
        {
            Assert.All(requestSpans, activity => Assert.Null(activity.GetTagItem("error.type")));
        }

        async Task BlockAsync(CancellationToken token)
        {
            blocked.SetResult();
            if (cause == "both")
            {
                await resume.Task.WaitAsync(testToken);
                token.ThrowIfCancellationRequested();
            }
            else
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InstallationCacheGateWaitHonorsEachCallersBudgetAndDoesNotDisturbItsOwner(bool cancelCaller)
    {
        VirtualTimeProvider clock = new(Now);
        CancellationToken testToken = TestContext.Current.CancellationToken;
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(testToken);
        TaskCompletionSource ownerEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource releaseOwner = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var signer = new ScriptedSigner(async (number, token) =>
        {
            if (number == 2)
            {
                ownerEntered.SetResult();
                await releaseOwner.Task.WaitAsync(token);
            }

            return "synthetic-jwt";
        });
        int mints = 0;
        int protectedReads = 0;
        using var handler = TestResponses.CreateConnectedHandler(Now.AddHours(2), (request, _) =>
        {
            string path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/access_tokens", StringComparison.Ordinal))
            {
                int minted = Interlocked.Increment(ref mints);
                return Task.FromResult(InstallationResponse(minted, clock.GetUtcNow().AddMinutes(2)));
            }

            if (path == "/repos/example-org/example-repo/actions/runners/31")
            {
                Interlocked.Increment(ref protectedReads);
                Assert.Equal("installation-2", request.Headers.Authorization?.Parameter);
                return Task.FromResult(TestResponses.Json(HttpStatusCode.OK,
                    """{"id":31,"name":"runner-31","status":"online","busy":false}"""));
            }

            return Task.FromResult(TestResponses.Json(HttpStatusCode.OK, path.EndsWith("/agents/31", StringComparison.Ordinal)
                ? """{"id":31,"name":"runner-31","runnerScaleSetId":7}""" : "null"));
        });
        using var http = new HttpClient(handler);
        using var client = CreateClient(http, signer, clock, ScaleSetRetryOptions.Disabled);
        Assert.Null(await client.GetScaleSetAsync(7, testToken));
        clock.Advance(TimeSpan.FromMinutes(2));
        Task<bool> owner = client.IsRunnerIdleAsync(7, 31, "runner-31", testToken);
        await ownerEntered.Task.WaitAsync(TimeSpan.FromSeconds(5), testToken);

        DateTimeOffset waitingDeadline = clock.GetUtcNow() + Budget;
        using (ScaleSetRequestRetry.Configure(options => options with { Deadline = waitingDeadline }))
        {
            Task<bool> waiter = client.IsRunnerIdleAsync(7, 31, "runner-31", caller.Token);
            try
            {
                Assert.False(waiter.IsCompleted);
                if (cancelCaller)
                {
                    await caller.CancelAsync();
                }
                else
                {
                    clock.Advance(Budget);
                }

                var cancellation = await Assert.ThrowsAnyAsync<OperationCanceledException>(
                    () => waiter.WaitAsync(TimeSpan.FromSeconds(5), testToken));
                if (cancelCaller)
                {
                    Assert.Equal(caller.Token, cancellation.CancellationToken);
                }
                else
                {
                    Assert.Contains("deadline", cancellation.Message, StringComparison.OrdinalIgnoreCase);
                    Assert.NotEqual(caller.Token, cancellation.CancellationToken);
                }
                Assert.Equal(2, signer.Calls);
                Assert.Equal(1, mints);
                Assert.Equal(0, protectedReads);
            }
            finally
            {
                releaseOwner.TrySetResult();
            }
        }

        Assert.True(await owner);
        Assert.True(await client.IsRunnerIdleAsync(7, 31, "runner-31", testToken));
        Assert.Equal(2, signer.Calls);
        Assert.Equal(2, mints);
        Assert.Equal(2, protectedReads);
    }

    [Fact]
    public async Task InitialMintAndProtectedRequestHaveSeparateElapsedBudgets()
    {
        VirtualTimeProvider clock = new(Now);
        CancellationToken testToken = TestContext.Current.CancellationToken;
        TaskCompletionSource registrationEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource completeRegistration = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var signer = new ScriptedSigner(static (_, _) => ValueTask.FromResult("synthetic-jwt"));
        using var handler = new RecordingHttpMessageHandler(async (request, token) =>
        {
            string path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/access_tokens", StringComparison.Ordinal))
            {
                clock.Advance(TimeSpan.FromSeconds(6));
                return InstallationResponse(1, Now.AddHours(1));
            }

            if (path.EndsWith("/registration-token", StringComparison.Ordinal))
            {
                registrationEntered.SetResult();
                await completeRegistration.Task.WaitAsync(token);
                return TestResponses.Json(HttpStatusCode.Created, """{"token":"registration"}""");
            }

            return path == "/actions/runner-registration"
                ? TestResponses.Json(HttpStatusCode.OK,
                    $$"""{"url":"https://actions-fixture.actions.githubusercontent.com","token":"{{TestResponses.CreateAdminToken(Now.AddHours(2))}}"}""")
                : TestResponses.Json(HttpStatusCode.OK, "null");
        });
        using var http = new HttpClient(handler);
        using var client = CreateClient(http, signer, clock, new ScaleSetRetryOptions { MaxElapsed = Budget, MaxRetries = 0 });
        Task<RunnerScaleSet?> operation = client.GetScaleSetAsync(7, testToken);
        await registrationEntered.Task.WaitAsync(TimeSpan.FromSeconds(5), testToken);
        clock.Advance(TimeSpan.FromSeconds(6));
        completeRegistration.SetResult();

        Assert.Null(await operation);
        Assert.Equal(1, signer.Calls);
        Assert.Equal(4, handler.Requests.Count);
    }

    private static ScaleSetClient CreateClient(HttpClient http, ScriptedSigner signer, TimeProvider clock,
        ScaleSetRetryOptions retry, bool usePat = false) => new(GitHubTarget.Parse("https://github.com/example-org/example-repo"),
        usePat ? ScaleSetCredentials.FromPersonalAccessToken("synthetic-pat") : ScaleSetCredentials.FromGitHubApp("app", 42, signer),
        new ScaleSetClientOptions { Retry = retry }, http, timeProvider: clock);

    private static HttpResponseMessage InstallationResponse(int number, DateTimeOffset expiry) => TestResponses.Json(HttpStatusCode.Created,
        $$"""{"token":"installation-{{number}}","expires_at":"{{expiry:O}}"}""");

    private sealed class ScriptedSigner(Func<int, CancellationToken, ValueTask<string>> sign) : IGitHubAppJwtProvider
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public ValueTask<string> CreateJwtAsync(string clientId, DateTimeOffset now, CancellationToken cancellationToken) =>
            sign(Interlocked.Increment(ref _calls), cancellationToken);
    }
}
