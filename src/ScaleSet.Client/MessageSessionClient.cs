using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;

namespace ScaleSet.Client;

/// <summary>Uses an initialized runner scale set session to poll and acknowledge queue messages.</summary>
/// <remarks>The caller owns this client and must call <see cref="CloseAsync"/> when the session is no longer needed.</remarks>
public sealed class MessageSessionClient : IMessageSession
{
    private const string ScaleSetPath = "/_apis/runtime/runnerscalesets";
    private readonly ScaleSetClient _client;
    private readonly long _scaleSetId;
    private readonly string _ownerName;
    private readonly object _lifecycleSync = new();
    private readonly object _refreshSync = new();
    private RunnerScaleSetSession _session;
    private TaskCompletionSource _operationsDrained = CreateCompletedSource();
    private TaskCompletionSource? _closeCompletion;
    private RefreshFlight? _refreshFlight;
    private int _activeOperations;
    private SessionLifecycleState _lifecycleState;

    internal MessageSessionClient(
        ScaleSetClient client,
        long scaleSetId,
        string ownerName,
        RunnerScaleSetSession session)
    {
        _client = client;
        _scaleSetId = scaleSetId;
        _ownerName = ownerName;
        _session = session;
    }

    /// <inheritdoc />
    public RunnerScaleSetSession Session => Volatile.Read(ref _session);

