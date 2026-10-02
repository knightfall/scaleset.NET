using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace ScaleSet.Client;

/// <summary>Names used by the scale set job-message protocol.</summary>
public static class JobMessageTypes
{
    /// <summary>Job is available to acquire.</summary>
    public const string JobAvailable = "JobAvailable";

    /// <summary>Job has been assigned.</summary>
    public const string JobAssigned = "JobAssigned";

    /// <summary>Job has started on a runner.</summary>
    public const string JobStarted = "JobStarted";

    /// <summary>Job has completed on a runner.</summary>
    public const string JobCompleted = "JobCompleted";
}

/// <summary>Wire representation of a runner label.</summary>
public sealed record Label
{
    /// <summary>Label category.</summary>
    [JsonPropertyName("type")]
    [JsonPropertyOrder(0)]
    public string Type { get; init; } = string.Empty;

    /// <summary>Label value.</summary>
    [JsonPropertyName("name")]
    [JsonPropertyOrder(1)]
    public string Name { get; init; } = string.Empty;
}

/// <summary>Wire representation of a GitHub Actions runner group.</summary>
public sealed record RunnerGroup
{
    /// <summary>Runner group identifier.</summary>
    [JsonPropertyName("id")]
    [JsonPropertyOrder(0)]
    public long Id { get; init; }

    /// <summary>Runner group name.</summary>
    [JsonPropertyName("name")]
    [JsonPropertyOrder(1)]
    public string Name { get; init; } = string.Empty;

    /// <summary>Number of runners in the group.</summary>
    [JsonPropertyName("size")]
    [JsonPropertyOrder(2)]
    public int Size { get; init; }

    /// <summary>Whether this is the default runner group.</summary>
    [JsonPropertyName("isDefaultGroup")]
    [JsonPropertyOrder(3)]
    public bool IsDefaultGroup { get; init; }
}

/// <summary>Wire response containing runner groups.</summary>
public sealed record RunnerGroupList
{
    /// <summary>Number of groups in the response.</summary>
    [JsonPropertyName("count")]
    [JsonPropertyOrder(0)]
    public int Count { get; init; }

    /// <summary>Runner groups returned by the service.</summary>
    [JsonPropertyName("value")]
    [JsonPropertyOrder(1)]
    public IReadOnlyList<RunnerGroup>? Value { get; init; }
}

/// <summary>Wire representation of a runner scale set.</summary>
public sealed record RunnerScaleSet
{
    /// <summary>Scale set identifier.</summary>
    [JsonPropertyName("id")]
    [JsonPropertyOrder(0)]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public long Id { get; init; }

    /// <summary>Scale set name.</summary>
    [JsonPropertyName("name")]
    [JsonPropertyOrder(1)]
    [GoOmitEmpty]
    public string? Name { get; init; }

    /// <summary>Owning runner group identifier.</summary>
    [JsonPropertyName("runnerGroupId")]
    [JsonPropertyOrder(2)]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public long RunnerGroupId { get; init; }

    /// <summary>Owning runner group name.</summary>
    [JsonPropertyName("runnerGroupName")]
    [JsonPropertyOrder(3)]
    [GoOmitEmpty]
    public string? RunnerGroupName { get; init; }

    /// <summary>Scale set labels.</summary>
    [JsonPropertyName("labels")]
    [JsonPropertyOrder(4)]
    [GoOmitEmpty]
    public IReadOnlyList<Label>? Labels { get; init; }

    /// <summary>Runner settings. The wire name intentionally uses capital letters.</summary>
    [JsonPropertyName("RunnerSetting")]
    [JsonPropertyOrder(5)]
    public RunnerSetting RunnerSetting { get; init; } = new();

    /// <summary>Creation time.</summary>
    [JsonPropertyName("createdOn")]
    [JsonPropertyOrder(6)]
    public DateTimeOffset CreatedOn { get; init; }

    /// <summary>JIT configuration service URL.</summary>
    [JsonPropertyName("runnerJitConfigUrl")]
    [JsonPropertyOrder(7)]
    [GoOmitEmpty]
    public string? RunnerJitConfigUrl { get; init; }

    /// <summary>Current scale set statistics.</summary>
    [JsonPropertyName("statistics")]
    [JsonPropertyOrder(8)]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public RunnerScaleSetStatistic? Statistics { get; init; }
}

