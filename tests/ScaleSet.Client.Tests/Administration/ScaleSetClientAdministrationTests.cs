using System.Net;
using System.Text;
using System.Text.Json;
using ScaleSet.Client;
using ScaleSet.Client.Tests.Transport;
using Xunit;

namespace ScaleSet.Client.Tests.Administration;

public sealed class ScaleSetClientAdministrationTests
{
    private static readonly long[] ExpectedScaleSetIds = [5L, 6L];

    [Fact]
    public async Task FindScaleSetAsyncEncodesQueryAndReturnsNullForNoMatches()
    {
        using ClientFixture fixture = new(HttpStatusCode.OK, "{\"count\":0,\"value\":[]}");

        RunnerScaleSet? result = await fixture.Client.FindScaleSetAsync(
            0,
            "linux runners & tools",
            TestContext.Current.CancellationToken);

        Assert.Null(result);
        RecordedHttpRequest request = AssertAdminRequest(fixture, HttpMethod.Get, "/_apis/runtime/runnerscalesets");
        Dictionary<string, string> query = ParseQuery(request.Uri);
        Assert.Equal("0", query["runnerGroupId"]);
        Assert.Equal("linux runners & tools", query["name"]);
        Assert.Equal("6.0-preview", query["api-version"]);
    }

    [Fact]
    public async Task FindScaleSetAsyncReturnsTheSingleCountedValue()
    {
        RunnerScaleSet expected = new() { Id = 42, Name = "linux" };
        string body = ScaleSetJson.Serialize(new RunnerScaleSetList { Count = 1, Value = [expected] });
        using ClientFixture fixture = new(HttpStatusCode.OK, body);

        RunnerScaleSet? result = await fixture.Client.FindScaleSetAsync(4, "linux", TestContext.Current.CancellationToken);

        Assert.Equal(expected, result);
        _ = AssertAdminRequest(fixture, HttpMethod.Get, "/_apis/runtime/runnerscalesets");
    }

