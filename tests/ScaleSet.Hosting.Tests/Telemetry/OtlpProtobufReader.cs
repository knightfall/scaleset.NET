using System.Buffers.Binary;
using System.Text;

namespace ScaleSet.Hosting.Tests.Telemetry;

internal static class OtlpProtobufReader
{
    public static IReadOnlyDictionary<string, string> ReadLogResource(byte[] payload) =>
        ReadResource(FirstNested(FirstNested(payload, 1, "ResourceLogs"), 1, "ResourceLogs.resource"), "ResourceLogs.resource");

    public static IReadOnlyDictionary<string, string> ReadTraceResource(byte[] payload) =>
        ReadResource(FirstNested(FirstNested(payload, 1, "ResourceSpans"), 1, "ResourceSpans.resource"), "ResourceSpans.resource");

    public static IReadOnlyDictionary<string, string> ReadMetricResource(byte[] payload) =>
        ReadResource(FirstNested(FirstNested(payload, 1, "ResourceMetrics"), 1, "ResourceMetrics.resource"), "ResourceMetrics.resource");

    public static IReadOnlyList<OtlpLogRecord> ReadLogRecords(byte[] payload)
    {
        List<OtlpLogRecord> records = [];
        foreach (ProtoField resourceLogs in ReadFields(payload).Where(static field => field.Number == 1))
        {
            foreach (ProtoField scopeLogs in ReadFields(resourceLogs.Payload).Where(static field => field.Number == 2))
            {
                foreach (ProtoField record in ReadFields(scopeLogs.Payload).Where(static field => field.Number == 2))
                {
                    var fields = ReadFields(record.Payload);
                    var bodyField = fields.FirstOrDefault(static field => field.Number == 5);
                    records.Add(new OtlpLogRecord(
                        ReadAnyValueString(bodyField.Payload),
                        ReadHex(fields.FirstOrDefault(static field => field.Number == 9).Payload),
                        ReadHex(fields.FirstOrDefault(static field => field.Number == 10).Payload)));
                }
            }
        }

        return records;
    }

    public static IReadOnlyList<OtlpTraceSpan> ReadTraceSpans(byte[] payload)
    {
        List<OtlpTraceSpan> spans = [];
        foreach (ProtoField resourceSpans in ReadFields(payload).Where(static field => field.Number == 1))
        {
            foreach (ProtoField scopeSpans in ReadFields(resourceSpans.Payload).Where(static field => field.Number == 2))
            {
                foreach (ProtoField span in ReadFields(scopeSpans.Payload).Where(static field => field.Number == 2))
                {
                    var fields = ReadFields(span.Payload);
                    spans.Add(new OtlpTraceSpan(
                        ReadHex(fields.FirstOrDefault(static field => field.Number == 1).Payload),
                        ReadHex(fields.FirstOrDefault(static field => field.Number == 2).Payload),
                        ReadString(fields.FirstOrDefault(static field => field.Number == 5).Payload)));
                }
            }
        }

        return spans;
    }

    public static IReadOnlyList<string> ReadMetricNames(byte[] payload)
    {
        List<string> names = [];
        foreach (ProtoField resourceMetrics in ReadFields(payload).Where(static field => field.Number == 1))
        {
            foreach (ProtoField scopeMetrics in ReadFields(resourceMetrics.Payload).Where(static field => field.Number == 2))
            {
                foreach (ProtoField metric in ReadFields(scopeMetrics.Payload).Where(static field => field.Number == 2))
                {
                    var name = ReadString(ReadFields(metric.Payload).FirstOrDefault(static field => field.Number == 1).Payload);
                    if (name is not null)
                    {
                        names.Add(name);
                    }
                }
            }
        }

        return names;
    }

