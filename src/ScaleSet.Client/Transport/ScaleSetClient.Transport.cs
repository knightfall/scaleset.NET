using System.Collections.Immutable;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Authentication;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace ScaleSet.Client;

public sealed partial class ScaleSetClient
{
    private const string ApiVersion = "6.0-preview";
    private static readonly TimeSpan MaximumTimerDelay = TimeSpan.FromMilliseconds(uint.MaxValue - 1d);
    private static readonly Action<ILogger, string, string, int?, string?, string?, Exception?> LogRequestFailure =
        LoggerMessage.Define<string, string, int?, string?, string?>(
            LogLevel.Warning,
            new EventId(1, "ScaleSetRequestFailure"),
            "ScaleSet HTTP {Method} request to {Host} failed with status {StatusCode}; activity {ActivityId}, request {GitHubRequestId}.");

    internal async Task<byte[]> SendActionsAsync(
        HttpMethod method,
        string path,
        byte[]? body,
        IReadOnlyDictionary<string, string>? query,
        int expectedStatus,
        CancellationToken cancellationToken,
        string? bearerTokenOverride = null)
    {
        var snapshot = await GetAdminSnapshotAsync(cancellationToken).ConfigureAwait(false);
        var uri = BuildActionsRequestUri(snapshot.ServiceUri, path, query);
        var bearerToken = bearerTokenOverride ?? snapshot.AdminToken;
        ValidateAuthenticationToken(bearerToken);
        var response = await SendHttpAsync(
            method,
            uri,
            body,
            $"Bearer {bearerToken}",
            contentType: "application/json",
            headers: null,
            dependency: "actions",
            expectedStatus,
            allowAccepted: false,
            timeout: _options.ControlRequestTimeout,
            queueTokenOnUnauthorized: bearerTokenOverride is not null,
            retryDiscoveryAuthentication: false,
            static bytes => bytes,
            cancellationToken).ConfigureAwait(false);

        return response!;
    }

    [SuppressMessage("Design", "CA1068:CancellationToken parameters must come last", Justification = "Keeps the shared partial-client helper argument order stable for API and session slices.")]
    internal async Task<T> SendActionsAsync<T>(
        HttpMethod method,
        string path,
        byte[]? body,
        IReadOnlyDictionary<string, string>? query,
        int expectedStatus,
        CancellationToken cancellationToken,
        Func<byte[], T> decode,
        string? bearerTokenOverride = null)
    {
        ArgumentNullException.ThrowIfNull(decode);
        var snapshot = await GetAdminSnapshotAsync(cancellationToken).ConfigureAwait(false);
        var uri = BuildActionsRequestUri(snapshot.ServiceUri, path, query);
        var bearerToken = bearerTokenOverride ?? snapshot.AdminToken;
        ValidateAuthenticationToken(bearerToken);
        var response = await SendHttpAsync(
            method,
            uri,
            body,
            $"Bearer {bearerToken}",
            contentType: "application/json",
            headers: null,
            dependency: "actions",
            expectedStatus,
            allowAccepted: false,
            timeout: _options.ControlRequestTimeout,
            queueTokenOnUnauthorized: bearerTokenOverride is not null,
            retryDiscoveryAuthentication: false,
            decode,
            cancellationToken).ConfigureAwait(false);

        return response!;
    }

    internal Task<byte[]?> SendQueueAsync(
        HttpMethod method,
        Uri uri,
        string bearerToken,
        byte[]? body,
        IReadOnlyDictionary<string, string>? headers,
        bool allowAccepted,
        CancellationToken cancellationToken)
    {
        ValidateQueueUri(uri);
        ValidateAuthenticationToken(bearerToken);
        var isPoll = method == HttpMethod.Get;
        if (!isPoll && method != HttpMethod.Delete)
        {
            throw new ArgumentException("Queue transport supports only GET polling and DELETE acknowledgement.", nameof(method));
        }

        return SendHttpAsync(
            method,
            uri,
            body,
            $"Bearer {bearerToken}",
            isPoll ? null : "application/json",
            headers,
            dependency: "queue",
            expectedStatus: isPoll ? 200 : 204,
            allowAccepted: isPoll && allowAccepted,
            timeout: isPoll ? _options.PollRequestTimeout : _options.ControlRequestTimeout,
            queueTokenOnUnauthorized: true,
            retryDiscoveryAuthentication: false,
            static bytes => bytes,
            cancellationToken);
    }

    [SuppressMessage("Design", "CA1068:CancellationToken parameters must come last", Justification = "Keeps the shared partial-client helper argument order stable for API and session slices.")]
    internal Task<T?> SendQueueAsync<T>(
        HttpMethod method,
        Uri uri,
        string bearerToken,
        byte[]? body,
        IReadOnlyDictionary<string, string>? headers,
        bool allowAccepted,
        CancellationToken cancellationToken,
        Func<byte[], T> decode)
        where T : class
    {
        ValidateQueueUri(uri);
        ValidateAuthenticationToken(bearerToken);
        ArgumentNullException.ThrowIfNull(decode);
        var isPoll = method == HttpMethod.Get;
        if (!isPoll && method != HttpMethod.Delete)
        {
            throw new ArgumentException("Queue transport supports only GET polling and DELETE acknowledgement.", nameof(method));
        }

        return SendHttpAsync(
            method,
            uri,
            body,
            $"Bearer {bearerToken}",
            isPoll ? null : "application/json",
            headers,
            dependency: "queue",
            expectedStatus: isPoll ? 200 : 204,
            allowAccepted: isPoll && allowAccepted,
            timeout: isPoll ? _options.PollRequestTimeout : _options.ControlRequestTimeout,
            queueTokenOnUnauthorized: true,
            retryDiscoveryAuthentication: false,
            decode,
            cancellationToken);
    }

    private async Task<byte[]> SendGitHubAsync(
        HttpMethod method,
        string path,
        byte[]? body,
        string authorization,
        string contentType,
        int expectedStatus,
        bool retryDiscoveryAuthentication,
        CancellationToken cancellationToken)
    {
        var uri = BuildGitHubRequestUri(Target.ApiBaseUri, path);
        var response = await SendHttpAsync(
            method,
            uri,
            body,
            authorization,
            contentType,
            headers: null,
            dependency: "github",
            expectedStatus,
            allowAccepted: false,
            timeout: _options.ControlRequestTimeout,
            queueTokenOnUnauthorized: false,
            retryDiscoveryAuthentication,
            static bytes => bytes,
            cancellationToken).ConfigureAwait(false);
        return response!;
    }

    private async Task<T> SendGitHubAsync<T>(
        HttpMethod method,
        string path,
        byte[]? body,
        string authorization,
        string contentType,
        int expectedStatus,
        Func<byte[], T> decode,
        bool retryDiscoveryAuthentication,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(decode);
        var uri = BuildGitHubRequestUri(Target.ApiBaseUri, path);
        var response = await SendHttpAsync(
            method,
            uri,
            body,
            authorization,
            contentType,
            headers: null,
            dependency: "github",
            expectedStatus,
            allowAccepted: false,
            timeout: _options.ControlRequestTimeout,
            queueTokenOnUnauthorized: false,
            retryDiscoveryAuthentication,
            decode,
            cancellationToken).ConfigureAwait(false);
        return response!;
    }

