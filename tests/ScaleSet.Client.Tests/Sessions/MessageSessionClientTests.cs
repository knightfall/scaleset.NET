using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using ScaleSet.Client;
using Xunit;

namespace ScaleSet.Client.Tests.Sessions;

public sealed class MessageSessionClientTests
{
    private const long LargeScaleSetId = 4_294_967_300;
    private const long LargeMessageId = 5_000_000_001;
    private const string QueueToken = "synthetic-queue-token";
    private const string RefreshedQueueToken = "synthetic-refreshed-queue-token";
    private const string DiscoveryJwt = "eyJhbGciOiJub25lIiwidHlwIjoiSldUIn0.eyJleHAiOjQxMDI0NDQ4MDB9.";
    private static readonly Guid SessionId = Guid.Parse("2c581b9c-5c84-44d0-85bd-d6d780de6124");
    private const string QueueUri = "https://queue.example.test/messages?region=west";

    [Fact]
    public async Task CreateMessageSessionAsync_PostsOwnerAndPublishesValidatedSnapshot()
    {
        using ClientFixture fixture = new((request, _) =>
        {
            if (IsCreateSession(request))
            {
                return Task.FromResult(JsonResponse(HttpStatusCode.OK, SessionJson(QueueToken)));
            }

            return Task.FromResult(NotFoundResponse());
        });

        MessageSessionClient session = await fixture.Client.CreateMessageSessionAsync(LargeScaleSetId, "runner-host", TestContext.Current.CancellationToken);

        RecordedRequest request = Assert.Single(fixture.Handler.Requests, IsCreateSession);
        RunnerScaleSetSession? body = ScaleSetJson.Deserialize<RunnerScaleSetSession>(request.Body);
        Assert.Equal("runner-host", body?.OwnerName);
        Assert.Equal($"Bearer {DiscoveryJwt}", request.Authorization);
        Assert.Equal($"/tenant/123/_apis/runtime/runnerscalesets/{LargeScaleSetId}/sessions", request.Uri.AbsolutePath);
        Assert.Equal(SessionId, session.Session.SessionId);
        Assert.Equal("runner-host", session.Session.OwnerName);
        Assert.Equal(QueueToken, session.Session.MessageQueueAccessToken);
        Assert.Equal("https://queue.example.test/messages?region=west", session.Session.MessageQueueUrl);
        Assert.Equal(3, session.Session.Statistics?.TotalAvailableJobs);
    }

    [Theory]
    [InlineData("id")]
    [InlineData("url")]
    [InlineData("token")]
    [InlineData("statistics")]
    public async Task CreateMessageSessionAsync_RejectsInvalidSnapshot(string invalidPart)
    {
        RunnerScaleSetSession session = ValidSession(QueueToken);
        session = invalidPart switch
        {
            "id" => session with { SessionId = Guid.Empty },
            "url" => session with { MessageQueueUrl = "http://queue.example.test/messages" },
            "token" => session with { MessageQueueAccessToken = " " },
            "statistics" => session with { Statistics = new RunnerScaleSetStatistic { TotalBusyRunners = -1 } },
            _ => throw new ArgumentOutOfRangeException(nameof(invalidPart)),
        };
        using ClientFixture fixture = new((request, _) => Task.FromResult(
            IsCreateSession(request)
                ? JsonResponse(HttpStatusCode.OK, ScaleSetJson.Serialize(session))
                : NotFoundResponse()));

        ScaleSetApiException exception = await Assert.ThrowsAsync<ScaleSetApiException>(
            () => fixture.Client.CreateMessageSessionAsync(LargeScaleSetId, "runner-host", TestContext.Current.CancellationToken));

        Assert.Equal(200, exception.StatusCode);
        Assert.Single(fixture.Handler.Requests, IsCreateSession);
    }

    [Fact]
    public async Task CreateMessageSessionAsync_RejectsLineBreakInQueueTokenWithoutLeakingIt()
    {
        const string tokenSentinel = "token-leak-sentinel";
        RunnerScaleSetSession session = ValidSession($"unsafe\r\nbearer {tokenSentinel}");
        using ClientFixture fixture = new((request, _) => Task.FromResult(
            IsCreateSession(request)
                ? JsonResponse(HttpStatusCode.OK, ScaleSetJson.Serialize(session))
                : NotFoundResponse()));

        ScaleSetApiException exception = await Assert.ThrowsAsync<ScaleSetApiException>(
            () => fixture.Client.CreateMessageSessionAsync(LargeScaleSetId, "runner-host", TestContext.Current.CancellationToken));

        Assert.Equal("ScaleSet response body could not be decoded.", exception.Message);
        string preview = Assert.IsType<string>(exception.BodyPreview);
        Assert.DoesNotContain(tokenSentinel, preview, StringComparison.Ordinal);
        Assert.Single(fixture.Handler.Requests, IsCreateSession);
    }

    [Fact]
    public async Task GetMessageAsync_PreservesQueueQueryAndSendsCursorCapacityAndAccept()
    {
        const string queueUrl = "https://queue.example.test/messages?region=west&lastMessageId=3&opaque=a%2Fb";
        using ClientFixture fixture = new((request, _) =>
        {
            if (IsCreateSession(request))
            {
                return Task.FromResult(JsonResponse(HttpStatusCode.OK, SessionJson(QueueToken, queueUrl)));
            }

            if (IsQueueRequest(request, HttpMethod.Get))
            {
                return Task.FromResult(JsonResponse(HttpStatusCode.OK, "{\"messageId\":42,\"messageType\":\"RunnerScaleSetJobMessages\"}"));
            }

            return Task.FromResult(NotFoundResponse());
        });
        MessageSessionClient session = await fixture.Client.CreateMessageSessionAsync(LargeScaleSetId, "runner-host", TestContext.Current.CancellationToken);

        RunnerScaleSetMessage? message = await session.GetMessageAsync(9, int.MaxValue, TestContext.Current.CancellationToken);

        RecordedRequest request = Assert.Single(fixture.Handler.Requests, record => IsQueueRequest(record, HttpMethod.Get));
        KeyValuePair<string, string>[] query = ParseQuery(request.Uri);
        Assert.Equal(42, message?.MessageId);
        Assert.Equal("Bearer " + QueueToken, request.Authorization);
        Assert.Equal("application/json; api-version=6.0-preview", request.Accept);
        Assert.Equal(int.MaxValue.ToString(CultureInfo.InvariantCulture), request.MaxCapacity);
        Assert.Equal("west", QueryValue(query, "region"));
        Assert.Equal("a/b", QueryValue(query, "opaque"));
        Assert.Equal("9", QueryValue(query, "lastMessageId"));
        Assert.Single(query, pair => pair.Key == "lastMessageId");
    }

