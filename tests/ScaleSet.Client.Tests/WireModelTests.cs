using System.Text.Json;
using ScaleSet.Client;
using Xunit;

namespace ScaleSet.Client.Tests;

public sealed class WireModelTests
{
    [Fact]
    public void RunnerScaleSetJsonUsesExactNamesAndOmissionRules()
    {
        var value = new RunnerScaleSet
        {
            Id = 0,
            Name = string.Empty,
            RunnerGroupId = 0,
            RunnerGroupName = string.Empty,
            Labels = Array.Empty<Label>(),
            RunnerSetting = new RunnerSetting { DisableUpdate = false },
            CreatedOn = default,
            RunnerJitConfigUrl = string.Empty,
            Statistics = null,
        };

        using var document = JsonDocument.Parse(ScaleSetJson.Serialize(value));
        var root = document.RootElement;

        Assert.Equal(JsonValueKind.Object, root.ValueKind);
        Assert.False(root.TryGetProperty("id", out _));
        Assert.False(root.TryGetProperty("name", out _));
        Assert.False(root.TryGetProperty("runnerGroupId", out _));
        Assert.False(root.TryGetProperty("runnerGroupName", out _));
        Assert.False(root.TryGetProperty("labels", out _));
        Assert.False(root.TryGetProperty("runnerJitConfigUrl", out _));
        Assert.False(root.TryGetProperty("statistics", out _));
        Assert.False(root.TryGetProperty("runnerSetting", out _));

        Assert.True(root.TryGetProperty("RunnerSetting", out var setting));
        Assert.Equal(JsonValueKind.Object, setting.ValueKind);
        Assert.False(setting.TryGetProperty("disableUpdate", out _));
        Assert.True(root.TryGetProperty("createdOn", out _));
    }

    [Fact]
    public void RunnerScaleSetJsonEmitsNonEmptyOptionalFieldsAndTrueDefaults()
    {
        var value = new RunnerScaleSet
        {
            Id = 4294967297,
            Name = "scale-set",
            RunnerGroupId = 4294967298,
            RunnerGroupName = "default",
            Labels = [new Label { Type = "System", Name = "linux" }],
            RunnerSetting = new RunnerSetting { DisableUpdate = true },
            RunnerJitConfigUrl = "https://actions.example/jit",
        };

        using var document = JsonDocument.Parse(ScaleSetJson.Serialize(value));
        var root = document.RootElement;

        Assert.Equal(4294967297, root.GetProperty("id").GetInt64());
        Assert.Equal(4294967298, root.GetProperty("runnerGroupId").GetInt64());
        Assert.Equal("scale-set", root.GetProperty("name").GetString());
        Assert.Equal("default", root.GetProperty("runnerGroupName").GetString());
        Assert.Equal("linux", root.GetProperty("labels")[0].GetProperty("name").GetString());
        Assert.True(root.GetProperty("RunnerSetting").GetProperty("disableUpdate").GetBoolean());
        Assert.Equal("https://actions.example/jit", root.GetProperty("runnerJitConfigUrl").GetString());
    }

    [Fact]
    public void SessionJsonOmitsEmptyOptionalFields()
    {
        var json = ScaleSetJson.Serialize(new RunnerScaleSetSession());

        Assert.Equal("{}", json);
    }

    [Fact]
    public void WireDtosKeepCaseSensitiveNamesAndEnvelopeNullFields()
    {
        var sessionJson = ScaleSetJson.Serialize(new RunnerScaleSetSession
        {
            MessageQueueAccessToken = "queue-token",
        });
        var jitJson = ScaleSetJson.Serialize(new RunnerScaleSetJitRunnerConfig
        {
            EncodedJitConfig = "encoded-jit",
        });
        var envelopeJson = ScaleSetJson.Serialize(new RunnerScaleSetMessageEnvelope
        {
            MessageId = 1,
            MessageType = "RunnerScaleSetJobMessages",
            Body = null,
            Statistics = null,
        });

        using var session = JsonDocument.Parse(sessionJson);
        using var jit = JsonDocument.Parse(jitJson);
        using var envelope = JsonDocument.Parse(envelopeJson);

        Assert.True(session.RootElement.TryGetProperty("messageQueueAccessToken", out _));
        Assert.False(session.RootElement.TryGetProperty("MessageQueueAccessToken", out _));
        Assert.Equal("encoded-jit", jit.RootElement.GetProperty("encodedJITConfig").GetString());
        Assert.False(jit.RootElement.TryGetProperty("encodedJitConfig", out _));
        Assert.Equal(JsonValueKind.Null, envelope.RootElement.GetProperty("body").ValueKind);
        Assert.Equal(JsonValueKind.Null, envelope.RootElement.GetProperty("statistics").ValueKind);
    }

    [Fact]
    public void DiagnosticToStringRedactsQueueTokenAndEncodedJitConfig()
    {
        const string queueSecret = "queue-token-sentinel";
        const string jitSecret = "jit-config-sentinel";
        var session = new RunnerScaleSetSession { MessageQueueAccessToken = queueSecret };
        var config = new RunnerScaleSetJitRunnerConfig { EncodedJitConfig = jitSecret };

        var sessionText = session.ToString();
        var configText = config.ToString();

        Assert.DoesNotContain(queueSecret, sessionText, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", sessionText, StringComparison.Ordinal);
        Assert.DoesNotContain(jitSecret, configText, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", configText, StringComparison.Ordinal);
    }
}
