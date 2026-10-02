using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ScaleSet.Providers.Docker;

internal sealed class DockerEngineClient : IDisposable
{
    private const int MaximumJsonResponseBytes = 8 * 1024 * 1024;
    private const int MaximumVersionResponseBytes = 64 * 1024;
    private const string MaximumSupportedApiVersion = "1.47";
    private const string MinimumSupportedApiVersion = "1.40";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = null,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly Uri _engineEndpoint;
    private readonly DockerProviderOptionsSnapshot _options;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _versionGate = new(1, 1);
    private string? _apiVersion;
    private string? _daemonArchitecture;
    private bool _disposed;

    public DockerEngineClient(
        DockerProviderOptionsSnapshot options,
        HttpClient? httpClient,
        TimeProvider timeProvider)
    {
        _options = options;
        _engineEndpoint = options.EngineEndpoint;
        _timeProvider = timeProvider;
        _ownsHttpClient = httpClient is null;
        _httpClient = httpClient ?? CreateSocketClient(options);
    }

    public string ApiVersion => _apiVersion
        ?? throw new InvalidOperationException("The Docker Engine API has not been negotiated.");

    public string DaemonArchitecture => _daemonArchitecture
        ?? throw new InvalidOperationException("The Docker Engine API has not been negotiated.");

    public async Task EnsureCompatibleEngineAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        if (_apiVersion is not null)
        {
            return;
        }

