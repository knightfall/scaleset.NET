using System.Collections.Concurrent;
using System.Net;
using System.Text;
using ScaleSet.Hosting.Configuration;
using ScaleSet.Hosting.Providers;
using ScaleSet.Providers.Docker;

namespace ScaleSet.Docker.Tests;

internal sealed class DockerTestHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<RecordedDockerRequest, CancellationToken, Task<HttpResponseMessage>> _responder;
    private readonly ConcurrentQueue<RecordedDockerRequest> _requests = new();
    private readonly bool _interceptHandshakeResponses;

    public DockerTestHttpMessageHandler(
        Func<RecordedDockerRequest, CancellationToken, Task<HttpResponseMessage>>? responder = null,
        string apiVersion = "1.56",
        string minimumApiVersion = "1.40",
        string daemonOs = "linux",
        string infoOs = "linux",
        string architecture = "arm64",
        bool interceptHandshakeResponses = true)
    {
        ApiVersion = apiVersion;
        MinimumApiVersion = minimumApiVersion;
        DaemonOs = daemonOs;
        InfoOs = infoOs;
        Architecture = architecture;
        _interceptHandshakeResponses = interceptHandshakeResponses;
        _responder = responder ?? ((_, _) => Task.FromResult(Json(HttpStatusCode.NotFound, "{}")));
    }

    public string ApiVersion { get; }

    public string MinimumApiVersion { get; }

    public string DaemonOs { get; }

    public string InfoOs { get; }

    public string Architecture { get; }

    public IReadOnlyList<RecordedDockerRequest> Requests => _requests.ToArray();

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        string requestBody = request.Content is null
            ? string.Empty
            : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        Uri requestUri = request.RequestUri ?? throw new InvalidOperationException("The test request URI was not set.");
        RecordedDockerRequest recorded = new(request.Method.Method, requestUri, requestBody);
        _requests.Enqueue(recorded);

        if (_interceptHandshakeResponses && requestUri.AbsolutePath == "/version")
        {
            string json = $"{{\"ApiVersion\":\"{ApiVersion}\",\"MinAPIVersion\":\"{MinimumApiVersion}\",\"Os\":\"{DaemonOs}\"}}";
            return Json(HttpStatusCode.OK, json);
        }

        if (_interceptHandshakeResponses && requestUri.AbsolutePath.EndsWith("/info", StringComparison.Ordinal))
        {
            string json = $"{{\"OSType\":\"{InfoOs}\",\"Architecture\":\"{Architecture}\"}}";
            return Json(HttpStatusCode.OK, json);
        }

        return await _responder(recorded, cancellationToken).ConfigureAwait(false);
    }

    public static HttpResponseMessage Json(HttpStatusCode statusCode, string json) => new(statusCode)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };
}

internal sealed record RecordedDockerRequest(string Method, Uri Uri, string Body);

internal sealed class DockerProviderTestContext : IDisposable
{
    private static readonly string[] ContainerNames = ["/runner"];

    public DockerProviderTestContext(
        DockerTestHttpMessageHandler handler,
        RunnerProfileOptions? profile = null,
        DockerProviderOptions? options = null,
        TimeProvider? timeProvider = null)
    {
        Handler = handler;
        HttpClient = new HttpClient(handler, disposeHandler: false);
        Provider = new DockerRunnerProvider(
            profile ?? CreateProfile(),
            options ?? new DockerProviderOptions(),
            HttpClient,
            timeProvider: timeProvider);
    }

    public DockerTestHttpMessageHandler Handler { get; }

    public HttpClient HttpClient { get; }

    public DockerRunnerProvider Provider { get; }

    public void Dispose()
    {
        Provider.Dispose();
        HttpClient.Dispose();
        Handler.Dispose();
    }

    public static RunnerProfileOptions CreateProfile() => new()
    {
        Image = "ghcr.io/example/runner:2.324.0",
        Architecture = RunnerArchitecture.Arm64,
        Cpu = 1.5m,
        Memory = 256,
        WorkFolder = "_work",
    };

    public static RunnerOwnershipScope CreateScope() => new("controller-a", "production", 42);

    public static RunnerIdentity CreateIdentity(long? gitHubRunnerId = null) =>
        new(CreateScope(), Guid.Parse("44444444-4444-4444-4444-444444444444"), "runner-17", gitHubRunnerId);

    public static RunnerResource CreateResource(
        RunnerResourceStatus status = RunnerResourceStatus.Created,
        long? gitHubRunnerId = null) =>
        new(new string('a', 64), CreateIdentity(gitHubRunnerId), status);

