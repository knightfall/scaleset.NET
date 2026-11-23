using System.Collections.Immutable;

namespace ScaleSet.Client;

/// <summary>Limits retries for one kind of rate-limited response.</summary>
/// <param name="MaxRetries">Maximum rate-limited retries allowed for this kind.</param>
/// <param name="MaxWait">Longest server-requested wait accepted; <see cref="TimeSpan.Zero"/> leaves it uncapped by this rule.</param>
public readonly record struct ScaleSetRateLimitRule(int MaxRetries = 0, TimeSpan MaxWait = default)
{
    internal void Validate(string paramName)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(MaxRetries, paramName);
        ArgumentOutOfRangeException.ThrowIfNegative(MaxWait.Ticks, paramName);
    }
}

/// <summary>Decision returned by a <see cref="ScaleSetRetryOptions.OnRateLimit"/> hook.</summary>
/// <param name="ShouldRetry">Whether the client should retry the rate-limited response.</param>
/// <param name="Delay">Delay before retrying; <see cref="TimeSpan.Zero"/> falls back to <see cref="ScaleSetRetryOptions.Backoff"/>.</param>
public readonly record struct ScaleSetRateLimitDecision(bool ShouldRetry, TimeSpan Delay = default)
{
    /// <summary>Decline retrying and return the rate-limit exception immediately.</summary>
    public static ScaleSetRateLimitDecision DoNotRetry => new(false, TimeSpan.Zero);

    /// <summary>Retry using the configured backoff calculation.</summary>
    public static ScaleSetRateLimitDecision RetryWithBackoff => new(true, TimeSpan.Zero);

    /// <summary>Retry after waiting the supplied <paramref name="delay"/>.</summary>
    /// <param name="delay">Non-negative time to wait before retrying.</param>
    public static ScaleSetRateLimitDecision RetryAfter(TimeSpan delay)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(delay.Ticks, nameof(delay));
        return new(true, delay);
    }
}

/// <summary>Input supplied to a custom <see cref="ScaleSetRetryOptions.ShouldRetry"/> delegate.</summary>
/// <param name="Method">HTTP method of the request.</param>
/// <param name="SafeUri">Redacted request URI.</param>
/// <param name="Attempt">Current retry attempt number.</param>
/// <param name="Response">HTTP response snapshot, when received.</param>
/// <param name="Exception">Transport failure, when one occurred.</param>
public sealed record ScaleSetRetryContext(
    HttpMethod Method,
    string SafeUri,
    int Attempt,
    ScaleSetResponseSnapshot? Response,
    Exception? Exception);

/// <summary>Input supplied to a custom <see cref="ScaleSetRetryOptions.Backoff"/> delegate.</summary>
/// <param name="WaitMin">Configured base backoff delay.</param>
/// <param name="WaitMax">Configured maximum backoff delay.</param>
/// <param name="Attempt">Current retry attempt number.</param>
/// <param name="Response">HTTP response snapshot, when received.</param>
/// <param name="RetryAfter">The server's retry delay hint, when valid.</param>
public sealed record ScaleSetBackoffContext(
    TimeSpan WaitMin,
    TimeSpan WaitMax,
    int Attempt,
    ScaleSetResponseSnapshot? Response,
    TimeSpan? RetryAfter);

/// <summary>Input supplied to a <see cref="ScaleSetRetryOptions.OnRateLimit"/> delegate.</summary>
/// <param name="RateLimit">The server's rate-limit details.</param>
/// <param name="Attempt">Current retry attempt number.</param>
/// <param name="Method">HTTP method of the request.</param>
/// <param name="SafeUri">Redacted request URI.</param>
public sealed record ScaleSetRateLimitDecisionContext(
    ScaleSetRateLimitException RateLimit,
    int Attempt,
    string Method,
    string SafeUri);

