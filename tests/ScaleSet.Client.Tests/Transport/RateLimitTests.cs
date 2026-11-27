using System.Globalization;
using System.Net;
using System.Text;
using ScaleSet.Client;
using Xunit;

namespace ScaleSet.Client.Tests.Transport;

public sealed class RateLimitTests
{
    private static readonly GitHubTarget Target = GitHubTarget.Parse("https://github.com/example-org/example-repo");
    private static readonly DateTimeOffset BaseNow = new(2026, 11, 21, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Classify429AsSecondaryByDefaultAndPrimaryWhenRemainingZeroWithoutSecondaryMessage()
    {
        using HttpClient secondaryHttp = new(CreateConnectedHandler((_, _) =>
            Task.FromResult(TestResponses.Json(HttpStatusCode.TooManyRequests, "{\"message\":\"too many requests\"}"))));
        using ScaleSetClient secondaryClient = CreateClient(secondaryHttp, ScaleSetRetryOptions.Disabled);

        ScaleSetRateLimitException secondaryEx = await Assert.ThrowsAsync<ScaleSetRateLimitException>(
            () => secondaryClient.GetScaleSetAsync(1, TestContext.Current.CancellationToken));
        Assert.Equal(ScaleSetRateLimitKind.Secondary, secondaryEx.Kind);
        Assert.Equal(429, secondaryEx.StatusCode);

        using HttpClient primaryHttp = new(CreateConnectedHandler((_, _) =>
        {
            HttpResponseMessage response = TestResponses.Json(HttpStatusCode.TooManyRequests, "{\"message\":\"API rate limit exceeded\"}");
            response.Headers.TryAddWithoutValidation("X-RateLimit-Remaining", "0");
            return Task.FromResult(response);
        }));
        using ScaleSetClient primaryClient = CreateClient(primaryHttp, ScaleSetRetryOptions.Disabled);

        ScaleSetRateLimitException primaryEx = await Assert.ThrowsAsync<ScaleSetRateLimitException>(
            () => primaryClient.GetScaleSetAsync(1, TestContext.Current.CancellationToken));
        Assert.Equal(ScaleSetRateLimitKind.Primary, primaryEx.Kind);
    }

    [Fact]
    public async Task Classify429WithRemainingZeroAndSecondaryMessageAsSecondary()
    {
        using HttpClient httpClient = new(CreateConnectedHandler((_, _) =>
        {
            HttpResponseMessage response = TestResponses.Json(
                HttpStatusCode.TooManyRequests,
                "{\"message\":\"You have exceeded a secondary rate limit.\"}");
            response.Headers.TryAddWithoutValidation("X-RateLimit-Remaining", "0");
            return Task.FromResult(response);
        }));
        using ScaleSetClient client = CreateClient(httpClient, ScaleSetRetryOptions.Disabled);

        ScaleSetRateLimitException exception = await Assert.ThrowsAsync<ScaleSetRateLimitException>(
            () => client.GetScaleSetAsync(1, TestContext.Current.CancellationToken));

        Assert.Equal(ScaleSetRateLimitKind.Secondary, exception.Kind);
    }

    [Fact]
    public async Task Plain403IsNotClassifiedAsRateLimitException()
    {
        using HttpClient httpClient = new(CreateConnectedHandler((_, _) =>
            Task.FromResult(TestResponses.Json(HttpStatusCode.Forbidden, "{\"message\":\"Resource not accessible by integration\"}"))));
        using ScaleSetClient client = CreateClient(httpClient, ScaleSetRetryOptions.Disabled);

        ScaleSetApiException exception = await Assert.ThrowsAsync<ScaleSetApiException>(
            () => client.GetScaleSetAsync(1, TestContext.Current.CancellationToken));

        Assert.IsNotType<ScaleSetRateLimitException>(exception);
        Assert.Equal(403, exception.StatusCode);
    }

    [Theory]
    [InlineData("retry-after", ScaleSetRateLimitKind.Secondary)]
    [InlineData("remaining-zero", ScaleSetRateLimitKind.Primary)]
    [InlineData("json-secondary", ScaleSetRateLimitKind.Secondary)]
    [InlineData("text-secondary-bom", ScaleSetRateLimitKind.Secondary)]
    public async Task Forbidden403WithRateLimitSignalsIsClassifiedAsRateLimitException(
        string variant,
        ScaleSetRateLimitKind expectedKind)
    {
        using HttpClient httpClient = new(CreateConnectedHandler((_, _) =>
        {
            HttpResponseMessage response;
            switch (variant)
            {
                case "retry-after":
                    response = TestResponses.Json(HttpStatusCode.Forbidden, "{\"message\":\"forbidden\"}");
                    response.Headers.TryAddWithoutValidation("Retry-After", "15");
                    break;
                case "remaining-zero":
                    response = TestResponses.Json(HttpStatusCode.Forbidden, "{\"message\":\"quota exhausted\"}");
                    response.Headers.TryAddWithoutValidation("X-RateLimit-Remaining", "0");
                    break;
                case "json-secondary":
                    response = TestResponses.Json(HttpStatusCode.Forbidden, "{\"message\":\"You have triggered a SECONDARY RATE limit.\"}");
                    break;
                case "text-secondary-bom":
                    byte[] payload = [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes("Secondary rate limit hit")];
                    response = new HttpResponseMessage(HttpStatusCode.Forbidden)
                    {
                        Content = new ByteArrayContent(payload),
                    };
                    response.Content.Headers.TryAddWithoutValidation("Content-Type", "text/plain; charset=utf-8");
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(variant));
            }

            return Task.FromResult(response);
        }));
        using ScaleSetClient client = CreateClient(httpClient, ScaleSetRetryOptions.Disabled);

        ScaleSetRateLimitException exception = await Assert.ThrowsAsync<ScaleSetRateLimitException>(
            () => client.GetScaleSetAsync(1, TestContext.Current.CancellationToken));

        Assert.Equal(403, exception.StatusCode);
        Assert.Equal(expectedKind, exception.Kind);
    }