    [SuppressMessage("Design", "CA1068:CancellationToken parameters must come last", Justification = "CancellationToken position follows the agreed shared internal transport contract.")]
    private async Task<T?> SendHttpAsync<T>(
        HttpMethod method,
        Uri uri,
        byte[]? body,
        string authorization,
        string? contentType,
        IReadOnlyDictionary<string, string>? headers,
        string dependency,
        int expectedStatus,
        bool allowAccepted,
        TimeSpan timeout,
        bool queueTokenOnUnauthorized,
        bool retryDiscoveryAuthentication,
        Func<byte[], T> decode,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(method);
        ArgumentNullException.ThrowIfNull(uri);
        ArgumentNullException.ThrowIfNull(decode);

        bool hasExplicitRetryPolicy;
        ScaleSetRetryOptions retryOptions = ResolveRetryOptions(out hasExplicitRetryPolicy);
        bool readOnly = method == HttpMethod.Get || method == HttpMethod.Head;
        bool allowDefaultRetry = dependency != "queue" && (readOnly || retryOptions.RetryAllReplayableRequests);
        string[] knownSecrets = CollectKnownSecrets(authorization, uri);
        string safeUri = ScaleSetClientDiagnostics.CreateSafeUri(
            uri,
            knownSecrets,
            redactPath: dependency == "queue").AbsoluteUri;

        TimeSpan? maxElapsed = retryOptions.MaxElapsed is { } configuredMaxElapsed && configuredMaxElapsed > TimeSpan.Zero
            ? configuredMaxElapsed
            : null;
        long requestStartTimestamp = maxElapsed is not null ? _timeProvider.GetTimestamp() : 0;
        using CancellationTokenSource? maxElapsedSource = maxElapsed is { } elapsedBudget
            ? new CancellationTokenSource(elapsedBudget, _timeProvider)
            : null;
        CancellationToken maxElapsedToken = maxElapsedSource?.Token ?? CancellationToken.None;

        using BoundedDeadlineScope? deadlineScope = BoundedDeadlineScope.Create(retryOptions.Deadline, _timeProvider);
        CancellationToken deadlineToken = deadlineScope?.Token ?? CancellationToken.None;

        int maxDiscoveryAuthRetries = hasExplicitRetryPolicy
            ? Math.Min(_options.MaxRegistrationDiscoveryAuthRetries, retryOptions.MaxRetries)
            : _options.MaxRegistrationDiscoveryAuthRetries;

        int retryAttempt = 0;
        int rateLimitRetries = 0;
        int discoveryAuthRetries = 0;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (deadlineToken.IsCancellationRequested)
            {
                throw new OperationCanceledException("The ScaleSet request exceeded its configured deadline.", deadlineToken);
            }

            if (maxElapsedToken.IsCancellationRequested)
            {
                throw CreateMaxElapsedException(method, safeUri, maxElapsed!.Value);
            }

            using CancellationTokenSource timeoutSource = new(timeout, _timeProvider);
            using CancellationTokenSource linkedSource = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                timeoutSource.Token,
                maxElapsedToken,
                deadlineToken);
            using HttpRequestMessage request = CreateRequest(method, uri, body, authorization, contentType, headers, dependency);
            using Activity? activity = ScaleSetClientDiagnostics.StartActivity(method, uri, dependency);
            long startTimestamp = _timeProvider.GetTimestamp();
            HttpResponseMessage? response = null;

            try
            {
                response = await _httpClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    linkedSource.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                activity?.SetTag("error.type", "caller_canceled");
                activity?.SetStatus(ActivityStatusCode.Error, "caller canceled request");
                ScaleSetClientDiagnostics.RecordAttempt(
                    dependency,
                    method.Method,
                    statusCode: null,
                    _timeProvider.GetElapsedTime(startTimestamp).TotalSeconds);
                throw new OperationCanceledException("The ScaleSet request was canceled by the caller.", cancellationToken);
            }
            catch (OperationCanceledException) when (deadlineToken.IsCancellationRequested)
            {
                activity?.SetTag("error.type", "deadline_exceeded");
                activity?.SetStatus(ActivityStatusCode.Error, "deadline exceeded");
                ScaleSetClientDiagnostics.RecordAttempt(
                    dependency,
                    method.Method,
                    statusCode: null,
                    _timeProvider.GetElapsedTime(startTimestamp).TotalSeconds);
                throw new OperationCanceledException("The ScaleSet request exceeded its configured deadline.", deadlineToken);
            }
            catch (OperationCanceledException) when (maxElapsedToken.IsCancellationRequested)
            {
                activity?.SetTag("error.type", "max_elapsed_exceeded");
                activity?.SetStatus(ActivityStatusCode.Error, "max elapsed exceeded");
                ScaleSetClientDiagnostics.RecordAttempt(
                    dependency,
                    method.Method,
                    statusCode: null,
                    _timeProvider.GetElapsedTime(startTimestamp).TotalSeconds);
                throw CreateMaxElapsedException(method, safeUri, maxElapsed!.Value);
            }
            catch (OperationCanceledException)
            {
                activity?.SetStatus(ActivityStatusCode.Error, "request timed out");
                ScaleSetClientDiagnostics.RecordAttempt(
                    dependency,
                    method.Method,
                    statusCode: null,
                    _timeProvider.GetElapsedTime(startTimestamp).TotalSeconds);
                ScaleSetApiException timeoutException = new(
                    "ScaleSet HTTP request timed out.",
                    statusCode: null,
                    method: method.Method,
                    safeUri: safeUri);

                TimeSpan? delay = await EvaluateStandardFailureRetryAsync(
                    retryOptions,
                    response: null,
                    method,
                    safeUri,
                    retryAttempt,
                    readOnly: allowDefaultRetry,
                    isCertificateError: false,
                    statusCode: null,
                    snapshot: null,
                    timeoutException,
                    retryAfterHeaderDelay: null,
                    maxElapsed,
                    knownSecrets,
                    maxElapsedToken,
                    deadlineToken,
                    cancellationToken).ConfigureAwait(false);
                if (delay is { } retryDelay &&
                    !WouldWaitOutlastBudget(retryDelay, maxElapsed, requestStartTimestamp, retryOptions.Deadline))
                {
                    retryAttempt++;
                    ScaleSetClientDiagnostics.RecordRetry(dependency, method.Method, "transient");
                    await WaitForRetryDelayAsync(
                        retryDelay,
                        method,
                        safeUri,
                        maxElapsed,
                        maxElapsedToken,
                        deadlineToken,
                        cancellationToken).ConfigureAwait(false);
                    continue;
                }

                LogFailure(timeoutException, uri);
                throw timeoutException;
            }
            catch (HttpRequestException httpException)
            {
                activity?.SetStatus(ActivityStatusCode.Error, "transport failure");
                ScaleSetClientDiagnostics.RecordAttempt(
                    dependency,
                    method.Method,
                    statusCode: null,
                    _timeProvider.GetElapsedTime(startTimestamp).TotalSeconds);
                bool isCertificateError = IsCertificateError(httpException);
                ScaleSetApiException transportException = new(
                    "ScaleSet HTTP transport failed.",
                    statusCode: null,
                    method: method.Method,
                    safeUri: safeUri);

                TimeSpan? delay = await EvaluateStandardFailureRetryAsync(
                    retryOptions,
                    response: null,
                    method,
                    safeUri,
                    retryAttempt,
                    readOnly: allowDefaultRetry,
                    isCertificateError,
                    statusCode: null,
                    snapshot: null,
                    retryOptions.ShouldRetry is not null ? httpException : transportException,
                    retryAfterHeaderDelay: null,
                    maxElapsed,
                    knownSecrets,
                    maxElapsedToken,
                    deadlineToken,
                    cancellationToken).ConfigureAwait(false);
                if (delay is { } retryDelay &&
                    !WouldWaitOutlastBudget(retryDelay, maxElapsed, requestStartTimestamp, retryOptions.Deadline))
                {
                    retryAttempt++;
                    ScaleSetClientDiagnostics.RecordRetry(dependency, method.Method, "transient");
                    await WaitForRetryDelayAsync(
                        retryDelay,
                        method,
                        safeUri,
                        maxElapsed,
                        maxElapsedToken,
                        deadlineToken,
                        cancellationToken).ConfigureAwait(false);
                    continue;
                }

                LogFailure(transportException, uri);
                throw transportException;
            }

