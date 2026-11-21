namespace ScaleSet.Client;

/// <summary>Optional system identity attached to client request metadata.</summary>
public sealed record ScaleSetClientMetadata
{
    /// <summary>Autoscaler or host name.</summary>
    public string System { get; init; } = string.Empty;

    /// <summary>Subsystem such as controller or listener.</summary>
    public string Subsystem { get; init; } = string.Empty;

    /// <summary>Host version.</summary>
    public string Version { get; init; } = string.Empty;

    /// <summary>Host source commit.</summary>
    public string CommitSha { get; init; } = string.Empty;

    /// <summary>Scale set identifier associated with this client.</summary>
    public long ScaleSetId { get; init; }
}

/// <summary>Timeout, retry, and diagnostic metadata options for <see cref="ScaleSetClient"/>.</summary>
public sealed record ScaleSetClientOptions
{
    private static readonly TimeSpan MaximumTimerDuration = TimeSpan.FromMilliseconds(uint.MaxValue - 1d);
    internal static readonly TimeSpan MaximumRetryDelay = TimeSpan.FromMinutes(1);

    /// <summary>Maximum duration for control-plane requests. Defaults to 60 seconds.</summary>
    public TimeSpan ControlRequestTimeout { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>Maximum duration for a long-poll request. Defaults to 65 seconds.</summary>
    public TimeSpan PollRequestTimeout { get; init; } = TimeSpan.FromSeconds(65);

    /// <summary>Retries after transient failures for read-only requests.</summary>
    public int MaxReadOnlyRetries { get; init; } = 2;

    /// <summary>Retries for 401/403 responses from Actions registration discovery.</summary>
    public int MaxRegistrationDiscoveryAuthRetries { get; init; } = 4;

    /// <summary>Base delay for bounded read-only retry backoff.</summary>
    public TimeSpan RetryBaseDelay { get; init; } = TimeSpan.FromMilliseconds(200);

    /// <summary>
    /// Maximum delay for locally calculated read-only retry backoff. Valid
    /// server Retry-After values may override this delay up to one minute.
    /// </summary>
    public TimeSpan RetryMaxDelay { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>Optional valid HTTP User-Agent product list.</summary>
    public string? UserAgent { get; init; }

    /// <summary>Optional host and scale set metadata used in the User-Agent comment.</summary>
    public ScaleSetClientMetadata? Metadata { get; init; }

    /// <summary>
    /// Optional unified retry, backoff, budget, and rate-limit policy. When omitted, read-only requests use
    /// <see cref="MaxReadOnlyRetries"/>, <see cref="RetryBaseDelay"/>, and <see cref="RetryMaxDelay"/> with a
    /// one-minute cap on <c>Retry-After</c> delays.
    /// </summary>
    public ScaleSetRetryOptions? Retry { get; init; }

    internal void Validate()
    {
        if (ControlRequestTimeout <= TimeSpan.Zero || ControlRequestTimeout > MaximumTimerDuration)
        {
            throw new ArgumentOutOfRangeException(nameof(ControlRequestTimeout), "Control request timeout must be positive and within the timer range.");
        }

        if (PollRequestTimeout <= TimeSpan.FromSeconds(50) || PollRequestTimeout > MaximumTimerDuration)
        {
            throw new ArgumentOutOfRangeException(nameof(PollRequestTimeout), "Poll request timeout must exceed the upstream 50-second long poll and stay within the timer range.");
        }

        if (MaxReadOnlyRetries is < 0 or > 10)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxReadOnlyRetries), "Read-only retries must be between 0 and 10.");
        }

        if (MaxRegistrationDiscoveryAuthRetries is < 0 or > 10)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxRegistrationDiscoveryAuthRetries), "Discovery authentication retries must be between 0 and 10.");
        }

        if (RetryBaseDelay < TimeSpan.Zero || RetryBaseDelay > MaximumRetryDelay)
        {
            throw new ArgumentOutOfRangeException(nameof(RetryBaseDelay), "Retry base delay must be between zero and one minute.");
        }

        if (RetryMaxDelay < RetryBaseDelay || RetryMaxDelay > MaximumRetryDelay)
        {
            throw new ArgumentOutOfRangeException(nameof(RetryMaxDelay), "Retry maximum delay must be at least the base delay and no more than one minute.");
        }

        if (UserAgent is not null && !IsValidUserAgent(UserAgent))
        {
            throw new ArgumentException("User-Agent must contain valid HTTP product/comment values.", nameof(UserAgent));
        }

        Retry?.Validate();
    }

    private static bool IsValidUserAgent(string userAgent)
    {
        try
        {
            using var request = new HttpRequestMessage();
            request.Headers.UserAgent.ParseAdd(userAgent);
            return request.Headers.UserAgent.Count > 0;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