    [Fact]
    public async Task IncompleteBodyOn403FallsBackToHeadersAndIgnoresIncompleteBodyText()
    {
        using HttpClient textOnlyHttp = new(CreateConnectedHandler((_, _) =>
        {
            HttpResponseMessage response = new(HttpStatusCode.Forbidden)
            {
                Content = new StreamContent(new PartialReadStream(Encoding.UTF8.GetBytes("{\"message\":\"secondary rate"))),
            };
            response.Content.Headers.TryAddWithoutValidation("Content-Type", "application/json");
            return Task.FromResult(response);
        }));
        using ScaleSetClient textOnlyClient = CreateClient(textOnlyHttp, ScaleSetRetryOptions.Disabled);

        ScaleSetApiException plain403 = await Assert.ThrowsAsync<ScaleSetApiException>(
            () => textOnlyClient.GetScaleSetAsync(1, TestContext.Current.CancellationToken));
        Assert.IsNotType<ScaleSetRateLimitException>(plain403);
        Assert.True(plain403.Response?.IsBodyIncomplete);

        using HttpClient headerAndPartialBodyHttp = new(CreateConnectedHandler((_, _) =>
        {
            HttpResponseMessage response = new(HttpStatusCode.Forbidden)
            {
                Content = new StreamContent(new PartialReadStream(Encoding.UTF8.GetBytes("{\"message\":\"secondary rate"))),
            };
            response.Headers.TryAddWithoutValidation("X-RateLimit-Remaining", "0");
            return Task.FromResult(response);
        }));
        using ScaleSetClient headerClient = CreateClient(headerAndPartialBodyHttp, ScaleSetRetryOptions.Disabled);

        ScaleSetRateLimitException classified = await Assert.ThrowsAsync<ScaleSetRateLimitException>(
            () => headerClient.GetScaleSetAsync(1, TestContext.Current.CancellationToken));
        Assert.Equal(ScaleSetRateLimitKind.Primary, classified.Kind);
        Assert.True(classified.Response?.IsBodyIncomplete);
    }

