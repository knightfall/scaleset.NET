using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Text;

namespace ScaleSet.Client.Tests.Transport;

internal sealed class RecordingHttpMessageHandler(
    Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responseFactory) : HttpMessageHandler
{
    private readonly ConcurrentQueue<RecordedRequest> _requests = new();

    internal IReadOnlyList<RecordedRequest> Requests => _requests.ToArray();

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var body = request.Content is null
            ? Array.Empty<byte>()
            : await request.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        _requests.Enqueue(new RecordedRequest(
            request.Method.Method,
            request.RequestUri!,
            request.Headers.Authorization?.ToString(),
            request.Content?.Headers.ContentType?.ToString(),
            request.Headers.UserAgent.ToString(),
            body));
        return await responseFactory(request, cancellationToken).ConfigureAwait(false);
    }
}

internal sealed record RecordedRequest(
    string Method,
    Uri Uri,
    string? Authorization,
    string? ContentType,
    string UserAgent,
    byte[] Body);

internal static class TestResponses
{
    internal static HttpResponseMessage Json(HttpStatusCode status, string json)
    {
        return new HttpResponseMessage(status)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
    }

    internal static string CreateAdminToken(DateTimeOffset expiresAt)
    {
        var header = Base64Url("{\"alg\":\"RS256\",\"typ\":\"JWT\"}");
        var payload = Base64Url($"{{\"exp\":{expiresAt.ToUnixTimeSeconds()}}}");
        return $"{header}.{payload}.test-signature";
    }

    public static RecordingHttpMessageHandler CreateConnectedHandler(
        DateTimeOffset adminExpiry,
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> actionsHandler) =>
        new(async (request, cancellationToken) =>
        {
            string path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/runners/registration-token", StringComparison.Ordinal))
            {
                return Json(HttpStatusCode.Created, "{\"token\":\"registration-token\"}");
            }

            if (path == "/actions/runner-registration")
            {
                string token = CreateAdminToken(adminExpiry);
                return Json(HttpStatusCode.OK, $"{{\"url\":\"https://actions-fixture.actions.githubusercontent.com/tenant\",\"token\":\"{token}\"}}");
            }

            return await actionsHandler(request, cancellationToken).ConfigureAwait(false);
        });

    private static string Base64Url(string value) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(value))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
}

internal sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
{
    private DateTimeOffset _now = now;

    public override DateTimeOffset GetUtcNow() => _now;

    public override long GetTimestamp() => Stopwatch.GetTimestamp();

    public override long TimestampFrequency => Stopwatch.Frequency;

    public override ITimer CreateTimer(
        TimerCallback callback,
        object? state,
        TimeSpan dueTime,
        TimeSpan period) => new Timer(callback, state, dueTime, period);

    internal void Advance(TimeSpan duration) => _now += duration;
}

internal sealed class VirtualTimeProvider(DateTimeOffset startUtc) : TimeProvider
{
    private readonly object _sync = new();
    private readonly List<VirtualTimer> _timers = [];
    private readonly List<TaskCompletionSource> _timerScheduledWaiters = [];
    private DateTimeOffset _now = startUtc;
    private long _timestamp = 1_000_000;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override DateTimeOffset GetUtcNow()
    {
        lock (_sync)
        {
            return _now;
        }
    }

    public override long GetTimestamp()
    {
        lock (_sync)
        {
            return _timestamp;
        }
    }

    public override ITimer CreateTimer(
        TimerCallback callback,
        object? state,
        TimeSpan dueTime,
        TimeSpan period)
    {
        ArgumentNullException.ThrowIfNull(callback);
        VirtualTimer timer = new(this, callback, state);
        timer.Change(dueTime, period);
        return timer;
    }

