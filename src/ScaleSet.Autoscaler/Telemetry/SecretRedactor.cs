using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using Serilog.Core;
using Serilog.Events;
using Serilog.Parsing;

namespace ScaleSet.Autoscaler.Telemetry;

/// <summary>
/// Redacts known secret values, credential-like fields, and common serialized
/// credential forms before a log event reaches an exporter.
/// </summary>
public sealed class SecretRedactor
{
    private static readonly Regex BearerCredential = new(
        @"(?i)\b(Bearer|Basic|RemoteAuth)\s+[A-Za-z0-9._~+/-]+=*",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    private static readonly Regex SerializedCredential = new(
        """(?i)(\b[\w.-]*(?:token|password|passwd|secret|private[_-]?key|authorization|api[_-]?key|jit[_-]?(?:config|payload)|credential)["']?\s*[:=]\s*)(?:"(?:\\.|[^"\\])*"|'(?:\\.|[^'\\])*'|(?:(?:Bearer|Basic|RemoteAuth)\s+)?[^\s,;\]}]+)""",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    private static readonly Regex StandaloneCredential = new(
        """(?i:-----BEGIN [A-Z0-9 -]*PRIVATE KEY-----[\s\S]*?(?:-----END [A-Z0-9 -]*PRIVATE KEY-----|\z))|\beyJ[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]*|\b(?i:gh[porsu]_|github_pat_)[A-Za-z0-9_]+""",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    private readonly ConcurrentDictionary<string, byte> _knownSecrets = new(StringComparer.Ordinal);

    public SecretRedactor(IEnumerable<string>? knownSecrets = null)
    {
        if (knownSecrets is not null)
        {
            foreach (var secret in knownSecrets)
            {
                AddSecret(secret);
            }
        }
    }

    public void AddSecret(string? secret)
    {
        if (!string.IsNullOrEmpty(secret))
        {
            _knownSecrets.TryAdd(secret, 0);
        }
    }

    public string Redact(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        var redacted = StandaloneCredential.Replace(value, "[REDACTED]");
        foreach (var secret in _knownSecrets.Keys.OrderByDescending(static item => item.Length))
        {
            redacted = redacted.Replace(secret, "[REDACTED]", StringComparison.Ordinal);
        }

        redacted = SerializedCredential.Replace(redacted, "$1[REDACTED]");
        return BearerCredential.Replace(redacted, "$1 [REDACTED]");
    }

    public static bool IsSensitiveName(string name)
    {
        var normalized = name.Replace("_", string.Empty, StringComparison.Ordinal)
            .Replace("-", string.Empty, StringComparison.Ordinal)
            .Replace(".", string.Empty, StringComparison.Ordinal)
            .ToLowerInvariant();

        return normalized.Contains("token", StringComparison.Ordinal) ||
               normalized.Contains("secret", StringComparison.Ordinal) ||
               normalized.Contains("password", StringComparison.Ordinal) ||
               normalized.Contains("passwd", StringComparison.Ordinal) ||
               normalized.Contains("authorization", StringComparison.Ordinal) ||
               normalized.Contains("credential", StringComparison.Ordinal) ||
               normalized.Contains("privatekey", StringComparison.Ordinal) ||
               normalized.Contains("apikey", StringComparison.Ordinal) ||
               normalized.Equals("pat", StringComparison.Ordinal) ||
               normalized.Contains("jitconfig", StringComparison.Ordinal) ||
               normalized.Contains("jitpayload", StringComparison.Ordinal);
    }
}

/// <summary>
/// Makes a safe copy before export because Serilog log events are immutable in
/// their message and exception fields. This also keeps raw events out of the
/// background export queue.
/// </summary>
internal sealed class RedactingLogEventSink : ILogEventSink, IDisposable
{
    private readonly SecretRedactor _redactor;
    private readonly Serilog.ILogger _exportLogger;
    private readonly Channel<LogEvent> _queue;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly Task _worker;
    private readonly TimeSpan _shutdownTimeout;
    private readonly Action _onDropped;
    private int _disposed;

    public RedactingLogEventSink(
        Serilog.ILogger exportLogger,
        SecretRedactor redactor,
        int queueLimit,
        TimeSpan shutdownTimeout,
        Action onDropped)
    {
        _exportLogger = exportLogger;
        _redactor = redactor;
        _shutdownTimeout = shutdownTimeout;
        _onDropped = onDropped;
        _queue = Channel.CreateBounded<LogEvent>(new BoundedChannelOptions(queueLimit)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
        });
        _worker = Task.Run(ExportAsync);
    }

    public void Emit(LogEvent logEvent)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            _onDropped();
            return;
        }

        try
        {
            var safeEvent = Redact(logEvent);
            if (!_queue.Writer.TryWrite(safeEvent))
            {
                _onDropped();
            }
        }
        catch
        {
            // Logging must not interrupt runner work if an event cannot be
            // transformed safely. The drop counter remains available to alert.
            _onDropped();
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _queue.Writer.TryComplete();
        var elapsed = Stopwatch.StartNew();
        if (!WaitWithinBudget(_worker, elapsed))
        {
            _cancellation.Cancel();
        }

        var disposeTask = DisposeExporterAsync();
        _ = WaitWithinBudget(disposeTask, elapsed);
    }

    private async Task DisposeExporterAsync()
    {
        try
        {
            await _worker.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ForceYielding);
            (_exportLogger as IDisposable)?.Dispose();
        }
        finally
        {
            _cancellation.Dispose();
        }
    }