    [Fact]
    public async Task RetryAfterAndResetParsingHandlesSecondsDatesPastInvalidAndOverflow()
    {
        VirtualTimeProvider clock = new(BaseNow);

        async Task<ScaleSetRateLimitException> ProbeAsync(Action<HttpResponseMessage> configure)
        {
            using HttpClient httpClient = new(CreateConnectedHandler((_, _) =>
            {
                HttpResponseMessage response = TestResponses.Json(HttpStatusCode.TooManyRequests, "{}");
                configure(response);
                return Task.FromResult(response);
            }));
            using ScaleSetClient client = CreateClient(httpClient, ScaleSetRetryOptions.Disabled, clock);
            return await Assert.ThrowsAsync<ScaleSetRateLimitException>(
                () => client.GetScaleSetAsync(1, TestContext.Current.CancellationToken));
        }

        Assert.Equal(TimeSpan.FromSeconds(12), (await ProbeAsync(r => r.Headers.TryAddWithoutValidation("Retry-After", "12"))).RetryAfter);
        Assert.Equal(TimeSpan.Zero, (await ProbeAsync(r => r.Headers.TryAddWithoutValidation("Retry-After", "0"))).RetryAfter);
        Assert.Equal(TimeSpan.Zero, (await ProbeAsync(r => r.Headers.TryAddWithoutValidation("Retry-After", "-5"))).RetryAfter);
        Assert.Equal(TimeSpan.Zero, (await ProbeAsync(r => r.Headers.TryAddWithoutValidation("Retry-After", "9223372036854775807"))).RetryAfter);
        Assert.Equal(TimeSpan.Zero, (await ProbeAsync(r => r.Headers.TryAddWithoutValidation("Retry-After", "not-a-date"))).RetryAfter);

        string futureDate = BaseNow.AddSeconds(42).ToString("R", CultureInfo.InvariantCulture);
        Assert.Equal(TimeSpan.FromSeconds(42), (await ProbeAsync(r => r.Headers.TryAddWithoutValidation("Retry-After", futureDate))).RetryAfter);

        string pastDate = BaseNow.AddSeconds(-10).ToString("R", CultureInfo.InvariantCulture);
        Assert.Equal(TimeSpan.Zero, (await ProbeAsync(r => r.Headers.TryAddWithoutValidation("Retry-After", pastDate))).RetryAfter);

        long futureResetUnix = BaseNow.AddSeconds(75).ToUnixTimeSeconds();
        ScaleSetRateLimitException primaryReset = await ProbeAsync(r =>
        {
            r.Headers.TryAddWithoutValidation("X-RateLimit-Remaining", "0");
            r.Headers.TryAddWithoutValidation("X-RateLimit-Reset", futureResetUnix.ToString(CultureInfo.InvariantCulture));
        });
        Assert.Equal(ScaleSetRateLimitKind.Primary, primaryReset.Kind);
        Assert.Equal(TimeSpan.FromSeconds(75), primaryReset.RetryAfter);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(futureResetUnix), primaryReset.Reset);

