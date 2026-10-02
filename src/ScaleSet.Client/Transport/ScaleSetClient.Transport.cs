using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace ScaleSet.Client;

public sealed partial class ScaleSetClient
{
    private const string ApiVersion = "6.0-preview";
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

        var readOnly = dependency != "queue" && (method == HttpMethod.Get || method == HttpMethod.Head);
        var knownSecrets = CollectKnownSecrets(authorization, uri);
        var transientRetries = 0;
        var authenticationRetries = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var timeoutSource = new CancellationTokenSource(timeout, _timeProvider);
            using var linkedSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);
            using var request = CreateRequest(method, uri, body, authorization, contentType, headers, dependency);
            using var activity = ScaleSetClientDiagnostics.StartActivity(method, uri, dependency);
            var startTimestamp = _timeProvider.GetTimestamp();
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
            catch (OperationCanceledException) when (readOnly && transientRetries < _options.MaxReadOnlyRetries)
            {
                activity?.SetStatus(ActivityStatusCode.Error, "request timed out");
                ScaleSetClientDiagnostics.RecordAttempt(
                    dependency,
                    method.Method,
                    statusCode: null,
                    _timeProvider.GetElapsedTime(startTimestamp).TotalSeconds);
                transientRetries++;
                ScaleSetClientDiagnostics.RecordRetry(dependency, method.Method, "transient");
                await DelayBeforeRetryAsync(transientRetries, cancellationToken).ConfigureAwait(false);
                continue;
            }
            catch (OperationCanceledException)
            {
                ScaleSetClientDiagnostics.RecordAttempt(
                    dependency,
                    method.Method,
                    statusCode: null,
                    _timeProvider.GetElapsedTime(startTimestamp).TotalSeconds);
                activity?.SetStatus(ActivityStatusCode.Error, "request timed out");
                var exception = new ScaleSetApiException(
                    "ScaleSet HTTP request timed out.",
                    statusCode: null,
                    method: method.Method,
                    safeUri: ScaleSetClientDiagnostics.CreateSafeUri(
                        uri,
                        redactPath: dependency == "queue").AbsoluteUri);
                LogFailure(exception, uri);
                throw exception;
            }
            catch (HttpRequestException) when (readOnly && transientRetries < _options.MaxReadOnlyRetries)
            {
                activity?.SetStatus(ActivityStatusCode.Error, "transport retry");
                ScaleSetClientDiagnostics.RecordAttempt(
                    dependency,
                    method.Method,
                    statusCode: null,
                    _timeProvider.GetElapsedTime(startTimestamp).TotalSeconds);
                transientRetries++;
                ScaleSetClientDiagnostics.RecordRetry(dependency, method.Method, "transient");
                await DelayBeforeRetryAsync(transientRetries, cancellationToken).ConfigureAwait(false);
                continue;
            }
            catch (HttpRequestException)
            {
                ScaleSetClientDiagnostics.RecordAttempt(
                    dependency,
                    method.Method,
                    statusCode: null,
                    _timeProvider.GetElapsedTime(startTimestamp).TotalSeconds);
                activity?.SetStatus(ActivityStatusCode.Error, "transport failure");
                var exception = new ScaleSetApiException(
                    "ScaleSet HTTP transport failed.",
                    statusCode: null,
                    method: method.Method,
                    safeUri: ScaleSetClientDiagnostics.CreateSafeUri(
                        uri,
                        redactPath: dependency == "queue").AbsoluteUri);
                LogFailure(exception, uri);
                throw exception;
            }

            using (response)
            {
                var statusCode = (int)response.StatusCode;
                activity?.SetTag("http.response.status_code", statusCode);
                var expected = IsExpectedStatus(response.StatusCode, expectedStatus) ||
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

                    byte[] responseBody;
                    try
                    {
                        responseBody = await ReadResponseBodyAsync(response, linkedSource.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        activity?.SetTag("error.type", "caller_canceled");
                        activity?.SetStatus(ActivityStatusCode.Error, "caller canceled response read");
                        throw new OperationCanceledException("The ScaleSet response read was canceled by the caller.", cancellationToken);
                    }
                    catch (OperationCanceledException)
                    {
                        activity?.SetTag("error.type", "response_timeout");
                        activity?.SetStatus(ActivityStatusCode.Error, "response body read timed out");
                        var wrapped = CreateResponseException(
                            response,
                            method,
                            uri,
                            [],
                            truncated: false,
                            queueTokenOnUnauthorized: false,
                            redactPath: dependency == "queue",
                            knownSecrets: knownSecrets,
                            message: "ScaleSet response body read timed out.");
                        LogFailure(wrapped, uri);
                        throw wrapped;
                    }
                    catch (Exception exception) when (exception is HttpRequestException or IOException)
                    {
                        activity?.SetTag("error.type", "response_read");
                        activity?.SetStatus(ActivityStatusCode.Error, "response body read failed");
                        var wrapped = CreateResponseException(
                            response,
                            method,
                            uri,
                            [],
                            truncated: false,
                            queueTokenOnUnauthorized: false,
                            redactPath: dependency == "queue",
                            knownSecrets: knownSecrets,
                            message: "ScaleSet response body could not be read.");
                        LogFailure(wrapped, uri);
                        throw wrapped;
                    }
                    ScaleSetClientDiagnostics.RecordAttempt(
                        dependency,
                        method.Method,
                        statusCode,
                        _timeProvider.GetElapsedTime(startTimestamp).TotalSeconds);
                    try
                    {
                        return decode(responseBody);
                    }
                    catch (Exception exception) when (IsDecodeFailure(exception))
                    {
                        activity?.SetTag("error.type", "response_decode");
                        activity?.SetStatus(ActivityStatusCode.Error, "response body decode failed");
                        var wrapped = CreateResponseException(
                            response,
                            method,
                            uri,
                            responseBody,
                            truncated: false,
                            queueTokenOnUnauthorized: false,
                            redactPath: dependency == "queue",
                            knownSecrets: knownSecrets,
                            message: "ScaleSet response body could not be decoded.");
                        LogFailure(wrapped, uri);
                        throw wrapped;
                    }
                }

                if (retryDiscoveryAuthentication &&
                    response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden &&
                    authenticationRetries < _options.MaxRegistrationDiscoveryAuthRetries)
                {
                    response.Dispose();
                    ScaleSetClientDiagnostics.RecordAttempt(
                        dependency,
                        method.Method,
                        statusCode,
                        _timeProvider.GetElapsedTime(startTimestamp).TotalSeconds);
                    authenticationRetries++;
                    ScaleSetClientDiagnostics.RecordRetry(dependency, method.Method, "discovery-auth");
                    await DelayBeforeRetryAsync(authenticationRetries, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                if (readOnly && IsTransient(response.StatusCode) && transientRetries < _options.MaxReadOnlyRetries)
                {
                    response.Dispose();
                    activity?.SetStatus(ActivityStatusCode.Error, "transient HTTP response");
                    ScaleSetClientDiagnostics.RecordAttempt(
                        dependency,
                        method.Method,
                        statusCode,
                        _timeProvider.GetElapsedTime(startTimestamp).TotalSeconds);
                    transientRetries++;
                    ScaleSetClientDiagnostics.RecordRetry(dependency, method.Method, "transient");
                    await DelayBeforeRetryAsync(transientRetries, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                activity?.SetStatus(ActivityStatusCode.Error, $"HTTP {statusCode}");
                (byte[] Body, bool Truncated) boundedBody;
                try
                {
                    boundedBody = await ScaleSetClientDiagnostics.ReadBoundedErrorBodyAsync(
                        response,
                        linkedSource.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw new OperationCanceledException("The ScaleSet error response read was canceled by the caller.", cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    var wrapped = CreateResponseException(
                        response,
                        method,
                        uri,
                        [],
                        truncated: false,
                        queueTokenOnUnauthorized: queueTokenOnUnauthorized,
                        redactPath: dependency == "queue",
                        knownSecrets: knownSecrets,
                        message: "ScaleSet error response body read timed out.");
                    LogFailure(wrapped, uri);
                    throw wrapped;
                }
                catch (Exception exception) when (exception is HttpRequestException or IOException)
                {
                    var wrapped = CreateResponseException(
                        response,
                        method,
                        uri,
                        [],
                        truncated: false,
                        queueTokenOnUnauthorized: queueTokenOnUnauthorized,
                        redactPath: dependency == "queue",
                        knownSecrets: knownSecrets,
                        message: "ScaleSet error response body could not be read.");
                    LogFailure(wrapped, uri);
                    throw wrapped;
                }

                activity?.SetTag("error.type", $"http_{statusCode}");
                ScaleSetClientDiagnostics.RecordAttempt(
                    dependency,
                    method.Method,
                    statusCode,
                    _timeProvider.GetElapsedTime(startTimestamp).TotalSeconds);
                var failure = CreateResponseException(
                    response,
                    method,
                    uri,
                    boundedBody.Body,
                    boundedBody.Truncated,
                    queueTokenOnUnauthorized: queueTokenOnUnauthorized,
                    redactPath: dependency == "queue",
                    knownSecrets: knownSecrets);
                LogFailure(failure, uri);
                throw failure;
            }
        }
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
        var request = new HttpRequestMessage(method, uri);
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

    private static ScaleSetApiException CreateResponseException(
        HttpResponseMessage response,
        HttpMethod method,
        Uri uri,
        ReadOnlySpan<byte> body,
        bool truncated,
        bool queueTokenOnUnauthorized,
        bool redactPath,
        IReadOnlyCollection<string> knownSecrets,
        string? message = null)
    {
        var status = (int)response.StatusCode;
        var domainError = queueTokenOnUnauthorized && response.StatusCode == HttpStatusCode.Unauthorized
            ? ScaleSetDomainError.MessageQueueTokenExpired
            : ScaleSetClientDiagnostics.ParseDomainError(body);
        var safeUri = ScaleSetClientDiagnostics.CreateSafeUri(uri, knownSecrets, redactPath).AbsoluteUri;
        var activityId = ScaleSetClientDiagnostics.CorrelationHeader(response, "ActivityId", knownSecrets);
        var githubRequestId = ScaleSetClientDiagnostics.CorrelationHeader(response, "X-GitHub-Request-Id", knownSecrets);
        var bodyPreview = ScaleSetClientDiagnostics.CreateBodyPreview(body, truncated, knownSecrets);
        var errorMessage = message ?? $"ScaleSet HTTP request failed with status {status}.";
        return new ScaleSetApiException(
            errorMessage,
            status,
            domainError,
            method: method.Method,
            safeUri: safeUri,
            activityId: activityId.Length == 0 ? null : activityId,
            githubRequestId: githubRequestId.Length == 0 ? null : githubRequestId,
            bodyPreview: bodyPreview);
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

    private async Task DelayBeforeRetryAsync(int retryNumber, CancellationToken cancellationToken)
    {
        var baseMilliseconds = _options.RetryBaseDelay.TotalMilliseconds;
        if (baseMilliseconds <= 0)
        {
            return;
        }

        var cap = _options.RetryMaxDelay.TotalMilliseconds;
        var exponential = Math.Min(cap, baseMilliseconds * Math.Pow(2, Math.Min(retryNumber - 1, 30)));
        var half = exponential / 2;
        var jittered = half + (Random.Shared.NextDouble() * half);
        await Task.Delay(TimeSpan.FromMilliseconds(jittered), _timeProvider, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<byte[]> ReadResponseBodyAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (response.Content is null)
        {
            return [];
        }

        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            return bytes.AsSpan(3).ToArray();
        }

        return bytes;
    }

    private static bool IsExpectedStatus(HttpStatusCode statusCode, int expectedStatus)
    {
        var numeric = (int)statusCode;
        return expectedStatus == 0
            ? numeric is >= 200 and <= 299
            : numeric == expectedStatus;
    }

    private static bool IsTransient(HttpStatusCode statusCode) =>
        statusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests ||
        (int)statusCode >= 500;

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