    [Fact]
    public async Task GetMessageAsync_MapsAcceptedToEmptyAndDoesNotAddZeroCursor()
    {
        using ClientFixture fixture = new((request, _) =>
        {
            if (IsCreateSession(request))
            {
                return Task.FromResult(JsonResponse(HttpStatusCode.OK, SessionJson(QueueToken)));
            }

            if (IsQueueRequest(request, HttpMethod.Get))
            {
                return Task.FromResult(JsonResponse(HttpStatusCode.Accepted, string.Empty));
            }

            return Task.FromResult(NotFoundResponse());
        });
        MessageSessionClient session = await fixture.Client.CreateMessageSessionAsync(LargeScaleSetId, "runner-host", TestContext.Current.CancellationToken);

        RunnerScaleSetMessage? message = await session.GetMessageAsync(0, 0, TestContext.Current.CancellationToken);

        RecordedRequest request = Assert.Single(fixture.Handler.Requests, record => IsQueueRequest(record, HttpMethod.Get));
        Assert.Null(message);
        Assert.Equal("west", QueryValue(ParseQuery(request.Uri), "region"));
        Assert.DoesNotContain(ParseQuery(request.Uri), pair => pair.Key == "lastMessageId");
        Assert.Equal("0", request.MaxCapacity);
    }

    [Fact]
    public async Task GetMessageAsync_RejectsNegativeCapacityBeforeSendingQueueRequest()
    {
        using ClientFixture fixture = new((request, _) =>
        {
            if (IsCreateSession(request))
            {
                return Task.FromResult(JsonResponse(HttpStatusCode.OK, SessionJson(QueueToken)));
            }

            return Task.FromResult(NotFoundResponse());
        });
        MessageSessionClient session = await fixture.Client.CreateMessageSessionAsync(LargeScaleSetId, "runner-host", TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => session.GetMessageAsync(0, -1, TestContext.Current.CancellationToken));

        Assert.DoesNotContain(fixture.Handler.Requests, request => IsQueueRequest(request, HttpMethod.Get));
    }

    [Fact]
    public async Task DeleteMessageAsync_AppendsLargeIdPreservesQueryAndUsesQueueToken()
    {
        const string queueUrl = "https://queue.example.test/messages?region=west&opaque=a%2Fb";
        using ClientFixture fixture = new((request, _) =>
        {
            if (IsCreateSession(request))
            {
                return Task.FromResult(JsonResponse(HttpStatusCode.OK, SessionJson(QueueToken, queueUrl)));
            }

            if (IsQueueRequest(request, HttpMethod.Delete))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
            }

            return Task.FromResult(NotFoundResponse());
        });
        MessageSessionClient session = await fixture.Client.CreateMessageSessionAsync(LargeScaleSetId, "runner-host", TestContext.Current.CancellationToken);

        await session.DeleteMessageAsync(LargeMessageId, TestContext.Current.CancellationToken);

