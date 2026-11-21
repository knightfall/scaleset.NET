using System.Net;
using Microsoft.Extensions.Logging;

namespace ScaleSet.Client;

/// <summary>GitHub Actions runner scale set client.</summary>
public sealed partial class ScaleSetClient : IDisposable
{
    private static readonly TimeSpan AdminRefreshWindow = TimeSpan.FromSeconds(60);
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly SocketsHttpHandler? _ownedHandler;
    private readonly ScaleSetCredentials _credentials;
    private readonly ScaleSetClientOptions _options;
    private readonly ILogger<ScaleSetClient>? _logger;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _adminRefreshGate = new(1, 1);
    private AdminServiceSnapshot? _adminSnapshot;
    private long _adminGeneration;
    private int _disposed;

    /// <summary>Creates a ScaleSet client with PAT or GitHub App credentials.</summary>
    /// <param name="target">Validated GitHub.com repository or organization target.</param>
    /// <param name="credentials">PAT or GitHub App credentials.</param>
    /// <param name="options">Timeout, retry, and metadata options.</param>
    /// <param name="httpClient">Optional caller-owned HTTP client; its redirect and TLS settings remain caller-controlled.</param>
    /// <param name="logger">Optional application logger.</param>
    /// <param name="timeProvider">Optional clock used for token expiry and request timeouts.</param>
    public ScaleSetClient(
        GitHubTarget target,
        ScaleSetCredentials credentials,
        ScaleSetClientOptions? options = null,
        HttpClient? httpClient = null,
        ILogger<ScaleSetClient>? logger = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(credentials);

        _options = options ?? new ScaleSetClientOptions();
        _options.Validate();
        _credentials = credentials;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
        Target = target;
        UserAgent = CreateUserAgent(_options);

        if (httpClient is null)
        {
            _ownedHandler = new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                ConnectTimeout = TimeSpan.FromSeconds(30),
                PooledConnectionIdleTimeout = TimeSpan.FromSeconds(90),
                PooledConnectionLifetime = TimeSpan.FromMinutes(15),
                KeepAlivePingDelay = TimeSpan.FromSeconds(30),
                KeepAlivePingTimeout = TimeSpan.FromSeconds(10),
                KeepAlivePingPolicy = HttpKeepAlivePingPolicy.WithActiveRequests,
            };
            _httpClient = new HttpClient(_ownedHandler, disposeHandler: false)
            {
                Timeout = Timeout.InfiniteTimeSpan,
                DefaultRequestVersion = HttpVersion.Version20,
                DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
            };
            _ownsHttpClient = true;
        }
        else
        {
            _httpClient = httpClient;
            _ownsHttpClient = false;
        }
    }

    /// <summary>The validated repository or organization target for this client.</summary>
    public GitHubTarget Target { get; }

    /// <summary>Configured client timeouts, retries, and host metadata.</summary>
    public ScaleSetClientOptions Options => _options;

    /// <summary>HTTP User-Agent value attached to every client request.</summary>
    public string UserAgent { get; }

    /// <summary>Disposes the internally-created HTTP client. Caller-supplied clients remain caller-owned.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
            _ownedHandler?.Dispose();
        }

        _adminRefreshGate.Dispose();
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
    }

    private bool IsCurrentAdminSnapshot(AdminServiceSnapshot? snapshot)
    {
        return snapshot is not null && snapshot.ExpiresAt >= _timeProvider.GetUtcNow() + AdminRefreshWindow;
    }

    private static string CreateUserAgent(ScaleSetClientOptions options)
    {
        if (options.UserAgent is not null)
        {
            return options.UserAgent;
        }

        var version = typeof(ScaleSetClient).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";
        var userAgent = $"ScaleSet.Client/{version}";
        if (options.Metadata is not { } metadata)
        {
            return userAgent;
        }

        var comment = string.Join(
            "; ",
            $"system={EscapeUserAgentComment(metadata.System)}",
            $"subsystem={EscapeUserAgentComment(metadata.Subsystem)}",
            $"version={EscapeUserAgentComment(metadata.Version)}",
            $"commit={EscapeUserAgentComment(metadata.CommitSha)}",
            $"scale-set={metadata.ScaleSetId}");
        return $"{userAgent} ({comment})";
    }

    private static string EscapeUserAgentComment(string value)
    {
        var safeValue = string.Concat(value.Select(character =>
            char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.' ? character : '-'));
        return safeValue.Length > 64 ? safeValue[..64] : safeValue;
    }

    private sealed class AdminServiceSnapshot(Uri serviceUri, string adminToken, DateTimeOffset expiresAt, long generation)
    {
        internal Uri ServiceUri { get; } = serviceUri;

        internal string AdminToken { get; } = adminToken;

        internal DateTimeOffset ExpiresAt { get; } = expiresAt;

        internal long Generation { get; } = generation;

        public override string ToString() =>
            $"AdminServiceSnapshot {{ ServiceUri = {ServiceUri.GetLeftPart(UriPartial.Authority)}, " +
            $"AdminToken = [REDACTED], ExpiresAt = {ExpiresAt:O}, Generation = {Generation} }}";
    }
}
