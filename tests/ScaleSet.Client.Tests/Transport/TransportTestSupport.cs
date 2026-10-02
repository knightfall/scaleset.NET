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