/// <summary>Runner settings returned for a scale set.</summary>
public sealed record RunnerSetting
{
    /// <summary>Whether automatic runner updates are disabled.</summary>
    [JsonPropertyName("disableUpdate")]
    [JsonPropertyOrder(0)]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool DisableUpdate { get; init; }
}

/// <summary>Name and work folder sent when generating a runner JIT configuration.</summary>
public sealed record RunnerScaleSetJitRunnerSetting
{
    /// <summary>Runner name.</summary>
    [JsonPropertyName("name")]
    [JsonPropertyOrder(0)]
    public string Name { get; init; } = string.Empty;

    /// <summary>Runner work folder.</summary>
    [JsonPropertyName("workFolder")]
    [JsonPropertyOrder(1)]
    public string WorkFolder { get; init; } = string.Empty;
}

/// <summary>Wire representation of a scale set queue response.</summary>
public sealed record RunnerScaleSetMessageEnvelope
{
    /// <summary>Queue message identifier.</summary>
    [JsonPropertyName("messageId")]
    [JsonPropertyOrder(0)]
    public long MessageId { get; init; }

    /// <summary>Outer queue message type.</summary>
    [JsonPropertyName("messageType")]
    [JsonPropertyOrder(1)]
    public string MessageType { get; init; } = string.Empty;

    /// <summary>A JSON document encoded inside the outer JSON string value.</summary>
    [JsonPropertyName("body")]
    [JsonPropertyOrder(2)]
    public string? Body { get; init; }

    /// <summary>Scale set statistics, including an explicit null when absent.</summary>
    [JsonPropertyName("statistics")]
    [JsonPropertyOrder(3)]
    public RunnerScaleSetStatistic? Statistics { get; init; }
}

/// <summary>Wire response containing scale sets.</summary>
public sealed record RunnerScaleSetList
{
    /// <summary>Number of scale sets in the response.</summary>
    [JsonPropertyName("count")]
    [JsonPropertyOrder(0)]
    public int Count { get; init; }

    /// <summary>Scale sets returned by the service.</summary>
    [JsonPropertyName("value")]
    [JsonPropertyOrder(1)]
    public IReadOnlyList<RunnerScaleSet>? Value { get; init; }
}

/// <summary>Wire response containing acquired runner request identifiers.</summary>
public sealed record AcquireJobsResponse
{
    /// <summary>Number of acquired jobs.</summary>
    [JsonPropertyName("count")]
    [JsonPropertyOrder(0)]
    public int Count { get; init; }

    /// <summary>Runner request identifiers returned by the service.</summary>
    [JsonPropertyName("value")]
    [JsonPropertyOrder(1)]
    public IReadOnlyList<long>? Value { get; init; }
}

/// <summary>Wire representation of a message session.</summary>
public sealed record RunnerScaleSetSession
{
    /// <summary>Message session identifier.</summary>
    [JsonPropertyName("sessionId")]
    [JsonPropertyOrder(0)]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public Guid SessionId { get; init; }

    /// <summary>Owner name for this message session.</summary>
    [JsonPropertyName("ownerName")]
    [JsonPropertyOrder(1)]
    [GoOmitEmpty]
    public string? OwnerName { get; init; }

    /// <summary>Scale set attached to the message session.</summary>
    [JsonPropertyName("runnerScaleSet")]
    [JsonPropertyOrder(2)]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public RunnerScaleSet? RunnerScaleSet { get; init; }

    /// <summary>Message queue URL.</summary>
    [JsonPropertyName("messageQueueUrl")]
    [JsonPropertyOrder(3)]
    [GoOmitEmpty]
    public string? MessageQueueUrl { get; init; }

    /// <summary>Short-lived message queue access token.</summary>
    [JsonPropertyName("messageQueueAccessToken")]
    [JsonPropertyOrder(4)]
    [GoOmitEmpty]
    public string? MessageQueueAccessToken { get; init; }

    /// <summary>Current queue statistics.</summary>
    [JsonPropertyName("statistics")]
    [JsonPropertyOrder(5)]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public RunnerScaleSetStatistic? Statistics { get; init; }

    /// <summary>Returns a diagnostic summary without exposing the queue token.</summary>
    public override string ToString() =>
        $"RunnerScaleSetSession {{ SessionId = {SessionId}, OwnerName = {OwnerName}, " +
        $"RunnerScaleSet = {RunnerScaleSet?.Name}, MessageQueueUrl = {MessageQueueUrl}, " +
        "MessageQueueAccessToken = [REDACTED], Statistics = " +
        $"{Statistics} }}";
}