    /// <inheritdoc />
    public Task<RunnerScaleSetMessage?> GetMessageAsync(
        long lastMessageId,
        int maxCapacity,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maxCapacity);
        return ExecuteWithQueueRefreshAsync(
            async (session, token) =>
            {
                var queueUri = GetQueueUri(session);
                if (lastMessageId > 0)
                {
                    queueUri = SetQueryParameter(queueUri, "lastMessageId", lastMessageId.ToString(CultureInfo.InvariantCulture));
                }

                var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["X-ScaleSetMaxCapacity"] = maxCapacity.ToString(CultureInfo.InvariantCulture),
                };
                return await _client.SendQueueAsync<RunnerScaleSetMessage>(
                    HttpMethod.Get,
                    queueUri,
                    session.MessageQueueAccessToken!,
                    body: null,
                    headers: headers,
                    allowAccepted: true,
                    token,
                    response => RunnerScaleSetMessageParser.Parse(response)).ConfigureAwait(false);
            },
            cancellationToken);
    }

    /// <inheritdoc />
    public Task DeleteMessageAsync(long messageId, CancellationToken cancellationToken)
    {
        return ExecuteWithQueueRefreshAsync(
            async (session, token) =>
            {
                var messageUri = AppendPathSegment(GetQueueUri(session), messageId.ToString(CultureInfo.InvariantCulture));
                await _client.SendQueueAsync(
                    HttpMethod.Delete,
                    messageUri,
                    session.MessageQueueAccessToken!,
                    body: null,
                    headers: null,
                    allowAccepted: false,
                    token).ConfigureAwait(false);
                return true;
            },
            cancellationToken);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<long>> AcquireJobsAsync(
        IReadOnlyList<long> runnerRequestIds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(runnerRequestIds);
        var requestIds = runnerRequestIds.ToArray();
        var body = JsonSerializer.SerializeToUtf8Bytes(requestIds);

        return ExecuteWithQueueRefreshAsync<IReadOnlyList<long>>(
            async (session, token) =>
            {
                var result = await _client.SendActionsAsync<AcquireJobsResponse>(
                    HttpMethod.Post,
                    $"{ScaleSetPath}/{_scaleSetId.ToString(CultureInfo.InvariantCulture)}/acquirejobs",
                    body,
                    query: null,
                    expectedStatus: 200,
                    token,
                    response => DeserializeResponse<AcquireJobsResponse>(response),
                    bearerTokenOverride: session.MessageQueueAccessToken).ConfigureAwait(false);
                return result.Value is null ? Array.Empty<long>() : Array.AsReadOnly(result.Value.ToArray());
            },
            cancellationToken);
    }

    /// <inheritdoc />
    public async Task CloseAsync(CancellationToken cancellationToken = default)
    {
        TaskCompletionSource completion;
        Task operationsDrained = Task.CompletedTask;
        var ownsClose = false;
        lock (_lifecycleSync)
        {
            if (_lifecycleState == SessionLifecycleState.Closed)
            {
                return;
            }

            if (_lifecycleState == SessionLifecycleState.Closing)
            {
                completion = _closeCompletion!;
            }
            else
            {
                _lifecycleState = SessionLifecycleState.Closing;
                completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _closeCompletion = completion;
                operationsDrained = _operationsDrained.Task;
                ownsClose = true;
            }
        }

        if (!ownsClose)
        {
            await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        try
        {
            await operationsDrained.WaitAsync(cancellationToken).ConfigureAwait(false);
            var session = Session;
            await _client.DeleteMessageSessionCoreAsync(_scaleSetId, session.SessionId, cancellationToken).ConfigureAwait(false);

            lock (_lifecycleSync)
            {
                _lifecycleState = SessionLifecycleState.Closed;
                _closeCompletion = null;
            }

            completion.TrySetResult();
        }
        catch (OperationCanceledException exception)
        {
            ResetCloseAfterFailure(completion);
            completion.TrySetCanceled(exception.CancellationToken);
            await completion.Task.ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            ResetCloseAfterFailure(completion);
            completion.TrySetException(exception);
            await completion.Task.ConfigureAwait(false);
        }
    }

    internal static RunnerScaleSetSession ParseAndValidateSession(byte[] response, string ownerName)
    {
        var json = Encoding.UTF8.GetString(TrimUtf8Bom(response)).TrimStart('\uFEFF');
        var session = ScaleSetJson.Deserialize<RunnerScaleSetSession>(json)
            ?? throw new JsonException("The message session response was empty.");
        return ValidateSession(session, ownerName);
    }

    private async Task<TResult> ExecuteWithQueueRefreshAsync<TResult>(
        Func<RunnerScaleSetSession, CancellationToken, Task<TResult>> operation,
        CancellationToken cancellationToken)
    {
        BeginOperation();
        try
        {
            var expiredSession = Session;
            try
            {
                return await operation(expiredSession, cancellationToken).ConfigureAwait(false);
            }
            catch (ScaleSetApiException exception) when (IsQueueUnauthorized(exception))
            {
                await RefreshSessionAsync(expiredSession, cancellationToken).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            return await operation(Session, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            EndOperation();
        }
    }

    private async Task RefreshSessionAsync(
        RunnerScaleSetSession expiredSession,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RefreshFlight flight;
            var ownsRefresh = false;
            lock (_refreshSync)
            {
                var currentSession = Session;
                if (!ReferenceEquals(currentSession, expiredSession) ||
                    currentSession.SessionId != expiredSession.SessionId ||
                    !string.Equals(
                        currentSession.MessageQueueAccessToken,
                        expiredSession.MessageQueueAccessToken,
                        StringComparison.Ordinal))
                {
                    return;
                }

                if (_refreshFlight is not null && ReferenceEquals(_refreshFlight.Snapshot, expiredSession))
                {
                    flight = _refreshFlight;
                }
                else
                {
                    flight = new RefreshFlight(expiredSession);
                    _refreshFlight = flight;
                    ownsRefresh = true;
                }
            }

            if (ownsRefresh)
            {
                await CompleteRefreshAsync(flight, cancellationToken).ConfigureAwait(false);
            }

            try
            {
                await flight.Completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (OperationCanceledException) when (
                !cancellationToken.IsCancellationRequested &&
                flight.Completion.Task.IsCanceled)
            {
                // Another operation owned this refresh and canceled its request. Retry under this caller's token.
            }
        }
    }

    private async Task CompleteRefreshAsync(RefreshFlight flight, CancellationToken cancellationToken)
    {
        Exception? failure = null;
        OperationCanceledException? cancellationFailure = null;
        try
        {
            var session = flight.Snapshot;
            var refreshedSession = await _client.SendActionsAsync<RunnerScaleSetSession>(
                HttpMethod.Patch,
                $"{ScaleSetPath}/{_scaleSetId.ToString(CultureInfo.InvariantCulture)}/sessions/{session.SessionId:D}",
                body: null,
                query: null,
                expectedStatus: 200,
                cancellationToken,
                response => ParseAndValidateSession(response, _ownerName)).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (refreshedSession.SessionId != session.SessionId)
            {
                throw new InvalidDataException("A queue token refresh returned a different message session.");
            }

            Volatile.Write(ref _session, refreshedSession);
        }
        catch (OperationCanceledException exception)
        {
            cancellationFailure = exception;
        }
        catch (Exception exception)
        {
            failure = exception;
        }
        lock (_refreshSync)
        {
            if (ReferenceEquals(_refreshFlight, flight))
            {
                _refreshFlight = null;
            }

            if (cancellationFailure is not null)
            {
                flight.Completion.TrySetCanceled(cancellationFailure.CancellationToken);
            }
            else if (failure is not null)
            {
                flight.Completion.TrySetException(failure);
            }
            else
            {
                flight.Completion.TrySetResult();
            }
        }
    }

    private void ResetCloseAfterFailure(TaskCompletionSource completion)
    {
        lock (_lifecycleSync)
        {
            if (_lifecycleState == SessionLifecycleState.Closing && ReferenceEquals(_closeCompletion, completion))
            {
                _lifecycleState = SessionLifecycleState.Open;
                _closeCompletion = null;
            }
        }
    }

    private static bool IsQueueUnauthorized(ScaleSetApiException exception) =>
        exception.StatusCode == 401 &&
        exception.DomainError == ScaleSetDomainError.MessageQueueTokenExpired;

    private void BeginOperation()
    {
        lock (_lifecycleSync)
        {
            if (_lifecycleState != SessionLifecycleState.Open)
            {
                throw new ObjectDisposedException(nameof(MessageSessionClient), "The message session is closing or closed.");
            }

            if (_activeOperations++ == 0)
            {
                _operationsDrained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }
    }

    private void EndOperation()
    {
        lock (_lifecycleSync)
        {
            if (--_activeOperations == 0)
            {
                _operationsDrained.TrySetResult();
            }
        }
    }

    private static TaskCompletionSource CreateCompletedSource()
    {
        var source = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        source.SetResult();
        return source;
    }

    private static Uri GetQueueUri(RunnerScaleSetSession session)
    {
        if (!Uri.TryCreate(session.MessageQueueUrl, UriKind.Absolute, out var queueUri))
        {
            throw new InvalidDataException("The message session contains an invalid queue URL.");
        }

        return queueUri;
    }

    private static RunnerScaleSetSession ValidateSession(RunnerScaleSetSession session, string fallbackOwnerName)
    {
        if (session.SessionId == Guid.Empty)
        {
            throw new InvalidDataException("The message session response does not contain a valid session ID.");
        }

        if (!Uri.TryCreate(session.MessageQueueUrl, UriKind.Absolute, out var queueUri) ||
            !string.Equals(queueUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrEmpty(queueUri.Host) ||
            queueUri.UserInfo.Length != 0 ||
            queueUri.Fragment.Length != 0)
        {
            throw new InvalidDataException("The message session response does not contain a valid HTTPS queue URL.");
        }

        if (string.IsNullOrWhiteSpace(session.MessageQueueAccessToken) ||
            session.MessageQueueAccessToken.Contains('\r') ||
            session.MessageQueueAccessToken.Contains('\n'))
        {
            throw new InvalidDataException("The message session response does not contain a valid queue access token.");
        }

        if (string.IsNullOrWhiteSpace(session.OwnerName) && string.IsNullOrWhiteSpace(fallbackOwnerName))
        {
            throw new InvalidDataException("The message session response does not contain an owner name.");
        }

        ValidateStatistics(session.Statistics);
        if (session.RunnerScaleSet is { } scaleSet)
        {
            ValidateStatistics(scaleSet.Statistics);
            session = session with
            {
                RunnerScaleSet = scaleSet with
                {
                    Labels = scaleSet.Labels is null
                        ? null
                        : Array.AsReadOnly(scaleSet.Labels.ToArray()),
                },
            };
        }

        return session with
        {
            OwnerName = string.IsNullOrWhiteSpace(session.OwnerName) ? fallbackOwnerName : session.OwnerName,
        };
    }

    private static void ValidateStatistics(RunnerScaleSetStatistic? statistics)
    {
        if (statistics is null)
        {
            return;
        }

        if (statistics.TotalAvailableJobs < 0 ||
            statistics.TotalAcquiredJobs < 0 ||
            statistics.TotalAssignedJobs < 0 ||
            statistics.TotalRunningJobs < 0 ||
            statistics.TotalRegisteredRunners < 0 ||
            statistics.TotalBusyRunners < 0 ||
            statistics.TotalIdleRunners < 0)
        {
            throw new InvalidDataException("The message session response contains invalid negative statistics.");
        }
    }

    private static T DeserializeResponse<T>(byte[] response)
        where T : class
    {
        var json = Encoding.UTF8.GetString(TrimUtf8Bom(response)).TrimStart('\uFEFF');
        return ScaleSetJson.Deserialize<T>(json)
            ?? throw new JsonException("The Actions service response was empty.");
    }

    private static ReadOnlySpan<byte> TrimUtf8Bom(ReadOnlySpan<byte> response) =>
        response.Length >= 3 && response[0] == 0xEF && response[1] == 0xBB && response[2] == 0xBF
            ? response[3..]
            : response;

    private static Uri SetQueryParameter(Uri uri, string name, string value)
    {
        var retainedParameters = uri.Query
            .TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Where(parameter => !string.Equals(
                WebUtility.UrlDecode(parameter.Split('=', 2)[0]),
                name,
                StringComparison.Ordinal))
            .ToList();
        retainedParameters.Add($"{Uri.EscapeDataString(name)}={Uri.EscapeDataString(value)}");

        var query = string.Join('&', retainedParameters);
        var baseUri = uri.GetLeftPart(UriPartial.Path);
        return new Uri(query.Length == 0 ? baseUri : $"{baseUri}?{query}", UriKind.Absolute);
    }

    private static Uri AppendPathSegment(Uri uri, string segment)
    {
        var baseUri = uri.GetLeftPart(UriPartial.Path).TrimEnd('/');
        var appendedUri = $"{baseUri}/{Uri.EscapeDataString(segment)}{uri.Query}";
        return new Uri(appendedUri, UriKind.Absolute);
    }

    private enum SessionLifecycleState
    {
        Open = 0,
        Closing = 1,
        Closed = 2,
    }

    private sealed class RefreshFlight(RunnerScaleSetSession snapshot)
    {
        internal RunnerScaleSetSession Snapshot { get; } = snapshot;

        internal TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