    public static Dictionary<string, string> CreateLabels(RunnerIdentity? identity = null, long? labelRunnerId = null)
    {
        identity ??= CreateIdentity();
        Dictionary<string, string> labels = new(StringComparer.Ordinal)
        {
            ["io.scaleset.jit-bootstrap"] = "2",
            [DockerProviderLabels.Product] = "scaleset.net",
            [DockerProviderLabels.ControllerId] = identity.Scope.ControllerId,
            [DockerProviderLabels.ScaleSetKey] = identity.Scope.ScaleSetKey,
            [DockerProviderLabels.ScaleSetId] = identity.Scope.ScaleSetId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            [DockerProviderLabels.OperationId] = identity.OperationId.ToString("D"),
            [DockerProviderLabels.RunnerName] = identity.RunnerName,
        };
        long? runnerId = labelRunnerId ?? identity.GitHubRunnerId;
        if (runnerId is long value)
        {
            labels[DockerProviderLabels.GitHubRunnerId] = value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        return labels;
    }

    public static string CreateInspectJson(
        RunnerIdentity? identity = null,
        string state = "created",
        int exitCode = 0,
        string startedAt = "0001-01-01T00:00:00Z",
        string finishedAt = "0001-01-01T00:00:00Z",
        string? id = null,
        long? labelRunnerId = null,
        string? rawEnvironmentValue = null,
        long nanoCpus = 0,
        long memoryBytes = 0)
    {
        Dictionary<string, string> labels = CreateLabels(identity, labelRunnerId);
        return System.Text.Json.JsonSerializer.Serialize(new
        {
            Id = id ?? new string('a', 64),
            Config = new
            {
                Env = rawEnvironmentValue is null ? Array.Empty<string>() : new[] { rawEnvironmentValue },
                Labels = labels,
            },
            HostConfig = new
            {
                NanoCpus = nanoCpus,
                Memory = memoryBytes,
            },
            State = new
            {
                Status = state,
                ExitCode = exitCode,
                StartedAt = startedAt,
                FinishedAt = finishedAt,
            },
        });
    }

    public static string CreateListJson(params (string Id, Dictionary<string, string> Labels)[] containers) =>
        System.Text.Json.JsonSerializer.Serialize(containers.Select(static container => new
        {
            Id = container.Id,
            Names = ContainerNames,
            Labels = container.Labels,
            State = "exited",
        }));
}

internal sealed class TrackingStringContent(string content) : StringContent(content, Encoding.UTF8, "application/json")
{
    public bool IsDisposed { get; private set; }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            IsDisposed = true;
        }

        base.Dispose(disposing);
    }
}

internal sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
{
    private readonly object _gate = new();
    private readonly List<ManualTimer> _timers = [];
    private DateTimeOffset _now = now;
    private long _timestamp;

    public override DateTimeOffset GetUtcNow()
    {
        lock (_gate)
        {
            return _now;
        }
    }

    public override long GetTimestamp()
    {
        lock (_gate)
        {
            return _timestamp;
        }
    }

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        ManualTimer timer = new(this, callback, state);
        lock (_gate)
        {
            _timers.Add(timer);
            ChangeTimer(timer, dueTime, period);
        }

        return timer;
    }

    public void Advance(TimeSpan amount)
    {
        List<(TimerCallback Callback, object? State)> callbacks = [];
        lock (_gate)
        {
            _now += amount;
            _timestamp = checked(_timestamp + amount.Ticks);
            foreach (ManualTimer timer in _timers)
            {
                if (timer.IsDisposed || timer.DueTimestamp is not long dueTimestamp || dueTimestamp > _timestamp)
                {
                    continue;
                }

                callbacks.Add((timer.Callback, timer.State));
                timer.DueTimestamp = timer.PeriodTicks is long periodTicks && periodTicks > 0
                    ? checked(_timestamp + periodTicks)
                    : null;
            }
        }

        foreach ((TimerCallback callback, object? state) in callbacks)
        {
            callback(state);
        }
    }

    private bool ChangeTimer(ManualTimer timer, TimeSpan dueTime, TimeSpan period)
    {
        if (timer.IsDisposed)
        {
            return false;
        }

        timer.DueTimestamp = dueTime == Timeout.InfiniteTimeSpan
            ? null
            : checked(_timestamp + dueTime.Ticks);
        timer.PeriodTicks = period == Timeout.InfiniteTimeSpan ? null : period.Ticks;
        return true;
    }

    private void DisposeTimer(ManualTimer timer)
    {
        lock (_gate)
        {
            timer.IsDisposed = true;
            timer.DueTimestamp = null;
        }
    }

    private sealed class ManualTimer(
        ManualTimeProvider owner,
        TimerCallback callback,
        object? state) : ITimer
    {
        public TimerCallback Callback { get; } = callback;

        public object? State { get; } = state;

        public long? DueTimestamp { get; set; }

        public long? PeriodTicks { get; set; }

        public bool IsDisposed { get; set; }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (owner._gate)
            {
                return owner.ChangeTimer(this, dueTime, period);
            }
        }

        public void Dispose() => owner.DisposeTimer(this);

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
