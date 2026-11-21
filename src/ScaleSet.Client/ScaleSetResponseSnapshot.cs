using System.Collections.ObjectModel;
using System.Net.Http.Headers;

namespace ScaleSet.Client;

/// <summary>
/// Read-only in-memory snapshot of the HTTP response attached to a <see cref="ScaleSetApiException"/>.
/// </summary>
/// <remarks>
/// Treat <see cref="Body"/> as sensitive: raw server responses can contain tokens or runner JIT configuration.
/// </remarks>
public sealed class ScaleSetResponseSnapshot
{
    internal ScaleSetResponseSnapshot(
        int statusCode,
        string? reasonPhrase,
        IReadOnlyDictionary<string, IReadOnlyList<string>> headers,
        IReadOnlyDictionary<string, IReadOnlyList<string>> trailingHeaders,
        ReadOnlyMemory<byte> body,
        bool isBodyIncomplete,
        bool isBodyTruncated)
    {
        StatusCode = statusCode;
        ReasonPhrase = reasonPhrase;
        Headers = headers;
        TrailingHeaders = trailingHeaders;
        Body = body;
        IsBodyIncomplete = isBodyIncomplete;
        IsBodyTruncated = isBodyTruncated;
    }

    /// <summary>HTTP response status code.</summary>
    public int StatusCode { get; }

    /// <summary>HTTP reason phrase returned with the response, when present.</summary>
    public string? ReasonPhrase { get; }

    /// <summary>Response and content headers copied from the HTTP response (case-insensitive keys).</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> Headers { get; }

    /// <summary>Trailing HTTP headers copied from the response (case-insensitive keys).</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> TrailingHeaders { get; }

    /// <summary>
    /// Raw bytes received from the server (including any UTF-8 BOM, or only the received prefix when
    /// <see cref="IsBodyIncomplete"/> is <see langword="true"/>).
    /// </summary>
    /// <remarks>
    /// Treat this buffer as sensitive; it is omitted from <see cref="ToString"/> and exception messages.
    /// </remarks>
    public ReadOnlyMemory<byte> Body { get; }

    /// <summary>Whether reading the response stream failed before reaching end-of-stream.</summary>
    public bool IsBodyIncomplete { get; }

    /// <summary>Whether an error response body exceeded the bounded capture limit and was truncated.</summary>
    public bool IsBodyTruncated { get; }

    /// <summary>Gets the first header value matching <paramref name="name"/> case-insensitively, or <see langword="null"/>.</summary>
    public string? GetHeaderValue(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return Headers.TryGetValue(name, out IReadOnlyList<string>? values) && values.Count > 0
            ? values[0]
            : null;
    }

    /// <summary>Returns a diagnostic summary without revealing header values or body contents.</summary>
    public override string ToString() =>
        $"ScaleSetResponseSnapshot {{ StatusCode = {StatusCode}, HeaderCount = {Headers.Count}, " +
        $"BodyLength = {Body.Length}, IsBodyIncomplete = {IsBodyIncomplete}, IsBodyTruncated = {IsBodyTruncated} }}";

    internal static ScaleSetResponseSnapshot FromHttpResponse(
        HttpResponseMessage response,
        ReadOnlyMemory<byte> rawBody,
        bool isBodyIncomplete,
        bool isBodyTruncated)
    {
        ArgumentNullException.ThrowIfNull(response);

        IReadOnlyDictionary<string, IReadOnlyList<string>> headers = CopyHeaders(
            response.Headers,
            response.Content?.Headers);
        IReadOnlyDictionary<string, IReadOnlyList<string>> trailingHeaders = CopyHeaders(
            response.TrailingHeaders,
            contentHeaders: null);

        return new ScaleSetResponseSnapshot(
            (int)response.StatusCode,
            response.ReasonPhrase,
            headers,
            trailingHeaders,
            rawBody,
            isBodyIncomplete,
            isBodyTruncated);
    }

    private static ReadOnlyDictionary<string, IReadOnlyList<string>> CopyHeaders(
        HttpHeaders primaryHeaders,
        HttpHeaders? contentHeaders)
    {
        Dictionary<string, List<string>> collected = new(StringComparer.OrdinalIgnoreCase);
        AppendHeaders(collected, primaryHeaders);
        if (contentHeaders is not null)
        {
            AppendHeaders(collected, contentHeaders);
        }

        Dictionary<string, IReadOnlyList<string>> frozen = new(collected.Count, StringComparer.OrdinalIgnoreCase);
        foreach (KeyValuePair<string, List<string>> entry in collected)
        {
            frozen[entry.Key] = Array.AsReadOnly(entry.Value.ToArray());
        }

        return new ReadOnlyDictionary<string, IReadOnlyList<string>>(frozen);
    }

    private static void AppendHeaders(Dictionary<string, List<string>> destination, HttpHeaders source)
    {
        foreach (KeyValuePair<string, IEnumerable<string>> header in source)
        {
            if (!destination.TryGetValue(header.Key, out List<string>? list))
            {
                list = [];
                destination[header.Key] = list;
            }

            list.AddRange(header.Value);
        }
    }
}
