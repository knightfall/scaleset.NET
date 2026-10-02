using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace ScaleSet.Hosting.Tests.Telemetry;

internal sealed class OtlpHttpTestReceiver : IAsyncDisposable
{
    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<OtlpHttpRequest>> _firstRequestByPath = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<OtlpHttpRequest> _requests = new();
    private readonly SemaphoreSlim _requestArrived = new(0);
    private readonly Task _serveTask;

    public OtlpHttpTestReceiver()
    {
        var socket = new TcpListener(IPAddress.Loopback, 0);
        socket.Start();
        var port = ((IPEndPoint)socket.LocalEndpoint).Port;
        socket.Stop();

        BaseEndpoint = new Uri($"http://127.0.0.1:{port}/otlp/", UriKind.Absolute);
        _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        _listener.Start();
        _serveTask = ServeAsync();
    }

    public Uri BaseEndpoint { get; }

    public IReadOnlyCollection<OtlpHttpRequest> Requests => _requests.ToArray();

    public async Task<OtlpHttpRequest> WaitForRequestAsync(string path, TimeSpan timeout)
    {
        var completion = _firstRequestByPath.GetOrAdd(
            path,
            static _ => new TaskCompletionSource<OtlpHttpRequest>(TaskCreationOptions.RunContinuationsAsynchronously));
        return await completion.Task.WaitAsync(timeout).ConfigureAwait(false);
    }

    public async Task<OtlpHttpRequest> WaitForMatchingRequestAsync(
        string path,
        Func<OtlpHttpRequest, bool> predicate,
        TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            var match = _requests.FirstOrDefault(request =>
                request.Path.Equals(path, StringComparison.Ordinal) && predicate(request));
            if (match is not null)
            {
                return match;
            }

            var remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                throw new TimeoutException($"No OTLP request matching {path} arrived before the test timeout.");
            }

            await _requestArrived.WaitAsync(remaining).ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _shutdown.Cancel();
        _listener.Close();
        try
        {
            await _serveTask.ConfigureAwait(false);
        }
        catch (HttpListenerException)
        {
            // Closing the listener interrupts an outstanding accept.
        }
        catch (OperationCanceledException)
        {
            // Disposal ended the accept loop.
        }

        _shutdown.Dispose();
        _requestArrived.Dispose();
    }

    private async Task ServeAsync()
    {
        while (!_shutdown.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync().WaitAsync(_shutdown.Token).ConfigureAwait(false);
            }
            catch (HttpListenerException) when (_shutdown.IsCancellationRequested)
            {
                return;
            }
            catch (ObjectDisposedException) when (_shutdown.IsCancellationRequested)
            {
                return;
            }

            await HandleAsync(context).ConfigureAwait(false);
        }
    }

    private async Task HandleAsync(HttpListenerContext context)
    {
        await using var body = new MemoryStream();
        await context.Request.InputStream.CopyToAsync(body, _shutdown.Token).ConfigureAwait(false);
        var path = context.Request.Url?.AbsolutePath ?? string.Empty;
        var request = new OtlpHttpRequest(
            path,
            body.ToArray(),
            context.Request.Headers["Authorization"],
            context.Request.ContentType);
        _requests.Enqueue(request);
        _requestArrived.Release();
        _firstRequestByPath.GetOrAdd(
            path,
            static _ => new TaskCompletionSource<OtlpHttpRequest>(TaskCreationOptions.RunContinuationsAsynchronously))
            .TrySetResult(request);

        context.Response.StatusCode = (int)HttpStatusCode.OK;
        context.Response.ContentType = "application/x-protobuf";
        context.Response.ContentLength64 = 0;
        context.Response.Close();
    }
}

internal sealed record OtlpHttpRequest(
    string Path,
    byte[] Body,
    string? AuthorizationHeader,
    string? ContentType);