            TimeSpan? nextDelay = null;
            string? nextRetryKind = null;
            bool countAsRateLimitRetry = false;
            bool countAsDiscoveryRetry = false;

            using (response)
            {
                int statusCode = (int)response.StatusCode;
                activity?.SetTag("http.response.status_code", statusCode);
                bool expected = IsExpectedStatus(response.StatusCode, expectedStatus) ||
                    (allowAccepted && response.StatusCode == HttpStatusCode.Accepted);

                if (expected)
                {
                    if (allowAccepted && response.StatusCode == HttpStatusCode.Accepted)
                    {
                        ScaleSetClientDiagnostics.RecordAttempt(
                            dependency,
                            method.Method,
                            statusCode,
                            _timeProvider.GetElapsedTime(startTimestamp).TotalSeconds);
                        return default;
                    }

                    ScaleSetClientDiagnostics.ResponseBodyReadResult bodyRead =
                        await ScaleSetClientDiagnostics.ReadResponseBytesAsync(
                            response,
                            int.MaxValue,
                            cancellationToken,
                            maxElapsedToken,
                            deadlineToken,
                            linkedSource.Token).ConfigureAwait(false);

                    if (bodyRead.Failure == ScaleSetClientDiagnostics.ResponseBodyReadFailure.MaxElapsed)
                    {
                        activity?.SetTag("error.type", "max_elapsed_exceeded");
                        activity?.SetStatus(ActivityStatusCode.Error, "max elapsed exceeded reading response");
                        ScaleSetClientDiagnostics.RecordAttempt(
                            dependency,
                            method.Method,
                            statusCode,
                            _timeProvider.GetElapsedTime(startTimestamp).TotalSeconds);
                        ScaleSetResponseSnapshot snapshot = ScaleSetResponseSnapshot.FromHttpResponse(
                            response,
                            bodyRead.RawBytes,
                            bodyRead.IsIncomplete,
                            bodyRead.Truncated);
                        throw CreateMaxElapsedException(response, method, safeUri, maxElapsed!.Value, knownSecrets, snapshot);
                    }

                    if (bodyRead.Failure != ScaleSetClientDiagnostics.ResponseBodyReadFailure.None)
                    {
                        ScaleSetResponseSnapshot snapshot = ScaleSetResponseSnapshot.FromHttpResponse(
                            response,
                            bodyRead.RawBytes,
                            bodyRead.IsIncomplete,
                            bodyRead.Truncated);
                        bool isTimeout = bodyRead.Failure == ScaleSetClientDiagnostics.ResponseBodyReadFailure.AttemptTimedOut;
                        activity?.SetTag("error.type", isTimeout ? "response_timeout" : "response_read");
                        activity?.SetStatus(ActivityStatusCode.Error, isTimeout ? "response body read timed out" : "response body read failed");
                        ScaleSetClientDiagnostics.RecordAttempt(
                            dependency,
                            method.Method,
                            statusCode,
                            _timeProvider.GetElapsedTime(startTimestamp).TotalSeconds);
                        ScaleSetApiException readFailure = CreateResponseException(
                            response,
                            method,
                            safeUri,
                            [],
                            truncated: false,
                            queueTokenOnUnauthorized: false,
                            knownSecrets: knownSecrets,
                            snapshot: snapshot,
                            message: isTimeout
                                ? "ScaleSet response body read timed out."
                                : "ScaleSet response body could not be read.");

                        if (retryOptions.ShouldRetry is not null)
                        {
                            TimeSpan? retryDelay = await EvaluateStandardFailureRetryAsync(
                                retryOptions,
                                response,
                                method,
                                safeUri,
                                retryAttempt,
                                readOnly: allowDefaultRetry,
                                isCertificateError: false,
                                statusCode: response.StatusCode,
                                snapshot,
                                readFailure,
                                retryAfterHeaderDelay: null,
                                maxElapsed,
                                knownSecrets,
                                maxElapsedToken,
                                deadlineToken,
                                cancellationToken).ConfigureAwait(false);
                            if (retryDelay is { } approvedDelay &&
                                !WouldWaitOutlastBudget(approvedDelay, maxElapsed, requestStartTimestamp, retryOptions.Deadline))
                            {
                                nextDelay = approvedDelay;
                                nextRetryKind = "transient";
                            }
                        }

                        if (nextDelay is null)
                        {
                            LogFailure(readFailure, uri);
                            throw readFailure;
                        }
                    }
                    else
                    {
                        ScaleSetClientDiagnostics.RecordAttempt(
                            dependency,
                            method.Method,
                            statusCode,
                            _timeProvider.GetElapsedTime(startTimestamp).TotalSeconds);
                        try
                        {
                            byte[] decodedInput = ScaleSetClientDiagnostics.TrimUtf8Bom(bodyRead.RawBytes).ToArray();
                            return decode(decodedInput);
                        }
                        catch (Exception exception) when (IsDecodeFailure(exception))
                        {
                            activity?.SetTag("error.type", "response_decode");
                            activity?.SetStatus(ActivityStatusCode.Error, "response body decode failed");
                            ScaleSetResponseSnapshot snapshot = ScaleSetResponseSnapshot.FromHttpResponse(
                                response,
                                bodyRead.RawBytes,
                                isBodyIncomplete: false,
                                isBodyTruncated: false);
                            ScaleSetApiException decodeFailure = CreateResponseException(
                                response,
                                method,
                                safeUri,
                                bodyRead.RawBytes,
                                truncated: false,
                                queueTokenOnUnauthorized: false,
                                knownSecrets: knownSecrets,
                                snapshot: snapshot,
                                message: "ScaleSet response body could not be decoded.");
                            LogFailure(decodeFailure, uri);
                            throw decodeFailure;
                        }
                    }
                }
                else
                {
                    ScaleSetClientDiagnostics.ResponseBodyReadResult bodyRead =
                        await ScaleSetClientDiagnostics.ReadResponseBytesAsync(
                            response,
                            ScaleSetClientDiagnostics.MaximumErrorBodyBytes,
                            cancellationToken,
                            maxElapsedToken,
                            deadlineToken,
                            linkedSource.Token).ConfigureAwait(false);

                    ScaleSetResponseSnapshot snapshot = ScaleSetResponseSnapshot.FromHttpResponse(
                        response,
                        bodyRead.RawBytes,
                        bodyRead.IsIncomplete,
                        bodyRead.Truncated);

                    if (bodyRead.Failure == ScaleSetClientDiagnostics.ResponseBodyReadFailure.MaxElapsed)
                    {
                        activity?.SetTag("error.type", "max_elapsed_exceeded");
                        activity?.SetStatus(ActivityStatusCode.Error, "max elapsed exceeded reading error response");
                        ScaleSetClientDiagnostics.RecordAttempt(
                            dependency,
                            method.Method,
                            statusCode,
                            _timeProvider.GetElapsedTime(startTimestamp).TotalSeconds);
                        throw CreateMaxElapsedException(response, method, safeUri, maxElapsed!.Value, knownSecrets, snapshot);
                    }

                    ScaleSetClientDiagnostics.RecordAttempt(
                        dependency,
                        method.Method,
                        statusCode,
                        _timeProvider.GetElapsedTime(startTimestamp).TotalSeconds);

                    DateTimeOffset now = _timeProvider.GetUtcNow();
                    ScaleSetClientDiagnostics.RateLimitClassification? rateLimit =
                        ScaleSetClientDiagnostics.TryClassifyRateLimit(
                            response,
                            bodyRead.RawBytes,
                            bodyRead.IsIncomplete,
                            now);

                    bool canRetryDiscoveryAuth = retryDiscoveryAuthentication &&
                        discoveryAuthRetries < maxDiscoveryAuthRetries &&
                        (!hasExplicitRetryPolicy || retryAttempt < retryOptions.MaxRetries);

                    if (rateLimit is not null)
                    {
                        ScaleSetRateLimitException rateLimitException = CreateRateLimitException(
                            response,
                            method,
                            safeUri,
                            bodyRead,
                            rateLimit,
                            rateLimitRetries,
                            knownSecrets,
                            snapshot);

                        if (canRetryDiscoveryAuth &&
                            response.StatusCode == HttpStatusCode.Forbidden &&
                            retryOptions.OnRateLimit is null)
                        {
                            TimeSpan candidateDelay = ComputeBackoffDelay(
                                retryOptions,
                                hasExplicitRetryPolicy ? retryAttempt : discoveryAuthRetries,
                                snapshot,
                                rateLimit.RetryAfterHeaderDelay,
                                response.StatusCode);
                            if (!WouldWaitOutlastBudget(candidateDelay, maxElapsed, requestStartTimestamp, retryOptions.Deadline))
                            {
                                nextDelay = candidateDelay;
                                nextRetryKind = "discovery-auth";
                                countAsDiscoveryRetry = true;
                            }
                        }
                        else
                        {
                            TimeSpan? candidateDelay = await EvaluateRateLimitRetryAsync(
                                retryOptions,
                                response,
                                method,
                                safeUri,
                                retryAttempt,
                                readOnly: allowDefaultRetry,
                                response.StatusCode,
                                rateLimit,
                                rateLimitException,
                                snapshot,
                                maxElapsed,
                                knownSecrets,
                                maxElapsedToken,
                                deadlineToken,
                                cancellationToken).ConfigureAwait(false);
                            if (candidateDelay is { } approvedDelay &&
                                !WouldWaitOutlastBudget(approvedDelay, maxElapsed, requestStartTimestamp, retryOptions.Deadline))
                            {
                                nextDelay = approvedDelay;
                                nextRetryKind = "rate-limit";
                                countAsRateLimitRetry = true;
                            }
                        }

                        if (nextDelay is null)
                        {
                            activity?.SetStatus(ActivityStatusCode.Error, $"HTTP {statusCode}");
                            activity?.SetTag("error.type", $"http_{statusCode}");
                            LogFailure(rateLimitException, uri);
                            throw rateLimitException;
                        }
                    }
                    else if (canRetryDiscoveryAuth &&
                        response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                    {
                        TimeSpan candidateDelay = ComputeBackoffDelay(
                            retryOptions,
                            hasExplicitRetryPolicy ? retryAttempt : discoveryAuthRetries,
                            snapshot,
                            retryAfterHeaderDelay: null,
                            response.StatusCode);
                        if (!WouldWaitOutlastBudget(candidateDelay, maxElapsed, requestStartTimestamp, retryOptions.Deadline))
                        {
                            nextDelay = candidateDelay;
                            nextRetryKind = "discovery-auth";
                            countAsDiscoveryRetry = true;
                        }
                        else
                        {
                            ScaleSetApiException failure = CreateResponseExceptionForReadResult(
                                response,
                                method,
                                safeUri,
                                bodyRead,
                                queueTokenOnUnauthorized,
                                knownSecrets,
                                snapshot);
                            activity?.SetStatus(ActivityStatusCode.Error, $"HTTP {statusCode}");
                            activity?.SetTag("error.type", $"http_{statusCode}");
                            LogFailure(failure, uri);
                            throw failure;
                        }
                    }
                    else
                    {
                        ScaleSetApiException failure = CreateResponseExceptionForReadResult(
                            response,
                            method,
                            safeUri,
                            bodyRead,
                            queueTokenOnUnauthorized,
                            knownSecrets,
                            snapshot);
                        TimeSpan? retryAfterHeaderDelay = response.StatusCode == HttpStatusCode.ServiceUnavailable
                            ? ScaleSetClientDiagnostics.ParseRetryAfter(response, now)
                            : null;
                        TimeSpan? candidateDelay = await EvaluateStandardFailureRetryAsync(
                            retryOptions,
                            response,
                            method,
                            safeUri,
                            retryAttempt,
                            readOnly: allowDefaultRetry,
                            isCertificateError: false,
                            response.StatusCode,
                            snapshot,
                            failure,
                            retryAfterHeaderDelay,
                            maxElapsed,
                            knownSecrets,
                            maxElapsedToken,
                            deadlineToken,
                            cancellationToken).ConfigureAwait(false);

                        if (candidateDelay is { } approvedDelay &&
                            !WouldWaitOutlastBudget(approvedDelay, maxElapsed, requestStartTimestamp, retryOptions.Deadline))
                        {
                            nextDelay = approvedDelay;
                            nextRetryKind = "transient";
                        }
                        else
                        {
                            activity?.SetStatus(ActivityStatusCode.Error, $"HTTP {statusCode}");
                            activity?.SetTag("error.type", $"http_{statusCode}");
                            LogFailure(failure, uri);
                            throw failure;
                        }
                    }
                }
            }

            if (countAsDiscoveryRetry)
            {
                discoveryAuthRetries++;
                if (hasExplicitRetryPolicy)
                {
                    retryAttempt++;
                }
            }
            else
            {
                retryAttempt++;
                if (countAsRateLimitRetry)
                {
                    rateLimitRetries++;
                }
            }

            activity?.SetStatus(ActivityStatusCode.Error, $"HTTP retry ({nextRetryKind})");
            ScaleSetClientDiagnostics.RecordRetry(dependency, method.Method, nextRetryKind!);
            await WaitForRetryDelayAsync(
                nextDelay!.Value,
                method,
                safeUri,
                maxElapsed,
                maxElapsedToken,
                deadlineToken,
                cancellationToken).ConfigureAwait(false);
        }
    }

    private ScaleSetRetryOptions ResolveRetryOptions(out bool hasExplicitPolicy)
    {
        ScaleSetRetryOptions retryOptions = _options.Retry ?? new ScaleSetRetryOptions
        {
            MaxRetries = _options.MaxReadOnlyRetries,
            WaitMin = _options.RetryBaseDelay,
            WaitMax = _options.RetryMaxDelay,
            RetryAllReplayableRequests = false,
            MaxRetryAfterDelay = ScaleSetClientOptions.MaximumRetryDelay,
        };

        ImmutableArray<Func<ScaleSetRetryOptions, ScaleSetRetryOptions>> adjusters =
            ScaleSetRequestRetry.GetCurrentAdjusters();
        hasExplicitPolicy = _options.Retry is not null || adjusters.Length > 0;

        foreach (Func<ScaleSetRetryOptions, ScaleSetRetryOptions> adjust in adjusters)
        {
            retryOptions = adjust(retryOptions)
                ?? throw new InvalidOperationException("Request retry adjuster cannot return null.");
        }

        retryOptions.Validate();
        return retryOptions;
    }

    private sealed class BoundedDeadlineScope : IDisposable
    {
        private readonly object _sync = new();
        private readonly DateTimeOffset _deadlineUtc;
        private readonly TimeProvider _timeProvider;
        private readonly CancellationTokenSource _cts = new();
        private ITimer? _timer;
        private bool _disposed;

        private BoundedDeadlineScope(DateTimeOffset deadlineUtc, TimeProvider timeProvider)
        {
            _deadlineUtc = deadlineUtc;
            _timeProvider = timeProvider;
        }

        public CancellationToken Token => _cts.Token;

        public static BoundedDeadlineScope? Create(DateTimeOffset? deadline, TimeProvider timeProvider)
        {
            if (deadline is not { } deadlineUtc)
            {
                return null;
            }

            BoundedDeadlineScope scope = new(deadlineUtc, timeProvider);
            TimeSpan remaining = deadlineUtc - timeProvider.GetUtcNow();
            if (remaining <= TimeSpan.Zero)
            {
                scope._cts.Cancel();
                return scope;
            }

            TimeSpan initialDelay = remaining > MaximumTimerDelay ? MaximumTimerDelay : remaining;
            scope._timer = timeProvider.CreateTimer(
                static state => ((BoundedDeadlineScope)state!).OnTimerFired(),
                scope,
                Timeout.InfiniteTimeSpan,
                Timeout.InfiniteTimeSpan);
            lock (scope._sync)
            {
                if (!scope._disposed)
                {
                    scope._timer.Change(initialDelay, Timeout.InfiniteTimeSpan);
                }
            }

            return scope;
        }

        private void OnTimerFired()
        {
            ITimer? timerToDispose = null;
            bool shouldCancel = false;

            lock (_sync)
            {
                if (_disposed)
                {
                    return;
                }

                TimeSpan remaining = _deadlineUtc - _timeProvider.GetUtcNow();
                if (remaining <= TimeSpan.Zero)
                {
                    timerToDispose = _timer;
                    _timer = null;
                    shouldCancel = true;
                }
                else
                {
                    TimeSpan nextDelay = remaining > MaximumTimerDelay ? MaximumTimerDelay : remaining;
                    _timer?.Change(nextDelay, Timeout.InfiniteTimeSpan);
                }
            }

            timerToDispose?.Dispose();
            if (shouldCancel)
            {
                try
                {
                    _cts.Cancel();
                }
                catch (ObjectDisposedException)
                {
                }
            }
        }

        public void Dispose()
        {
            ITimer? timerToDispose;
            lock (_sync)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                timerToDispose = _timer;
                _timer = null;
            }

            timerToDispose?.Dispose();
            _cts.Dispose();
        }
    }

    private bool WouldWaitOutlastBudget(
        TimeSpan wait,
        TimeSpan? maxElapsed,
        long requestStartTimestamp,
        DateTimeOffset? deadline)
    {
        if (maxElapsed is { } elapsedBudget &&
            wait >= elapsedBudget - _timeProvider.GetElapsedTime(requestStartTimestamp))
        {
            return true;
        }

        if (deadline is { } deadlineUtc &&
            wait >= deadlineUtc - _timeProvider.GetUtcNow())
        {
            return true;
        }

        return false;
    }

    private async Task WaitForRetryDelayAsync(
        TimeSpan delay,
        HttpMethod method,
        string safeUri,
        TimeSpan? maxElapsed,
        CancellationToken maxElapsedToken,
        CancellationToken deadlineToken,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (deadlineToken.IsCancellationRequested)
        {
            throw new OperationCanceledException("The ScaleSet request exceeded its configured deadline.", deadlineToken);
        }

        if (maxElapsedToken.IsCancellationRequested)
        {
            throw CreateMaxElapsedException(method, safeUri, maxElapsed!.Value);
        }

        if (delay <= TimeSpan.Zero)
        {
            return;
        }

        using CancellationTokenSource linkedWait = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            maxElapsedToken,
            deadlineToken);
        try
        {
            TimeSpan remaining = delay;
            while (remaining > TimeSpan.Zero)
            {
                TimeSpan chunk = remaining > MaximumTimerDelay ? MaximumTimerDelay : remaining;
                await Task.Delay(chunk, _timeProvider, linkedWait.Token).ConfigureAwait(false);
                remaining -= chunk;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException("The ScaleSet request retry wait was canceled by the caller.", cancellationToken);
        }
        catch (OperationCanceledException) when (deadlineToken.IsCancellationRequested)
        {
            throw new OperationCanceledException("The ScaleSet request exceeded its configured deadline.", deadlineToken);
        }
        catch (OperationCanceledException) when (maxElapsedToken.IsCancellationRequested)
        {
            throw CreateMaxElapsedException(method, safeUri, maxElapsed!.Value);
        }
    }

    private static async ValueTask<TimeSpan?> EvaluateStandardFailureRetryAsync(
        ScaleSetRetryOptions retryOptions,
        HttpResponseMessage? response,
        HttpMethod method,
        string safeUri,
        int retryAttempt,
        bool readOnly,
        bool isCertificateError,
        HttpStatusCode? statusCode,
        ScaleSetResponseSnapshot? snapshot,
        Exception failureException,
        TimeSpan? retryAfterHeaderDelay,
        TimeSpan? maxElapsed,
        IReadOnlyCollection<string> knownSecrets,
        CancellationToken maxElapsedToken,
        CancellationToken deadlineToken,
        CancellationToken cancellationToken)
    {
        if (retryAttempt >= retryOptions.MaxRetries)
        {
            return null;
        }

        bool shouldRetry;
        if (retryOptions.ShouldRetry is { } customShouldRetry)
        {
            shouldRetry = await InvokeBudgetedHookAsync(
                hookToken => customShouldRetry(
                    new ScaleSetRetryContext(method, safeUri, retryAttempt, snapshot, failureException),
                    hookToken),
                response,
                method,
                safeUri,
                snapshot,
                maxElapsed,
                knownSecrets,
                maxElapsedToken,
                deadlineToken,
                cancellationToken).ConfigureAwait(false);
        }
        else
        {
            shouldRetry = !isCertificateError &&
                readOnly &&
                (statusCode is null || IsTransient(statusCode.Value));
        }

        if (!shouldRetry)
        {
            return null;
        }

        if (statusCode == HttpStatusCode.ServiceUnavailable &&
            retryOptions.MaxRetryAfterDelay is { } maxDelay &&
            retryAfterHeaderDelay is { } serverDelay &&
            serverDelay > maxDelay)
        {
            return null;
        }

        return ComputeBackoffDelay(retryOptions, retryAttempt, snapshot, retryAfterHeaderDelay, statusCode);
    }

    private static async ValueTask<TimeSpan?> EvaluateRateLimitRetryAsync(
        ScaleSetRetryOptions retryOptions,
        HttpResponseMessage response,
        HttpMethod method,
        string safeUri,
        int retryAttempt,
        bool readOnly,
        HttpStatusCode statusCode,
        ScaleSetClientDiagnostics.RateLimitClassification rateLimit,
        ScaleSetRateLimitException rateLimitException,
        ScaleSetResponseSnapshot snapshot,
        TimeSpan? maxElapsed,
        IReadOnlyCollection<string> knownSecrets,
        CancellationToken maxElapsedToken,
        CancellationToken deadlineToken,
        CancellationToken cancellationToken)
    {
        if (retryAttempt >= retryOptions.MaxRetries)
        {
            return null;
        }

        if (retryOptions.OnRateLimit is { } onRateLimit)
        {
            ScaleSetRateLimitDecision decision = await InvokeBudgetedHookAsync(
                hookToken => onRateLimit(
                    new ScaleSetRateLimitDecisionContext(rateLimitException, retryAttempt, method.Method, safeUri),
                    hookToken),
                response,
                method,
                safeUri,
                snapshot,
                maxElapsed,
                knownSecrets,
                maxElapsedToken,
                deadlineToken,
                cancellationToken).ConfigureAwait(false);
            if (!decision.ShouldRetry)
            {
                return null;
            }

            return decision.Delay > TimeSpan.Zero
                ? decision.Delay
                : ComputeBackoffDelay(retryOptions, retryAttempt, snapshot, rateLimit.RetryAfterHeaderDelay, statusCode);
        }

        if (statusCode != HttpStatusCode.TooManyRequests || !readOnly)
        {
            return null;
        }

        if (retryOptions.MaxRetryAfterDelay is { } maxDelay &&
            rateLimit.RetryAfterHeaderDelay is { } serverDelay &&
            serverDelay > maxDelay)
        {
            return null;
        }

        return ComputeBackoffDelay(retryOptions, retryAttempt, snapshot, rateLimit.RetryAfterHeaderDelay, statusCode);
    }

    private static async ValueTask<TResult> InvokeBudgetedHookAsync<TResult>(
        Func<CancellationToken, ValueTask<TResult>> invoke,
        HttpResponseMessage? response,
        HttpMethod method,
        string safeUri,
        ScaleSetResponseSnapshot? snapshot,
        TimeSpan? maxElapsed,
        IReadOnlyCollection<string> knownSecrets,
        CancellationToken maxElapsedToken,
        CancellationToken deadlineToken,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (deadlineToken.IsCancellationRequested)
        {
            throw new OperationCanceledException("The ScaleSet request exceeded its configured deadline.", deadlineToken);
        }

        if (maxElapsedToken.IsCancellationRequested)
        {
            throw response is not null && snapshot is not null
                ? CreateMaxElapsedException(response, method, safeUri, maxElapsed!.Value, knownSecrets, snapshot)
                : CreateMaxElapsedException(method, safeUri, maxElapsed!.Value);
        }

        using CancellationTokenSource linkedHook = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            maxElapsedToken,
            deadlineToken);
        try
        {
            return await invoke(linkedHook.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException("The ScaleSet request was canceled by the caller.", cancellationToken);
        }
        catch (OperationCanceledException) when (deadlineToken.IsCancellationRequested)
        {
            throw new OperationCanceledException("The ScaleSet request exceeded its configured deadline.", deadlineToken);
        }
        catch (OperationCanceledException) when (maxElapsedToken.IsCancellationRequested)
        {
            throw response is not null && snapshot is not null
                ? CreateMaxElapsedException(response, method, safeUri, maxElapsed!.Value, knownSecrets, snapshot)
                : CreateMaxElapsedException(method, safeUri, maxElapsed!.Value);
        }
    }

    private static TimeSpan ComputeBackoffDelay(
        ScaleSetRetryOptions retryOptions,
        int attempt,
        ScaleSetResponseSnapshot? snapshot,
        TimeSpan? retryAfterHeaderDelay,
        HttpStatusCode? statusCode)
    {
        if (retryOptions.Backoff is { } customBackoff)
        {
            TimeSpan customDelay = customBackoff(new ScaleSetBackoffContext(
                retryOptions.WaitMin,
                retryOptions.WaitMax,
                attempt,
                snapshot,
                retryAfterHeaderDelay));
            return customDelay < TimeSpan.Zero ? TimeSpan.Zero : customDelay;
        }

        if (statusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable &&
            retryAfterHeaderDelay is { } serverDelay)
        {
            return serverDelay;
        }

        double baseMilliseconds = retryOptions.WaitMin.TotalMilliseconds;
        if (baseMilliseconds <= 0)
        {
            return TimeSpan.Zero;
        }

        double maxMilliseconds = retryOptions.WaitMax.TotalMilliseconds;
        double exponential = baseMilliseconds * Math.Pow(2, Math.Min(attempt, 30));
        if (exponential >= maxMilliseconds)
        {
            return retryOptions.WaitMax;
        }

        double jitter = Random.Shared.NextDouble() * (exponential / 2.0);
        double delayMilliseconds = Math.Min(maxMilliseconds, exponential + jitter);
        return TimeSpan.FromMilliseconds(delayMilliseconds);
    }

    private static ScaleSetApiException CreateResponseExceptionForReadResult(
        HttpResponseMessage response,
        HttpMethod method,
        string safeUri,
        ScaleSetClientDiagnostics.ResponseBodyReadResult bodyRead,
        bool queueTokenOnUnauthorized,
        IReadOnlyCollection<string> knownSecrets,
        ScaleSetResponseSnapshot snapshot)
    {
        string? message = bodyRead.Failure switch
        {
            ScaleSetClientDiagnostics.ResponseBodyReadFailure.AttemptTimedOut => "ScaleSet error response body read timed out.",
            ScaleSetClientDiagnostics.ResponseBodyReadFailure.ReadError => "ScaleSet error response body could not be read.",
            _ => null,
        };
        ReadOnlySpan<byte> previewBytes = bodyRead.IsIncomplete ? [] : bodyRead.RawBytes;
        return CreateResponseException(
            response,
            method,
            safeUri,
            previewBytes,
            bodyRead.Truncated,
            queueTokenOnUnauthorized,
            knownSecrets,
            snapshot,
            message);
    }

    private static ScaleSetApiException CreateResponseException(
        HttpResponseMessage response,
        HttpMethod method,
        string safeUri,
        ReadOnlySpan<byte> body,
        bool truncated,
        bool queueTokenOnUnauthorized,
        IReadOnlyCollection<string> knownSecrets,
        ScaleSetResponseSnapshot snapshot,
        string? message = null)
    {
        int status = (int)response.StatusCode;
        ScaleSetDomainError? domainError = queueTokenOnUnauthorized && response.StatusCode == HttpStatusCode.Unauthorized
            ? ScaleSetDomainError.MessageQueueTokenExpired
            : ScaleSetClientDiagnostics.ParseDomainError(body);
        string activityId = ScaleSetClientDiagnostics.CorrelationHeader(response, "ActivityId", knownSecrets);
        string githubRequestId = ScaleSetClientDiagnostics.CorrelationHeader(response, "X-GitHub-Request-Id", knownSecrets);
        string? bodyPreview = ScaleSetClientDiagnostics.CreateBodyPreview(body, truncated, knownSecrets);
        string errorMessage = message ?? $"ScaleSet HTTP request failed with status {status}.";
        return new ScaleSetApiException(
            errorMessage,
            status,
            domainError,
            method: method.Method,
            safeUri: safeUri,
            activityId: activityId.Length == 0 ? null : activityId,
            githubRequestId: githubRequestId.Length == 0 ? null : githubRequestId,
            bodyPreview: bodyPreview,
            response: snapshot);
    }

    private static ScaleSetRateLimitException CreateRateLimitException(
        HttpResponseMessage response,
        HttpMethod method,
        string safeUri,
        ScaleSetClientDiagnostics.ResponseBodyReadResult bodyRead,
        ScaleSetClientDiagnostics.RateLimitClassification rateLimit,
        int rateLimitRetries,
        IReadOnlyCollection<string> knownSecrets,
        ScaleSetResponseSnapshot snapshot)
    {
        ReadOnlySpan<byte> previewBytes = bodyRead.IsIncomplete ? [] : bodyRead.RawBytes;
        ScaleSetDomainError? domainError = ScaleSetClientDiagnostics.ParseDomainError(previewBytes);
        string activityId = ScaleSetClientDiagnostics.CorrelationHeader(response, "ActivityId", knownSecrets);
        string githubRequestId = ScaleSetClientDiagnostics.CorrelationHeader(response, "X-GitHub-Request-Id", knownSecrets);
        string? bodyPreview = ScaleSetClientDiagnostics.CreateBodyPreview(previewBytes, bodyRead.Truncated, knownSecrets);
        string message = rateLimit.RetryAfter > TimeSpan.Zero
            ? $"ScaleSet HTTP request rate limited (retry after {rateLimit.RetryAfter}) with status {rateLimit.StatusCode}."
            : $"ScaleSet HTTP request rate limited with status {rateLimit.StatusCode}.";
        return new ScaleSetRateLimitException(
            message,
            rateLimit.StatusCode,
            rateLimit.Kind,
            rateLimit.RetryAfter,
            rateLimit.Reset,
            rateLimitRetries,
            domainError,
            method: method.Method,
            safeUri: safeUri,
            activityId: activityId.Length == 0 ? null : activityId,
            githubRequestId: githubRequestId.Length == 0 ? null : githubRequestId,
            bodyPreview: bodyPreview,
            response: snapshot);
    }

    private static ScaleSetMaxElapsedExceededException CreateMaxElapsedException(
        HttpMethod method,
        string safeUri,
        TimeSpan maxElapsed) =>
        new(
            $"ScaleSet HTTP request exceeded MaxElapsed budget of {maxElapsed}.",
            maxElapsed,
            method: method.Method,
            safeUri: safeUri);

    private static ScaleSetMaxElapsedExceededException CreateMaxElapsedException(
        HttpResponseMessage response,
        HttpMethod method,
        string safeUri,
        TimeSpan maxElapsed,
        IReadOnlyCollection<string> knownSecrets,
        ScaleSetResponseSnapshot snapshot)
    {
        string activityId = ScaleSetClientDiagnostics.CorrelationHeader(response, "ActivityId", knownSecrets);
        string githubRequestId = ScaleSetClientDiagnostics.CorrelationHeader(response, "X-GitHub-Request-Id", knownSecrets);
        string? bodyPreview = ScaleSetClientDiagnostics.CreateBodyPreview([], truncated: false, knownSecrets);
        return new ScaleSetMaxElapsedExceededException(
            $"ScaleSet HTTP request exceeded MaxElapsed budget of {maxElapsed}.",
            maxElapsed,
            (int)response.StatusCode,
            method.Method,
            safeUri,
            activityId: activityId.Length == 0 ? null : activityId,
            githubRequestId: githubRequestId.Length == 0 ? null : githubRequestId,
            bodyPreview: bodyPreview,
            response: snapshot);
    }

    private static bool IsCertificateError(HttpRequestException exception)
    {
        if (exception.HttpRequestError == HttpRequestError.SecureConnectionError)
        {
            return true;
        }

        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is AuthenticationException)
            {
                return true;
            }
        }

        return false;
    }

    private HttpRequestMessage CreateRequest(
        HttpMethod method,
        Uri uri,
        byte[]? body,
        string authorization,
        string? contentType,
        IReadOnlyDictionary<string, string>? headers,
        string dependency)
    {
        var request = new HttpRequestMessage(method, uri)
        {
            Version = _httpClient.DefaultRequestVersion,
            VersionPolicy = _httpClient.DefaultVersionPolicy,
        };
        try
        {
            request.Headers.Authorization = AuthenticationHeaderValue.Parse(authorization);
            request.Headers.UserAgent.ParseAdd(UserAgent);

            if (method == HttpMethod.Get && dependency == "queue" &&
                headers?.Keys.Any(name => string.Equals(name, "Accept", StringComparison.OrdinalIgnoreCase)) != true)
            {
                request.Headers.Accept.ParseAdd("application/json; api-version=6.0-preview");
            }

            if (headers is not null)
            {
                foreach (var (name, value) in headers)
                {
                    if (name.Any(char.IsControl) || value.Any(character => character is '\r' or '\n'))
                    {
                        throw new ArgumentException("HTTP header names and values cannot contain control characters.", nameof(headers));
                    }

                    if (IsReservedHeader(name))
                    {
                        throw new ArgumentException($"The '{name}' header is managed by ScaleSet.Client.", nameof(headers));
                    }

                    if (!request.Headers.TryAddWithoutValidation(name, value) &&
                        request.Content?.Headers.TryAddWithoutValidation(name, value) != true)
                    {
                        throw new ArgumentException($"The '{name}' header is not valid for this request.", nameof(headers));
                    }
                }
            }

            if (body is not null || contentType is not null)
            {
                request.Content = new ByteArrayContent(body ?? []);
                if (contentType is not null)
                {
                    request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
                }
            }

            return request;
        }
        catch
        {
            request.Dispose();
            throw;
        }
    }

    private void LogFailure(ScaleSetApiException exception, Uri requestUri)
    {
        if (_logger is null)
        {
            return;
        }

        LogRequestFailure(
            _logger,
            exception.Method ?? "UNKNOWN",
            requestUri.Host,
            exception.StatusCode,
            exception.ActivityId,
            exception.GitHubRequestId,
            null);
    }

    private static bool IsExpectedStatus(HttpStatusCode statusCode, int expectedStatus)
    {
        int numeric = (int)statusCode;
        return expectedStatus == 0
            ? numeric is >= 200 and <= 299
            : numeric == expectedStatus;
    }

    private static bool IsTransient(HttpStatusCode statusCode) =>
        statusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests ||
        ((int)statusCode >= 500 && statusCode != HttpStatusCode.NotImplemented);

    private static bool IsDecodeFailure(Exception exception) =>
        exception is JsonException or FormatException or InvalidOperationException or ArgumentException or
            InvalidDataException or ScaleSetApiException;

    private static bool IsReservedHeader(string name) =>
        string.Equals(name, "Authorization", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(name, "Host", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(name, "Content-Type", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(name, "Content-Length", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(name, "User-Agent", StringComparison.OrdinalIgnoreCase);

    private string[] CollectKnownSecrets(string authorization, Uri uri)
    {
        var secrets = new List<string>();
        if (_credentials is PersonalAccessTokenCredentials pat)
        {
            secrets.Add(pat.Token);
        }

        var adminSnapshot = Volatile.Read(ref _adminSnapshot);
        if (adminSnapshot is not null)
        {
            secrets.Add(adminSnapshot.AdminToken);
        }

        var separatorIndex = authorization.IndexOf(' ');
        if (separatorIndex >= 0 && separatorIndex + 1 < authorization.Length)
        {
            secrets.Add(authorization[(separatorIndex + 1)..]);
        }

        var query = uri.Query.TrimStart('?');
        foreach (var part in query.Split('&', StringSplitOptions.RemoveEmptyEntries).Take(16))
        {
            var separator = part.IndexOf('=');
            if (separator < 0)
            {
                continue;
            }

            var name = Uri.UnescapeDataString(part[..separator]).Replace('+', ' ');
            var normalized = string.Concat(name.Where(char.IsLetterOrDigit)).ToLowerInvariant();
            if (normalized is "sig" or "signature" or "token" or "accesstoken" or "authorization" or "key" or "credential")
            {
                var value = Uri.UnescapeDataString(part[(separator + 1)..]).Replace('+', ' ');
                if (value.Length > 0 && value.Length <= 4096)
                {
                    secrets.Add(value);
                }
            }
        }

        return secrets.ToArray();
    }

    private static void ValidateQueueUri(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        if (!uri.IsAbsoluteUri ||
            !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrEmpty(uri.Host) ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Fragment))
        {
            throw new ArgumentException("The queue URL must be an absolute HTTPS URI without credentials or a fragment.", nameof(uri));
        }
    }

    private static void ValidateAuthenticationToken(string token)
    {
        ArgumentNullException.ThrowIfNull(token);
        if (!ScaleSetCredentials.IsSafeAuthenticationToken(token))
        {
            throw new ArgumentException("Authentication token contains characters that are unsafe in an HTTP header.", nameof(token));
        }
    }

    private static Uri BuildGitHubRequestUri(Uri apiBaseUri, string path)
    {
        return BuildRelativeRequestUri(apiBaseUri, path);
    }

    private static Uri BuildActionsRequestUri(
        Uri serviceBaseUri,
        string path,
        IReadOnlyDictionary<string, string>? query)
    {
        var requestUri = BuildRelativeRequestUri(serviceBaseUri, path);
        var values = new List<KeyValuePair<string, string>>();
        AddQuery(values, serviceBaseUri.Query);
        var pathQueryIndex = path.IndexOf('?');
        if (pathQueryIndex >= 0)
        {
            AddQuery(values, path[(pathQueryIndex + 1)..]);
        }

        if (query is not null)
        {
            values.AddRange(query);
        }

        var apiVersion = values.FirstOrDefault(pair => pair.Key == "api-version").Value;
        if (string.IsNullOrEmpty(apiVersion))
        {
            values.RemoveAll(pair => pair.Key == "api-version");
            values.Add(new KeyValuePair<string, string>("api-version", ApiVersion));
        }

        var builder = new UriBuilder(requestUri)
        {
            Query = EncodeQuery(values),
        };
        return builder.Uri;
    }

    private static Uri BuildRelativeRequestUri(Uri baseUri, string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (path.Contains('#') || path.StartsWith("//", StringComparison.Ordinal) ||
            (!path.StartsWith('/') && Uri.TryCreate(path, UriKind.Absolute, out _)))
        {
            throw new ArgumentException("Request path must be relative and cannot include a fragment or authority.", nameof(path));
        }

        var queryIndex = path.IndexOf('?');
        var pathPart = queryIndex >= 0 ? path[..queryIndex] : path;
        foreach (var segment in pathPart.Split('/'))
        {
            var decoded = Uri.UnescapeDataString(segment);
            if (decoded is "." or "..")
            {
                throw new ArgumentException("Request path cannot traverse parent or current directory segments.", nameof(path));
            }
        }

        var baseWithoutQuery = new UriBuilder(baseUri)
        {
            Query = string.Empty,
            Fragment = string.Empty,
        }.Uri;
        var baseDirectory = new Uri(baseWithoutQuery.AbsoluteUri.TrimEnd('/') + "/", UriKind.Absolute);
        var relativePath = pathPart.TrimStart('/');
        return string.IsNullOrEmpty(relativePath) ? baseDirectory : new Uri(baseDirectory, relativePath);
    }

    private static void AddQuery(List<KeyValuePair<string, string>> values, string rawQuery)
    {
        var query = rawQuery.TrimStart('?');
        if (query.Length == 0)
        {
            return;
        }

        foreach (var part in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = part.IndexOf('=');
            var rawName = separator >= 0 ? part[..separator] : part;
            var rawValue = separator >= 0 ? part[(separator + 1)..] : string.Empty;
            try
            {
                values.Add(new KeyValuePair<string, string>(DecodeQueryPart(rawName), DecodeQueryPart(rawValue)));
            }
            catch (UriFormatException exception)
            {
                throw new ArgumentException("Request query contains invalid URL encoding.", nameof(rawQuery), exception);
            }
        }
    }

    private static string DecodeQueryPart(string value) => Uri.UnescapeDataString(value.Replace('+', ' '));

    private static string EncodeQuery(IEnumerable<KeyValuePair<string, string>> values)
    {
        return string.Join(
            "&",
            values.Select(pair =>
                $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}"));
    }
}