/// <summary>Current job and runner counts for a scale set.</summary>
public sealed record RunnerScaleSetStatistic
{
    /// <summary>Available jobs.</summary>
    [JsonPropertyName("totalAvailableJobs")]
    [JsonPropertyOrder(0)]
    public int TotalAvailableJobs { get; init; }

    /// <summary>Acquired jobs.</summary>
    [JsonPropertyName("totalAcquiredJobs")]
    [JsonPropertyOrder(1)]
    public int TotalAcquiredJobs { get; init; }

    /// <summary>Assigned jobs.</summary>
    [JsonPropertyName("totalAssignedJobs")]
    [JsonPropertyOrder(2)]
    public int TotalAssignedJobs { get; init; }

    /// <summary>Running jobs.</summary>
    [JsonPropertyName("totalRunningJobs")]
    [JsonPropertyOrder(3)]
    public int TotalRunningJobs { get; init; }

    /// <summary>Registered runners.</summary>
    [JsonPropertyName("totalRegisteredRunners")]
    [JsonPropertyOrder(4)]
    public int TotalRegisteredRunners { get; init; }

    /// <summary>Busy runners.</summary>
    [JsonPropertyName("totalBusyRunners")]
    [JsonPropertyOrder(5)]
    public int TotalBusyRunners { get; init; }

    /// <summary>Idle runners.</summary>
    [JsonPropertyName("totalIdleRunners")]
    [JsonPropertyOrder(6)]
    public int TotalIdleRunners { get; init; }
}

/// <summary>Wire response containing runner references.</summary>
public sealed record RunnerReferenceList
{
    /// <summary>Number of runner references.</summary>
    [JsonPropertyName("count")]
    [JsonPropertyOrder(0)]
    public int Count { get; init; }

    /// <summary>Runner references returned by the service.</summary>
    [JsonPropertyName("value")]
    [JsonPropertyOrder(1)]
    public IReadOnlyList<RunnerReference>? Value { get; init; }
}

/// <summary>Wire reference to a registered runner.</summary>
public sealed record RunnerReference
{
    /// <summary>Runner identifier.</summary>
    [JsonPropertyName("id")]
    [JsonPropertyOrder(0)]
    public long Id { get; init; }

    /// <summary>Runner name.</summary>
    [JsonPropertyName("name")]
    [JsonPropertyOrder(1)]
    public string Name { get; init; } = string.Empty;

    /// <summary>Owning scale set identifier.</summary>
    [JsonPropertyName("runnerScaleSetId")]
    [JsonPropertyOrder(2)]
    public long RunnerScaleSetId { get; init; }
}

/// <summary>Wire response containing a runner reference and encoded JIT configuration.</summary>
public sealed record RunnerScaleSetJitRunnerConfig
{
    /// <summary>Runner created for the JIT configuration.</summary>
    [JsonPropertyName("runner")]
    [JsonPropertyOrder(0)]
    public RunnerReference? Runner { get; init; }

    /// <summary>Encoded runner JIT configuration.</summary>
    [JsonPropertyName("encodedJITConfig")]
    [JsonPropertyOrder(1)]
    public string EncodedJitConfig { get; init; } = string.Empty;

    /// <summary>Returns a diagnostic summary without exposing the encoded JIT configuration.</summary>
    public override string ToString() =>
        $"RunnerScaleSetJitRunnerConfig {{ Runner = {Runner}, EncodedJitConfig = [REDACTED] }}";
}

/// <summary>Common fields in a job message.</summary>
public abstract record JobMessage
{
    /// <summary>Job message discriminator.</summary>
    [JsonPropertyName("messageType")]
    [JsonPropertyOrder(10)]
    public abstract string MessageType { get; }

    /// <summary>Runner request identifier.</summary>
    [JsonPropertyName("runnerRequestId")]
    [JsonPropertyOrder(11)]
    public long RunnerRequestId { get; init; }

    /// <summary>Repository name.</summary>
    [JsonPropertyName("repositoryName")]
    [JsonPropertyOrder(12)]
    public string RepositoryName { get; init; } = string.Empty;

    /// <summary>Repository owner name.</summary>
    [JsonPropertyName("ownerName")]
    [JsonPropertyOrder(13)]
    public string OwnerName { get; init; } = string.Empty;

    /// <summary>Job identifier.</summary>
    [JsonPropertyName("jobId")]
    [JsonPropertyOrder(14)]
    public string JobId { get; init; } = string.Empty;