        await _versionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_apiVersion is not null)
            {
                return;
            }

            DockerApiResult<DockerVersionResponse> versionResult = await SendJsonAsync<DockerVersionResponse>(
                HttpMethod.Get,
                "version",
                "version",
                _options.RequestTimeout,
                [HttpStatusCode.OK],
                parseStatuses: [HttpStatusCode.OK],
                maximumResponseBytes: MaximumVersionResponseBytes,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            DockerVersionResponse version = versionResult.Content
                ?? throw ProviderFailure("version", "invalid-engine-response");

            if (!string.Equals(version.Os, "linux", StringComparison.OrdinalIgnoreCase))
            {
                throw ProviderFailure("version", "non-linux-daemon");
            }

            EngineApiVersion daemonMaximum = EngineApiVersion.Parse(version.ApiVersion, "version");
            EngineApiVersion daemonMinimum = EngineApiVersion.Parse(version.MinApiVersion, "version");
            EngineApiVersion clientMinimum = EngineApiVersion.Parse(MinimumSupportedApiVersion, "version");
            EngineApiVersion clientMaximum = EngineApiVersion.Parse(MaximumSupportedApiVersion, "version");
            if (daemonMaximum < daemonMinimum || daemonMaximum < clientMinimum || daemonMinimum > clientMaximum)
            {
                throw ProviderFailure("version", "unsupported-api-version");
            }

            EngineApiVersion selected = daemonMaximum < clientMaximum ? daemonMaximum : clientMaximum;
            if (selected < daemonMinimum || selected < clientMinimum)
            {
                throw ProviderFailure("version", "unsupported-api-version");
            }

            string selectedVersion = selected.ToString();
            DockerApiResult<DockerInfoResponse> infoResult = await SendJsonAsync<DockerInfoResponse>(
                HttpMethod.Get,
                $"v{selectedVersion}/info",
                "info",
                _options.RequestTimeout,
                [HttpStatusCode.OK],
                parseStatuses: [HttpStatusCode.OK],
                maximumResponseBytes: MaximumVersionResponseBytes,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            DockerInfoResponse info = infoResult.Content
                ?? throw ProviderFailure("info", "invalid-engine-response");
            if (!string.Equals(info.OsType, "linux", StringComparison.OrdinalIgnoreCase))
            {
                throw ProviderFailure("info", "non-linux-daemon");
            }

            if (string.IsNullOrWhiteSpace(info.Architecture))
            {
                throw ProviderFailure("info", "invalid-engine-response");
            }

            _daemonArchitecture = info.Architecture;
            _apiVersion = selectedVersion;
        }
        finally
        {
            _versionGate.Release();
        }
    }

    public async Task<DockerApiResult<TResponse>> SendJsonAsync<TResponse>(
        HttpMethod method,
        string relativePath,
        string operation,
        TimeSpan timeout,
        IReadOnlyCollection<HttpStatusCode> acceptedStatuses,
        IReadOnlyCollection<HttpStatusCode> parseStatuses,
        object? requestBody = null,
        bool outcomeMayBeUnknown = false,
        int maximumResponseBytes = MaximumJsonResponseBytes,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(method);
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        ArgumentNullException.ThrowIfNull(acceptedStatuses);
        ArgumentNullException.ThrowIfNull(parseStatuses);

        Uri requestUri = new(_engineEndpoint, relativePath);
        using HttpRequestMessage request = new(method, requestUri);
        if (requestBody is not null)
        {
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(requestBody, requestBody.GetType(), JsonOptions);
            request.Content = new SensitiveByteArrayContent(bytes);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        }

        using CancellationTokenSource timeoutSource = new(timeout, _timeProvider);
        using CancellationTokenSource requestSource = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeoutSource.Token);
        long started = _timeProvider.GetTimestamp();
        HttpStatusCode? statusCode = null;
        bool failed = true;
        using Activity? activity = DockerProviderDiagnostics.Start(operation, method);
        try
        {
            using HttpResponseMessage response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                requestSource.Token).ConfigureAwait(false);
            statusCode = response.StatusCode;
            EnsureNoRedirect(response, requestUri, operation);
            if (!acceptedStatuses.Contains(response.StatusCode))
            {
                throw new DockerProviderException(
                    operation,
                    "engine-http-error",
                    response.StatusCode,
                    outcomeMayBeUnknown);
            }

            TResponse? content = default;
            if (parseStatuses.Contains(response.StatusCode))
            {
                content = await ReadJsonAsync<TResponse>(response, maximumResponseBytes, requestSource.Token)
                    .ConfigureAwait(false);
            }

            failed = false;
            return new DockerApiResult<TResponse>(response.StatusCode, content);
        }
        catch (DockerProviderException)
        {
            throw;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new DockerProviderException(operation, "request-timeout", statusCode, outcomeMayBeUnknown);
        }
        catch (HttpRequestException)
        {
            throw new DockerProviderException(operation, "engine-unavailable", statusCode, outcomeMayBeUnknown);
        }
        catch (IOException)
        {
            throw new DockerProviderException(operation, "engine-response-read-failed", statusCode, outcomeMayBeUnknown);
        }
        catch (JsonException)
        {
            throw new DockerProviderException(operation, "invalid-engine-response", statusCode, outcomeMayBeUnknown);
        }
        finally
        {
            double duration = _timeProvider.GetElapsedTime(started).TotalSeconds;
            DockerProviderDiagnostics.Record(operation, method, statusCode, duration);
            DockerProviderDiagnostics.SetStatus(activity, statusCode, failed);
        }
    }

    public async Task PullImageAsync(
        string image,
        string platform,
        DockerProviderOptionsSnapshot options,
        CancellationToken cancellationToken)
    {
        string apiVersion = ApiVersion;
        string path = $"v{apiVersion}/images/create?fromImage={Uri.EscapeDataString(image)}&platform={Uri.EscapeDataString(platform)}";
        Uri requestUri = new(_engineEndpoint, path);
        using HttpRequestMessage request = new(HttpMethod.Post, requestUri);
        using CancellationTokenSource timeoutSource = new(options.ImagePullTimeout, _timeProvider);
        using CancellationTokenSource requestSource = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeoutSource.Token);
        long started = _timeProvider.GetTimestamp();
        HttpStatusCode? statusCode = null;
        bool failed = true;
        using Activity? activity = DockerProviderDiagnostics.Start("pull-image", HttpMethod.Post);
        try
        {
            using HttpResponseMessage response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                requestSource.Token).ConfigureAwait(false);
            statusCode = response.StatusCode;
            EnsureNoRedirect(response, requestUri, "pull-image");
            if (response.StatusCode != HttpStatusCode.OK)
            {
                throw new DockerProviderException(
                    "pull-image",
                    "image-pull-failed",
                    response.StatusCode,
                    outcomeMayBeUnknown: true);
            }

            await ReadPullProgressAsync(
                response,
                options.MaximumPullResponseBytes,
                options.MaximumPullLineBytes,
                requestSource.Token).ConfigureAwait(false);
            failed = false;
        }
        catch (DockerProviderException)
        {
            throw;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new DockerProviderException("pull-image", "request-timeout", statusCode, outcomeMayBeUnknown: true);
        }
        catch (HttpRequestException)
        {
            throw new DockerProviderException("pull-image", "engine-unavailable", statusCode, outcomeMayBeUnknown: true);
        }
        catch (IOException)
        {
            throw new DockerProviderException("pull-image", "image-pull-stream-failed", statusCode, outcomeMayBeUnknown: true);
        }
        catch (JsonException)
        {
            throw new DockerProviderException("pull-image", "invalid-image-pull-stream", statusCode, outcomeMayBeUnknown: true);
        }
        finally
        {
            DockerProviderDiagnostics.Record(
                "pull-image",
                HttpMethod.Post,
                statusCode,
                _timeProvider.GetElapsedTime(started).TotalSeconds);
            DockerProviderDiagnostics.SetStatus(activity, statusCode, failed);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _versionGate.Dispose();
        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }
    }

    private static HttpClient CreateSocketClient(DockerProviderOptionsSnapshot options)
    {
        if (!string.Equals(options.EngineEndpoint.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("A caller-owned HttpClient is required for a TLS Docker Engine endpoint.", nameof(options));
        }

        SocketsHttpHandler handler = new()
        {
            AllowAutoRedirect = false,
            ConnectCallback = async (_, cancellationToken) =>
            {
                Socket socket = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                try
                {
                    await socket.ConnectAsync(new UnixDomainSocketEndPoint(options.SocketPath), cancellationToken)
                        .ConfigureAwait(false);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            },
        };
        return new HttpClient(handler, disposeHandler: true)
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
    }

    private static void EnsureNoRedirect(HttpResponseMessage response, Uri originalUri, string operation)
    {
        if (response.RequestMessage?.RequestUri is Uri responseUri && responseUri != originalUri)
        {
            throw new DockerProviderException(operation, "redirect-rejected", response.StatusCode);
        }
    }

    private static async Task<TResponse?> ReadJsonAsync<TResponse>(
        HttpResponseMessage response,
        int maximumResponseBytes,
        CancellationToken cancellationToken)
    {
        if (response.Content is null)
        {
            return default;
        }

        byte[] bytes = await ReadBoundedBytesAsync(response.Content, maximumResponseBytes, cancellationToken)
            .ConfigureAwait(false);
        if (bytes.Length == 0)
        {
            return default;
        }

        try
        {
            return JsonSerializer.Deserialize<TResponse>(bytes, JsonOptions);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private static async Task<byte[]> ReadBoundedBytesAsync(
        HttpContent content,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength is long contentLength && contentLength > maximumBytes)
        {
            throw new IOException("The Docker Engine response exceeded its size limit.");
        }

        await using Stream stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(checked(maximumBytes + 1));
        int written = 0;
        try
        {
            while (true)
            {
                int remaining = Math.Min(buffer.Length - written, 16 * 1024);
                int read = await stream.ReadAsync(buffer.AsMemory(written, remaining), cancellationToken)
                    .ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                written += read;
                if (written > maximumBytes)
                {
                    throw new IOException("The Docker Engine response exceeded its size limit.");
                }
            }

            return buffer.AsSpan(0, written).ToArray();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(buffer);
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static async Task ReadPullProgressAsync(
        HttpResponseMessage response,
        int maximumTotalBytes,
        int maximumLineBytes,
        CancellationToken cancellationToken)
    {
        if (response.Content is null)
        {
            throw new IOException("The Docker Engine image-pull stream was empty.");
        }

        await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        byte[] readBuffer = ArrayPool<byte>.Shared.Rent(8 * 1024);
        ArrayBufferWriter<byte> lineBuffer = new();
        int totalBytes = 0;
        try
        {
            while (true)
            {
                int read = await stream.ReadAsync(readBuffer.AsMemory(0, readBuffer.Length), cancellationToken)
                    .ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                totalBytes = checked(totalBytes + read);
                if (totalBytes > maximumTotalBytes)
                {
                    throw new IOException("The Docker Engine image-pull stream exceeded its size limit.");
                }

                for (int index = 0; index < read; index++)
                {
                    byte current = readBuffer[index];
                    if (current == (byte)'\n')
                    {
                        ProcessPullProgressLine(lineBuffer.WrittenMemory);
                        lineBuffer.Clear();
                        continue;
                    }

                    if (lineBuffer.WrittenCount >= maximumLineBytes)
                    {
                        throw new IOException("The Docker Engine image-pull record exceeded its size limit.");
                    }

                    Span<byte> destination = lineBuffer.GetSpan(1);
                    destination[0] = current;
                    lineBuffer.Advance(1);
                }
            }

            if (lineBuffer.WrittenCount > 0)
            {
                ProcessPullProgressLine(lineBuffer.WrittenMemory);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(readBuffer);
        }
    }

    private static void ProcessPullProgressLine(ReadOnlyMemory<byte> line)
    {
        ReadOnlyMemory<byte> json = line;
        while (!json.IsEmpty && (json.Span[^1] == (byte)'\r' || json.Span[^1] == (byte)' ' || json.Span[^1] == (byte)'\t'))
        {
            json = json[..^1];
        }

        if (json.IsEmpty)
        {
            return;
        }

        using JsonDocument document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("Invalid Docker image-pull progress record.");
        }

        foreach (JsonProperty property in document.RootElement.EnumerateObject())
        {
            bool hasError = property.NameEquals("error") || property.NameEquals("Error");
            if (hasError && property.Value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(property.Value.GetString()))
            {
                throw new DockerProviderException("pull-image", "image-pull-failed", HttpStatusCode.OK, outcomeMayBeUnknown: true);
            }

            bool hasErrorDetail = property.NameEquals("errorDetail") || property.NameEquals("ErrorDetail");
            if (hasErrorDetail
                && property.Value.ValueKind == JsonValueKind.Object
                && property.Value.TryGetProperty("message", out JsonElement message)
                && message.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(message.GetString()))
            {
                throw new DockerProviderException("pull-image", "image-pull-failed", HttpStatusCode.OK, outcomeMayBeUnknown: true);
            }
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private static DockerProviderException ProviderFailure(string operation, string failureCode) =>
        new(operation, failureCode);

    private readonly record struct EngineApiVersion(int Major, int Minor) : IComparable<EngineApiVersion>
    {
        public int CompareTo(EngineApiVersion other)
        {
            int majorComparison = Major.CompareTo(other.Major);
            return majorComparison != 0 ? majorComparison : Minor.CompareTo(other.Minor);
        }

        public static bool operator <(EngineApiVersion left, EngineApiVersion right) => left.CompareTo(right) < 0;

        public static bool operator >(EngineApiVersion left, EngineApiVersion right) => left.CompareTo(right) > 0;

        public static bool operator <=(EngineApiVersion left, EngineApiVersion right) => left.CompareTo(right) <= 0;

        public static bool operator >=(EngineApiVersion left, EngineApiVersion right) => left.CompareTo(right) >= 0;

        public static EngineApiVersion Parse(string? value, string operation)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                throw ProviderFailure(operation, "invalid-engine-version");
            }

            string[] parts = value.Split('.', StringSplitOptions.None);
            if (parts.Length != 2
                || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out int major)
                || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out int minor)
                || major < 1
                || minor < 0)
            {
                throw ProviderFailure(operation, "invalid-engine-version");
            }

            return new EngineApiVersion(major, minor);
        }

        public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{Major}.{Minor}");
    }

    private sealed class DockerVersionResponse
    {
        [JsonPropertyName("ApiVersion")]
        public string? ApiVersion { get; init; }

        [JsonPropertyName("MinAPIVersion")]
        public string? MinApiVersion { get; init; }

        [JsonPropertyName("Os")]
        public string? Os { get; init; }
    }

    private sealed class DockerInfoResponse
    {
        [JsonPropertyName("OSType")]
        public string? OsType { get; init; }

        [JsonPropertyName("Architecture")]
        public string? Architecture { get; init; }
    }

    private sealed class SensitiveByteArrayContent(byte[] content) : ByteArrayContent(content)
    {
        private readonly byte[] _content = content;

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                CryptographicOperations.ZeroMemory(_content);
            }

            base.Dispose(disposing);
        }
    }
}

internal sealed record DockerApiResult<TResponse>(HttpStatusCode StatusCode, TResponse? Content);