        RecordedRequest request = Assert.Single(fixture.Handler.Requests, record => IsQueueRequest(record, HttpMethod.Delete));
        Assert.Equal("/messages/5000000001", request.Uri.AbsolutePath);
        Assert.Equal("west", QueryValue(ParseQuery(request.Uri), "region"));
        Assert.Equal("a/b", QueryValue(ParseQuery(request.Uri), "opaque"));
        Assert.Equal("Bearer " + QueueToken, request.Authorization);
        Assert.Equal("application/json", request.ContentType);
    }

    [Fact]
    public async Task AcquireJobsAsync_UsesQueueBearerAndPreservesInt64Identifiers()
    {
        long[] requestIds = [long.MaxValue, 4_294_967_297];
        using ClientFixture fixture = new((request, _) =>
        {
            if (IsCreateSession(request))
            {
                return Task.FromResult(JsonResponse(HttpStatusCode.OK, SessionJson(QueueToken)));
            }

            if (IsAcquireJobs(request))
            {
                return Task.FromResult(JsonResponse(HttpStatusCode.OK, "{\"count\":2,\"value\":[9223372036854775807,4294967297]}"));
            }

            return Task.FromResult(NotFoundResponse());
        });
        MessageSessionClient session = await fixture.Client.CreateMessageSessionAsync(LargeScaleSetId, "runner-host", TestContext.Current.CancellationToken);

        IReadOnlyList<long> acquired = await session.AcquireJobsAsync(requestIds, TestContext.Current.CancellationToken);

        RecordedRequest request = Assert.Single(fixture.Handler.Requests, IsAcquireJobs);
        using JsonDocument body = JsonDocument.Parse(request.Body);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal($"/tenant/123/_apis/runtime/runnerscalesets/{LargeScaleSetId}/acquirejobs", request.Uri.AbsolutePath);
        Assert.Equal("Bearer " + QueueToken, request.Authorization);
        Assert.Equal("application/json", request.ContentType);
        Assert.Equal(requestIds, body.RootElement.EnumerateArray().Select(value => value.GetInt64()));
        Assert.Equal(requestIds, acquired);
    }

    [Fact]
    public async Task AcquireJobsAsync_EncodesAnEmptyRequestList()
    {
        using ClientFixture fixture = new((request, _) =>
        {
            if (IsCreateSession(request))
            {
                return Task.FromResult(JsonResponse(HttpStatusCode.OK, SessionJson(QueueToken)));
            }

            if (IsAcquireJobs(request))
            {
                return Task.FromResult(JsonResponse(HttpStatusCode.OK, "{\"count\":0,\"value\":[]}"));
            }

            return Task.FromResult(NotFoundResponse());
        });
        MessageSessionClient session = await fixture.Client.CreateMessageSessionAsync(LargeScaleSetId, "runner-host", TestContext.Current.CancellationToken);

        IReadOnlyList<long> acquired = await session.AcquireJobsAsync(Array.Empty<long>(), TestContext.Current.CancellationToken);

        RecordedRequest request = Assert.Single(fixture.Handler.Requests, IsAcquireJobs);
        Assert.Equal("[]", request.Body);
        Assert.Empty(acquired);
    }

    [Theory]
    [InlineData("poll")]
    [InlineData("ack")]
    [InlineData("acquire")]
    public async Task QueueUnauthorizedRefreshesAndRetriesExactlyOnce(string operation)
    {
        int queueTokenRequests = 0;
        int refreshRequests = 0;
        using ClientFixture fixture = new((request, _) =>
        {
            if (IsCreateSession(request))
            {
                return Task.FromResult(JsonResponse(HttpStatusCode.OK, SessionJson(QueueToken)));
            }

            if (IsRefresh(request))
            {
                Interlocked.Increment(ref refreshRequests);
                return Task.FromResult(JsonResponse(HttpStatusCode.OK, SessionJson(RefreshedQueueToken)));
            }

            if (IsSessionOperation(request, operation))
            {
                Interlocked.Increment(ref queueTokenRequests);
                return Task.FromResult(string.Equals(request.Authorization, "Bearer " + QueueToken, StringComparison.Ordinal)
                    ? new HttpResponseMessage(HttpStatusCode.Unauthorized)
                    : SuccessForOperation(operation));
            }

            return Task.FromResult(NotFoundResponse());
        });
        MessageSessionClient session = await fixture.Client.CreateMessageSessionAsync(LargeScaleSetId, "runner-host", TestContext.Current.CancellationToken);

        await InvokeSessionOperationAsync(operation, session);

        Assert.Equal(2, Volatile.Read(ref queueTokenRequests));
        Assert.Equal(1, Volatile.Read(ref refreshRequests));
        Assert.Equal(RefreshedQueueToken, session.Session.MessageQueueAccessToken);
        RecordedRequest refresh = Assert.Single(fixture.Handler.Requests, IsRefresh);
        Assert.Equal("Bearer " + DiscoveryJwt, refresh.Authorization);
    }

    [Theory]
    [InlineData("poll")]
    [InlineData("ack")]
    [InlineData("acquire")]
    public async Task QueueUnauthorizedAfterOneRefreshIsReturnedWithoutAnotherRetry(string operation)
    {
        int operationRequests = 0;
        int refreshRequests = 0;
        using ClientFixture fixture = new((request, _) =>
        {
            if (IsCreateSession(request))
            {
                return Task.FromResult(JsonResponse(HttpStatusCode.OK, SessionJson(QueueToken)));
            }

            if (IsRefresh(request))
            {
                Interlocked.Increment(ref refreshRequests);
                return Task.FromResult(JsonResponse(HttpStatusCode.OK, SessionJson(RefreshedQueueToken)));
            }

            if (IsSessionOperation(request, operation))
            {
                Interlocked.Increment(ref operationRequests);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
            }

            return Task.FromResult(NotFoundResponse());
        });
        MessageSessionClient session = await fixture.Client.CreateMessageSessionAsync(LargeScaleSetId, "runner-host", TestContext.Current.CancellationToken);

        ScaleSetApiException exception = await Assert.ThrowsAsync<ScaleSetApiException>(
            () => InvokeSessionOperationAsync(operation, session));

        Assert.Equal(401, exception.StatusCode);
        Assert.Equal(ScaleSetDomainError.MessageQueueTokenExpired, exception.DomainError);
        Assert.Equal(2, Volatile.Read(ref operationRequests));
        Assert.Equal(1, Volatile.Read(ref refreshRequests));
    }

    [Theory]
    [InlineData("poll")]
    [InlineData("ack")]
    [InlineData("acquire")]
    public async Task RefreshWithDifferentGuidNeverPublishesOrRetries(string operation)
    {
        using ClientFixture fixture = new((request, _) =>
        {
            if (IsCreateSession(request))
            {
                return Task.FromResult(JsonResponse(HttpStatusCode.OK, SessionJson(QueueToken)));
            }

            if (IsRefresh(request))
            {
                var original = ScaleSetJson.Deserialize<RunnerScaleSetSession>(SessionJson(RefreshedQueueToken))!;
                return Task.FromResult(JsonResponse(HttpStatusCode.OK,
                    ScaleSetJson.Serialize(original with { SessionId = Guid.NewGuid() })));
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
        });
        var session = await fixture.Client.CreateMessageSessionAsync(LargeScaleSetId, "runner-host", TestContext.Current.CancellationToken);
        var originalSession = session.Session;

        await Assert.ThrowsAsync<InvalidDataException>(() => InvokeSessionOperationAsync(operation, session));
        Assert.Same(originalSession, session.Session);
        Assert.Single(fixture.Handler.Requests, IsRefresh);
        Assert.Single(fixture.Handler.Requests, request => IsSessionOperation(request, operation));
    }

    [Fact]
    public async Task ConcurrentUnauthorizedPollsCoalesceWhenRefreshKeepsSameIdAndToken()
    {
        const int callers = 8;
        int totalPollRequests = 0;
        int initialUnauthorizedPolls = 0;
        int successfulPolls = 0;
        int refreshRequests = 0;
        TaskCompletionSource allInitialPolls = NewSignal();
        TaskCompletionSource refreshStarted = NewSignal();
        TaskCompletionSource releaseRefresh = NewSignal();
        using ClientFixture fixture = new(async (request, cancellationToken) =>
        {
            if (IsCreateSession(request))
            {
                return JsonResponse(HttpStatusCode.OK, SessionJson(QueueToken));
            }

            if (IsRefresh(request))
            {
                if (Interlocked.Increment(ref refreshRequests) == 1)
                {
                    refreshStarted.TrySetResult();
                    await releaseRefresh.Task.WaitAsync(cancellationToken);
                }

                return JsonResponse(HttpStatusCode.OK, SessionJson(QueueToken));
            }

            if (IsQueueRequest(request, HttpMethod.Get) &&
                string.Equals(request.Authorization, "Bearer " + QueueToken, StringComparison.Ordinal))
            {
                int count = Interlocked.Increment(ref totalPollRequests);
                if (count <= callers)
                {
                    Interlocked.Increment(ref initialUnauthorizedPolls);
                    if (count == callers)
                    {
                        allInitialPolls.TrySetResult();
                    }

                    await allInitialPolls.Task.WaitAsync(cancellationToken);
                    return new HttpResponseMessage(HttpStatusCode.Unauthorized);
                }

                Interlocked.Increment(ref successfulPolls);
                return JsonResponse(HttpStatusCode.OK, MessageJson(42));
            }

            return NotFoundResponse();
        });
        MessageSessionClient session = await fixture.Client.CreateMessageSessionAsync(LargeScaleSetId, "runner-host", TestContext.Current.CancellationToken);
        RunnerScaleSetSession priorSnapshot = session.Session;

        Task<RunnerScaleSetMessage?>[] polls = Enumerable.Range(0, callers)
            .Select(_ => session.GetMessageAsync(0, 10, TestContext.Current.CancellationToken))
            .ToArray();
        await refreshStarted.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        releaseRefresh.TrySetResult();
        RunnerScaleSetMessage?[] messages = await Task.WhenAll(polls);

        Assert.All(messages, message => Assert.Equal(42, message?.MessageId));
        Assert.Equal(callers, Volatile.Read(ref initialUnauthorizedPolls));
        Assert.Equal(callers, Volatile.Read(ref successfulPolls));
        Assert.Equal(2 * callers, Volatile.Read(ref totalPollRequests));
        Assert.Equal(1, Volatile.Read(ref refreshRequests));
        Assert.NotSame(priorSnapshot, session.Session);
        Assert.Equal(priorSnapshot.SessionId, session.Session.SessionId);
        Assert.Equal(priorSnapshot.MessageQueueAccessToken, session.Session.MessageQueueAccessToken);
    }

    [Fact]
    public async Task CanceledRefreshOwnerDoesNotCancelIndependentAcknowledgment()
    {
        int refreshRequests = 0;
        int acknowledgmentRequests = 0;
        TaskCompletionSource refreshStarted = NewSignal();
        TaskCompletionSource firstRefreshCanceled = NewSignal();
        TaskCompletionSource acknowledgmentUnauthorized = NewSignal();
        TaskCompletionSource holdFirstRefresh = NewSignal();
        using ClientFixture fixture = new(async (request, cancellationToken) =>
        {
            if (IsCreateSession(request))
            {
                return JsonResponse(HttpStatusCode.OK, SessionJson(QueueToken));
            }

            if (IsRefresh(request))
            {
                int attempt = Interlocked.Increment(ref refreshRequests);
                if (attempt == 1)
                {
                    refreshStarted.TrySetResult();
                    try
                    {
                        await holdFirstRefresh.Task.WaitAsync(cancellationToken);
                    }
                    catch (OperationCanceledException)
                    {
                        firstRefreshCanceled.TrySetResult();
                        throw;
                    }
                }

                return JsonResponse(HttpStatusCode.OK, SessionJson(RefreshedQueueToken));
            }

            if (IsQueueRequest(request, HttpMethod.Get))
            {
                return new HttpResponseMessage(HttpStatusCode.Unauthorized);
            }

            if (IsQueueRequest(request, HttpMethod.Delete))
            {
                int attempt = Interlocked.Increment(ref acknowledgmentRequests);
                if (attempt == 1)
                {
                    acknowledgmentUnauthorized.TrySetResult();
                    return new HttpResponseMessage(HttpStatusCode.Unauthorized);
                }

                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }

            return NotFoundResponse();
        });
        MessageSessionClient session = await fixture.Client.CreateMessageSessionAsync(
            LargeScaleSetId,
            "runner-host",
            TestContext.Current.CancellationToken);
        using CancellationTokenSource pollCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        using CancellationTokenSource acknowledgmentCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);

        Task<RunnerScaleSetMessage?> poll = session.GetMessageAsync(0, 10, pollCancellation.Token);
        await refreshStarted.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Task acknowledgment = session.DeleteMessageAsync(LargeMessageId, acknowledgmentCancellation.Token);
        await acknowledgmentUnauthorized.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await Task.Yield();
        pollCancellation.Cancel();

        await firstRefreshCanceled.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => poll);
        await acknowledgment;

        Assert.Equal(2, Volatile.Read(ref refreshRequests));
        Assert.Equal(2, Volatile.Read(ref acknowledgmentRequests));
        Assert.Equal(RefreshedQueueToken, session.Session.MessageQueueAccessToken);
    }

    [Fact]
    public async Task FailedRefreshDoesNotPreventALaterOperationFromRefreshing()
    {
        int refreshRequests = 0;
        int polls = 0;
        using ClientFixture fixture = new((request, _) =>
        {
            if (IsCreateSession(request))
            {
                return Task.FromResult(JsonResponse(HttpStatusCode.OK, SessionJson(QueueToken)));
            }

            if (IsRefresh(request))
            {
                int attempt = Interlocked.Increment(ref refreshRequests);
                return Task.FromResult(attempt == 1
                    ? JsonResponse(HttpStatusCode.ServiceUnavailable, "retry later")
                    : JsonResponse(HttpStatusCode.OK, SessionJson(RefreshedQueueToken)));
            }

            if (IsQueueRequest(request, HttpMethod.Get))
            {
                int attempt = Interlocked.Increment(ref polls);
                return Task.FromResult(attempt < 3
                    ? new HttpResponseMessage(HttpStatusCode.Unauthorized)
                    : JsonResponse(HttpStatusCode.OK, MessageJson(88)));
            }

            return Task.FromResult(NotFoundResponse());
        });
        MessageSessionClient session = await fixture.Client.CreateMessageSessionAsync(LargeScaleSetId, "runner-host", TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<ScaleSetApiException>(
            () => session.GetMessageAsync(0, 10, TestContext.Current.CancellationToken));
        RunnerScaleSetMessage? recovered = await session.GetMessageAsync(0, 10, TestContext.Current.CancellationToken);

        Assert.Equal(88, recovered?.MessageId);
        Assert.Equal(2, Volatile.Read(ref refreshRequests));
        Assert.Equal(3, Volatile.Read(ref polls));
        Assert.Equal(RefreshedQueueToken, session.Session.MessageQueueAccessToken);
    }

    [Fact]
    public async Task CreateMessageSessionAsync_RejectsJsonNull()
    {
        using ClientFixture fixture = new((request, _) => Task.FromResult(
            IsCreateSession(request)
                ? JsonResponse(HttpStatusCode.OK, "null")
                : NotFoundResponse()));

        ScaleSetApiException exception = await Assert.ThrowsAsync<ScaleSetApiException>(
            () => fixture.Client.CreateMessageSessionAsync(LargeScaleSetId, "runner-host", TestContext.Current.CancellationToken));

        Assert.Equal((int)HttpStatusCode.OK, exception.StatusCode);
        Assert.Single(fixture.Handler.Requests, IsCreateSession);
    }

    [Fact]
    public async Task RefreshSession_RejectsJsonNullWithoutReplacingCurrentSession()
    {
        using ClientFixture fixture = new((request, _) =>
        {
            if (IsCreateSession(request))
            {
                return Task.FromResult(JsonResponse(HttpStatusCode.OK, SessionJson(QueueToken)));
            }

            if (IsRefresh(request))
            {
                return Task.FromResult(JsonResponse(HttpStatusCode.OK, "null"));
            }

            if (IsQueueRequest(request, HttpMethod.Get))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
            }

            return Task.FromResult(NotFoundResponse());
        });
        MessageSessionClient session = await fixture.Client.CreateMessageSessionAsync(LargeScaleSetId, "runner-host", TestContext.Current.CancellationToken);

        ScaleSetApiException exception = await Assert.ThrowsAsync<ScaleSetApiException>(
            () => session.GetMessageAsync(0, 10, TestContext.Current.CancellationToken));

        Assert.Equal((int)HttpStatusCode.OK, exception.StatusCode);
        Assert.Equal(QueueToken, session.Session.MessageQueueAccessToken);
        Assert.Equal(SessionId, session.Session.SessionId);
    }

    [Fact]
    public async Task DeleteMessageSessionAsyncRetiresExactIdWithFreshAdministrationCredentials()
    {
        using ClientFixture fixture = new((request, _) => Task.FromResult(IsClose(request)
            ? new HttpResponseMessage(HttpStatusCode.NoContent)
            : NotFoundResponse()));

        await fixture.Client.DeleteMessageSessionAsync(LargeScaleSetId, SessionId, TestContext.Current.CancellationToken);

        RecordedRequest close = Assert.Single(fixture.Handler.Requests, IsClose);
        Assert.Equal("actions.example.test", close.Uri.Host);
        Assert.Equal($"/tenant/123/_apis/runtime/runnerscalesets/{LargeScaleSetId}/sessions/{SessionId:D}", close.Uri.AbsolutePath);
        Assert.Equal("Bearer " + DiscoveryJwt, close.Authorization);
        Assert.Empty(close.Body);
        Assert.DoesNotContain(fixture.Handler.Requests, IsCreateSession);
        Assert.DoesNotContain(fixture.Handler.Requests, request => request.Uri.Host == "queue.example.test");
    }

    [Fact]
    public async Task DeleteMessageSessionAsyncValidatesIdentifiersBeforeAdministrationDiscovery()
    {
        using ClientFixture fixture = new((_, _) => Task.FromResult(NotFoundResponse()));
        CancellationToken token = TestContext.Current.CancellationToken;

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => fixture.Client.DeleteMessageSessionAsync(0, SessionId, token));
        await Assert.ThrowsAsync<ArgumentException>(
            () => fixture.Client.DeleteMessageSessionAsync(LargeScaleSetId, Guid.Empty, token));

        Assert.Empty(fixture.Handler.Requests);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task DeleteMessageSessionAsyncKeepsClosedOrMissingResponsesAsFailures(HttpStatusCode status)
    {
        const string expired = """{"typeKey":"RunnerScaleSetSessionExpiredException"}""";
        using ClientFixture fixture = new((_, _) => Task.FromResult(JsonResponse(status, expired)));

        var error = await Assert.ThrowsAsync<ScaleSetApiException>(
            () => fixture.Client.DeleteMessageSessionAsync(LargeScaleSetId, SessionId, TestContext.Current.CancellationToken));

        Assert.Equal((int)status, error.StatusCode);
        Assert.Contains("RunnerScaleSetSessionExpiredException", error.BodyPreview, StringComparison.Ordinal);
        Assert.Single(fixture.Handler.Requests, IsClose);
    }

    [Fact]
    public async Task DeleteMessageSessionAsyncDoesNotRepeatAnUncertainDeleteUnderRetryPolicy()
    {
        using ClientFixture fixture = new((_, _) => Task.FromException<HttpResponseMessage>(
            new HttpRequestException("Simulated loss of the close response.")));
        using IDisposable retry = ScaleSetRequestRetry.Configure(static options => options with
        {
            MaxRetries = 2,
            WaitMin = TimeSpan.Zero,
            ShouldRetry = static (_, _) => ValueTask.FromResult(true),
        });

        var error = await Assert.ThrowsAsync<ScaleSetApiException>(
            () => fixture.Client.DeleteMessageSessionAsync(LargeScaleSetId, SessionId, TestContext.Current.CancellationToken));

        Assert.Null(error.StatusCode);
        Assert.True(error.IsTransientTransportFailure);
        Assert.Single(fixture.Handler.Requests, IsClose);
    }

    [Fact]
    public async Task CloseAsyncUsesAdminCredentialIsIdempotentAndRejectsLaterOperations()
    {
        int closes = 0;
        int polls = 0;
        using ClientFixture fixture = new((request, _) =>
        {
            if (IsCreateSession(request))
            {
                return Task.FromResult(JsonResponse(HttpStatusCode.OK, SessionJson(QueueToken)));
            }

            if (IsClose(request))
            {
                Interlocked.Increment(ref closes);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
            }

            if (IsQueueRequest(request, HttpMethod.Get))
            {
                Interlocked.Increment(ref polls);
                return Task.FromResult(JsonResponse(HttpStatusCode.OK, MessageJson(1)));
            }

            return Task.FromResult(NotFoundResponse());
        });
        IMessageSession session = Assert.IsAssignableFrom<IMessageSession>(await fixture.Client.CreateMessageSessionAsync(
            LargeScaleSetId, "runner-host", TestContext.Current.CancellationToken));

        await session.CloseAsync(TestContext.Current.CancellationToken);
        await session.CloseAsync(TestContext.Current.CancellationToken);

        RecordedRequest close = Assert.Single(fixture.Handler.Requests, IsClose);
        Assert.Equal("Bearer " + DiscoveryJwt, close.Authorization);
        Assert.Equal($"/tenant/123/_apis/runtime/runnerscalesets/{LargeScaleSetId}/sessions/{SessionId:D}", close.Uri.AbsolutePath);
        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => session.GetMessageAsync(0, 10, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => session.AcquireJobsAsync([1], TestContext.Current.CancellationToken));
        Assert.Equal(0, Volatile.Read(ref polls));
        Assert.Equal(1, Volatile.Read(ref closes));

        fixture.Client.Dispose();
        using HttpResponseMessage transportProbe = await fixture.HttpClient.GetAsync(
            "https://actions.example.test/transport-ownership-probe", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, transportProbe.StatusCode);
    }

    [Fact]
    public async Task CloseFailureCanBeRetried()
    {
        int closes = 0;
        using ClientFixture fixture = new((request, _) =>
        {
            if (IsCreateSession(request))
            {
                return Task.FromResult(JsonResponse(HttpStatusCode.OK, SessionJson(QueueToken)));
            }

            if (IsClose(request))
            {
                int attempt = Interlocked.Increment(ref closes);
                return Task.FromResult(attempt == 1
                    ? JsonResponse(HttpStatusCode.ServiceUnavailable, "retry close")
                    : new HttpResponseMessage(HttpStatusCode.NoContent));
            }

            return Task.FromResult(NotFoundResponse());
        });
        MessageSessionClient session = await fixture.Client.CreateMessageSessionAsync(LargeScaleSetId, "runner-host", TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<ScaleSetApiException>(() => session.CloseAsync(TestContext.Current.CancellationToken));
        await session.CloseAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, Volatile.Read(ref closes));
        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => session.DeleteMessageAsync(1, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CancelDuringRefreshThenCloseWaitsUntilRefreshCompletes()
    {
        TaskCompletionSource refreshStarted = NewSignal();
        TaskCompletionSource refreshCancellationObserved = NewSignal();
        TaskCompletionSource finishRefreshHandler = NewSignal();
        TaskCompletionSource refreshHandlerFinished = NewSignal();
        bool closeSawRefreshFinished = false;
        using ClientFixture fixture = new(async (request, cancellationToken) =>
        {
            if (IsCreateSession(request))
            {
                return JsonResponse(HttpStatusCode.OK, SessionJson(QueueToken));
            }

            if (IsQueueRequest(request, HttpMethod.Get))
            {
                return new HttpResponseMessage(HttpStatusCode.Unauthorized);
            }

            if (IsRefresh(request))
            {
                refreshStarted.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    refreshCancellationObserved.TrySetResult();
                    await finishRefreshHandler.Task;
                    refreshHandlerFinished.TrySetResult();
                    throw;
                }
            }

            if (IsClose(request))
            {
                closeSawRefreshFinished = refreshHandlerFinished.Task.IsCompleted;
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }

            return NotFoundResponse();
        });
        MessageSessionClient session = await fixture.Client.CreateMessageSessionAsync(LargeScaleSetId, "runner-host", TestContext.Current.CancellationToken);
        using CancellationTokenSource operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);

        Task<RunnerScaleSetMessage?> poll = session.GetMessageAsync(0, 10, operationCancellation.Token);
        await refreshStarted.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Task close = session.CloseAsync(TestContext.Current.CancellationToken);
        operationCancellation.Cancel();
        await refreshCancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.False(poll.IsCompleted);
        Assert.DoesNotContain(fixture.Handler.Requests, IsClose);
        finishRefreshHandler.TrySetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => poll);
        await close;

        Assert.True(closeSawRefreshFinished);
        Assert.Single(fixture.Handler.Requests, IsClose);
    }

    [Theory]
    [InlineData("create")]
    [InlineData("poll")]
    [InlineData("ack")]
    [InlineData("acquire")]
    [InlineData("refresh")]
    [InlineData("close")]
    public async Task CancellationTokenReachesEverySessionRequest(string operation)
    {
        bool blockOperation = false;
        TaskCompletionSource requestStarted = NewSignal();
        using ClientFixture fixture = new(async (request, cancellationToken) =>
        {
            if (operation == "create" && IsCreateSession(request))
            {
                requestStarted.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            if (operation != "create" && blockOperation && IsCancellationTarget(request, operation))
            {
                requestStarted.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            if (IsCreateSession(request))
            {
                return JsonResponse(HttpStatusCode.OK, SessionJson(QueueToken));
            }

            if (operation == "refresh" && IsQueueRequest(request, HttpMethod.Get))
            {
                return new HttpResponseMessage(HttpStatusCode.Unauthorized);
            }

            return DefaultSessionResponse(request);
        });

        MessageSessionClient? session = null;
        if (operation != "create")
        {
            session = await fixture.Client.CreateMessageSessionAsync(LargeScaleSetId, "runner-host", TestContext.Current.CancellationToken);
            blockOperation = true;
        }

        using CancellationTokenSource cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        Task call = operation == "create"
            ? fixture.Client.CreateMessageSessionAsync(LargeScaleSetId, "runner-host", cancellation.Token)
            : InvokeCancellationTargetAsync(operation, session!, cancellation.Token);
        await requestStarted.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call);
    }

    [Fact]
    public async Task SessionMutationsDoNotRetryTransientFailuresAutomatically()
    {
        int creates = 0;
        using ClientFixture createFixture = new((request, _) =>
        {
            if (IsCreateSession(request))
            {
                Interlocked.Increment(ref creates);
                return Task.FromResult(JsonResponse(HttpStatusCode.ServiceUnavailable, "no create retry"));
            }

            return Task.FromResult(NotFoundResponse());
        });

        await Assert.ThrowsAsync<ScaleSetApiException>(
            () => createFixture.Client.CreateMessageSessionAsync(LargeScaleSetId, "runner-host", TestContext.Current.CancellationToken));
        Assert.Equal(1, Volatile.Read(ref creates));

        int acquires = 0;
        int acknowledgments = 0;
        using ClientFixture operationFixture = new((request, _) =>
        {
            if (IsCreateSession(request))
            {
                return Task.FromResult(JsonResponse(HttpStatusCode.OK, SessionJson(QueueToken)));
            }

            if (IsAcquireJobs(request))
            {
                Interlocked.Increment(ref acquires);
                return Task.FromResult(JsonResponse(HttpStatusCode.ServiceUnavailable, "no acquire retry"));
            }

            if (IsQueueRequest(request, HttpMethod.Delete))
            {
                Interlocked.Increment(ref acknowledgments);
                return Task.FromResult(JsonResponse(HttpStatusCode.ServiceUnavailable, "no ack retry"));
            }

            return Task.FromResult(DefaultSessionResponse(request));
        });
        MessageSessionClient session = await operationFixture.Client.CreateMessageSessionAsync(LargeScaleSetId, "runner-host", TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<ScaleSetApiException>(
            () => session.AcquireJobsAsync([long.MaxValue], TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ScaleSetApiException>(
            () => session.DeleteMessageAsync(LargeMessageId, TestContext.Current.CancellationToken));

        Assert.Equal(1, Volatile.Read(ref acquires));
        Assert.Equal(1, Volatile.Read(ref acknowledgments));
    }

    private static async Task InvokeSessionOperationAsync(string operation, MessageSessionClient session)
    {
        switch (operation)
        {
            case "poll":
                _ = await session.GetMessageAsync(0, 10, TestContext.Current.CancellationToken);
                break;
            case "ack":
                await session.DeleteMessageAsync(7, TestContext.Current.CancellationToken);
                break;
            case "acquire":
                _ = await session.AcquireJobsAsync([long.MaxValue], TestContext.Current.CancellationToken);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(operation));
        }
    }

    private static async Task InvokeCancellationTargetAsync(
        string operation,
        MessageSessionClient session,
        CancellationToken cancellationToken)
    {
        switch (operation)
        {
            case "poll":
            case "refresh":
                _ = await session.GetMessageAsync(0, 10, cancellationToken);
                break;
            case "ack":
                await session.DeleteMessageAsync(7, cancellationToken);
                break;
            case "acquire":
                _ = await session.AcquireJobsAsync([1], cancellationToken);
                break;
            case "close":
                await session.CloseAsync(cancellationToken);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(operation));
        }
    }

    private static bool IsCancellationTarget(RecordedRequest request, string operation) => operation switch
    {
        "poll" => IsQueueRequest(request, HttpMethod.Get),
        "ack" => IsQueueRequest(request, HttpMethod.Delete),
        "acquire" => IsAcquireJobs(request),
        "refresh" => IsRefresh(request),
        "close" => IsClose(request),
        _ => false,
    };

    private static bool IsSessionOperation(RecordedRequest request, string operation) => operation switch
    {
        "poll" => IsQueueRequest(request, HttpMethod.Get),
        "ack" => IsQueueRequest(request, HttpMethod.Delete),
        "acquire" => IsAcquireJobs(request),
        _ => false,
    };

    private static HttpResponseMessage SuccessForOperation(string operation) => operation switch
    {
        "poll" => JsonResponse(HttpStatusCode.OK, MessageJson(17)),
        "ack" => new HttpResponseMessage(HttpStatusCode.NoContent),
        "acquire" => JsonResponse(HttpStatusCode.OK, "{\"count\":1,\"value\":[9223372036854775807]}"),
        _ => throw new ArgumentOutOfRangeException(nameof(operation)),
    };

    private static bool IsCreateSession(RecordedRequest request) =>
        request.Method == HttpMethod.Post && request.Uri.AbsolutePath.EndsWith("/sessions", StringComparison.Ordinal);

    private static bool IsRefresh(RecordedRequest request) =>
        request.Method == HttpMethod.Patch && request.Uri.AbsolutePath.Contains("/sessions/", StringComparison.Ordinal);

    private static bool IsClose(RecordedRequest request) =>
        request.Method == HttpMethod.Delete && request.Uri.AbsolutePath.Contains("/sessions/", StringComparison.Ordinal);

    private static bool IsAcquireJobs(RecordedRequest request) =>
        request.Method == HttpMethod.Post && request.Uri.AbsolutePath.EndsWith("/acquirejobs", StringComparison.Ordinal);

    private static bool IsQueueRequest(RecordedRequest request, HttpMethod method) =>
        request.Method == method && string.Equals(request.Uri.Host, "queue.example.test", StringComparison.Ordinal);

    private static HttpResponseMessage DefaultSessionResponse(RecordedRequest request)
    {
        if (IsCreateSession(request))
        {
            return JsonResponse(HttpStatusCode.OK, SessionJson(QueueToken));
        }

        if (IsRefresh(request))
        {
            return JsonResponse(HttpStatusCode.OK, SessionJson(RefreshedQueueToken));
        }

        if (IsQueueRequest(request, HttpMethod.Get))
        {
            return JsonResponse(HttpStatusCode.OK, MessageJson(1));
        }

        if (IsQueueRequest(request, HttpMethod.Delete) || IsClose(request))
        {
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        }

        if (IsAcquireJobs(request))
        {
            return JsonResponse(HttpStatusCode.OK, "{\"count\":0,\"value\":[]}");
        }

        return NotFoundResponse();
    }

    private static RunnerScaleSetSession ValidSession(string queueToken, string queueUrl = QueueUri) => new()
    {
        SessionId = SessionId,
        OwnerName = "runner-host",
        MessageQueueUrl = queueUrl,
        MessageQueueAccessToken = queueToken,
        Statistics = new RunnerScaleSetStatistic
        {
            TotalAvailableJobs = 3,
            TotalAcquiredJobs = 2,
            TotalAssignedJobs = 1,
            TotalRunningJobs = 1,
            TotalRegisteredRunners = 4,
            TotalBusyRunners = 1,
            TotalIdleRunners = 3,
        },
    };

    private static string SessionJson(string queueToken, string queueUrl = QueueUri) =>
        ScaleSetJson.Serialize(ValidSession(queueToken, queueUrl));

    private static string MessageJson(long messageId) =>
        $"{{\"messageId\":{messageId},\"messageType\":\"RunnerScaleSetJobMessages\"}}";

    private static HttpResponseMessage JsonResponse(HttpStatusCode statusCode, string body) => new(statusCode)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    private static HttpResponseMessage NotFoundResponse() => JsonResponse(HttpStatusCode.NotFound, "not found");

    private static KeyValuePair<string, string>[] ParseQuery(Uri uri)
    {
        return uri.Query.TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(parameter => parameter.Split('=', 2))
            .Select(parts => new KeyValuePair<string, string>(
                WebUtility.UrlDecode(parts[0]) ?? string.Empty,
                parts.Length == 1 ? string.Empty : WebUtility.UrlDecode(parts[1]) ?? string.Empty))
            .ToArray();
    }

    private static string? QueryValue(KeyValuePair<string, string>[] query, string key) =>
        query.LastOrDefault(pair => pair.Key == key).Value;

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class ClientFixture : IDisposable
    {
        public ClientFixture(Func<RecordedRequest, CancellationToken, Task<HttpResponseMessage>> responder)
        {
            Handler = new RecordingHandler(DispatchAsync);
            HttpClient = new HttpClient(Handler);
            Client = new ScaleSetClient(
                GitHubTarget.Parse("https://github.com/example-org/example-repo"),
                ScaleSetCredentials.FromPersonalAccessToken("synthetic-pat"),
                httpClient: HttpClient);
            _responder = responder;
        }

        private readonly Func<RecordedRequest, CancellationToken, Task<HttpResponseMessage>> _responder;

        public RecordingHandler Handler { get; }

        public HttpClient HttpClient { get; }

        public ScaleSetClient Client { get; }

        public void Dispose()
        {
            Client.Dispose();
            HttpClient.Dispose();
        }

        private async Task<HttpResponseMessage> DispatchAsync(RecordedRequest request, CancellationToken cancellationToken)
        {
            if (request.Uri.Host == "api.github.com" && request.Uri.AbsolutePath.EndsWith("/runners/registration-token", StringComparison.Ordinal))
            {
                return JsonResponse(HttpStatusCode.Created, "{\"token\":\"synthetic-registration-token\",\"expires_at\":\"2099-01-01T00:00:00Z\"}");
            }

            if (request.Uri.Host == "api.github.com" && request.Uri.AbsolutePath == "/actions/runner-registration")
            {
                return JsonResponse(HttpStatusCode.OK, $"{{\"url\":\"https://actions.example.test/tenant/123/\",\"token\":\"{DiscoveryJwt}\"}}");
            }

            return await _responder(request, cancellationToken).ConfigureAwait(false);
        }
    }

    private sealed class RecordingHandler(
        Func<RecordedRequest, CancellationToken, Task<HttpResponseMessage>> responder) : HttpMessageHandler
    {
        private readonly ConcurrentQueue<RecordedRequest> _requests = new();

        public IReadOnlyList<RecordedRequest> Requests => _requests.ToArray();

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Uri requestUri = request.RequestUri ?? throw new InvalidOperationException("Request URI is missing.");
            string body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            string? maxCapacity = request.Headers.TryGetValues("X-ScaleSetMaxCapacity", out IEnumerable<string>? values)
                ? values.Single()
                : null;
            RecordedRequest record = new(
                request.Method,
                requestUri,
                request.Headers.Authorization?.ToString(),
                request.Headers.Accept.FirstOrDefault()?.ToString(),
                request.Content?.Headers.ContentType?.ToString(),
                maxCapacity,
                body);
            _requests.Enqueue(record);
            return await responder(record, cancellationToken).ConfigureAwait(false);
        }
    }

    private sealed record RecordedRequest(
        HttpMethod Method,
        Uri Uri,
        string? Authorization,
        string? Accept,
        string? ContentType,
        string? MaxCapacity,
        string Body);
}
