using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ScaleSet.Client;

/// <summary>Names consumers can use to subscribe to this library's traces and metrics.</summary>
public static class ScaleSetClientTelemetry
{
    /// <summary>Activity source name for client HTTP requests.</summary>
    public const string ActivitySourceName = "ScaleSet.Client";

    /// <summary>Meter name for client HTTP requests.</summary>
    public const string MeterName = "ScaleSet.Client";
}

internal static partial class ScaleSetClientDiagnostics
{
    internal const int MaximumErrorBodyBytes = 65536;
    private const int MaximumPreviewCharacters = 1024;
    private const int MaximumCorrelationCharacters = 128;

    internal static readonly ActivitySource ActivitySource = new(ScaleSetClientTelemetry.ActivitySourceName);
    internal static readonly Meter Meter = new(ScaleSetClientTelemetry.MeterName);
    private static readonly Counter<long> Requests = Meter.CreateCounter<long>(
        "scaleset.client.requests",
        unit: "{request}",
        description: "HTTP requests sent by ScaleSet.Client.");
    private static readonly Counter<long> Retries = Meter.CreateCounter<long>(
        "scaleset.client.retries",
        unit: "{retry}",
        description: "Bounded HTTP retries performed by ScaleSet.Client.");
    private static readonly Histogram<double> Duration = Meter.CreateHistogram<double>(
        "scaleset.client.request.duration",
        unit: "s",
        description: "Duration of ScaleSet.Client HTTP attempts.");

    internal static Activity? StartActivity(HttpMethod method, Uri uri, string dependency)
    {
        var activity = ActivitySource.StartActivity("scaleset.client.request", ActivityKind.Internal);
        activity?.SetTag("http.request.method", NormalizeMethod(method.Method));
        activity?.SetTag("server.address", uri.Host);
        activity?.SetTag("url.scheme", uri.Scheme);
        activity?.SetTag("scaleset.dependency", NormalizeDependency(dependency));
        return activity;
    }

    internal static void RecordAttempt(
        string dependency,
        string method,
        int? statusCode,
        double durationSeconds)
    {
        var statusClass = statusCode switch
        {
            null => "none",
            < 300 => "2xx",
            < 400 => "3xx",
            < 500 => "4xx",
            _ => "5xx",
        };
        var tags = new TagList
        {
            { "dependency", NormalizeDependency(dependency) },
            { "method", NormalizeMethod(method) },
            { "status_class", statusClass },
        };
        Requests.Add(1, tags);
        Duration.Record(durationSeconds, tags);
    }

    internal static void RecordRetry(string dependency, string method, string retryKind)
    {
        var tags = new TagList
        {
            { "dependency", NormalizeDependency(dependency) },
            { "method", NormalizeMethod(method) },
            { "retry_kind", retryKind == "discovery-auth" ? "discovery-auth" : "transient" },
        };
        Retries.Add(1, tags);
    }

    private static string NormalizeDependency(string dependency) => dependency switch
    {
        "github" => "github",
        "actions" => "actions",
        "queue" => "queue",
        _ => "other",
    };

    private static string NormalizeMethod(string method) => method.ToUpperInvariant() switch
    {
        "GET" => "GET",
        "HEAD" => "HEAD",
        "POST" => "POST",
        "PUT" => "PUT",
        "PATCH" => "PATCH",
        "DELETE" => "DELETE",
        _ => "OTHER",
    };