    private static Dictionary<string, string> ReadResource(byte[] resourcePayload, string description)
    {
        Dictionary<string, string> attributes = new(StringComparer.Ordinal);
        foreach (ProtoField keyValue in ReadFields(resourcePayload).Where(static field => field.Number == 1))
        {
            var fields = ReadFields(keyValue.Payload);
            var key = ReadString(fields.FirstOrDefault(static field => field.Number == 1).Payload);
            var value = ReadAnyValueString(fields.FirstOrDefault(static field => field.Number == 2).Payload);
            if (key is null || value is null)
            {
                throw new InvalidDataException($"The OTLP {description} is missing a string key/value attribute.");
            }

            attributes.Add(key, value);
        }

        return attributes;
    }

    private static byte[] FirstNested(byte[] payload, int fieldNumber, string description)
    {
        var field = ReadFields(payload).FirstOrDefault(item => item.Number == fieldNumber && item.Payload is not null);
        return field.Payload ?? throw new InvalidDataException($"The OTLP payload did not contain {description}.");
    }

    private static string? ReadAnyValueString(byte[]? payload)
    {
        if (payload is null)
        {
            return null;
        }

        var stringValue = ReadFields(payload).FirstOrDefault(static field => field.Number == 1);
        return ReadString(stringValue.Payload);
    }

    private static string? ReadString(byte[]? bytes) => bytes is null ? null : Encoding.UTF8.GetString(bytes);

    private static string? ReadHex(byte[]? bytes) => bytes is null ? null : Convert.ToHexString(bytes).ToLowerInvariant();

    private static IReadOnlyList<ProtoField> ReadFields(byte[]? payload)
    {
        if (payload is null)
        {
            return Array.Empty<ProtoField>();
        }

        List<ProtoField> fields = [];
        ReadOnlySpan<byte> data = payload;
        var offset = 0;
        while (offset < data.Length)
        {
            var tag = ReadVarint(data, ref offset);
            var number = (int)(tag >> 3);
            var wireType = (int)(tag & 7);
            switch (wireType)
            {
                case 0:
                    fields.Add(new ProtoField(number, wireType, null, ReadVarint(data, ref offset)));
                    break;
                case 1:
                    EnsureRemaining(data, offset, sizeof(ulong));
                    fields.Add(new ProtoField(number, wireType, null, BinaryPrimitives.ReadUInt64LittleEndian(data[offset..])));
                    offset += sizeof(ulong);
                    break;
                case 2:
                    var length = checked((int)ReadVarint(data, ref offset));
                    EnsureRemaining(data, offset, length);
                    fields.Add(new ProtoField(number, wireType, data.Slice(offset, length).ToArray(), 0));
                    offset += length;
                    break;
                case 5:
                    EnsureRemaining(data, offset, sizeof(uint));
                    offset += sizeof(uint);
                    fields.Add(new ProtoField(number, wireType, null, 0));
                    break;
                default:
                    throw new InvalidDataException($"Unsupported OTLP protobuf wire type {wireType}.");
            }
        }

        return fields;
    }

    private static ulong ReadVarint(ReadOnlySpan<byte> data, ref int offset)
    {
        ulong result = 0;
        for (var shift = 0; shift < 64; shift += 7)
        {
            EnsureRemaining(data, offset, 1);
            var current = data[offset++];
            result |= (ulong)(current & 0x7F) << shift;
            if ((current & 0x80) == 0)
            {
                return result;
            }
        }

        throw new InvalidDataException("An OTLP protobuf varint exceeded its valid length.");
    }

    private static void EnsureRemaining(ReadOnlySpan<byte> data, int offset, int count)
    {
        if (count < 0 || offset < 0 || count > data.Length - offset)
        {
            throw new InvalidDataException("The OTLP protobuf payload ended unexpectedly.");
        }
    }

    private readonly record struct ProtoField(int Number, int WireType, byte[]? Payload, ulong NumberValue);
}

internal sealed record OtlpLogRecord(string? Body, string? TraceId, string? SpanId);

internal sealed record OtlpTraceSpan(string? TraceId, string? SpanId, string? Name);
