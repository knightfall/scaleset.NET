using System.Text.Json;

namespace ScaleSet.Client;

/// <summary>Parses the queue envelope and the JSON document encoded in its body string.</summary>
public static class RunnerScaleSetMessageParser
{
    /// <summary>Parses one runner scale set queue response.</summary>
    /// <param name="responseJson">The outer JSON response.</param>
    /// <returns>The parsed queue message and recognized job messages.</returns>
    /// <exception cref="JsonException">The response or a known message is malformed.</exception>
    /// <exception cref="NotSupportedException">The outer message type is unsupported.</exception>
    public static RunnerScaleSetMessage Parse(string responseJson)
    {
        ArgumentNullException.ThrowIfNull(responseJson);
        var json = responseJson.Length > 0 && responseJson[0] == '\uFEFF'
            ? responseJson[1..]
            : responseJson;

        using var document = JsonDocument.Parse(json);
        return ParseEnvelope(document.RootElement);
    }

    /// <summary>Parses one UTF-8 runner scale set queue response.</summary>
    /// <param name="responseJson">The UTF-8 encoded outer JSON response.</param>
    /// <returns>The parsed queue message and recognized job messages.</returns>
    /// <exception cref="JsonException">The response or a known message is malformed.</exception>
    /// <exception cref="NotSupportedException">The outer message type is unsupported.</exception>
    public static RunnerScaleSetMessage Parse(ReadOnlySpan<byte> responseJson)
    {
        if (responseJson.Length >= 3 &&
            responseJson[0] == 0xEF &&
            responseJson[1] == 0xBB &&
            responseJson[2] == 0xBF)
        {
            responseJson = responseJson[3..];
        }

        using var document = JsonDocument.Parse(responseJson.ToArray());
        return ParseEnvelope(document.RootElement);
    }

    private static RunnerScaleSetMessage ParseEnvelope(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("The runner scale set response must be a JSON object.");
        }

        var outerType = ReadString(root, "messageType") ?? string.Empty;
        if (!string.Equals(outerType, "RunnerScaleSetJobMessages", StringComparison.Ordinal))
        {
            throw new NotSupportedException($"Unsupported runner scale set message type '{outerType}'.");
        }

        var messageId = ReadInt64(root, "messageId");
        var body = ReadString(root, "body");
        var statistics = ReadStatistics(root);

        var jobAvailableMessages = new List<JobAvailableMessage>();
        var jobAssignedMessages = new List<JobAssignedMessage>();
        var jobStartedMessages = new List<JobStartedMessage>();
        var jobCompletedMessages = new List<JobCompletedMessage>();

        if (!string.IsNullOrEmpty(body))
        {
            using var bodyDocument = JsonDocument.Parse(body);
            var bodyRoot = bodyDocument.RootElement;
            if (bodyRoot.ValueKind == JsonValueKind.Array)
            {
                foreach (var message in bodyRoot.EnumerateArray())
                {
                    ParseInnerMessage(
                        message,
                        jobAvailableMessages,
                        jobAssignedMessages,
                        jobStartedMessages,
                        jobCompletedMessages);
                }
            }
            else if (bodyRoot.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
            {
                throw new JsonException("The encoded runner scale set message body must be a JSON array or null.");
            }
        }

        return new RunnerScaleSetMessage
        {
            MessageId = messageId,
            Statistics = statistics,
            JobAvailableMessages = Array.AsReadOnly(jobAvailableMessages.ToArray()),
            JobAssignedMessages = Array.AsReadOnly(jobAssignedMessages.ToArray()),
            JobStartedMessages = Array.AsReadOnly(jobStartedMessages.ToArray()),
            JobCompletedMessages = Array.AsReadOnly(jobCompletedMessages.ToArray()),
        };
    }

    private static void ParseInnerMessage(
        JsonElement message,
        List<JobAvailableMessage> jobAvailableMessages,
        List<JobAssignedMessage> jobAssignedMessages,
        List<JobStartedMessage> jobStartedMessages,
        List<JobCompletedMessage> jobCompletedMessages)
    {
        if (message.ValueKind == JsonValueKind.Null)
        {
            return;
        }

        if (message.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("A job message must be a JSON object.");
        }

        var messageType = ReadString(message, "messageType");
        if (messageType is null)
        {
            return;
        }

        var context = ScaleSetJson.GeneratedContext;
        switch (messageType)
        {
            case JobMessageTypes.JobAvailable:
                jobAvailableMessages.Add(DeserializeKnown<JobAvailableMessage>(message, context.JobAvailableMessage));
                break;
            case JobMessageTypes.JobAssigned:
                jobAssignedMessages.Add(DeserializeKnown<JobAssignedMessage>(message, context.JobAssignedMessage));
                break;
            case JobMessageTypes.JobStarted:
                jobStartedMessages.Add(DeserializeKnown<JobStartedMessage>(message, context.JobStartedMessage));
                break;
            case JobMessageTypes.JobCompleted:
                jobCompletedMessages.Add(DeserializeKnown<JobCompletedMessage>(message, context.JobCompletedMessage));
                break;
            default:
                // The Go client deliberately ignores message types it does not know yet.
                break;
        }
    }

    private static T DeserializeKnown<T>(
        JsonElement message,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> jsonTypeInfo)
        where T : class
    {
        return message.Deserialize(jsonTypeInfo)
            ?? throw new JsonException("A known job message cannot be null.");
    }

    private static RunnerScaleSetStatistic? ReadStatistics(JsonElement root)
    {
        if (!root.TryGetProperty("statistics", out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        return value.Deserialize(ScaleSetJson.GeneratedContext.RunnerScaleSetStatistic)
            ?? throw new JsonException("The statistics value cannot be null.");
    }

    private static string? ReadString(JsonElement source, string propertyName)
    {
        if (!source.TryGetProperty(propertyName, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.String)
        {
            throw new JsonException($"The '{propertyName}' property must be a string or null.");
        }

        return value.GetString();
    }

    private static long ReadInt64(JsonElement source, string propertyName)
    {
        if (!source.TryGetProperty(propertyName, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return 0;
        }

        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out var result))
        {
            throw new JsonException($"The '{propertyName}' property must be an Int64 value.");
        }

        return result;
    }
}