    internal static async ValueTask<ResponseBodyReadResult> ReadResponseBytesAsync(
        HttpResponseMessage response,
        int maxBytes,
        CancellationToken callerToken,
        CancellationToken attemptLinkedToken)
    {
        if (response.Content is null)
        {
            return new ResponseBodyReadResult([], false, false, ResponseBodyReadFailure.None);
        }

        await using MemoryStream memory = new();
        byte[] buffer = new byte[8192];
        bool truncated = false;
        int totalRead = 0;

        try
        {
            await using Stream stream = await response.Content.ReadAsStreamAsync(attemptLinkedToken).ConfigureAwait(false);
            while (true)
            {
                int remainingWithProbe = maxBytes == int.MaxValue
                    ? buffer.Length
                    : Math.Min(buffer.Length, (maxBytes + 1) - totalRead);
                if (remainingWithProbe <= 0)
                {
                    truncated = true;
                    break;
                }

                int read = await stream.ReadAsync(buffer.AsMemory(0, remainingWithProbe), attemptLinkedToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                if (maxBytes != int.MaxValue && totalRead + read > maxBytes)
                {
                    int writable = maxBytes - totalRead;
                    if (writable > 0)
                    {
                        memory.Write(buffer, 0, writable);
                    }

                    truncated = true;
                    break;
                }

                memory.Write(buffer, 0, read);
                totalRead += read;
            }

            return new ResponseBodyReadResult(memory.ToArray(), truncated, IsIncomplete: false, ResponseBodyReadFailure.None);
        }
        catch (OperationCanceledException) when (callerToken.IsCancellationRequested)
        {
            throw new OperationCanceledException("The ScaleSet response read was canceled by the caller.", callerToken);
        }
        catch (OperationCanceledException)
        {
            return new ResponseBodyReadResult(memory.ToArray(), Truncated: false, IsIncomplete: true, ResponseBodyReadFailure.AttemptTimedOut);
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException)
        {
            return new ResponseBodyReadResult(memory.ToArray(), Truncated: false, IsIncomplete: true, ResponseBodyReadFailure.ReadError);
        }
    }

    internal static ReadOnlySpan<byte> TrimUtf8Bom(ReadOnlySpan<byte> bytes) =>
        bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF
            ? bytes[3..]
            : bytes;

    internal static string? CreateBodyPreview(
        ReadOnlySpan<byte> body,
        bool truncated,
        IReadOnlyCollection<string>? knownSecrets = null)
    {
        if (body.IsEmpty && !truncated)
        {
            return null;
        }

        var text = Encoding.UTF8.GetString(body);
        if (text.Length > 0 && text[0] == '\uFEFF')
        {
            text = text[1..];
        }

        string sanitized;
        try
        {
            using var document = JsonDocument.Parse(text);
            using var output = new MemoryStream();
            using (var writer = new Utf8JsonWriter(output))
            {
                WriteRedactedJson(document.RootElement, writer);
            }

            sanitized = Encoding.UTF8.GetString(output.ToArray());
        }
        catch (JsonException)
        {
            sanitized = RedactFreeText(text, knownSecrets);
        }

        sanitized = RedactFreeText(sanitized, knownSecrets);
        if (truncated)
        {
            sanitized += " …[truncated]";
        }

        if (sanitized.Length > MaximumPreviewCharacters)
        {
            sanitized = sanitized[..(MaximumPreviewCharacters - 1)] + "…";
        }

        return sanitized;
    }

    internal static ScaleSetDomainError? ParseDomainError(ReadOnlySpan<byte> body)
    {
        if (body.IsEmpty)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(body.ToArray());
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("typeName", out var typeName) ||
                typeName.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            var exceptionName = typeName.GetString();
            if (exceptionName?.Contains("AgentExistsException", StringComparison.Ordinal) == true)
            {
                return ScaleSetDomainError.AgentExists;
            }

            if (exceptionName?.Contains("AgentNotFoundException", StringComparison.Ordinal) == true)
            {
                return ScaleSetDomainError.AgentNotFound;
            }

            if (exceptionName?.Contains("JobStillRunningException", StringComparison.Ordinal) == true)
            {
                return ScaleSetDomainError.JobStillRunning;
            }
        }
        catch (JsonException)
        {
            return null;
        }

        return null;
    }

    internal static string CorrelationHeader(
        HttpResponseMessage response,
        string name,
        IReadOnlyCollection<string>? knownSecrets = null)
    {
        if (!response.Headers.TryGetValues(name, out var values))
        {
            return string.Empty;
        }

        var value = values.FirstOrDefault() ?? string.Empty;
        var sanitized = RedactFreeText(
            string.Concat(value.Where(character => !char.IsControl(character))),
            knownSecrets);
        return sanitized[..Math.Min(MaximumCorrelationCharacters, sanitized.Length)];
    }

    internal static Uri CreateSafeUri(
        Uri uri,
        IReadOnlyCollection<string>? knownSecrets = null,
        bool redactPath = false)
    {
        var safe = new UriBuilder(uri)
        {
            UserName = string.Empty,
            Password = string.Empty,
            Query = string.Empty,
            Fragment = string.Empty,
        };
        if (redactPath)
        {
            safe.Path = "/";
        }

        if (redactPath || knownSecrets is null || knownSecrets.Count == 0)
        {
            return safe.Uri;
        }

        var safeAbsoluteUri = safe.Uri.AbsoluteUri;
        foreach (var secret in knownSecrets)
        {
            if (secret.Length == 0)
            {
                continue;
            }

            safeAbsoluteUri = safeAbsoluteUri.Replace(secret, "[REDACTED]", StringComparison.Ordinal);
            var escapedSecret = Uri.EscapeDataString(secret);
            if (!string.Equals(escapedSecret, secret, StringComparison.Ordinal))
            {
                safeAbsoluteUri = safeAbsoluteUri.Replace(escapedSecret, "[REDACTED]", StringComparison.Ordinal);
            }
        }

        return new Uri(safeAbsoluteUri, UriKind.Absolute);
    }