/// <summary>Per-request retry, backoff, budget, and rate-limit policy for <see cref="ScaleSetClient"/>.</summary>
public sealed record ScaleSetRetryOptions
{
    private static readonly TimeSpan MaximumTimerDuration = TimeSpan.FromMilliseconds(uint.MaxValue - 1d);

    /// <summary>Upstream default maximum retry attempts after the initial attempt.</summary>
    public const int DefaultMaxRetries = 4;

    /// <summary>Upstream default base retry delay (1 second).</summary>
    public static readonly TimeSpan DefaultWaitMin = TimeSpan.FromSeconds(1);

    /// <summary>Upstream default maximum exponential retry delay (30 seconds).</summary>
    public static readonly TimeSpan DefaultWaitMax = TimeSpan.FromSeconds(30);

    /// <summary>Maximum number of retries after the first attempt. Zero disables retries.</summary>
    public int MaxRetries { get; init; } = DefaultMaxRetries;

    /// <summary>Base exponential backoff delay when no server <c>Retry-After</c> header applies.</summary>
    public TimeSpan WaitMin { get; init; } = DefaultWaitMin;

    /// <summary>Maximum locally computed exponential backoff delay.</summary>
    public TimeSpan WaitMax { get; init; } = DefaultWaitMax;

    /// <summary>
    /// Optional total duration budget for an HTTP request (including attempts, retry waits, and reading
    /// the final response body). <see langword="null"/> or <see cref="TimeSpan.Zero"/> leaves it unlimited.
    /// </summary>
    public TimeSpan? MaxElapsed { get; init; }

    /// <summary>Optional absolute UTC deadline limiting the request and any retry waits.</summary>
    public DateTimeOffset? Deadline { get; init; }

    /// <summary>Optional predicate overriding non-rate-limited retry eligibility.</summary>
    public Func<ScaleSetRetryContext, CancellationToken, ValueTask<bool>>? ShouldRetry { get; init; }

    /// <summary>Optional delay calculator invoked when retrying an attempt.</summary>
    public Func<ScaleSetBackoffContext, TimeSpan>? Backoff { get; init; }

    /// <summary>Optional rate-limit policy hook deciding whether and how long to wait on 429 or rate-limited 403 responses.</summary>
    public Func<ScaleSetRateLimitDecisionContext, CancellationToken, ValueTask<ScaleSetRateLimitDecision>>? OnRateLimit { get; init; }

    /// <summary>
    /// Whether non-read-only replayable control-plane requests (such as buffered POST/PATCH/DELETE calls)
    /// are eligible for default retries. Queue requests remain excluded.
    /// </summary>
    public bool RetryAllReplayableRequests { get; init; }

    /// <summary>
    /// Optional cap above which default automatic retries reject server <c>Retry-After</c> hints instead
    /// of waiting. <see langword="null"/> allows arbitrary server hints (still bounded by <see cref="MaxElapsed"/>
    /// and <see cref="Deadline"/>).
    /// </summary>
    public TimeSpan? MaxRetryAfterDelay { get; init; }

    /// <summary>A policy that disables all retries, including registration-discovery retries.</summary>
    public static ScaleSetRetryOptions Disabled { get; } = new() { MaxRetries = 0 };

    /// <summary>Creates a retry policy matching upstream <c>actions/scaleset</c> defaults.</summary>
    public static ScaleSetRetryOptions CreateUpstreamDefault() => new()
    {
        MaxRetries = DefaultMaxRetries,
        WaitMin = DefaultWaitMin,
        WaitMax = DefaultWaitMax,
        RetryAllReplayableRequests = true,
        MaxRetryAfterDelay = null,
    };

    internal void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfNegative(MaxRetries);

        if (WaitMin < TimeSpan.Zero || WaitMin > MaximumTimerDuration)
        {
            throw new ArgumentOutOfRangeException(nameof(WaitMin), "Retry base delay must be non-negative and within the timer range.");
        }

        if (WaitMax < WaitMin || WaitMax > MaximumTimerDuration)
        {
            throw new ArgumentOutOfRangeException(nameof(WaitMax), "Retry maximum delay must be at least WaitMin and within the timer range.");
        }

