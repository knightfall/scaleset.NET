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
                Assert.Equal("admin.example", request.RequestUri.Host);
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
                Assert.Equal("admin.example", request.RequestUri.Host);
                Assert.StartsWith("eyJhbGciOiJSUzI1NiJ9.", request.Headers.Authorization?.Parameter);
                refreshed = true;
                return Json(HttpStatusCode.OK, """
                    {"sessionId":"11111111-1111-1111-1111-111111111111","ownerName":"controller",
                     "messageQueueUrl":"https://queue.example/queue","messageQueueAccessToken":"replacement-queue-token",
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
                url = "https://admin.example/tenant/",
                token = $"eyJhbGciOiJSUzI1NiJ9.{payload}.signature",
            }));
        }

        Assert.Equal("admin.example", request.RequestUri.Host);
        Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
        Assert.StartsWith("eyJhbGciOiJSUzI1NiJ9.", request.Headers.Authorization?.Parameter);
        if (request.Method == HttpMethod.Post && path.EndsWith("/sessions", StringComparison.Ordinal))
        {
            return Json(HttpStatusCode.OK, """
                {"sessionId":"11111111-1111-1111-1111-111111111111","ownerName":"controller",
                 "messageQueueUrl":"https://queue.example/queue","messageQueueAccessToken":"queue-token",
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
