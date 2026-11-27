using System.Net;
using System.Text;
using System.Text.Json;
using ScaleSet.Client;
using ScaleSet.Listener;
using Xunit;

namespace ScaleSet.Listener.Tests;

public sealed class ListenerTransportIntegrationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SuccessfulAcquisitionThenHostCancellationStillAcknowledgesBeforeExplicitClose(bool expiresDuringAcknowledgment)
    {
        using var stop = new CancellationTokenSource();
        var operations = new List<string>();
        bool acquired = false;
        bool refreshed = false;
        using var transport = new ProtocolHandler(async (request, token) =>
        {
            string path = request.RequestUri!.AbsolutePath;
            operations.Add($"{request.Method} {path}");
            if (path == "/queue" && request.Method == HttpMethod.Get)
            {
                Assert.Equal("queue-token", request.Headers.Authorization?.Parameter);
                Assert.Equal("10", Assert.Single(request.Headers.GetValues("X-ScaleSetMaxCapacity")));
                return Json(HttpStatusCode.OK, MessageJson);
            }

            if (path.EndsWith("/acquirejobs", StringComparison.Ordinal))
            {
                Assert.Equal("admin-fixture.actions.githubusercontent.com", request.RequestUri.Host);
                Assert.Equal("queue-token", request.Headers.Authorization?.Parameter);
                Assert.Equal("[5000000001]", await request.Content!.ReadAsStringAsync(token));
                acquired = true;
                return Json(HttpStatusCode.OK, "{\"count\":1,\"value\":[5000000001]}");
            }

            if (path == "/queue/7000000001")
            {
                Assert.Equal(HttpMethod.Delete, request.Method);
                Assert.True(acquired);
                Assert.True(stop.IsCancellationRequested);
                Assert.False(token.IsCancellationRequested);
                Assert.Equal(refreshed ? "replacement-queue-token" : "queue-token", request.Headers.Authorization?.Parameter);
                if (expiresDuringAcknowledgment && !refreshed)
                {
                    return new HttpResponseMessage(HttpStatusCode.Unauthorized);
                }

                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }

            if (request.Method == HttpMethod.Patch)
            {
                Assert.True(expiresDuringAcknowledgment);
                Assert.True(stop.IsCancellationRequested);
                Assert.False(token.IsCancellationRequested);
                Assert.Equal("admin-fixture.actions.githubusercontent.com", request.RequestUri.Host);
                Assert.StartsWith("eyJhbGciOiJSUzI1NiJ9.", request.Headers.Authorization?.Parameter);
                refreshed = true;
                return Json(HttpStatusCode.OK, """
                    {"sessionId":"11111111-1111-1111-1111-111111111111","ownerName":"controller",
                     "messageQueueUrl":"https://queue-fixture.actions.githubusercontent.com/queue","messageQueueAccessToken":"replacement-queue-token",
                     "statistics":{"totalAssignedJobs":0}}
                    """);
            }

            return RegistrationOrSession(request);
        });
        using var http = new HttpClient(transport);
        using var client = CreateClient(http);
        MessageSessionClient session = await client.CreateMessageSessionAsync(42, "controller", TestContext.Current.CancellationToken);
        var listener = new ScaleSetListener(session, new ScaleSetListenerOptions { ScaleSetId = 42 });
        var handled = new List<long>();
        var handler = new Handler(async (message, token) =>
        {
            Assert.NotNull(message);
            handled.Add(message.MessageId);
            if (message.MessageId > 0)
            {
                JobAvailableMessage available = Assert.Single(message.JobAvailableMessages);
                IReadOnlyList<long> result = await session.AcquireJobsAsync([available.RunnerRequestId], token);
                Assert.Equal([5000000001L], result);
                stop.Cancel();
            }
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => listener.RunAsync(handler, stop.Token));
        await session.CloseAsync(TestContext.Current.CancellationToken);

        Assert.Equal([-1L, 7000000001L], handled);
        var expected = new List<string>
        {
            "POST /orgs/example/actions/runners/registration-token", "POST /actions/runner-registration",
            "POST /tenant/_apis/runtime/runnerscalesets/42/sessions", "GET /queue",
            "POST /tenant/_apis/runtime/runnerscalesets/42/acquirejobs", "DELETE /queue/7000000001",
        };
        if (expiresDuringAcknowledgment)
        {
            expected.Add("PATCH /tenant/_apis/runtime/runnerscalesets/42/sessions/11111111-1111-1111-1111-111111111111");
            expected.Add("DELETE /queue/7000000001");
        }

        expected.Add("DELETE /tenant/_apis/runtime/runnerscalesets/42/sessions/11111111-1111-1111-1111-111111111111");
        Assert.Equal(expected, operations);
    }

    [Fact]
    public async Task HandlerFailureLeavesConcreteSessionMessageUnacknowledged()
    {
        var operations = new List<string>();
        using var transport = new ProtocolHandler((request, _) =>
        {
            string path = request.RequestUri!.AbsolutePath;
            operations.Add($"{request.Method} {path}");
            return Task.FromResult(path == "/queue"
                ? Json(HttpStatusCode.OK, MessageJson)
                : RegistrationOrSession(request));
        });
        using var http = new HttpClient(transport);
        using var client = CreateClient(http);
        MessageSessionClient session = await client.CreateMessageSessionAsync(42, "controller", TestContext.Current.CancellationToken);
        var listener = new ScaleSetListener(session, new ScaleSetListenerOptions { ScaleSetId = 42 });
        var failure = new InvalidOperationException("provisioning failed before acknowledgment");
        var handler = new Handler((message, _) => message?.MessageId == 7000000001
            ? Task.FromException(failure)
            : Task.CompletedTask);

        InvalidOperationException actual = await Assert.ThrowsAsync<InvalidOperationException>(
            () => listener.RunAsync(handler, TestContext.Current.CancellationToken));
        await session.CloseAsync(TestContext.Current.CancellationToken);

        Assert.Same(failure, actual);
        Assert.Equal(5, operations.Count);
        Assert.Equal("GET /queue", operations[3]);
        Assert.Equal("DELETE /tenant/_apis/runtime/runnerscalesets/42/sessions/11111111-1111-1111-1111-111111111111", operations[4]);
        Assert.DoesNotContain("DELETE /queue/7000000001", operations);
    }

    [Theory]
    [InlineData("same-session")]
    [InlineData("different-session")]
    [InlineData("owner-lost")]
    [InlineData("ambiguous")]
    public async Task PumpBoundsConcreteAcknowledgementAndNeverSendsAnOldDeleteToReplacement(string outcome)
    {
        using var stopping = new CancellationTokenSource();
        using var ownership = new CancellationTokenSource();
        var fixture = new EngineFixture();
        var clock = new ListenerTestClock();
        bool refreshed = false;
        int polls = 0;
        int deletes = 0;
        int refreshes = 0;
        int customRetries = 0;
        using var transport = new ProtocolHandler((request, token) =>
        {
            string path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Get && path == "/queue")
            {
                polls++;
                if (polls == 1)
                {
                    Assert.DoesNotContain("lastMessageId", request.RequestUri.Query, StringComparison.Ordinal);
                    return Task.FromResult(Json(HttpStatusCode.OK, MessageJson));
                }

                Assert.Contains("lastMessageId=7000000001", request.RequestUri.Query, StringComparison.Ordinal);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Accepted));
            }

            if (request.Method == HttpMethod.Delete && path.StartsWith("/queue", StringComparison.Ordinal))
            {
                deletes++;
                Assert.True(stopping.IsCancellationRequested);
                Assert.False(token.IsCancellationRequested);
                Assert.Equal("/queue/7000000001", path);
                if (outcome == "ambiguous")
                {
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
                }

                Assert.Equal(refreshed ? "new-queue-token" : "queue-token", request.Headers.Authorization?.Parameter);
                return Task.FromResult(new HttpResponseMessage(refreshed ? HttpStatusCode.NoContent : HttpStatusCode.Unauthorized));
            }

            if (request.Method == HttpMethod.Patch)
            {
                refreshes++;
                Assert.True(stopping.IsCancellationRequested);
                Assert.False(token.IsCancellationRequested);
                Assert.EndsWith("/sessions/11111111-1111-1111-1111-111111111111", path);
                if (outcome == "owner-lost")
                {
                    ownership.Cancel();
                    Assert.True(token.IsCancellationRequested);
                }

                refreshed = true;
                bool different = outcome == "different-session";
                return Task.FromResult(Json(HttpStatusCode.OK, JsonSerializer.Serialize(new
                {
                    sessionId = different ? "22222222-2222-2222-2222-222222222222" : "11111111-1111-1111-1111-111111111111",
                    messageQueueUrl = different ? "https://queue-fixture.actions.githubusercontent.com/queue-replacement" : "https://queue-fixture.actions.githubusercontent.com/queue",
                    messageQueueAccessToken = "new-queue-token",
                    statistics = new { totalAssignedJobs = 0 },
                })));
            }

            return Task.FromResult(RegistrationOrSession(request));
        });
        using var http = new HttpClient(transport);
        using var client = CreateClient(http);
        var token = TestContext.Current.CancellationToken;
        var session = await client.CreateMessageSessionAsync(42, "controller", token);
        var key = new ScaleSetListenerSessionKey("github.com/example", 42, session.Session.SessionId);
        var policy = new OperationPolicy(clock) { Owner = ownership.Token };
        fixture.Handler = (delivery, _) =>
        {
            if (delivery.Kind == ScaleSetListenerDeliveryKind.Message)
            {
                stopping.Cancel();
            }

            return Task.FromResult(ScaleSetListenerDeliveryDisposition.Completed);
        };
        var handler = new EngineFixture.FixtureHandler(fixture);
        var pump = new ScaleSetListenerPump(session, new(key, ownership.Token), fixture.Store, handler, policy, clock);
        Assert.Equal(ScaleSetListenerStepStatus.Initialized, (await pump.StepAsync(token)).Status);
        using var retry = ScaleSetRequestRetry.Configure(options => options with
        {
            ShouldRetry = (_, _) =>
            {
                customRetries++;
                return ValueTask.FromResult(true);
            },
        });

        switch (outcome)
        {
            case "same-session":
                Assert.Equal(ScaleSetListenerStepStatus.MessageAcknowledged, (await pump.StepAsync(stopping.Token)).Status);
                Assert.Equal(7000000001, fixture.Store.Saved!.LastAcknowledgedMessageId);
                Assert.Null(fixture.Store.Saved.Pending);
                var reconstructed = new ScaleSetListenerPump(session, new(key, ownership.Token), fixture.Store, handler, policy, clock);
                Assert.Equal(ScaleSetListenerStepStatus.EmptyPoll, (await reconstructed.StepAsync(token)).Status);
                break;
            case "different-session":
                await Assert.ThrowsAsync<InvalidDataException>(() => pump.StepAsync(stopping.Token));
                Assert.Equal(key.SessionId, session.Session.SessionId);
                Assert.Equal(ScaleSetListenerStepStatus.RecoveryRequired, (await pump.StepAsync(token)).Status);
                break;
            case "owner-lost":
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pump.StepAsync(stopping.Token));
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pump.StepAsync(token));
                break;
            default:
                var failure = await Assert.ThrowsAsync<ScaleSetApiException>(() => pump.StepAsync(stopping.Token));
                Assert.Equal(503, failure.StatusCode);
                Assert.Equal(ScaleSetListenerStepStatus.RecoveryRequired, (await pump.StepAsync(token)).Status);
                break;
        }

        if (outcome != "same-session")
        {
            Assert.Equal(ScaleSetListenerPendingPhase.Acknowledging, fixture.Store.Saved!.Pending!.Phase);
            Assert.Null(fixture.Store.Saved.LastAcknowledgedMessageId);
        }

        Assert.Equal(outcome == "same-session" ? 2 : 1, deletes);
        Assert.Equal(outcome == "same-session" ? 2 : 1, polls);
        Assert.Equal(outcome == "ambiguous" ? 0 : 1, refreshes);
        Assert.Equal(0, customRetries);
        Assert.Single(fixture.Deliveries, delivery => delivery.Kind == ScaleSetListenerDeliveryKind.Message);
        await session.CloseAsync(token);
    }

    private const string MessageJson = """
        {"messageId":7000000001,"messageType":"RunnerScaleSetJobMessages","statistics":{"totalAvailableJobs":1},
         "body":"[{\"messageType\":\"JobAvailable\",\"runnerRequestId\":5000000001}]"}
        """;

    private static ScaleSetClient CreateClient(HttpClient http) => new(
        GitHubTarget.Parse("https://github.com/example"),
        ScaleSetCredentials.FromPersonalAccessToken("pat-token"),
        httpClient: http);

    private static HttpResponseMessage RegistrationOrSession(HttpRequestMessage request)
    {
        string path = request.RequestUri!.AbsolutePath;
        if (path == "/orgs/example/actions/runners/registration-token")
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("pat-token", request.Headers.Authorization?.Parameter);
            return Json(HttpStatusCode.Created, "{\"token\":\"registration-token\",\"expires_at\":\"2100-01-01T00:00:00Z\"}");
        }

        if (path == "/actions/runner-registration")
        {
            Assert.Equal("RemoteAuth", request.Headers.Authorization?.Scheme);
            Assert.Equal("registration-token", request.Headers.Authorization?.Parameter);
            string payload = Convert.ToBase64String(Encoding.UTF8.GetBytes("{\"exp\":4102444800}"))
                .TrimEnd('=').Replace('+', '-').Replace('/', '_');
            return Json(HttpStatusCode.OK, JsonSerializer.Serialize(new
            {
                url = "https://admin-fixture.actions.githubusercontent.com/tenant/",
                token = $"eyJhbGciOiJSUzI1NiJ9.{payload}.signature",
            }));
        }

        Assert.Equal("admin-fixture.actions.githubusercontent.com", request.RequestUri.Host);
        Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
        Assert.StartsWith("eyJhbGciOiJSUzI1NiJ9.", request.Headers.Authorization?.Parameter);
        if (request.Method == HttpMethod.Post && path.EndsWith("/sessions", StringComparison.Ordinal))
        {
            return Json(HttpStatusCode.OK, """
                {"sessionId":"11111111-1111-1111-1111-111111111111","ownerName":"controller",
                 "messageQueueUrl":"https://queue-fixture.actions.githubusercontent.com/queue","messageQueueAccessToken":"queue-token",
                 "statistics":{"totalAssignedJobs":0}}
                """);
        }

        Assert.Equal(HttpMethod.Delete, request.Method);
        Assert.EndsWith("/sessions/11111111-1111-1111-1111-111111111111", path);
        return new HttpResponseMessage(HttpStatusCode.NoContent);
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) => new(status)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    private sealed class Handler(Func<RunnerScaleSetMessage?, CancellationToken, Task> handle) : IScaleSetMessageHandler
    {
        public Task HandleAsync(RunnerScaleSetMessage? message, CancellationToken cancellationToken) => handle(message, cancellationToken);
    }

    private sealed class ProtocolHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }
}