    internal async Task<TimeSpan> WaitForActiveTimerAsync(
        Func<TimeSpan, bool> predicate,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            Task waitTask;
            lock (_sync)
            {
                foreach (VirtualTimer timer in _timers)
                {
                    if (!timer.IsDisposed && timer.RequestedDueTime is { } dueTime && predicate(dueTime))
                    {
                        return dueTime;
                    }
                }

                TaskCompletionSource waiter = new(TaskCreationOptions.RunContinuationsAsynchronously);
                _timerScheduledWaiters.Add(waiter);
                waitTask = waiter.Task;
            }

            await waitTask.WaitAsync(cancellationToken);
        }
    }

    internal void Advance(TimeSpan duration)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(duration.Ticks, nameof(duration));
        List<(TimerCallback Callback, object? State)> dueCallbacks = [];

        lock (_sync)
        {
            _now += duration;
            _timestamp += duration.Ticks;

            for (int index = _timers.Count - 1; index >= 0; index--)
            {
                VirtualTimer timer = _timers[index];
                if (timer.IsDisposed || timer.DueTimestamp is not { } dueTimestamp)
                {
                    _timers.RemoveAt(index);
                    continue;
                }

                if (dueTimestamp <= _timestamp)
                {
                    dueCallbacks.Add((timer.Callback, timer.State));
                    if (timer.Period > TimeSpan.Zero && timer.Period != Timeout.InfiniteTimeSpan)
                    {
                        timer.DueTimestamp = _timestamp + timer.Period.Ticks;
                    }
                    else
                    {
                        timer.DueTimestamp = null;
                        _timers.RemoveAt(index);
                    }
                }
            }
        }

        foreach ((TimerCallback callback, object? state) in dueCallbacks)
        {
            callback(state);
        }
    }

    private void ScheduleTimer(VirtualTimer timer, TimeSpan dueTime, TimeSpan period)
    {
        bool fireImmediately = false;
        TaskCompletionSource[] waiters;

        lock (_sync)
        {
            if (timer.IsDisposed)
            {
                return;
            }

            timer.Period = period;
            if (dueTime == Timeout.InfiniteTimeSpan)
            {
                timer.RequestedDueTime = null;
                timer.DueTimestamp = null;
                _timers.Remove(timer);
            }
            else if (dueTime <= TimeSpan.Zero)
            {
                timer.RequestedDueTime = TimeSpan.Zero;
                timer.DueTimestamp = null;
                _timers.Remove(timer);
                fireImmediately = true;
            }
            else
            {
                timer.RequestedDueTime = dueTime;
                timer.DueTimestamp = _timestamp + dueTime.Ticks;
                if (!_timers.Contains(timer))
                {
                    _timers.Add(timer);
                }
            }

            waiters = _timerScheduledWaiters.ToArray();
            _timerScheduledWaiters.Clear();
        }

        foreach (TaskCompletionSource waiter in waiters)
        {
            waiter.TrySetResult();
        }

        if (fireImmediately)
        {
            _ = Task.Run(() => timer.Callback(timer.State));
        }
    }

    private void RemoveTimer(VirtualTimer timer)
    {
        lock (_sync)
        {
            _timers.Remove(timer);
        }
    }

    private sealed class VirtualTimer(
        VirtualTimeProvider owner,
        TimerCallback callback,
        object? state) : ITimer
    {
        public TimerCallback Callback { get; } = callback;

        public object? State { get; } = state;

        public TimeSpan? RequestedDueTime { get; set; }

        public long? DueTimestamp { get; set; }

        public TimeSpan Period { get; set; } = Timeout.InfiniteTimeSpan;

        public bool IsDisposed { get; private set; }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            if (IsDisposed)
            {
                return false;
            }

            owner.ScheduleTimer(this, dueTime, period);
            return true;
        }

        public void Dispose()
        {
            IsDisposed = true;
            owner.RemoveTimer(this);
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}

internal sealed class PartialReadStream(byte[] prefix, TaskCompletionSource? blockAfterPrefix = null) : Stream
{
    private int _step;

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (_step++ == 0)
        {
            prefix.CopyTo(buffer);
            return prefix.Length;
        }

        if (blockAfterPrefix is not null)
        {
            blockAfterPrefix.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }

        throw new IOException("stream interrupted mid-body");
    }

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override void Flush() => throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
