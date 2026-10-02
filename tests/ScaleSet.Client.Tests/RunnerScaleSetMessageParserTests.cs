using System.Text;
using System.Text.Json;
using System.Globalization;
using ScaleSet.Client;
using Xunit;

namespace ScaleSet.Client.Tests;

public sealed class RunnerScaleSetMessageParserTests
{
    [Fact]
    public void ParseDeserializesEncodedKnownMessages()
    {
        const string body = """
            [
              {"messageType":"JobAvailable","acquireJobUrl":"https://actions.example/acquire","runnerRequestId":8589934593,"repositoryName":"repo","ownerName":"org","jobId":"job-1","jobWorkflowRef":"org/repo/.github/workflows/ci.yml@refs/heads/main","jobDisplayName":"Build","workflowRunId":8589934594,"eventName":"push","requestLabels":["linux","x64"],"queueTime":"2026-10-01T02:03:04Z"},
              {"messageType":"JobAssigned","runnerRequestId":8589934595},
              {"messageType":"JobStarted","runnerId":8589934596,"runnerName":"runner-1","runnerRequestId":8589934597},
              {"messageType":"JobCompleted","result":"Succeeded","runnerId":8589934598,"runnerName":"runner-2","runnerRequestId":8589934599}
            ]
            """;

        var response = CreateResponse(body, messageId: 8589934592);
        using var outer = JsonDocument.Parse(response);
        Assert.Equal(JsonValueKind.String, outer.RootElement.GetProperty("body").ValueKind);

        var parsed = RunnerScaleSetMessageParser.Parse(response);

        Assert.Equal(8589934592, parsed.MessageId);
        Assert.Single(parsed.JobAvailableMessages);
        Assert.Single(parsed.JobAssignedMessages);
        Assert.Single(parsed.JobStartedMessages);
        Assert.Single(parsed.JobCompletedMessages);
        Assert.Equal(8589934593, parsed.JobAvailableMessages[0].RunnerRequestId);
        Assert.Equal(8589934594, parsed.JobAvailableMessages[0].WorkflowRunId);
        Assert.Equal("https://actions.example/acquire", parsed.JobAvailableMessages[0].AcquireJobUrl);
        Assert.Equal(
            "2026-10-01T02:03:04+00:00",
            parsed.JobAvailableMessages[0].QueueTime.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture));
        Assert.Equal(8589934596, parsed.JobStartedMessages[0].RunnerId);
        Assert.Equal("Succeeded", parsed.JobCompletedMessages[0].Result);
    }

    [Fact]
    public void ParseNullBodyProducesEmptyBatch()
    {
        const string response = """
            {"messageId":1,"messageType":"RunnerScaleSetJobMessages","body":null,"statistics":null}
            """;

        var parsed = RunnerScaleSetMessageParser.Parse(response);

        Assert.Equal(1, parsed.MessageId);
        Assert.Null(parsed.Statistics);
        Assert.Empty(parsed.JobAvailableMessages);
        Assert.Empty(parsed.JobAssignedMessages);
        Assert.Empty(parsed.JobStartedMessages);
        Assert.Empty(parsed.JobCompletedMessages);
    }

    [Fact]
    public void ParseEmptyBodyProducesEmptyBatch()
    {
        var parsed = RunnerScaleSetMessageParser.Parse(CreateResponse(string.Empty));

        Assert.Empty(parsed.JobAvailableMessages);
        Assert.Empty(parsed.JobAssignedMessages);
        Assert.Empty(parsed.JobStartedMessages);
        Assert.Empty(parsed.JobCompletedMessages);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("null")]
    public void ParseEmptyEncodedCollectionProducesEmptyBatch(string body)
    {
        var parsed = RunnerScaleSetMessageParser.Parse(CreateResponse(body));

        Assert.Empty(parsed.JobAvailableMessages);
        Assert.Empty(parsed.JobAssignedMessages);
        Assert.Empty(parsed.JobStartedMessages);
        Assert.Empty(parsed.JobCompletedMessages);
    }

    [Fact]
    public void ParseUnknownInnerTypeIsIgnored()
    {
        const string body = "[{\"messageType\":\"FutureMessage\",\"runnerId\":\"not-an-integer\"}]";

        var parsed = RunnerScaleSetMessageParser.Parse(CreateResponse(body));

        Assert.Empty(parsed.JobAvailableMessages);
        Assert.Empty(parsed.JobAssignedMessages);
        Assert.Empty(parsed.JobStartedMessages);
        Assert.Empty(parsed.JobCompletedMessages);
    }

    [Fact]
    public void ParseRejectsUnsupportedOuterType()
    {
        var response = JsonSerializer.Serialize(new
        {
            messageId = 1,
            messageType = "OtherEnvelope",
            body = "[]",
        });

        var exception = Assert.Throws<NotSupportedException>(() => RunnerScaleSetMessageParser.Parse(response));

        Assert.Contains("OtherEnvelope", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ParseRejectsMalformedOuterJson()
    {
        Assert.ThrowsAny<JsonException>(() => RunnerScaleSetMessageParser.Parse("{not-json"));
    }

    [Fact]
    public void ParseRejectsMalformedKnownMessage()
    {
        const string body = "[{\"messageType\":\"JobStarted\",\"runnerId\":\"not-an-integer\"}]";

        Assert.Throws<JsonException>(() => RunnerScaleSetMessageParser.Parse(CreateResponse(body)));
    }

    [Fact]
    public void ParseRejectsNonArrayEncodedBody()
    {
        Assert.Throws<JsonException>(() => RunnerScaleSetMessageParser.Parse(CreateResponse("{}")));
    }

    [Fact]
    public void ParseStripsUtf8Bom()
    {
        var response = Encoding.UTF8.GetBytes("\uFEFF" + CreateResponse("[]"));

        var parsed = RunnerScaleSetMessageParser.Parse(response);

        Assert.Equal(1, parsed.MessageId);
        Assert.Empty(parsed.JobAvailableMessages);
    }

    private static string CreateResponse(string? body, long messageId = 1) =>
        JsonSerializer.Serialize(new
        {
            messageId,
            messageType = "RunnerScaleSetJobMessages",
            body,
            statistics = (RunnerScaleSetStatistic?)null,
        });
}