    /// <summary>Workflow reference for the job.</summary>
    [JsonPropertyName("jobWorkflowRef")]
    [JsonPropertyOrder(15)]
    public string JobWorkflowRef { get; init; } = string.Empty;

    /// <summary>Display name of the job.</summary>
    [JsonPropertyName("jobDisplayName")]
    [JsonPropertyOrder(16)]
    public string JobDisplayName { get; init; } = string.Empty;

    /// <summary>Workflow run identifier.</summary>
    [JsonPropertyName("workflowRunId")]
    [JsonPropertyOrder(17)]
    public long WorkflowRunId { get; init; }

    /// <summary>Workflow event name.</summary>
    [JsonPropertyName("eventName")]
    [JsonPropertyOrder(18)]
    public string EventName { get; init; } = string.Empty;

    /// <summary>Requested runner labels.</summary>
    [JsonPropertyName("requestLabels")]
    [JsonPropertyOrder(19)]
    public IReadOnlyList<string>? RequestLabels { get; init; }

    /// <summary>Time when the job was queued.</summary>
    [JsonPropertyName("queueTime")]
    [JsonPropertyOrder(20)]
    public DateTimeOffset QueueTime { get; init; }

    /// <summary>Time when the scale set assignment was made.</summary>
    [JsonPropertyName("scaleSetAssignTime")]
    [JsonPropertyOrder(21)]
    public DateTimeOffset ScaleSetAssignTime { get; init; }

    /// <summary>Time when the runner assignment was made.</summary>
    [JsonPropertyName("runnerAssignTime")]
    [JsonPropertyOrder(22)]
    public DateTimeOffset RunnerAssignTime { get; init; }

    /// <summary>Time when the job finished.</summary>
    [JsonPropertyName("finishTime")]
    [JsonPropertyOrder(23)]
    public DateTimeOffset FinishTime { get; init; }
}

/// <summary>Job available for acquisition.</summary>
public sealed record JobAvailableMessage : JobMessage
{
    /// <inheritdoc />
    public override string MessageType => JobMessageTypes.JobAvailable;

    /// <summary>URL to acquire this job.</summary>
    [JsonPropertyName("acquireJobUrl")]
    [JsonPropertyOrder(0)]
    public string AcquireJobUrl { get; init; } = string.Empty;
}

/// <summary>Job assigned to a runner.</summary>
public sealed record JobAssignedMessage : JobMessage
{
    /// <inheritdoc />
    public override string MessageType => JobMessageTypes.JobAssigned;
}

/// <summary>Job that has started on a runner.</summary>
public sealed record JobStartedMessage : JobMessage
{
    /// <inheritdoc />
    public override string MessageType => JobMessageTypes.JobStarted;

    /// <summary>Runner identifier.</summary>
    [JsonPropertyName("runnerId")]
    [JsonPropertyOrder(0)]
    public long RunnerId { get; init; }

    /// <summary>Runner name.</summary>
    [JsonPropertyName("runnerName")]
    [JsonPropertyOrder(1)]
    public string RunnerName { get; init; } = string.Empty;
}

/// <summary>Job completed by a runner.</summary>
public sealed record JobCompletedMessage : JobMessage
{
    /// <inheritdoc />
    public override string MessageType => JobMessageTypes.JobCompleted;

    /// <summary>Job result.</summary>
    [JsonPropertyName("result")]
    [JsonPropertyOrder(0)]
    public string Result { get; init; } = string.Empty;

    /// <summary>Runner identifier.</summary>
    [JsonPropertyName("runnerId")]
    [JsonPropertyOrder(1)]
    public long RunnerId { get; init; }

    /// <summary>Runner name.</summary>
    [JsonPropertyName("runnerName")]
    [JsonPropertyOrder(2)]
    public string RunnerName { get; init; } = string.Empty;
}

/// <summary>Parsed queue message and its recognized job messages.</summary>
public sealed record RunnerScaleSetMessage
{
    /// <summary>Queue message identifier.</summary>
    public long MessageId { get; init; }

    /// <summary>Scale set statistics from the queue response.</summary>
    public RunnerScaleSetStatistic? Statistics { get; init; }

    /// <summary>Recognized job-available messages.</summary>
    public IReadOnlyList<JobAvailableMessage> JobAvailableMessages { get; init; } = Array.Empty<JobAvailableMessage>();

    /// <summary>Recognized job-assigned messages.</summary>
    public IReadOnlyList<JobAssignedMessage> JobAssignedMessages { get; init; } = Array.Empty<JobAssignedMessage>();