    [Fact]
    public async Task FindScaleSetAsyncRejectsMultipleOrInconsistentCountedValues()
    {
        string multipleMatches = ScaleSetJson.Serialize(new RunnerScaleSetList
        {
            Count = 2,
            Value = [new RunnerScaleSet { Id = 1 }, new RunnerScaleSet { Id = 2 }],
        });
        using ClientFixture multipleFixture = new(HttpStatusCode.OK, multipleMatches);

        await Assert.ThrowsAsync<ScaleSetApiException>(
            () => multipleFixture.Client.FindScaleSetAsync(1, "linux", TestContext.Current.CancellationToken));

        string inconsistentCount = ScaleSetJson.Serialize(new RunnerScaleSetList { Count = 1, Value = [] });
        using ClientFixture inconsistentFixture = new(HttpStatusCode.OK, inconsistentCount);

        await Assert.ThrowsAsync<ScaleSetApiException>(
            () => inconsistentFixture.Client.FindScaleSetAsync(1, "linux", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ListScaleSetsAsyncUsesOnlyTheRunnerGroupQueryAndNormalizesNullValueToEmpty()
    {
        using ClientFixture fixture = new(HttpStatusCode.OK, "{\"count\":0,\"value\":null}");

        IReadOnlyList<RunnerScaleSet> result = await fixture.Client.ListScaleSetsAsync(3, TestContext.Current.CancellationToken);

        Assert.Empty(result);
        RecordedHttpRequest request = AssertAdminRequest(fixture, HttpMethod.Get, "/_apis/runtime/runnerscalesets");
        Dictionary<string, string> query = ParseQuery(request.Uri);
        Assert.Equal("3", query["runnerGroupId"]);
        Assert.False(query.ContainsKey("name"));
        Assert.Equal("6.0-preview", query["api-version"]);
    }

    [Fact]
    public async Task ListScaleSetsAsyncDeserializesValueArray()
    {
        string body = ScaleSetJson.Serialize(new RunnerScaleSetList
        {
            Count = 2,
            Value = [new RunnerScaleSet { Id = 5 }, new RunnerScaleSet { Id = 6 }],
        });
        using ClientFixture fixture = new(HttpStatusCode.OK, body);

        IReadOnlyList<RunnerScaleSet> result = await fixture.Client.ListScaleSetsAsync(2, TestContext.Current.CancellationToken);

        Assert.Equal(ExpectedScaleSetIds, result.Select(scaleSet => scaleSet.Id));
    }

    [Fact]
    public async Task GetScaleSetAsyncPreservesJsonNullResult()
    {
        using ClientFixture fixture = new(HttpStatusCode.OK, "null");

        RunnerScaleSet? result = await fixture.Client.GetScaleSetAsync(15, TestContext.Current.CancellationToken);

        Assert.Null(result);
        _ = AssertAdminRequest(fixture, HttpMethod.Get, "/_apis/runtime/runnerscalesets/15");
    }

    [Fact]
    public async Task GetScaleSetAsyncReturnsTheWireModel()
    {
        using ClientFixture fixture = new(HttpStatusCode.OK, "{\"id\":15,\"name\":\"linux\"}");

        RunnerScaleSet? result = await fixture.Client.GetScaleSetAsync(15, TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Equal(15, result.Id);
        Assert.Equal("linux", result.Name);
    }

    [Fact]
    public async Task AdministrationIdsRemain64BitInAllResourcePaths()
    {
        const long largeId = 5_000_000_001L;

        using ClientFixture scaleSetFixture = new(HttpStatusCode.OK, "null");
        _ = await scaleSetFixture.Client.GetScaleSetAsync(largeId, TestContext.Current.CancellationToken);
        _ = AssertAdminRequest(scaleSetFixture, HttpMethod.Get, $"/_apis/runtime/runnerscalesets/{largeId}");

        using ClientFixture runnerFixture = new(HttpStatusCode.OK, "null");
        _ = await runnerFixture.Client.GetRunnerAsync(largeId, TestContext.Current.CancellationToken);
        _ = AssertAdminRequest(runnerFixture, HttpMethod.Get, $"/_apis/distributedtask/pools/0/agents/{largeId}");

        using ClientFixture jitFixture = new(HttpStatusCode.OK, "{}");
        _ = await jitFixture.Client.GenerateJitConfigAsync(
            largeId,
            new RunnerScaleSetJitRunnerSetting { Name = "runner", WorkFolder = "_work" },
            TestContext.Current.CancellationToken);
        _ = AssertAdminRequest(
            jitFixture,
            HttpMethod.Post,
            $"/_apis/runtime/runnerscalesets/{largeId}/generatejitconfig");

        using ClientFixture removeFixture = new(HttpStatusCode.NoContent, string.Empty);
        await removeFixture.Client.RemoveRunnerAsync(largeId, TestContext.Current.CancellationToken);
        _ = AssertAdminRequest(removeFixture, HttpMethod.Delete, $"/_apis/distributedtask/pools/0/agents/{largeId}");
    }

    [Fact]
    public async Task MalformedSuccessResponseRetainsCorrelationAndRedactsBodyPreview()
    {
        const string secretSentinel = "never-expose-this-secret";
        Dictionary<string, string> responseHeaders = new(StringComparer.OrdinalIgnoreCase)
        {
            ["ActivityId"] = "activity-123",
            ["X-GitHub-Request-Id"] = "request-456",
        };
        using ClientFixture fixture = new(
            HttpStatusCode.OK,
            $"{{\"token\":\"{secretSentinel}\",\"id\":\"not-a-number\"}}",
            responseHeaders);

        ScaleSetApiException exception = await Assert.ThrowsAsync<ScaleSetApiException>(
            () => fixture.Client.GetScaleSetAsync(18, TestContext.Current.CancellationToken));

        Assert.Equal((int)HttpStatusCode.OK, exception.StatusCode);
        Assert.Equal("GET", exception.Method);
        Assert.Equal("https://actions-fixture.actions.githubusercontent.com/_apis/runtime/runnerscalesets/18", exception.SafeUri);
        Assert.Equal("activity-123", exception.ActivityId);
        Assert.Equal("request-456", exception.GitHubRequestId);
        Assert.NotNull(exception.BodyPreview);
        Assert.DoesNotContain(secretSentinel, exception.BodyPreview);
    }

    [Fact]
    public async Task GetRunnerGroupByNameAsyncEncodesNameAndRequiresOneValue()
    {
        string body = ScaleSetJson.Serialize(new RunnerGroupList
        {
            Count = 1,
            Value = [new RunnerGroup { Id = 7, Name = "Build & Test" }],
        });
        using ClientFixture fixture = new(HttpStatusCode.OK, body);

        RunnerGroup result = await fixture.Client.GetRunnerGroupByNameAsync("Build & Test", TestContext.Current.CancellationToken);

        Assert.Equal(7, result.Id);
        Assert.Equal("Build & Test", result.Name);
        RecordedHttpRequest request = AssertAdminRequest(fixture, HttpMethod.Get, "/_apis/runtime/runnergroups/");
        Dictionary<string, string> query = ParseQuery(request.Uri);
        Assert.Equal("Build & Test", query["groupName"]);
        Assert.Equal("6.0-preview", query["api-version"]);
    }

    [Fact]
    public async Task GetRunnerGroupByNameAsyncRejectsZeroAndMultipleMatches()
    {
        using ClientFixture noMatchFixture = new(HttpStatusCode.OK, "{\"count\":0,\"value\":[]}");
        await Assert.ThrowsAsync<ScaleSetApiException>(
            () => noMatchFixture.Client.GetRunnerGroupByNameAsync("Build", TestContext.Current.CancellationToken));

        string multipleGroups = ScaleSetJson.Serialize(new RunnerGroupList
        {
            Count = 2,
            Value = [new RunnerGroup { Id = 1 }, new RunnerGroup { Id = 2 }],
        });
        using ClientFixture multipleFixture = new(HttpStatusCode.OK, multipleGroups);
        await Assert.ThrowsAsync<ScaleSetApiException>(
            () => multipleFixture.Client.GetRunnerGroupByNameAsync("Build", TestContext.Current.CancellationToken));

        using ClientFixture inconsistentFixture = new(HttpStatusCode.OK, "{\"count\":1,\"value\":[]}");
        await Assert.ThrowsAsync<ScaleSetApiException>(
            () => inconsistentFixture.Client.GetRunnerGroupByNameAsync("Build", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CreateScaleSetAsyncAddsNameLabelAndSystemTypeWithoutMutatingInput()
    {
        RunnerScaleSet input = new() { Name = "linux-runners", RunnerGroupId = 12, Labels = [] };
        using ClientFixture fixture = new(HttpStatusCode.OK, "{\"id\":99,\"name\":\"linux-runners\"}");

        RunnerScaleSet result = await fixture.Client.CreateScaleSetAsync(input, TestContext.Current.CancellationToken);

        Assert.Equal(99, result.Id);
        Assert.Empty(input.Labels!);
        RecordedHttpRequest request = AssertAdminRequest(fixture, HttpMethod.Post, "/_apis/runtime/runnerscalesets");
        Assert.Equal("application/json", request.ContentType);
        using JsonDocument document = JsonDocument.Parse(request.Body);
        JsonElement labels = document.RootElement.GetProperty("labels");
        Assert.Equal(1, labels.GetArrayLength());
        Assert.Equal("linux-runners", labels[0].GetProperty("name").GetString());
        Assert.Equal("System", labels[0].GetProperty("type").GetString());
    }

    [Fact]
    public async Task CreateScaleSetAsyncDefaultsSuppliedLabelTypesOnACopy()
    {
        Label inputLabel = new() { Name = "self-hosted", Type = string.Empty };
        RunnerScaleSet input = new() { Labels = [inputLabel] };
        using ClientFixture fixture = new(HttpStatusCode.OK, "{}");

        _ = await fixture.Client.CreateScaleSetAsync(input, TestContext.Current.CancellationToken);

        Assert.Equal(string.Empty, input.Labels![0].Type);
        RecordedHttpRequest request = AssertAdminRequest(fixture, HttpMethod.Post, "/_apis/runtime/runnerscalesets");
        using JsonDocument document = JsonDocument.Parse(request.Body);
        Assert.Equal("System", document.RootElement.GetProperty("labels")[0].GetProperty("type").GetString());
    }

    [Fact]
    public async Task CreateScaleSetAsyncAllowsMissingNameWhenLabelsAreSupplied()
    {
        RunnerScaleSet input = new() { Labels = [new Label { Name = "custom" }] };
        using ClientFixture fixture = new(HttpStatusCode.OK, "{}");

        _ = await fixture.Client.CreateScaleSetAsync(input, TestContext.Current.CancellationToken);

        RecordedHttpRequest request = AssertAdminRequest(fixture, HttpMethod.Post, "/_apis/runtime/runnerscalesets");
        using JsonDocument document = JsonDocument.Parse(request.Body);
        Assert.Equal("custom", document.RootElement.GetProperty("labels")[0].GetProperty("name").GetString());
    }

    [Fact]
    public async Task CreateScaleSetAsyncRequiresNameOnlyWhenLabelsAreMissing()
    {
        RunnerScaleSet input = new() { Labels = null };
        using ClientFixture fixture = new(HttpStatusCode.OK, "{}");

        await Assert.ThrowsAsync<ArgumentException>(
            () => fixture.Client.CreateScaleSetAsync(input, TestContext.Current.CancellationToken));

        Assert.Empty(fixture.Handler.Requests);
    }

    [Fact]
    public async Task UpdateScaleSetAsyncDefaultsOnlySuppliedLabelTypesAndDoesNotMutateInput()
    {
        Label inputLabel = new() { Name = "self-hosted", Type = string.Empty };
        RunnerScaleSet input = new() { Name = "linux-runners", Labels = [inputLabel] };
        using ClientFixture fixture = new(HttpStatusCode.OK, "{\"id\":99}");

        RunnerScaleSet result = await fixture.Client.UpdateScaleSetAsync(99, input, TestContext.Current.CancellationToken);

        Assert.Equal(99, result.Id);
        Assert.Equal(string.Empty, input.Labels![0].Type);
        RecordedHttpRequest request = AssertAdminRequest(fixture, HttpMethod.Patch, "/_apis/runtime/runnerscalesets/99");
        using JsonDocument document = JsonDocument.Parse(request.Body);
        Assert.Equal("System", document.RootElement.GetProperty("labels")[0].GetProperty("type").GetString());
    }

    [Fact]
    public async Task UpdateScaleSetAsyncDoesNotAddCreateOnlyNameLabel()
    {
        RunnerScaleSet input = new() { Name = "linux-runners", Labels = null };
        using ClientFixture fixture = new(HttpStatusCode.OK, "{}");

        _ = await fixture.Client.UpdateScaleSetAsync(99, input, TestContext.Current.CancellationToken);

        Assert.Null(input.Labels);
        RecordedHttpRequest request = AssertAdminRequest(fixture, HttpMethod.Patch, "/_apis/runtime/runnerscalesets/99");
        using JsonDocument document = JsonDocument.Parse(request.Body);
        Assert.False(document.RootElement.TryGetProperty("labels", out _));
    }

    [Fact]
    public async Task DeleteScaleSetAsyncRequiresExactNoContentStatus()
    {
        using ClientFixture successFixture = new(HttpStatusCode.NoContent, string.Empty);

        await successFixture.Client.DeleteScaleSetAsync(18, TestContext.Current.CancellationToken);

        _ = AssertAdminRequest(successFixture, HttpMethod.Delete, "/_apis/runtime/runnerscalesets/18");

        using ClientFixture failureFixture = new(HttpStatusCode.OK, string.Empty);
        await Assert.ThrowsAsync<ScaleSetApiException>(
            () => failureFixture.Client.DeleteScaleSetAsync(18, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GenerateJitConfigAsyncSendsNameAndWorkFolderAndRejectsNullResponse()
    {
        RunnerScaleSetJitRunnerSetting setting = new() { Name = "runner-7", WorkFolder = "_work" };
        using ClientFixture fixture = new(HttpStatusCode.OK, "null");

        ScaleSetApiException exception = await Assert.ThrowsAsync<ScaleSetApiException>(
            () => fixture.Client.GenerateJitConfigAsync(7, setting, TestContext.Current.CancellationToken));

        Assert.Equal((int)HttpStatusCode.OK, exception.StatusCode);
        RecordedHttpRequest request = AssertAdminRequest(fixture, HttpMethod.Post, "/_apis/runtime/runnerscalesets/7/generatejitconfig");
        Assert.Equal("application/json", request.ContentType);
        using JsonDocument document = JsonDocument.Parse(request.Body);
        Assert.Equal("runner-7", document.RootElement.GetProperty("name").GetString());
        Assert.Equal("_work", document.RootElement.GetProperty("workFolder").GetString());
    }

    [Fact]
    public async Task RequiredAdministrationEndpointsRejectJsonNullBodies()
    {
        using ClientFixture listFixture = new(HttpStatusCode.OK, "null");
        ScaleSetApiException listException = await Assert.ThrowsAsync<ScaleSetApiException>(
            () => listFixture.Client.ListScaleSetsAsync(1, TestContext.Current.CancellationToken));
        Assert.Equal((int)HttpStatusCode.OK, listException.StatusCode);

        using ClientFixture findSetFixture = new(HttpStatusCode.OK, "null");
        ScaleSetApiException findSetException = await Assert.ThrowsAsync<ScaleSetApiException>(
            () => findSetFixture.Client.FindScaleSetAsync(1, "linux", TestContext.Current.CancellationToken));
        Assert.Equal((int)HttpStatusCode.OK, findSetException.StatusCode);

        using ClientFixture createFixture = new(HttpStatusCode.OK, "null");
        ScaleSetApiException createException = await Assert.ThrowsAsync<ScaleSetApiException>(
            () => createFixture.Client.CreateScaleSetAsync(new RunnerScaleSet { Name = "linux" }, TestContext.Current.CancellationToken));
        Assert.Equal((int)HttpStatusCode.OK, createException.StatusCode);

        using ClientFixture updateFixture = new(HttpStatusCode.OK, "null");
        ScaleSetApiException updateException = await Assert.ThrowsAsync<ScaleSetApiException>(
            () => updateFixture.Client.UpdateScaleSetAsync(7, new RunnerScaleSet { Name = "linux" }, TestContext.Current.CancellationToken));
        Assert.Equal((int)HttpStatusCode.OK, updateException.StatusCode);

        using ClientFixture findRunnerFixture = new(HttpStatusCode.OK, "null");
        ScaleSetApiException findRunnerException = await Assert.ThrowsAsync<ScaleSetApiException>(
            () => findRunnerFixture.Client.FindRunnerByNameAsync("runner-1", TestContext.Current.CancellationToken));
        Assert.Equal((int)HttpStatusCode.OK, findRunnerException.StatusCode);

        using ClientFixture groupFixture = new(HttpStatusCode.OK, "null");
        ScaleSetApiException groupException = await Assert.ThrowsAsync<ScaleSetApiException>(
            () => groupFixture.Client.GetRunnerGroupByNameAsync("Default", TestContext.Current.CancellationToken));
        Assert.Equal((int)HttpStatusCode.OK, groupException.StatusCode);
    }

    [Fact]
    public async Task AdministrationDecodeRejectsTrailingJsonGarbage()
    {
        using ClientFixture fixture = new(HttpStatusCode.OK, "{\"id\":15}{\"id\":16}");

        ScaleSetApiException exception = await Assert.ThrowsAsync<ScaleSetApiException>(
            () => fixture.Client.GetScaleSetAsync(15, TestContext.Current.CancellationToken));

        Assert.Equal((int)HttpStatusCode.OK, exception.StatusCode);
    }

    [Fact]
    public async Task GenerateJitConfigAsyncDeserializesRunnerAndEncodedConfiguration()
    {
        RunnerScaleSetJitRunnerConfig expected = new()
        {
            Runner = new RunnerReference { Id = 5, Name = "runner-5", RunnerScaleSetId = 7 },
            EncodedJitConfig = "synthetic-jit-config",
        };
        using ClientFixture fixture = new(HttpStatusCode.OK, ScaleSetJson.Serialize(expected));

        RunnerScaleSetJitRunnerConfig? result = await fixture.Client.GenerateJitConfigAsync(
            7,
            new RunnerScaleSetJitRunnerSetting { Name = "runner-5", WorkFolder = "_work" },
            TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Equal("synthetic-jit-config", result.EncodedJitConfig);
        Assert.Equal(5, result.Runner!.Id);
    }

    [Fact]
    public async Task GetRunnerAsyncUsesRunnerCollectionAndPreservesJsonNull()
    {
        using ClientFixture fixture = new(HttpStatusCode.OK, "null");

        RunnerReference? result = await fixture.Client.GetRunnerAsync(31, TestContext.Current.CancellationToken);

        Assert.Null(result);
        _ = AssertAdminRequest(fixture, HttpMethod.Get, "/_apis/distributedtask/pools/0/agents/31");
    }

    [Fact]
    public async Task GetRunnerAsyncDeserializesRunnerReference()
    {
        using ClientFixture fixture = new(
            HttpStatusCode.OK,
            "{\"id\":31,\"name\":\"runner-31\",\"runnerScaleSetId\":7}");

        RunnerReference? result = await fixture.Client.GetRunnerAsync(31, TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Equal(31, result.Id);
        Assert.Equal("runner-31", result.Name);
        Assert.Equal(7, result.RunnerScaleSetId);
    }

    [Theory]
    [InlineData(7, """{"id":31,"name":"runner-31","status":"online","busy":false}""", true)]
    [InlineData(7, """{"id":31,"name":"runner-31","status":"online","busy":true}""", false)]
    [InlineData(7, """{"id":31,"name":"runner-31","status":"online"}""", false)]
    [InlineData(8, """{"id":31,"name":"runner-31","status":"online","busy":false}""", false)]
    public async Task IsRunnerIdleAsync_RequiresExactMembershipAndPositiveIdleEvidence(
        int membershipScaleSet, string availability, bool expected)
    {
        string membership = ScaleSetJson.Serialize(new RunnerReference
        {
            Id = 31, Name = "runner-31", RunnerScaleSetId = membershipScaleSet,
        });
        using ClientFixture fixture = new(HttpStatusCode.OK, membership, runnerAvailability: availability);

        bool idle = await fixture.Client.IsRunnerIdleAsync(7, 31, "runner-31", TestContext.Current.CancellationToken);

        Assert.Equal(expected, idle);
        var availabilityRequests = fixture.Handler.Requests.Where(request =>
            request.Uri.AbsolutePath.EndsWith("/actions/runners/31", StringComparison.Ordinal));
        if (membershipScaleSet == 7)
        {
            Assert.Equal("Bearer synthetic-pat", Assert.Single(availabilityRequests).Authorization);
        }
        else
        {
            Assert.Empty(availabilityRequests);
        }
    }

    [Theory]
    [InlineData("membership", HttpStatusCode.NotFound, false)]
    [InlineData("rest", HttpStatusCode.NotFound, false)]
    [InlineData("membership", HttpStatusCode.Forbidden, true)]
    [InlineData("rest", HttpStatusCode.Forbidden, true)]
    [InlineData("registration", HttpStatusCode.NotFound, true)]
    [InlineData("discovery", HttpStatusCode.NotFound, true)]
    public async Task IsRunnerIdleAsync_ReturnsFalseOnlyForRunnerLookupAbsence(
        string failingStage, HttpStatusCode status, bool throws)
    {
        using var handler = new RecordingHttpMessageHandler((message, _) =>
        {
            string path = message.RequestUri!.AbsolutePath;
            string stage = path switch
            {
                "/actions/runner-registration" => "discovery",
                var value when value.EndsWith("/registration-token", StringComparison.Ordinal) => "registration",
                var value when value.EndsWith("/agents/31", StringComparison.Ordinal) => "membership",
                var value when value.EndsWith("/actions/runners/31", StringComparison.Ordinal) => "rest",
                _ => throw new InvalidOperationException("Unexpected request."),
            };
            if (stage == failingStage)
            {
                return Task.FromResult(TestResponses.Json(status, "{}"));
            }

            return Task.FromResult(stage switch
            {
                "registration" => TestResponses.Json(HttpStatusCode.Created, """{"token":"synthetic-registration-token"}"""),
                "discovery" => TestResponses.Json(HttpStatusCode.OK,
                    $$"""{"url":"https://actions-fixture.actions.githubusercontent.com","token":"{{RecordingHandler.DiscoveryJwt}}"}"""),
                "membership" => TestResponses.Json(HttpStatusCode.OK, """{"id":31,"name":"runner-31","runnerScaleSetId":7}"""),
                _ => throw new InvalidOperationException("The test should stop at the prescribed failure."),
            });
        });
        using var http = new HttpClient(handler);
        using var client = new ScaleSetClient(GitHubTarget.Parse("https://github.com/example-org/example-repo"),
            ScaleSetCredentials.FromPersonalAccessToken("synthetic-pat"), httpClient: http);

        var idle = client.IsRunnerIdleAsync(7, 31, "runner-31", TestContext.Current.CancellationToken);
        if (throws)
        {
            Assert.Equal((int)status, (await Assert.ThrowsAsync<ScaleSetApiException>(() => idle)).StatusCode);
        }
        else
        {
            Assert.False(await idle);
        }

        Assert.Equal(failingStage == "rest" ? 1 : 0,
            handler.Requests.Count(call => call.Uri.AbsolutePath.EndsWith("/actions/runners/31", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task IsRunnerIdleAsync_PropagatesCancellationDuringRestLookup()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        var reachedRest = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = TestResponses.CreateConnectedHandler(DateTimeOffset.UtcNow.AddHours(1), async (message, caller) =>
        {
            if (message.RequestUri!.AbsolutePath.EndsWith("/agents/31", StringComparison.Ordinal))
            {
                return TestResponses.Json(HttpStatusCode.OK, """{"id":31,"name":"runner-31","runnerScaleSetId":7}""");
            }

            reachedRest.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, caller);
            throw new InvalidOperationException("Cancellation should stop the request.");
        });
        using var http = new HttpClient(handler);
        using var client = new ScaleSetClient(GitHubTarget.Parse("https://github.com/example-org/example-repo"),
            ScaleSetCredentials.FromPersonalAccessToken("synthetic-pat"), httpClient: http);

        var idle = client.IsRunnerIdleAsync(7, 31, "runner-31", cancellation.Token);
        await reachedRest.Task.WaitAsync(TimeSpan.FromSeconds(5), token);
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => idle);
        Assert.Single(handler.Requests, call => call.Uri.AbsolutePath.EndsWith("/actions/runners/31", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("https://github.com/org/repo", "https://pipelines.actions.githubusercontent.com/", true)]
    [InlineData("https://github.com/org/repo", "https://pipelines.actions.githubusercontent.com:8443/", false)]
    [InlineData("https://github.com/org/repo", "https://unexpected.example.test/", false)]
    [InlineData("https://tenant.ghe.com/org/repo", "https://pipelines.actions.tenant.ghe.com/", true)]
    [InlineData("https://tenant.ghe.com/org/repo", "https://pipelines.actions.other.ghe.com/", false)]
    [InlineData("https://tenant.ghe.com/org/repo", "https://pipelines.actions.githubusercontent.com/", false)]
    public void OwnedAndInjectedTransportsRequireTheSameActionsOrigin(string target, string service, bool expected)
    {
        GitHubTarget parsed = GitHubTarget.Parse(target);
        ScaleSetCredentials credentials = ScaleSetCredentials.FromPersonalAccessToken("synthetic-pat");
        using var ownedClient = new ScaleSetClient(parsed, credentials);
        using var transport = new HttpClient();
        using var injectedClient = new ScaleSetClient(parsed, credentials,
            new ScaleSetClientOptions { IsTrustedDiscoveredEndpoint = _ => true }, transport);
        Assert.Equal(expected, ownedClient.TryValidateDiscoveredEndpoint(service, out _));
        Assert.Equal(expected, injectedClient.TryValidateDiscoveredEndpoint(service, out _));
    }

    [Fact]
    public async Task FindRunnerByNameAsyncEncodesNameAndReturnsNullForNoMatches()
    {
        using ClientFixture fixture = new(HttpStatusCode.OK, "{\"count\":0,\"value\":[]}");

        RunnerReference? result = await fixture.Client.FindRunnerByNameAsync("runner & one", TestContext.Current.CancellationToken);

        Assert.Null(result);
        RecordedHttpRequest request = AssertAdminRequest(fixture, HttpMethod.Get, "/_apis/distributedtask/pools/0/agents");
        Dictionary<string, string> query = ParseQuery(request.Uri);
        Assert.Equal("runner & one", query["agentName"]);
        Assert.Equal("6.0-preview", query["api-version"]);
    }

    [Fact]
    public async Task FindRunnerByNameAsyncReturnsOneRunnerAndRejectsMultipleMatches()
    {
        RunnerReference expected = new() { Id = 12, Name = "runner-12", RunnerScaleSetId = 7 };
        string singleMatch = ScaleSetJson.Serialize(new RunnerReferenceList { Count = 1, Value = [expected] });
        using ClientFixture singleFixture = new(HttpStatusCode.OK, singleMatch);

        RunnerReference? result = await singleFixture.Client.FindRunnerByNameAsync("runner-12", TestContext.Current.CancellationToken);

        Assert.Equal(expected, result);

        string multipleMatches = ScaleSetJson.Serialize(new RunnerReferenceList
        {
            Count = 2,
            Value = [expected, new RunnerReference { Id = 13, Name = "runner-12" }],
        });
        using ClientFixture multipleFixture = new(HttpStatusCode.OK, multipleMatches);

        await Assert.ThrowsAsync<ScaleSetApiException>(
            () => multipleFixture.Client.FindRunnerByNameAsync("runner-12", TestContext.Current.CancellationToken));

        using ClientFixture inconsistentFixture = new(HttpStatusCode.OK, "{\"count\":1,\"value\":[]}");
        await Assert.ThrowsAsync<ScaleSetApiException>(
            () => inconsistentFixture.Client.FindRunnerByNameAsync("runner-12", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RemoveRunnerAsyncRequiresExactNoContentAndPreservesDomainClassification()
    {
        using ClientFixture successFixture = new(HttpStatusCode.NoContent, string.Empty);

        await successFixture.Client.RemoveRunnerAsync(22, TestContext.Current.CancellationToken);

        _ = AssertAdminRequest(successFixture, HttpMethod.Delete, "/_apis/distributedtask/pools/0/agents/22");

        using ClientFixture busyFixture = new(
            HttpStatusCode.Conflict,
            "{\"typeName\":\"Microsoft.VisualStudio.Services.AgentPool.JobStillRunningException\",\"message\":\"busy\"}");
        ScaleSetApiException busyException = await Assert.ThrowsAsync<ScaleSetApiException>(
            () => busyFixture.Client.RemoveRunnerAsync(22, TestContext.Current.CancellationToken));
        Assert.Equal(ScaleSetDomainError.JobStillRunning, busyException.DomainError);

        using ClientFixture missingFixture = new(
            HttpStatusCode.NotFound,
            "{\"typeName\":\"Microsoft.VisualStudio.Services.AgentPool.AgentNotFoundException\",\"message\":\"missing\"}");
        ScaleSetApiException missingException = await Assert.ThrowsAsync<ScaleSetApiException>(
            () => missingFixture.Client.RemoveRunnerAsync(22, TestContext.Current.CancellationToken));
        Assert.Equal(ScaleSetDomainError.AgentNotFound, missingException.DomainError);
    }

    [Fact]
    public async Task ResourceIdsMustBePositiveAndGroupZeroIsAllowed()
    {
        using ClientFixture fixture = new(HttpStatusCode.OK, "{\"count\":0,\"value\":[]}");

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => fixture.Client.GetScaleSetAsync(0, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => fixture.Client.UpdateScaleSetAsync(0, new RunnerScaleSet(), TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => fixture.Client.DeleteScaleSetAsync(0, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => fixture.Client.GenerateJitConfigAsync(
            0,
            new RunnerScaleSetJitRunnerSetting { Name = "runner", WorkFolder = "_work" },
            TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => fixture.Client.GetRunnerAsync(-1, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => fixture.Client.RemoveRunnerAsync(0, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => fixture.Client.FindScaleSetAsync(-1, "linux", TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => fixture.Client.ListScaleSetsAsync(-1, TestContext.Current.CancellationToken));

        Assert.Empty(fixture.Handler.Requests);
    }

    private static RecordedHttpRequest AssertAdminRequest(ClientFixture fixture, HttpMethod method, string path)
    {
        RecordedHttpRequest request = fixture.Handler.AdminRequests.Single();
        Assert.Equal(method, request.Method);
        Assert.Equal(path, request.Uri.AbsolutePath);
        Assert.Equal($"Bearer {RecordingHandler.DiscoveryJwt}", request.Authorization);
        Assert.Equal("6.0-preview", ParseQuery(request.Uri)["api-version"]);
        return request;
    }

    private static Dictionary<string, string> ParseQuery(Uri uri)
    {
        Dictionary<string, string> result = new(StringComparer.Ordinal);
        string query = uri.Query.TrimStart('?');
        if (query.Length == 0)
        {
            return result;
        }

        foreach (string pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] parts = pair.Split('=', 2);
            string key = WebUtility.UrlDecode(parts[0]) ?? string.Empty;
            result[key] = parts.Length == 2 ? WebUtility.UrlDecode(parts[1]) ?? string.Empty : string.Empty;
        }

        return result;
    }

    private sealed class ClientFixture : IDisposable
    {
        private readonly HttpClient _httpClient;

        public ClientFixture(
            HttpStatusCode responseStatus,
            string responseBody,
            IReadOnlyDictionary<string, string>? responseHeaders = null,
            string? runnerAvailability = null)
        {
            Handler = new RecordingHandler(responseStatus, responseBody, responseHeaders, runnerAvailability);
            _httpClient = new HttpClient(Handler);
            Client = new ScaleSetClient(
                GitHubTarget.Parse("https://github.com/example-org/example-repo"),
                ScaleSetCredentials.FromPersonalAccessToken("synthetic-pat"),
                httpClient: _httpClient);
        }

        public RecordingHandler Handler { get; }

        public ScaleSetClient Client { get; }

        public void Dispose()
        {
            Client.Dispose();
            _httpClient.Dispose();
        }
    }

    private sealed class RecordingHandler(
        HttpStatusCode responseStatus,
        string responseBody,
        IReadOnlyDictionary<string, string>? responseHeaders,
        string? runnerAvailability) : HttpMessageHandler
    {
        public const string DiscoveryJwt = "eyJhbGciOiJub25lIiwidHlwIjoiSldUIn0.eyJleHAiOjQxMDI0NDQ4MDB9.";

        private const string ActionsServiceHost = "actions-fixture.actions.githubusercontent.com";

        private readonly List<RecordedHttpRequest> _requests = [];

        public IReadOnlyList<RecordedHttpRequest> Requests => _requests;

        public IEnumerable<RecordedHttpRequest> AdminRequests => _requests.Where(request =>
            string.Equals(request.Uri.Host, ActionsServiceHost, StringComparison.OrdinalIgnoreCase));

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Uri requestUri = request.RequestUri ?? throw new InvalidOperationException("Request URI is missing.");
            string body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            _requests.Add(new RecordedHttpRequest(
                request.Method,
                requestUri,
                request.Headers.Authorization?.ToString(),
                request.Content?.Headers.ContentType?.MediaType,
                body));

            if (string.Equals(requestUri.Host, "api.github.com", StringComparison.OrdinalIgnoreCase)
                && requestUri.AbsolutePath.EndsWith("/actions/runners/registration-token", StringComparison.Ordinal))
            {
                return JsonResponse(HttpStatusCode.Created, "{\"token\":\"synthetic-registration-token\",\"expires_at\":\"2099-01-01T00:00:00Z\"}");
            }

            if (string.Equals(requestUri.Host, "api.github.com", StringComparison.OrdinalIgnoreCase)
                && string.Equals(requestUri.AbsolutePath, "/actions/runner-registration", StringComparison.Ordinal))
            {
                return JsonResponse(
                    HttpStatusCode.OK,
                    $"{{\"url\":\"https://{ActionsServiceHost}\",\"token\":\"{DiscoveryJwt}\"}}");
            }

            if (requestUri.Host == "api.github.com" && runnerAvailability is not null)
            {
                return JsonResponse(HttpStatusCode.OK, runnerAvailability);
            }

            return JsonResponse(responseStatus, responseBody, responseHeaders);
        }

        private static HttpResponseMessage JsonResponse(
            HttpStatusCode statusCode,
            string body,
            IReadOnlyDictionary<string, string>? headers = null)
        {
            HttpResponseMessage response = new(statusCode)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };

            if (headers is not null)
            {
                foreach (KeyValuePair<string, string> header in headers)
                {
                    response.Headers.TryAddWithoutValidation(header.Key, header.Value);
                }
            }

            return response;
        }
    }

    private sealed record RecordedHttpRequest(
        HttpMethod Method,
        Uri Uri,
        string? Authorization,
        string? ContentType,
        string Body);
}