    private async Task ExportAsync()
    {
        try
        {
            await foreach (var logEvent in _queue.Reader.ReadAllAsync(_cancellation.Token).ConfigureAwait(false))
            {
                try
                {
                    _exportLogger.Write(logEvent);
                }
                catch
                {
                    // Export failures are isolated from the application logger.
                    _onDropped();
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Shutdown reached its configured flush deadline.
        }
    }

    private LogEvent Redact(LogEvent logEvent)
    {
        var properties = logEvent.Properties.Select(property =>
            new LogEventProperty(property.Key, RedactValue(property.Value, property.Key)));
        var templateText = _redactor.Redact(logEvent.MessageTemplate.Text);
        var template = new MessageTemplateParser().Parse(templateText);
        var safeException = logEvent.Exception is null
            ? null
            : new RedactedException(_redactor.Redact(logEvent.Exception.Message), _redactor.Redact(logEvent.Exception.ToString()));

        return new LogEvent(
            logEvent.Timestamp,
            logEvent.Level,
            safeException,
            template,
            properties,
            logEvent.TraceId ?? default,
            logEvent.SpanId ?? default);
    }

    private LogEventPropertyValue RedactValue(LogEventPropertyValue value, string propertyName)
    {
        if (SecretRedactor.IsSensitiveName(propertyName))
        {
            return new ScalarValue("[REDACTED]");
        }

        return value switch
        {
            ScalarValue scalar => RedactScalar(scalar),
            StructureValue structure => new StructureValue(
                structure.Properties.Select(property => new LogEventProperty(
                    property.Name,
                    RedactValue(property.Value, property.Name))),
                structure.TypeTag),
            SequenceValue sequence => new SequenceValue(sequence.Elements.Select(element => RedactValue(element, propertyName))),
            DictionaryValue dictionary => new DictionaryValue(dictionary.Elements.Select(entry =>
                new KeyValuePair<ScalarValue, LogEventPropertyValue>(
                    RedactScalar(entry.Key),
                    RedactValue(entry.Value, entry.Key.Value?.ToString() ?? propertyName)))),
            _ => value,
        };
    }

    private ScalarValue RedactScalar(ScalarValue scalar)
    {
        if (scalar.Value?.ToString() is not { } text)
        {
            return scalar;
        }

        string redacted = _redactor.Redact(text);
        return redacted == text ? scalar : new ScalarValue(redacted);
    }

    private bool WaitWithinBudget(Task task, Stopwatch elapsed)
    {
        var remaining = _shutdownTimeout - elapsed.Elapsed;
        return remaining > TimeSpan.Zero && task.Wait(remaining);
    }

    private sealed class RedactedException(string message, string serialized) : Exception(message)
    {
        public override string ToString() => serialized;
    }
}