        ScaleSetRateLimitException secondaryReset = await ProbeAsync(r =>
            r.Headers.TryAddWithoutValidation("X-RateLimit-Reset", futureResetUnix.ToString(CultureInfo.InvariantCulture)));
        Assert.Equal(ScaleSetRateLimitKind.Secondary, secondaryReset.Kind);
        Assert.Equal(TimeSpan.Zero, secondaryReset.RetryAfter);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(futureResetUnix), secondaryReset.Reset);
    }

    [Theory]
    [InlineData((int)HttpStatusCode.TooManyRequests)]
    [InlineData((int)HttpStatusCode.ServiceUnavailable)]
    public async Task DefaultReadRetriesHonorRetryAfterUpToOneMinuteAndSkipBeyondOneMinute(int statusCode)
    {
        VirtualTimeProvider clock = new(BaseNow);
        int calls = 0;
        using HttpClient retryableHttp = new(CreateConnectedHandler((_, _) =>
        {
            calls++;
            if (calls == 1)
            {
                HttpResponseMessage response = TestResponses.Json((HttpStatusCode)statusCode, "slow down");
                response.Headers.TryAddWithoutValidation("Retry-After", "45");
                return Task.FromResult(response);
            }

            return Task.FromResult(TestResponses.Json(HttpStatusCode.OK, "{\"id\":9}"));
        }));
        using ScaleSetClient retryClient = new(
            Target,
            ScaleSetCredentials.FromPersonalAccessToken("synthetic-pat"),
            new ScaleSetClientOptions
            {
                MaxReadOnlyRetries = 1,
                RetryBaseDelay = TimeSpan.Zero,
                RetryMaxDelay = TimeSpan.Zero,
            },
            retryableHttp,
            timeProvider: clock);

        Task<RunnerScaleSet?> task = retryClient.GetScaleSetAsync(9, TestContext.Current.CancellationToken);
        TimeSpan scheduledDelay = await clock.WaitForActiveTimerAsync(
            delay => delay == TimeSpan.FromSeconds(45),
            TestContext.Current.CancellationToken);
        Assert.Equal(TimeSpan.FromSeconds(45), scheduledDelay);
        clock.Advance(TimeSpan.FromSeconds(45));

        RunnerScaleSet? resolved = await task;
        Assert.Equal(9, resolved?.Id);
        Assert.Equal(2, calls);

        int skippedCalls = 0;
        using HttpClient overCapHttp = new(CreateConnectedHandler((_, _) =>
        {
            skippedCalls++;
            HttpResponseMessage response = TestResponses.Json((HttpStatusCode)statusCode, "too long");
            response.Headers.TryAddWithoutValidation("Retry-After", "61");
            return Task.FromResult(response);
        }));
        using ScaleSetClient overCapClient = new(
            Target,
            ScaleSetCredentials.FromPersonalAccessToken("synthetic-pat"),
            new ScaleSetClientOptions
            {
                MaxReadOnlyRetries = 3,
                RetryBaseDelay = TimeSpan.Zero,
                RetryMaxDelay = TimeSpan.Zero,
            },
            overCapHttp,
            timeProvider: clock);

        ScaleSetApiException skippedException = await Assert.ThrowsAnyAsync<ScaleSetApiException>(
            () => overCapClient.GetScaleSetAsync(9, TestContext.Current.CancellationToken));
        Assert.Equal(statusCode, skippedException.StatusCode);
        Assert.Equal(1, skippedCalls);
    }

    [Fact]
    public async Task UpstreamDefaultPolicyHonorsFullRetryAfterOverOneMinute()
    {
        VirtualTimeProvider clock = new(BaseNow);
        int calls = 0;
        using HttpClient httpClient = new(CreateConnectedHandler((_, _) =>
        {
            calls++;
            if (calls == 1)
            {
                HttpResponseMessage response = TestResponses.Json(HttpStatusCode.TooManyRequests, "{}");
                response.Headers.TryAddWithoutValidation("Retry-After", "90");
                return Task.FromResult(response);
            }

            return Task.FromResult(TestResponses.Json(HttpStatusCode.OK, "{\"id\":14}"));
        }));
        using ScaleSetClient client = CreateClient(httpClient, ScaleSetRetryOptions.CreateUpstreamDefault(), clock);

        Task<RunnerScaleSet?> task = client.GetScaleSetAsync(14, TestContext.Current.CancellationToken);
        _ = await clock.WaitForActiveTimerAsync(delay => delay == TimeSpan.FromSeconds(90), TestContext.Current.CancellationToken);
        clock.Advance(TimeSpan.FromSeconds(90));

        RunnerScaleSet? result = await task;
        Assert.Equal(14, result?.Id);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task OnRateLimitPolicyHandlesPrimarySecondaryMaxWaitAndPostMutations()
    {
        VirtualTimeProvider clock = new(BaseNow);
        ScaleSetRetryOptions retryOptions = new()
        {
            MaxRetries = 4,
            WaitMin = TimeSpan.Zero,
            WaitMax = TimeSpan.Zero,
            OnRateLimit = ScaleSetRateLimitPolicies.Create(
                new ScaleSetRateLimitRule(),
                new ScaleSetRateLimitRule(MaxRetries: 2, MaxWait: TimeSpan.FromSeconds(5))),
        };

        int primaryCalls = 0;
        using HttpClient primaryHttp = new(CreateConnectedHandler((_, _) =>
        {
            primaryCalls++;
            HttpResponseMessage response = TestResponses.Json(HttpStatusCode.Forbidden, "{\"message\":\"primary spent\"}");
            response.Headers.TryAddWithoutValidation("X-RateLimit-Remaining", "0");
            return Task.FromResult(response);
        }));
        using ScaleSetClient primaryClient = CreateClient(primaryHttp, retryOptions, clock);
        ScaleSetRateLimitException primaryEx = await Assert.ThrowsAsync<ScaleSetRateLimitException>(
            () => primaryClient.GenerateJitConfigAsync(
                7,
                new RunnerScaleSetJitRunnerSetting { Name = "r1", WorkFolder = "_work" },
                TestContext.Current.CancellationToken));
        Assert.Equal(1, primaryCalls);
        Assert.Equal(0, primaryEx.Retries);

        int secondaryCalls = 0;
        using HttpClient secondaryHttp = new(CreateConnectedHandler((_, _) =>
        {
            secondaryCalls++;
            HttpResponseMessage response = TestResponses.Json(HttpStatusCode.Forbidden, "{\"message\":\"secondary rate limit\"}");
            response.Headers.TryAddWithoutValidation("Retry-After", "2");
            return Task.FromResult(response);
        }));
        using ScaleSetClient secondaryClient = CreateClient(secondaryHttp, retryOptions, clock);

        Task secondaryTask = secondaryClient.GenerateJitConfigAsync(
            7,
            new RunnerScaleSetJitRunnerSetting { Name = "r2", WorkFolder = "_work" },
            TestContext.Current.CancellationToken);
        for (int index = 0; index < 2; index++)
        {
            _ = await clock.WaitForActiveTimerAsync(delay => delay == TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
            clock.Advance(TimeSpan.FromSeconds(2));
        }

        ScaleSetRateLimitException secondaryEx = await Assert.ThrowsAsync<ScaleSetRateLimitException>(() => secondaryTask);
        Assert.Equal(3, secondaryCalls);
        Assert.Equal(2, secondaryEx.Retries);

        int tooLongCalls = 0;
        using HttpClient tooLongHttp = new(CreateConnectedHandler((_, _) =>
        {
            tooLongCalls++;
            HttpResponseMessage response = TestResponses.Json(HttpStatusCode.TooManyRequests, "{}");
            response.Headers.TryAddWithoutValidation("Retry-After", "6");
            return Task.FromResult(response);
        }));
        using ScaleSetClient tooLongClient = CreateClient(tooLongHttp, retryOptions, clock);
        ScaleSetRateLimitException tooLongEx = await Assert.ThrowsAsync<ScaleSetRateLimitException>(
            () => tooLongClient.GenerateJitConfigAsync(
                7,
                new RunnerScaleSetJitRunnerSetting { Name = "r3", WorkFolder = "_work" },
                TestContext.Current.CancellationToken));
        Assert.Equal(1, tooLongCalls);
        Assert.Equal(TimeSpan.FromSeconds(6), tooLongEx.RetryAfter);
        Assert.Equal(0, tooLongEx.Retries);
    }

    [Fact]
    public async Task TransportAndServerRetriesDoNotIncrementRateLimitRetriesCount()
    {
        int calls = 0;
        using HttpClient httpClient = new(CreateConnectedHandler((_, _) =>
        {
            calls++;
            return Task.FromResult(calls == 1
                ? TestResponses.Json(HttpStatusCode.InternalServerError, "server error")
                : TestResponses.Json(HttpStatusCode.TooManyRequests, "rate limit"));
        }));
        ScaleSetRetryOptions retryOptions = new()
        {
            MaxRetries = 2,
            WaitMin = TimeSpan.Zero,
            WaitMax = TimeSpan.Zero,
        };
        using ScaleSetClient client = CreateClient(httpClient, retryOptions);

        ScaleSetRateLimitException exception = await Assert.ThrowsAsync<ScaleSetRateLimitException>(
            () => client.GetScaleSetAsync(1, TestContext.Current.CancellationToken));

        Assert.Equal(3, calls);
        Assert.Equal(1, exception.Retries);
    }

    [Fact]
    public async Task CustomOnRateLimitGovernsDiscovery403RateLimit()
    {
        int customDiscoveryCalls = 0;
        using HttpClient customHttp = new(new RecordingHttpMessageHandler((request, _) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/runners/registration-token", StringComparison.Ordinal))
            {
                return Task.FromResult(TestResponses.Json(HttpStatusCode.Created, "{\"token\":\"registration-token\"}"));
            }

            customDiscoveryCalls++;
            HttpResponseMessage response = TestResponses.Json(HttpStatusCode.Forbidden, "{\"message\":\"secondary rate limit\"}");
            return Task.FromResult(response);
        }));
        using ScaleSetClient customClient = CreateClient(customHttp, new ScaleSetRetryOptions
        {
            MaxRetries = 4,
            WaitMin = TimeSpan.Zero,
            WaitMax = TimeSpan.Zero,
            OnRateLimit = (_, _) => ValueTask.FromResult(ScaleSetRateLimitDecision.DoNotRetry),
        });

        await Assert.ThrowsAsync<ScaleSetRateLimitException>(
            () => customClient.GetScaleSetAsync(1, TestContext.Current.CancellationToken));
        Assert.Equal(1, customDiscoveryCalls);

        int defaultDiscoveryCalls = 0;
        using HttpClient defaultHttp = new(new RecordingHttpMessageHandler((request, _) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/runners/registration-token", StringComparison.Ordinal))
            {
                return Task.FromResult(TestResponses.Json(HttpStatusCode.Created, "{\"token\":\"registration-token\"}"));
            }

            if (request.RequestUri.AbsolutePath == "/actions/runner-registration")
            {
                defaultDiscoveryCalls++;
                if (defaultDiscoveryCalls == 1)
                {
                    return Task.FromResult(TestResponses.Json(HttpStatusCode.Forbidden, "{\"message\":\"secondary rate limit\"}"));
                }

                string token = TestResponses.CreateAdminToken(BaseNow.AddMinutes(10));
                return Task.FromResult(TestResponses.Json(HttpStatusCode.OK, $"{{\"url\":\"https://actions-fixture.actions.githubusercontent.com/tenant\",\"token\":\"{token}\"}}"));
            }

            return Task.FromResult(TestResponses.Json(HttpStatusCode.OK, "{\"id\":5}"));
        }));
        using ScaleSetClient defaultClient = new(
            Target,
            ScaleSetCredentials.FromPersonalAccessToken("synthetic-pat"),
            new ScaleSetClientOptions
            {
                MaxRegistrationDiscoveryAuthRetries = 2,
                RetryBaseDelay = TimeSpan.Zero,
                RetryMaxDelay = TimeSpan.Zero,
            },
            defaultHttp);

        RunnerScaleSet? scaleSet = await defaultClient.GetScaleSetAsync(5, TestContext.Current.CancellationToken);
        Assert.Equal(5, scaleSet?.Id);
        Assert.Equal(2, defaultDiscoveryCalls);
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
