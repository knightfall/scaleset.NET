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
                return Json(HttpStatusCode.OK, $"{{\"url\":\"https://actions.example/tenant\",\"token\":\"{token}\"}}");
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