    private static void WriteRedactedJson(JsonElement value, Utf8JsonWriter writer)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in value.EnumerateObject())
                {
                    writer.WritePropertyName(property.Name);
                    if (IsSensitiveProperty(property.Name))
                    {
                        writer.WriteStringValue("[REDACTED]");
                    }
                    else
                    {
                        WriteRedactedJson(property.Value, writer);
                    }
                }

                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in value.EnumerateArray())
                {
                    WriteRedactedJson(item, writer);
                }

                writer.WriteEndArray();
                break;
            default:
                value.WriteTo(writer);
                break;
        }
    }

    private static bool IsSensitiveProperty(string propertyName)
    {
        var normalized = string.Concat(propertyName.Where(char.IsLetterOrDigit)).ToLowerInvariant();
        return normalized.Contains("token", StringComparison.Ordinal) ||
            normalized.Contains("secret", StringComparison.Ordinal) ||
            normalized.Contains("password", StringComparison.Ordinal) ||
            normalized.Contains("authorization", StringComparison.Ordinal) ||
            normalized.Contains("privatekey", StringComparison.Ordinal) ||
            normalized.Contains("encodedjitconfig", StringComparison.Ordinal) ||
            normalized.Contains("url", StringComparison.Ordinal) ||
            normalized.Contains("uri", StringComparison.Ordinal);
    }

    private static string RedactFreeText(string text, IReadOnlyCollection<string>? knownSecrets = null)
    {
        if (knownSecrets is not null)
        {
            foreach (var secret in knownSecrets)
            {
                if (secret.Length == 0)
                {
                    continue;
                }

                text = text.Replace(secret, "[REDACTED]", StringComparison.Ordinal);
                var escaped = Uri.EscapeDataString(secret);
                if (!string.Equals(escaped, secret, StringComparison.Ordinal))
                {
                    text = text.Replace(escaped, "[REDACTED]", StringComparison.Ordinal);
                }
            }
        }

        text = PemPrivateKeyRegex().Replace(text, "[REDACTED-PRIVATE-KEY]");
        text = HttpUrlRegex().Replace(text, "[REDACTED-URL]");
        text = BearerCredentialRegex().Replace(text, "$1 [REDACTED]");
        text = CompactJwtRegex().Replace(text, "[REDACTED-JWT]");
        text = NamedSecretRegex().Replace(text, "$1[REDACTED]");
        return text;
    }

    [GeneratedRegex(@"(?i)\b(Bearer|RemoteAuth)\s+[A-Za-z0-9._~+/-]+=*")]
    private static partial Regex BearerCredentialRegex();

    [GeneratedRegex(@"\beyJ[A-Za-z0-9_-]*\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\b")]
    private static partial Regex CompactJwtRegex();

    [GeneratedRegex("""(?is)-----BEGIN [^-]*PRIVATE KEY-----.*?-----END [^-]*PRIVATE KEY-----""")]
    private static partial Regex PemPrivateKeyRegex();

    [GeneratedRegex("""(?i)\bhttps?://[^\s"'<>]+""")]
    private static partial Regex HttpUrlRegex();

    [GeneratedRegex("""(?i)([\"']?\b(?:access[_-]?token|messagequeueaccesstoken|admin[_-]?token|token|secret|password|authorization|private[_-]?key|encodedjitconfig)\b[\"']?\s*[:=]\s*)(\"[^\"]*\"|'[^']*'|[^,;\s}]+)""")]
    private static partial Regex NamedSecretRegex();

    internal enum ResponseBodyReadFailure
    {
        None = 0,
        AttemptTimedOut = 1,
        ReadError = 2,
    }

    internal readonly record struct ResponseBodyReadResult(
        byte[] RawBytes,
        bool Truncated,
        bool IsIncomplete,
        ResponseBodyReadFailure Failure);
}