        if (MaxElapsed is { } maxElapsed && (maxElapsed < TimeSpan.Zero || maxElapsed > MaximumTimerDuration))
        {
            throw new ArgumentOutOfRangeException(nameof(MaxElapsed), "MaxElapsed must be non-negative and within the timer range.");
        }

        if (MaxRetryAfterDelay is { } maxRetryAfter && (maxRetryAfter < TimeSpan.Zero || maxRetryAfter > MaximumTimerDuration))
        {
            throw new ArgumentOutOfRangeException(nameof(MaxRetryAfterDelay), "MaxRetryAfterDelay must be non-negative and within the timer range.");
        }
    }
}

/// <summary>Factories for common <see cref="ScaleSetRetryOptions.OnRateLimit"/> policies.</summary>
public static class ScaleSetRateLimitPolicies
{
    /// <summary>
    /// Builds an <see cref="ScaleSetRetryOptions.OnRateLimit"/> hook applying <paramref name="primary"/> to
    /// spent primary quotas and <paramref name="secondary"/> to secondary rate limits.
    /// </summary>
    /// <param name="primary">Retry limits for spent primary quotas.</param>
    /// <param name="secondary">Retry limits for secondary and unattributed limits.</param>
    public static Func<ScaleSetRateLimitDecisionContext, CancellationToken, ValueTask<ScaleSetRateLimitDecision>> Create(
        ScaleSetRateLimitRule primary,
        ScaleSetRateLimitRule secondary)
    {
        primary.Validate(nameof(primary));
        secondary.Validate(nameof(secondary));

        return (context, _) =>
        {
            ArgumentNullException.ThrowIfNull(context);
            ScaleSetRateLimitRule rule = context.RateLimit.Kind == ScaleSetRateLimitKind.Primary
                ? primary
                : secondary;

            if (context.RateLimit.Retries >= rule.MaxRetries)
            {
                return ValueTask.FromResult(ScaleSetRateLimitDecision.DoNotRetry);
            }

            if (rule.MaxWait > TimeSpan.Zero && context.RateLimit.RetryAfter > rule.MaxWait)
            {
                return ValueTask.FromResult(ScaleSetRateLimitDecision.DoNotRetry);
            }

            return ValueTask.FromResult(new ScaleSetRateLimitDecision(true, context.RateLimit.RetryAfter));
        };
    }
}

/// <summary>
/// Scopes per-call retry policy adjustments across asynchronous execution without modifying shared client state.
/// </summary>
public static class ScaleSetRequestRetry
{
    private static readonly AsyncLocal<ImmutableArray<Func<ScaleSetRetryOptions, ScaleSetRetryOptions>>> Adjusters = new();

    /// <summary>
    /// Applies <paramref name="adjust"/> to the current async call's retry options until the returned handle is disposed.
    /// Nested scopes apply outer-first.
    /// </summary>
    /// <param name="adjust">Function that derives retry options from the current options.</param>
    public static IDisposable Configure(Func<ScaleSetRetryOptions, ScaleSetRetryOptions> adjust)
    {
        ArgumentNullException.ThrowIfNull(adjust);
        ImmutableArray<Func<ScaleSetRetryOptions, ScaleSetRetryOptions>> prior = Adjusters.Value;
        Adjusters.Value = prior.IsDefault
            ? [adjust]
            : prior.Add(adjust);
        return new Scope(prior);
    }

    internal static ImmutableArray<Func<ScaleSetRetryOptions, ScaleSetRetryOptions>> GetCurrentAdjusters()
    {
        ImmutableArray<Func<ScaleSetRetryOptions, ScaleSetRetryOptions>> current = Adjusters.Value;
        return current.IsDefault ? [] : current;
    }

    private sealed class Scope(ImmutableArray<Func<ScaleSetRetryOptions, ScaleSetRetryOptions>> prior) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                Adjusters.Value = prior;
            }
        }
    }
}