    /// <summary>Recognized job-started messages.</summary>
    public IReadOnlyList<JobStartedMessage> JobStartedMessages { get; init; } = Array.Empty<JobStartedMessage>();

    /// <summary>Recognized job-completed messages.</summary>
    public IReadOnlyList<JobCompletedMessage> JobCompletedMessages { get; init; } = Array.Empty<JobCompletedMessage>();
}

/// <summary>Wire discriminator used to inspect a job message before decoding its body.</summary>
public sealed record JobMessageType
{
    /// <summary>Job message discriminator.</summary>
    [JsonPropertyName("messageType")]
    [JsonPropertyOrder(0)]
    public string MessageType { get; init; } = string.Empty;
}

/// <summary>Common wire serialization helpers using the source-generated ScaleSet contract.</summary>
public static class ScaleSetJson
{
    internal static ScaleSetJsonContext GeneratedContext { get; } = ScaleSetJsonContext.Create();

    private static readonly JsonSerializerOptions Options = new()
    {
        TypeInfoResolver = GeneratedContext.WithAddedModifier(ApplyGoOmitEmpty),
    };

    /// <summary>Serializes a ScaleSet wire model with its exact field and omission rules.</summary>
    public static string Serialize<T>(T value)
    {
        var jsonTypeInfo = Options.GetTypeInfo(typeof(T));
        return JsonSerializer.Serialize(value, jsonTypeInfo);
    }

    /// <summary>Deserializes a ScaleSet wire model with its generated metadata.</summary>
    public static T? Deserialize<T>(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        var jsonTypeInfo = Options.GetTypeInfo(typeof(T));
        return (T?)JsonSerializer.Deserialize(json, jsonTypeInfo);
    }

    private static void ApplyGoOmitEmpty(JsonTypeInfo typeInfo)
    {
        foreach (var property in typeInfo.Properties)
        {
            var omitEmpty = property.AttributeProvider?
                .GetCustomAttributes(typeof(GoOmitEmptyAttribute), inherit: false)
                .OfType<GoOmitEmptyAttribute>()
                .Any(attribute => !attribute.IncludeEmpty) == true;
            if (!omitEmpty)
            {
                continue;
            }

            var priorPredicate = property.ShouldSerialize;
            property.ShouldSerialize = (instance, value) =>
                (priorPredicate?.Invoke(instance, value) ?? true) && !IsGoEmpty(value);
        }
    }

    private static bool IsGoEmpty(object? value)
    {
        if (value is null)
        {
            return true;
        }

        if (value is string text)
        {
            return text.Length == 0;
        }

        if (value is System.Collections.ICollection collection)
        {
            return collection.Count == 0;
        }

        if (value is System.Collections.IEnumerable enumerable)
        {
            var iterator = enumerable.GetEnumerator();
            try
            {
                return !iterator.MoveNext();
            }
            finally
            {
                (iterator as IDisposable)?.Dispose();
            }
        }

        return false;
    }
}

[AttributeUsage(AttributeTargets.Property)]
internal sealed class GoOmitEmptyAttribute : Attribute
{
    public bool IncludeEmpty { get; set; }
}

[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(Label))]
[JsonSerializable(typeof(RunnerGroup))]
[JsonSerializable(typeof(RunnerGroupList))]
[JsonSerializable(typeof(RunnerScaleSet))]
[JsonSerializable(typeof(RunnerSetting))]
[JsonSerializable(typeof(RunnerScaleSetJitRunnerSetting))]
[JsonSerializable(typeof(RunnerScaleSetMessageEnvelope))]
[JsonSerializable(typeof(RunnerScaleSetList))]
[JsonSerializable(typeof(AcquireJobsResponse))]
[JsonSerializable(typeof(RunnerScaleSetSession))]
[JsonSerializable(typeof(RunnerScaleSetStatistic))]
[JsonSerializable(typeof(RunnerReferenceList))]
[JsonSerializable(typeof(RunnerReference))]
[JsonSerializable(typeof(RunnerScaleSetJitRunnerConfig))]
[JsonSerializable(typeof(JobMessageType))]
[JsonSerializable(typeof(JobAvailableMessage))]
[JsonSerializable(typeof(JobAssignedMessage))]
[JsonSerializable(typeof(JobStartedMessage))]
[JsonSerializable(typeof(JobCompletedMessage))]
internal sealed partial class ScaleSetJsonContext : JsonSerializerContext
{
    internal static ScaleSetJsonContext Create() => new();
}
