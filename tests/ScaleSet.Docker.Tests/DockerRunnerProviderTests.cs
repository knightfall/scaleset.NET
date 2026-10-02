using System.Collections.Immutable;
using System.Net;
using System.Text.Json;
using ScaleSet.Hosting.Configuration;
using ScaleSet.Hosting.Providers;
using ScaleSet.Providers.Docker;
using Xunit;

namespace ScaleSet.Docker.Tests;

public sealed class DockerRunnerProviderTests
{
    private static readonly string[] SleepCommand = ["/bin/sh", "-c", "sleep 30"];
    private static readonly string[] SyntheticJitEnvironment = ["ACTIONS_RUNNER_INPUT_JITCONFIG=synthetic-jit"];
    private static readonly string[] HostSocketBind = ["/custom/docker.sock:/var/run/docker.sock"];

    [Fact]
    public void DockerProviderOptions_UseAcceptedControlAndImagePullBudgets()
    {
        DockerProviderOptions options = new();

        Assert.Equal(TimeSpan.FromSeconds(60), options.RequestTimeout);
        Assert.Equal(TimeSpan.FromMinutes(15), options.ImagePullTimeout);
    }

    [Fact]
    public async Task DiscoverAsync_NegotiatesAndUsesHighestImplementedApi()
    {
        using DockerProviderTestContext context = new(new DockerTestHttpMessageHandler(
            (request, _) => Task.FromResult(
                request.Uri.AbsolutePath == "/v1.47/containers/json"
                    ? DockerTestHttpMessageHandler.Json(HttpStatusCode.OK, "[]")
                    : DockerTestHttpMessageHandler.Json(HttpStatusCode.NotFound, "{}"))));

        ImmutableArray<RunnerResource> resources = await context.Provider.DiscoverAsync(
            DockerProviderTestContext.CreateScope(),
            TestContext.Current.CancellationToken);

        Assert.Empty(resources);
        Assert.Contains(context.Handler.Requests, request => request.Uri.AbsolutePath == "/v1.47/info");
        Assert.Contains(context.Handler.Requests, request => request.Uri.AbsolutePath == "/v1.47/containers/json");
        Assert.DoesNotContain(context.Handler.Requests, request => request.Uri.AbsolutePath.Contains("v1.56", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("1.39", "1.24")]
    [InlineData("1.56", "1.48")]
    [InlineData("1.9", "1.8")]
    public async Task DiscoverAsync_RejectsUnsupportedApiRangesAndComparesVersionsNumerically(
        string serverMaximum,
        string serverMinimum)
    {
        using DockerProviderTestContext context = new(new DockerTestHttpMessageHandler(
            apiVersion: serverMaximum,
            minimumApiVersion: serverMinimum));

        DockerProviderException exception = await Assert.ThrowsAsync<DockerProviderException>(
            () => context.Provider.DiscoverAsync(
                DockerProviderTestContext.CreateScope(),
                TestContext.Current.CancellationToken));

        Assert.Equal("unsupported-api-version", exception.FailureCode);
        Assert.DoesNotContain(serverMaximum, exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("windows", "linux")]
    [InlineData("linux", "windows")]
    public async Task DiscoverAsync_RejectsNonLinuxDaemons(string daemonOs, string infoOs)
    {
        using DockerProviderTestContext context = new(new DockerTestHttpMessageHandler(
            daemonOs: daemonOs,
            infoOs: infoOs));

        DockerProviderException exception = await Assert.ThrowsAsync<DockerProviderException>(
            () => context.Provider.DiscoverAsync(
                DockerProviderTestContext.CreateScope(),
                TestContext.Current.CancellationToken));

        Assert.Equal("non-linux-daemon", exception.FailureCode);
    }

    [Fact]
    public async Task DiscoverAsync_FiltersExactOwnerAndInspectsStoppedContainers()
    {
        RunnerIdentity identity = DockerProviderTestContext.CreateIdentity();
        Dictionary<string, string> ownedLabels = DockerProviderTestContext.CreateLabels(identity, labelRunnerId: 83);
        Dictionary<string, string> foreignLabels = DockerProviderTestContext.CreateLabels(identity);
        foreignLabels[DockerProviderLabels.ControllerId] = "controller-b";
        string ownedId = new('b', 64);
        string foreignId = new('c', 64);
        using DockerProviderTestContext context = new(new DockerTestHttpMessageHandler(
            (request, _) => Task.FromResult(request.Uri.AbsolutePath switch
            {
                "/v1.47/containers/json" => DockerTestHttpMessageHandler.Json(
                    HttpStatusCode.OK,
                    DockerProviderTestContext.CreateListJson((ownedId, ownedLabels), (foreignId, foreignLabels))),
                var path when path == $"/v1.47/containers/{ownedId}/json" => DockerTestHttpMessageHandler.Json(
                    HttpStatusCode.OK,
                    DockerProviderTestContext.CreateInspectJson(
                        identity,
                        state: "exited",
                        exitCode: 17,
                        startedAt: "2026-10-01T03:04:05Z",
                        finishedAt: "2026-10-01T04:05:06Z",
                        id: ownedId,
                        labelRunnerId: 83)),
                _ => DockerTestHttpMessageHandler.Json(HttpStatusCode.NotFound, "{}"),
            })));

        ImmutableArray<RunnerResource> resources = await context.Provider.DiscoverAsync(
            identity.Scope,
            TestContext.Current.CancellationToken);

        RunnerResource resource = Assert.Single(resources);
        Assert.Equal(ownedId, resource.ProviderResourceId);
        Assert.Equal(RunnerResourceStatus.Exited, resource.Status);
        Assert.Equal(17, resource.ExitCode);
        Assert.Equal(83L, resource.Identity.GitHubRunnerId);
        Assert.Equal(new DateTimeOffset(2026, 10, 1, 3, 4, 5, TimeSpan.Zero), resource.StartedAt);
        Assert.Equal(new DateTimeOffset(2026, 10, 1, 4, 5, 6, TimeSpan.Zero), resource.FinishedAt);

        RecordedDockerRequest listRequest = Assert.Single(
            context.Handler.Requests,
            request => request.Uri.AbsolutePath == "/v1.47/containers/json");
        Assert.Contains("all=1", listRequest.Uri.Query, StringComparison.Ordinal);
        Dictionary<string, string[]> filters = ReadContainerFilters(listRequest.Uri);
        Assert.Contains($"{DockerProviderLabels.Product}=scaleset.net", filters["label"]);
        Assert.Contains($"{DockerProviderLabels.ControllerId}=controller-a", filters["label"]);
        Assert.Contains($"{DockerProviderLabels.ScaleSetKey}=production", filters["label"]);
        Assert.Contains($"{DockerProviderLabels.ScaleSetId}=42", filters["label"]);
        Assert.DoesNotContain(context.Handler.Requests, request => request.Uri.AbsolutePath.Contains(foreignId, StringComparison.Ordinal));
    }

    [Fact]
    public async Task DiscoverAsync_RejectsMalformedIdentityLabelsForOwnedResource()
    {
        RunnerIdentity identity = DockerProviderTestContext.CreateIdentity();
        Dictionary<string, string> labels = DockerProviderTestContext.CreateLabels(identity);
        labels.Remove(DockerProviderLabels.OperationId);
        string containerId = new('d', 64);
        using DockerProviderTestContext context = new(new DockerTestHttpMessageHandler(
            (request, _) => Task.FromResult(request.Uri.AbsolutePath == "/v1.47/containers/json"
                ? DockerTestHttpMessageHandler.Json(HttpStatusCode.OK, DockerProviderTestContext.CreateListJson((containerId, labels)))
                : DockerTestHttpMessageHandler.Json(HttpStatusCode.NotFound, "{}"))));

        DockerProviderException exception = await Assert.ThrowsAsync<DockerProviderException>(
            () => context.Provider.DiscoverAsync(identity.Scope, TestContext.Current.CancellationToken));

        Assert.Equal("invalid-owned-container-labels", exception.FailureCode);
        Assert.DoesNotContain(context.Handler.Requests, request =>
            request.Uri.AbsolutePath.StartsWith("/v1.47/containers/", StringComparison.Ordinal)
            && request.Uri.AbsolutePath.EndsWith("/json", StringComparison.Ordinal)
            && request.Uri.AbsolutePath != "/v1.47/containers/json");
    }

    [Fact]
    public async Task DiscoverAsync_ResourceRemovedBeforeInspectReturnsMissingEvidence()
    {
        RunnerIdentity identity = DockerProviderTestContext.CreateIdentity();
        string containerId = new('d', 64);
        using DockerProviderTestContext context = new(new DockerTestHttpMessageHandler(
            (request, _) => Task.FromResult(request.Uri.AbsolutePath switch
            {
                "/v1.47/containers/json" => DockerTestHttpMessageHandler.Json(
                    HttpStatusCode.OK,
                    DockerProviderTestContext.CreateListJson((containerId, DockerProviderTestContext.CreateLabels(identity)))),
                var path when path == $"/v1.47/containers/{containerId}/json" => DockerTestHttpMessageHandler.Json(HttpStatusCode.NotFound, "{}"),
                _ => DockerTestHttpMessageHandler.Json(HttpStatusCode.NotFound, "{}"),
            })));

        RunnerResource resource = Assert.Single(await context.Provider.DiscoverAsync(
            identity.Scope,
            TestContext.Current.CancellationToken));

        Assert.Equal(containerId, resource.ProviderResourceId);
        Assert.Equal(identity.OperationId, resource.Identity.OperationId);
        Assert.Equal(RunnerResourceStatus.Missing, resource.Status);
    }

    [Fact]
    public async Task InspectAsync_ExpectedIdentityCanBeEnrichedFromLabel()
    {
        RunnerIdentity identity = DockerProviderTestContext.CreateIdentity();
        using DockerProviderTestContext context = new(new DockerTestHttpMessageHandler(
            (request, _) => Task.FromResult(request.Uri.AbsolutePath.EndsWith("/json", StringComparison.Ordinal)
                ? DockerTestHttpMessageHandler.Json(HttpStatusCode.OK, DockerProviderTestContext.CreateInspectJson(identity, labelRunnerId: 208))
                : DockerTestHttpMessageHandler.Json(HttpStatusCode.NotFound, "{}"))));

        RunnerResource? inspected = await context.Provider.InspectAsync(
            DockerProviderTestContext.CreateResource(),
            TestContext.Current.CancellationToken);

        RunnerResource actual = inspected ?? throw new InvalidOperationException("The container was not returned.");
        Assert.Equal(208L, actual.Identity.GitHubRunnerId);
        Assert.Equal(identity.OperationId, actual.Identity.OperationId);
        Assert.Equal(identity.RunnerName, actual.Identity.RunnerName);
    }

    [Fact]
    public async Task InspectAsync_KnownGitHubRunnerIdMustMatchOwnedLabel()
    {
        RunnerIdentity expected = DockerProviderTestContext.CreateIdentity(111);
        string containerId = new('d', 64);
        using DockerProviderTestContext context = new(new DockerTestHttpMessageHandler(
            (request, _) => Task.FromResult(request.Uri.AbsolutePath.EndsWith("/json", StringComparison.Ordinal)
                ? DockerTestHttpMessageHandler.Json(HttpStatusCode.OK, DockerProviderTestContext.CreateInspectJson(
                    expected,
                    id: containerId,
                    labelRunnerId: 112))
                : DockerTestHttpMessageHandler.Json(HttpStatusCode.NotFound, "{}"))));

        DockerProviderException exception = await Assert.ThrowsAsync<DockerProviderException>(
            () => context.Provider.InspectAsync(
                new RunnerResource(containerId, expected, RunnerResourceStatus.Unknown),
                TestContext.Current.CancellationToken));

        Assert.Equal("resource-ownership-mismatch", exception.FailureCode);
    }

    [Fact]
    public async Task CreateAsync_UsesDeterministicNameExactLabelsAndSafeBody()
    {
        const string jit = "synthetic-jit-secret";
        RunnerIdentity identity = DockerProviderTestContext.CreateIdentity(91);
        RunnerProfileOptions profile = DockerProviderTestContext.CreateProfile();
        profile.Network = "bridge";
        int createCount = 0;
        string containerId = new('e', 64);
        using DockerProviderTestContext context = new(new DockerTestHttpMessageHandler(
            (request, _) => Task.FromResult(request.Uri.AbsolutePath switch
            {
                var path when path.Contains("/images/", StringComparison.Ordinal) && path.EndsWith("/json", StringComparison.Ordinal) =>
                    DockerTestHttpMessageHandler.Json(HttpStatusCode.OK, "{\"Id\":\"sha256:test\"}"),
                "/v1.47/containers/create" when Interlocked.Increment(ref createCount) == 1 =>
                    DockerTestHttpMessageHandler.Json(HttpStatusCode.Created, $"{{\"Id\":\"{containerId}\"}}"),
                "/v1.47/containers/create" => DockerTestHttpMessageHandler.Json(HttpStatusCode.Conflict, "{}"),
                var path when path.StartsWith("/v1.47/containers/", StringComparison.Ordinal) && path.EndsWith("/json", StringComparison.Ordinal) =>
                    DockerTestHttpMessageHandler.Json(HttpStatusCode.OK, DockerProviderTestContext.CreateInspectJson(identity, id: containerId)),
                _ => DockerTestHttpMessageHandler.Json(HttpStatusCode.NotFound, "{}"),
            })),
            profile: profile);

        RunnerResource first = await context.Provider.CreateAsync(
            new RunnerCreateRequest(identity, jit),
            TestContext.Current.CancellationToken);
        RunnerResource second = await context.Provider.CreateAsync(
            new RunnerCreateRequest(identity, jit),
            TestContext.Current.CancellationToken);

        Assert.Equal(RunnerResourceStatus.Created, first.Status);
        Assert.Equal(RunnerResourceStatus.Created, second.Status);
        Assert.Equal(containerId, first.ProviderResourceId);
        Assert.Equal(containerId, second.ProviderResourceId);
        Assert.Equal(2, createCount);
        Assert.DoesNotContain(context.Handler.Requests, request => request.Method == "POST" && request.Uri.AbsolutePath.EndsWith("/start", StringComparison.Ordinal));

        RecordedDockerRequest[] createRequests = context.Handler.Requests
            .Where(request => request.Method == "POST" && request.Uri.AbsolutePath == "/v1.47/containers/create")
            .ToArray();
        Assert.Equal(2, createRequests.Length);
        string firstName = ReadQueryValue(createRequests[0].Uri, "name");
        string secondName = ReadQueryValue(createRequests[1].Uri, "name");
        Assert.Equal(firstName, secondName);
        Assert.Contains("platform=linux%2Farm64", createRequests[0].Uri.Query, StringComparison.OrdinalIgnoreCase);

        using JsonDocument body = JsonDocument.Parse(createRequests[0].Body);
        JsonElement root = body.RootElement;
        Assert.Equal("ghcr.io/example/runner:2.324.0", root.GetProperty("Image").GetString());
        Assert.Equal("runner", root.GetProperty("User").GetString());
        Assert.Equal("/home/runner/run.sh", root.GetProperty("Cmd")[0].GetString());
        string[] environment = root.GetProperty("Env").EnumerateArray().Select(static item => item.GetString()!).ToArray();
        Assert.Equal(new[] { $"ACTIONS_RUNNER_INPUT_JITCONFIG={jit}" }, environment);
        JsonElement labels = root.GetProperty("Labels");
        Assert.Equal("scaleset.net", labels.GetProperty(DockerProviderLabels.Product).GetString());
        Assert.Equal("controller-a", labels.GetProperty(DockerProviderLabels.ControllerId).GetString());
        Assert.Equal("production", labels.GetProperty(DockerProviderLabels.ScaleSetKey).GetString());
        Assert.Equal("42", labels.GetProperty(DockerProviderLabels.ScaleSetId).GetString());
        Assert.Equal(identity.OperationId.ToString("D"), labels.GetProperty(DockerProviderLabels.OperationId).GetString());
        Assert.Equal("runner-17", labels.GetProperty(DockerProviderLabels.RunnerName).GetString());
        Assert.Equal("91", labels.GetProperty(DockerProviderLabels.GitHubRunnerId).GetString());
        JsonElement host = root.GetProperty("HostConfig");
        Assert.Equal(1_500_000_000L, host.GetProperty("NanoCpus").GetInt64());
        Assert.Equal(268_435_456L, host.GetProperty("Memory").GetInt64());
        Assert.False(host.GetProperty("Privileged").GetBoolean());
        Assert.False(host.GetProperty("AutoRemove").GetBoolean());
        Assert.Equal("bridge", host.GetProperty("NetworkMode").GetString());
        Assert.False(root.TryGetProperty("WorkingDir", out _));
        Assert.False(host.TryGetProperty("RestartPolicy", out _));
        Assert.DoesNotContain("github-app", createRequests[0].Body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("personal-access-token", createRequests[0].Body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(jit, new RunnerCreateRequest(identity, jit).ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task CreateAsync_DoesNotReplayCreateAfterTransportFailure()
    {
        const string jit = "jit-that-must-not-appear-in-errors";
        int createCount = 0;
        using DockerProviderTestContext context = new(new DockerTestHttpMessageHandler(
            (request, _) =>
            {
                if (request.Uri.AbsolutePath.Contains("/images/", StringComparison.Ordinal))
                {
                    return Task.FromResult(DockerTestHttpMessageHandler.Json(HttpStatusCode.OK, "{\"Id\":\"sha256:test\"}"));
                }

                if (request.Uri.AbsolutePath == "/v1.47/containers/create")
                {
                    Interlocked.Increment(ref createCount);
                    return Task.FromException<HttpResponseMessage>(new HttpRequestException("synthetic transport failure"));
                }

                return Task.FromResult(DockerTestHttpMessageHandler.Json(HttpStatusCode.NotFound, "{}"));
            }));

        DockerProviderException exception = await Assert.ThrowsAsync<DockerProviderException>(
            () => context.Provider.CreateAsync(
                new RunnerCreateRequest(DockerProviderTestContext.CreateIdentity(), jit),
                TestContext.Current.CancellationToken));

        Assert.Equal(1, createCount);
        Assert.True(exception.OutcomeMayBeUnknown);
        Assert.Equal("engine-unavailable", exception.FailureCode);
        Assert.DoesNotContain(jit, exception.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("synthetic transport failure", exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task CreateAsync_PullsMissingPinnedImageWithBoundedProgressStream()
    {
        int imageInspections = 0;
        const string jit = "synthetic-jit";
        using DockerProviderTestContext context = new(new DockerTestHttpMessageHandler(
            (request, _) =>
            {
                string path = request.Uri.AbsolutePath;
                if (path.Contains("/images/", StringComparison.Ordinal) && path.EndsWith("/json", StringComparison.Ordinal))
                {
                    int count = Interlocked.Increment(ref imageInspections);
                    return Task.FromResult(DockerTestHttpMessageHandler.Json(
                        count == 1 ? HttpStatusCode.NotFound : HttpStatusCode.OK,
                        count == 1 ? "{}" : "{\"Id\":\"sha256:pinned\"}"));
                }

                if (path == "/v1.47/images/create")
                {
                    return Task.FromResult(DockerTestHttpMessageHandler.Json(HttpStatusCode.OK, "{\"status\":\"Pull complete\"}\n"));
                }

                if (path == "/v1.47/containers/create")
                {
                    return Task.FromResult(DockerTestHttpMessageHandler.Json(HttpStatusCode.Created, $"{{\"Id\":\"{new string('f', 64)}\"}}"));
                }

                return Task.FromResult(DockerTestHttpMessageHandler.Json(HttpStatusCode.NotFound, "{}"));
            }));

        RunnerResource created = await context.Provider.CreateAsync(
            new RunnerCreateRequest(DockerProviderTestContext.CreateIdentity(), jit),
            TestContext.Current.CancellationToken);

        Assert.Equal(RunnerResourceStatus.Created, created.Status);
        RecordedDockerRequest pullRequest = Assert.Single(
            context.Handler.Requests,
            request => request.Method == "POST" && request.Uri.AbsolutePath == "/v1.47/images/create");
        Assert.Contains("fromImage=ghcr.io%2Fexample%2Frunner%3A2.324.0", pullRequest.Uri.Query, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("platform=linux%2Farm64", pullRequest.Uri.Query, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, imageInspections);
    }

    [Fact]
    public async Task CreateAsync_DoesNotPullMissingPinnedImageWhenDisabled()
    {
        DockerProviderOptions options = new() { PullImageIfMissing = false };
        using DockerProviderTestContext context = new(new DockerTestHttpMessageHandler(
            (request, _) => Task.FromResult(request.Uri.AbsolutePath.Contains("/images/", StringComparison.Ordinal)
                ? DockerTestHttpMessageHandler.Json(HttpStatusCode.NotFound, "{}")
                : DockerTestHttpMessageHandler.Json(HttpStatusCode.NotFound, "{}"))),
            options: options);

        DockerProviderException exception = await Assert.ThrowsAsync<DockerProviderException>(
            () => context.Provider.CreateAsync(new RunnerCreateRequest(
                DockerProviderTestContext.CreateIdentity(),
                "synthetic-jit"),
                TestContext.Current.CancellationToken));

        Assert.Equal("pinned-image-not-available", exception.FailureCode);
        Assert.DoesNotContain(context.Handler.Requests, request => request.Method == "POST");
    }

    [Fact]
    public async Task CreateAsync_PullProgressErrorsDoNotExposeResponseText()
    {
        const string privatePullError = "private-registry-response-secret";
        using DockerProviderTestContext context = new(new DockerTestHttpMessageHandler(
            (request, _) => Task.FromResult(request.Uri.AbsolutePath switch
            {
                var path when path.Contains("/images/", StringComparison.Ordinal) && path.EndsWith("/json", StringComparison.Ordinal) =>
                    DockerTestHttpMessageHandler.Json(HttpStatusCode.NotFound, "{}"),
                "/v1.47/images/create" => DockerTestHttpMessageHandler.Json(
                    HttpStatusCode.OK,
                    $"{{\"error\":\"{privatePullError}\"}}\n"),
                _ => DockerTestHttpMessageHandler.Json(HttpStatusCode.NotFound, "{}"),
            })));

        DockerProviderException exception = await Assert.ThrowsAsync<DockerProviderException>(
            () => context.Provider.CreateAsync(new RunnerCreateRequest(
                DockerProviderTestContext.CreateIdentity(),
                "synthetic-jit"),
                TestContext.Current.CancellationToken));

        Assert.Equal("image-pull-failed", exception.FailureCode);
        Assert.DoesNotContain(privatePullError, exception.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("synthetic-jit", exception.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(context.Handler.Requests, request => request.Uri.AbsolutePath == "/v1.47/containers/create");
    }

    [Fact]
    public async Task CreateAsync_RejectsOversizedPullProgressRecordWithoutEchoingIt()
    {
        DockerProviderOptions options = new() { MaximumPullLineBytes = 1024 };
        string largeResponse = $"{{\"status\":\"{new string('s', 2048)}\"}}\n";
        using DockerProviderTestContext context = new(new DockerTestHttpMessageHandler(
            (request, _) => Task.FromResult(request.Uri.AbsolutePath switch
            {
                var path when path.Contains("/images/", StringComparison.Ordinal) && path.EndsWith("/json", StringComparison.Ordinal) =>
                    DockerTestHttpMessageHandler.Json(HttpStatusCode.NotFound, "{}"),
                "/v1.47/images/create" => DockerTestHttpMessageHandler.Json(HttpStatusCode.OK, largeResponse),
                _ => DockerTestHttpMessageHandler.Json(HttpStatusCode.NotFound, "{}"),
            })),
            options: options);

        DockerProviderException exception = await Assert.ThrowsAsync<DockerProviderException>(
            () => context.Provider.CreateAsync(new RunnerCreateRequest(
                DockerProviderTestContext.CreateIdentity(),
                "synthetic-jit"),
                TestContext.Current.CancellationToken));

        Assert.Equal("image-pull-stream-failed", exception.FailureCode);
        Assert.DoesNotContain(new string('s', 128), exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task CreateAsync_RejectsOversizedTotalPullStream()
    {
        DockerProviderOptions options = new() { MaximumPullResponseBytes = 1024 * 1024 };
        string progressLine = $"{{\"status\":\"{new string('p', 60_000)}\"}}\n";
        string largeResponse = string.Concat(Enumerable.Repeat(progressLine, 18));
        using DockerProviderTestContext context = new(new DockerTestHttpMessageHandler(
            (request, _) => Task.FromResult(request.Uri.AbsolutePath switch
            {
                var path when path.Contains("/images/", StringComparison.Ordinal) && path.EndsWith("/json", StringComparison.Ordinal) =>
                    DockerTestHttpMessageHandler.Json(HttpStatusCode.NotFound, "{}"),
                "/v1.47/images/create" => DockerTestHttpMessageHandler.Json(HttpStatusCode.OK, largeResponse),
                _ => DockerTestHttpMessageHandler.Json(HttpStatusCode.NotFound, "{}"),
            })),
            options: options);

        DockerProviderException exception = await Assert.ThrowsAsync<DockerProviderException>(
            () => context.Provider.CreateAsync(new RunnerCreateRequest(
                DockerProviderTestContext.CreateIdentity(),
                "synthetic-jit"),
                TestContext.Current.CancellationToken));

        Assert.Equal("image-pull-stream-failed", exception.FailureCode);
        Assert.DoesNotContain(new string('p', 128), exception.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(context.Handler.Requests, request => request.Uri.AbsolutePath == "/v1.47/containers/create");
    }

    [Fact]
    public async Task CreateAsync_UsesExplicitUserCommandAndWorkingDirectoryOverrides()
    {
        DockerProviderOptions options = new()
        {
            User = "0",
            Command = ["/bin/sh", "-c", "sleep 30"],
            WorkingDirectory = "/tmp/runner-work",
        };
        using DockerProviderTestContext context = new(new DockerTestHttpMessageHandler(
            (request, _) => Task.FromResult(request.Uri.AbsolutePath switch
            {
                var path when path.Contains("/images/", StringComparison.Ordinal) && path.EndsWith("/json", StringComparison.Ordinal) =>
                    DockerTestHttpMessageHandler.Json(HttpStatusCode.OK, "{\"Id\":\"sha256:test\"}"),
                "/v1.47/containers/create" => DockerTestHttpMessageHandler.Json(HttpStatusCode.Created, $"{{\"Id\":\"{new string('1', 64)}\"}}"),
                _ => DockerTestHttpMessageHandler.Json(HttpStatusCode.NotFound, "{}"),
            })),
            options: options);

        _ = await context.Provider.CreateAsync(new RunnerCreateRequest(
            DockerProviderTestContext.CreateIdentity(),
            "synthetic-jit"),
            TestContext.Current.CancellationToken);

        RecordedDockerRequest createRequest = Assert.Single(
            context.Handler.Requests,
            request => request.Uri.AbsolutePath == "/v1.47/containers/create");
        using JsonDocument body = JsonDocument.Parse(createRequest.Body);
        Assert.Equal("0", body.RootElement.GetProperty("User").GetString());
        Assert.Equal(
            SleepCommand,
            body.RootElement.GetProperty("Cmd").EnumerateArray().Select(static item => item.GetString()!).ToArray());
        Assert.Equal("/tmp/runner-work", body.RootElement.GetProperty("WorkingDir").GetString());
        Assert.Equal(
            SyntheticJitEnvironment,
            body.RootElement.GetProperty("Env").EnumerateArray().Select(static item => item.GetString()!).ToArray());
    }

    [Fact]
    public async Task CreateAsync_DockerInDockerAndHostSocketRequireExplicitProfileSettings()
    {
        RunnerProfileOptions dindProfile = DockerProviderTestContext.CreateProfile();
        dindProfile.DockerBuildMode = DockerBuildMode.DockerInDocker;
        dindProfile.Privileged = true;
        using DockerProviderTestContext dindContext = new(new DockerTestHttpMessageHandler(
            (request, _) => Task.FromResult(request.Uri.AbsolutePath switch
            {
                var path when path.Contains("/images/", StringComparison.Ordinal) =>
                    DockerTestHttpMessageHandler.Json(HttpStatusCode.OK, "{\"Id\":\"sha256:test\"}"),
                "/v1.47/containers/create" =>
                    DockerTestHttpMessageHandler.Json(HttpStatusCode.Created, $"{{\"Id\":\"{new string('2', 64)}\"}}"),
                _ => DockerTestHttpMessageHandler.Json(HttpStatusCode.NotFound, "{}"),
            })),
            profile: dindProfile);

        _ = await dindContext.Provider.CreateAsync(new RunnerCreateRequest(
            DockerProviderTestContext.CreateIdentity(),
            "synthetic-jit"),
            TestContext.Current.CancellationToken);

        RecordedDockerRequest dindCreate = Assert.Single(
            dindContext.Handler.Requests,
            request => request.Uri.AbsolutePath == "/v1.47/containers/create");
        using (JsonDocument body = JsonDocument.Parse(dindCreate.Body))
        {
            JsonElement host = body.RootElement.GetProperty("HostConfig");
            Assert.True(host.GetProperty("Privileged").GetBoolean());
            Assert.False(host.TryGetProperty("Binds", out _));
        }

        RunnerProfileOptions socketProfile = DockerProviderTestContext.CreateProfile();
        socketProfile.DockerBuildMode = DockerBuildMode.HostSocket;
        using DockerProviderTestContext socketContext = new(new DockerTestHttpMessageHandler(
            (request, _) => Task.FromResult(request.Uri.AbsolutePath switch
            {
                var path when path.Contains("/images/", StringComparison.Ordinal) =>
                    DockerTestHttpMessageHandler.Json(HttpStatusCode.OK, "{\"Id\":\"sha256:test\"}"),
                "/v1.47/containers/create" =>
                    DockerTestHttpMessageHandler.Json(HttpStatusCode.Created, $"{{\"Id\":\"{new string('3', 64)}\"}}"),
                _ => DockerTestHttpMessageHandler.Json(HttpStatusCode.NotFound, "{}"),
            })),
            profile: socketProfile,
            options: new DockerProviderOptions { HostDockerSocketPath = "/custom/docker.sock" });

        _ = await socketContext.Provider.CreateAsync(new RunnerCreateRequest(
            DockerProviderTestContext.CreateIdentity(),
            "synthetic-jit"),
            TestContext.Current.CancellationToken);

        RecordedDockerRequest socketCreate = Assert.Single(
            socketContext.Handler.Requests,
            request => request.Uri.AbsolutePath == "/v1.47/containers/create");
        using JsonDocument socketBody = JsonDocument.Parse(socketCreate.Body);
        Assert.Equal(
            HostSocketBind,
            socketBody.RootElement.GetProperty("HostConfig").GetProperty("Binds").EnumerateArray().Select(static item => item.GetString()!).ToArray());
    }

    [Fact]
    public async Task CreateAsync_Api140RequiresNativeArchitecture()
    {
        using DockerProviderTestContext context = new(new DockerTestHttpMessageHandler(
            apiVersion: "1.40",
            architecture: "x86_64"));

        DockerProviderException exception = await Assert.ThrowsAsync<DockerProviderException>(
            () => context.Provider.CreateAsync(new RunnerCreateRequest(
                DockerProviderTestContext.CreateIdentity(),
                "synthetic-jit"),
                TestContext.Current.CancellationToken));

        Assert.Equal("api-version-cannot-enforce-platform", exception.FailureCode);
        Assert.DoesNotContain(context.Handler.Requests, request => request.Uri.AbsolutePath.Contains("/images/", StringComparison.Ordinal));
        Assert.DoesNotContain(context.Handler.Requests, request => request.Uri.AbsolutePath.EndsWith("/containers/create", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CreateAsync_Api140UsesNativeArchitectureWithoutPlatformQuery()
    {
        using DockerProviderTestContext context = new(new DockerTestHttpMessageHandler(
            (request, _) => Task.FromResult(request.Uri.AbsolutePath switch
            {
                var path when path.Contains("/images/", StringComparison.Ordinal) && path.EndsWith("/json", StringComparison.Ordinal) =>
                    DockerTestHttpMessageHandler.Json(HttpStatusCode.OK, "{\"Id\":\"sha256:test\"}"),
                var path when path.EndsWith("/containers/create", StringComparison.Ordinal) =>
                    DockerTestHttpMessageHandler.Json(HttpStatusCode.Created, $"{{\"Id\":\"{new string('d', 64)}\"}}"),
                _ => DockerTestHttpMessageHandler.Json(HttpStatusCode.NotFound, "{}"),
            }),
            apiVersion: "1.40",
            minimumApiVersion: "1.12",
            daemonOs: "linux",
            infoOs: "linux",
            architecture: "aarch64"),
            profile: new RunnerProfileOptions
            {
                Image = "ghcr.io/example/runner:2.324.0",
                Architecture = RunnerArchitecture.Arm64,
                Cpu = 1,
                Memory = 256,
                WorkFolder = "_work",
            });

        RunnerResource created = await context.Provider.CreateAsync(new RunnerCreateRequest(
            DockerProviderTestContext.CreateIdentity(),
            "synthetic-jit"),
            TestContext.Current.CancellationToken);

        Assert.Equal(RunnerResourceStatus.Created, created.Status);
        RecordedDockerRequest createRequest = Assert.Single(
            context.Handler.Requests,
            request => request.Uri.AbsolutePath == "/v1.40/containers/create");
        Assert.DoesNotContain("platform=", createRequest.Uri.Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StartAsync_RequiresOwnershipAndStartsCreatedContainer()
    {
        RunnerIdentity identity = DockerProviderTestContext.CreateIdentity(22);
        int inspectCount = 0;
        string containerId = new('4', 64);
        using DockerProviderTestContext context = new(new DockerTestHttpMessageHandler(
            (request, _) => Task.FromResult(request.Uri.AbsolutePath switch
            {
                var path when path == $"/v1.47/containers/{containerId}/json" =>
                    DockerTestHttpMessageHandler.Json(HttpStatusCode.OK, DockerProviderTestContext.CreateInspectJson(
                        identity,
                        state: Interlocked.Increment(ref inspectCount) == 1 ? "created" : "running",
                        id: containerId)),
                var path when path == $"/v1.47/containers/{containerId}/start" => new HttpResponseMessage(HttpStatusCode.NotModified),
                _ => DockerTestHttpMessageHandler.Json(HttpStatusCode.NotFound, "{}"),
            })));

        RunnerResource started = await context.Provider.StartAsync(
            new RunnerResource(containerId, identity, RunnerResourceStatus.Created),
            TestContext.Current.CancellationToken);

        Assert.Equal(RunnerResourceStatus.Running, started.Status);
        Assert.Equal(2, inspectCount);
        Assert.Single(
            context.Handler.Requests,
            request => request.Method == "POST" && request.Uri.AbsolutePath.EndsWith("/start", StringComparison.Ordinal));
        Assert.DoesNotContain(context.Handler.Requests, request => request.Method == "POST" && request.Uri.AbsolutePath.EndsWith("/create", StringComparison.Ordinal));
    }

    [Fact]
    public async Task StartAsync_RunningIsIdempotentAndNotIdleEvidence()
    {
        RunnerIdentity identity = DockerProviderTestContext.CreateIdentity();
        string containerId = new('5', 64);
        using DockerProviderTestContext context = new(new DockerTestHttpMessageHandler(
            (request, _) => Task.FromResult(request.Uri.AbsolutePath == $"/v1.47/containers/{containerId}/json"
                ? DockerTestHttpMessageHandler.Json(HttpStatusCode.OK, DockerProviderTestContext.CreateInspectJson(identity, state: "running", id: containerId))
                : DockerTestHttpMessageHandler.Json(HttpStatusCode.NotFound, "{}"))));

        RunnerResource running = await context.Provider.StartAsync(
            new RunnerResource(containerId, identity, RunnerResourceStatus.Created),
            TestContext.Current.CancellationToken);

        Assert.Equal(RunnerResourceStatus.Running, running.Status);
        Assert.DoesNotContain(context.Handler.Requests, request => request.Method == "POST");
    }

    [Fact]
    public async Task StartAsync_OnlyAllowsCreatedState()
    {
        RunnerIdentity identity = DockerProviderTestContext.CreateIdentity();
        string containerId = new('6', 64);
        using DockerProviderTestContext context = new(new DockerTestHttpMessageHandler(
            (request, _) => Task.FromResult(request.Uri.AbsolutePath.EndsWith("/json", StringComparison.Ordinal)
                ? DockerTestHttpMessageHandler.Json(HttpStatusCode.OK, DockerProviderTestContext.CreateInspectJson(identity, state: "exited", id: containerId))
                : DockerTestHttpMessageHandler.Json(HttpStatusCode.NotFound, "{}"))));

        DockerProviderException exception = await Assert.ThrowsAsync<DockerProviderException>(
            () => context.Provider.StartAsync(
                new RunnerResource(containerId, identity, RunnerResourceStatus.Created),
                TestContext.Current.CancellationToken));

        Assert.Equal("resource-not-startable", exception.FailureCode);
        Assert.DoesNotContain(context.Handler.Requests, request => request.Method == "POST");
    }

    [Fact]
    public async Task StartAsync_RejectsMismatchedOwnershipBeforeMutation()
    {
        RunnerIdentity expectedIdentity = DockerProviderTestContext.CreateIdentity();
        RunnerIdentity actualIdentity = new(
            expectedIdentity.Scope,
            expectedIdentity.OperationId,
            "different-runner");
        string containerId = new('7', 64);
        using DockerProviderTestContext context = new(new DockerTestHttpMessageHandler(
            (request, _) => Task.FromResult(request.Uri.AbsolutePath.EndsWith("/json", StringComparison.Ordinal)
                ? DockerTestHttpMessageHandler.Json(HttpStatusCode.OK, DockerProviderTestContext.CreateInspectJson(actualIdentity, id: containerId))
                : DockerTestHttpMessageHandler.Json(HttpStatusCode.NotFound, "{}"))));

        DockerProviderException exception = await Assert.ThrowsAsync<DockerProviderException>(
            () => context.Provider.StartAsync(
                new RunnerResource(containerId, expectedIdentity, RunnerResourceStatus.Created),
                TestContext.Current.CancellationToken));

        Assert.Equal("resource-ownership-mismatch", exception.FailureCode);
        Assert.DoesNotContain(context.Handler.Requests, request => request.Method == "POST");
    }

    [Theory]
    [InlineData("created", RunnerResourceStatus.Created, false)]
    [InlineData("running", RunnerResourceStatus.Running, false)]
    [InlineData("exited", RunnerResourceStatus.Exited, true)]
    [InlineData("dead", RunnerResourceStatus.Dead, true)]
    [InlineData("paused", RunnerResourceStatus.Unknown, false)]
    public async Task InspectAsync_MapsLifecycleEvidenceAndOmitsEnvironment(
        string dockerStatus,
        RunnerResourceStatus expectedStatus,
        bool expectedTerminal)
    {
        const string privateEnvironment = "ACTIONS_RUNNER_INPUT_JITCONFIG=inspect-response-jit-secret";
        RunnerIdentity identity = DockerProviderTestContext.CreateIdentity();
        string containerId = new('8', 64);
        using DockerProviderTestContext context = new(new DockerTestHttpMessageHandler(
            (request, _) => Task.FromResult(request.Uri.AbsolutePath.EndsWith("/json", StringComparison.Ordinal)
                ? DockerTestHttpMessageHandler.Json(HttpStatusCode.OK, DockerProviderTestContext.CreateInspectJson(
                    identity,
                    state: dockerStatus,
                    exitCode: 9,
                    startedAt: "2026-10-01T00:00:00Z",
                    finishedAt: "2026-10-01T00:30:00Z",
                    id: containerId,
                    rawEnvironmentValue: privateEnvironment))
                : DockerTestHttpMessageHandler.Json(HttpStatusCode.NotFound, "{}"))));

        RunnerResource? inspected = await context.Provider.InspectAsync(
            new RunnerResource(containerId, identity, RunnerResourceStatus.Unknown),
            TestContext.Current.CancellationToken);

        Assert.NotNull(inspected);
        Assert.Equal(expectedStatus, inspected.Status);
        Assert.Equal(expectedTerminal ? 9 : null, inspected.ExitCode);
        Assert.Equal(new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero), inspected.StartedAt);
        Assert.Equal(expectedTerminal ? new DateTimeOffset(2026, 10, 1, 0, 30, 0, TimeSpan.Zero) : null, inspected.FinishedAt);
        Assert.DoesNotContain(privateEnvironment, JsonSerializer.Serialize(inspected), StringComparison.Ordinal);
    }

    [Fact]
    public async Task InspectAsync_MissingResourceReturnsNull()
    {
        using DockerProviderTestContext context = new(new DockerTestHttpMessageHandler(
            (request, _) => Task.FromResult(request.Uri.AbsolutePath.EndsWith("/json", StringComparison.Ordinal)
                ? DockerTestHttpMessageHandler.Json(HttpStatusCode.NotFound, "{\"message\":\"missing\"}")
                : DockerTestHttpMessageHandler.Json(HttpStatusCode.NotFound, "{}"))));

        RunnerResource? inspected = await context.Provider.InspectAsync(
            DockerProviderTestContext.CreateResource(),
            TestContext.Current.CancellationToken);

        Assert.Null(inspected);
    }

    [Fact]
    public async Task RemoveAsync_MissingIsIdempotent()
    {
        using DockerProviderTestContext context = new(new DockerTestHttpMessageHandler(
            (request, _) => Task.FromResult(request.Uri.AbsolutePath.EndsWith("/json", StringComparison.Ordinal)
                ? DockerTestHttpMessageHandler.Json(HttpStatusCode.NotFound, "{}")
                : DockerTestHttpMessageHandler.Json(HttpStatusCode.NotFound, "{}"))));

        await context.Provider.RemoveAsync(
            DockerProviderTestContext.CreateResource(),
            force: false,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.DoesNotContain(context.Handler.Requests, request => request.Method == "DELETE");
    }

    [Fact]
    public async Task RemoveAsync_ConfirmedTerminalResourceCanBeRemovedWithoutForce()
    {
        RunnerIdentity identity = DockerProviderTestContext.CreateIdentity();
        string containerId = new('c', 64);
        using DockerProviderTestContext context = new(new DockerTestHttpMessageHandler(
            (request, _) => Task.FromResult(request.Uri.AbsolutePath switch
            {
                var path when path == $"/v1.47/containers/{containerId}/json" =>
                    DockerTestHttpMessageHandler.Json(HttpStatusCode.OK, DockerProviderTestContext.CreateInspectJson(identity, state: "exited", id: containerId)),
                var path when path == $"/v1.47/containers/{containerId}" => new HttpResponseMessage(HttpStatusCode.NoContent),
                _ => DockerTestHttpMessageHandler.Json(HttpStatusCode.NotFound, "{}"),
            })));

        await context.Provider.RemoveAsync(
            new RunnerResource(containerId, identity, RunnerResourceStatus.Exited),
            force: false,
            cancellationToken: TestContext.Current.CancellationToken);

        RecordedDockerRequest removeRequest = Assert.Single(context.Handler.Requests, request => request.Method == "DELETE");
        Assert.Empty(removeRequest.Uri.Query);
    }

    [Theory]
    [InlineData("running")]
    [InlineData("paused")]
    public async Task RemoveAsync_ActiveOrUnknownRequiresExplicitForce(string dockerStatus)
    {
        RunnerIdentity identity = DockerProviderTestContext.CreateIdentity();
        string containerId = new('9', 64);
        using DockerProviderTestContext context = new(new DockerTestHttpMessageHandler(
            (request, _) => Task.FromResult(request.Uri.AbsolutePath.EndsWith("/json", StringComparison.Ordinal)
                ? DockerTestHttpMessageHandler.Json(HttpStatusCode.OK, DockerProviderTestContext.CreateInspectJson(identity, state: dockerStatus, id: containerId))
                : DockerTestHttpMessageHandler.Json(HttpStatusCode.NotFound, "{}"))));

        DockerProviderException exception = await Assert.ThrowsAsync<DockerProviderException>(
            () => context.Provider.RemoveAsync(
                new RunnerResource(containerId, identity, RunnerResourceStatus.Running),
                force: false,
                cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal("resource-may-be-active", exception.FailureCode);
        Assert.DoesNotContain(context.Handler.Requests, request => request.Method == "DELETE");
    }

    [Fact]
    public async Task RemoveAsync_ValidatesOwnershipAndForcesOnlyWhenRequested()
    {
        RunnerIdentity identity = DockerProviderTestContext.CreateIdentity();
        string containerId = new('a', 64);
        using DockerProviderTestContext context = new(new DockerTestHttpMessageHandler(
            (request, _) => Task.FromResult(request.Uri.AbsolutePath switch
            {
                var path when path == $"/v1.47/containers/{containerId}/json" =>
                    DockerTestHttpMessageHandler.Json(HttpStatusCode.OK, DockerProviderTestContext.CreateInspectJson(identity, state: "running", id: containerId)),
                var path when path == $"/v1.47/containers/{containerId}" => new HttpResponseMessage(HttpStatusCode.NotFound),
                _ => DockerTestHttpMessageHandler.Json(HttpStatusCode.NotFound, "{}"),
            })));

        await context.Provider.RemoveAsync(
            new RunnerResource(containerId, identity, RunnerResourceStatus.Running),
            force: true,
            cancellationToken: TestContext.Current.CancellationToken);

        RecordedDockerRequest removeRequest = Assert.Single(context.Handler.Requests, request => request.Method == "DELETE");
        Assert.Equal("?force=1", removeRequest.Uri.Query);
    }

    [Fact]
    public async Task RemoveAsync_RejectsMismatchedOwnershipBeforeDelete()
    {
        RunnerIdentity expectedIdentity = DockerProviderTestContext.CreateIdentity();
        RunnerIdentity actualIdentity = new(expectedIdentity.Scope, expectedIdentity.OperationId, "foreign-runner");
        string containerId = new('b', 64);
        using DockerProviderTestContext context = new(new DockerTestHttpMessageHandler(
            (request, _) => Task.FromResult(request.Uri.AbsolutePath.EndsWith("/json", StringComparison.Ordinal)
                ? DockerTestHttpMessageHandler.Json(HttpStatusCode.OK, DockerProviderTestContext.CreateInspectJson(actualIdentity, state: "exited", id: containerId))
                : new HttpResponseMessage(HttpStatusCode.NoContent))));

        DockerProviderException exception = await Assert.ThrowsAsync<DockerProviderException>(
            () => context.Provider.RemoveAsync(
                new RunnerResource(containerId, expectedIdentity, RunnerResourceStatus.Exited),
                force: false,
                cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal("resource-ownership-mismatch", exception.FailureCode);
        Assert.DoesNotContain(context.Handler.Requests, request => request.Method == "DELETE");
    }

    [Fact]
    public async Task ErrorsAndRequestModels_NeverExposeJitOrResponseBody()
    {
        const string jitSecret = "create-jit-private-value";
        const string responseSecret = "docker-daemon-private-response";
        TrackingStringContent? responseContent = null;
        using DockerProviderTestContext context = new(new DockerTestHttpMessageHandler(
            (request, _) =>
            {
                if (request.Uri.AbsolutePath.Contains("/images/", StringComparison.Ordinal))
                {
                    return Task.FromResult(DockerTestHttpMessageHandler.Json(HttpStatusCode.OK, "{\"Id\":\"sha256:test\"}"));
                }

                if (request.Uri.AbsolutePath == "/v1.47/containers/create")
                {
                    responseContent = new TrackingStringContent($"{{\"message\":\"{responseSecret}\"}}");
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = responseContent });
                }

                return Task.FromResult(DockerTestHttpMessageHandler.Json(HttpStatusCode.NotFound, "{}"));
            }));
        RunnerCreateRequest request = new(DockerProviderTestContext.CreateIdentity(), jitSecret);

        DockerProviderException exception = await Assert.ThrowsAsync<DockerProviderException>(
            () => context.Provider.CreateAsync(request, TestContext.Current.CancellationToken));

        Assert.DoesNotContain(jitSecret, request.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(jitSecret, exception.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(responseSecret, exception.ToString(), StringComparison.Ordinal);
        Assert.NotNull(responseContent);
        Assert.True(responseContent.IsDisposed);
        Assert.True(exception.OutcomeMayBeUnknown);
        Assert.Null(exception.InnerException);
    }

    [Fact]
    public async Task Dispose_DoesNotDisposeCallerOwnedHttpClient()
    {
        using DockerProviderTestContext context = new(new DockerTestHttpMessageHandler(
            (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("ok"),
            })));

        context.Provider.Dispose();
        using HttpResponseMessage response = await context.HttpClient.GetAsync(
            "http://docker/after-dispose",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task DiscoverAsync_CancellationReachesHandler()
    {
        TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        bool cancellationObserved = false;
        using DockerProviderTestContext context = new(new DockerTestHttpMessageHandler(
            async (request, cancellationToken) =>
            {
                if (request.Uri.AbsolutePath == "/version")
                {
                    started.TrySetResult();
                    try
                    {
                        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        cancellationObserved = true;
                        throw;
                    }
                }

                return DockerTestHttpMessageHandler.Json(HttpStatusCode.OK, "{}");
            },
            interceptHandshakeResponses: false));
        using CancellationTokenSource cancellation = new();
        Task<ImmutableArray<RunnerResource>> discover = context.Provider.DiscoverAsync(
            DockerProviderTestContext.CreateScope(),
            cancellation.Token);

        await started.Task;
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => discover);

        Assert.True(cancellationObserved);
        Assert.DoesNotContain(context.Handler.Requests, request => request.Uri.AbsolutePath.EndsWith("/containers/json", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DiscoverAsync_RequestTimeoutUsesInjectedTimeProvider()
    {
        TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        bool timeoutObserved = false;
        ManualTimeProvider timeProvider = new(DateTimeOffset.UnixEpoch);
        DockerProviderOptions options = new() { RequestTimeout = TimeSpan.FromSeconds(30) };
        using DockerProviderTestContext context = new(new DockerTestHttpMessageHandler(
            async (request, cancellationToken) =>
            {
                if (request.Uri.AbsolutePath == "/version")
                {
                    started.TrySetResult();
                    try
                    {
                        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        timeoutObserved = true;
                        throw;
                    }
                }

                return DockerTestHttpMessageHandler.Json(HttpStatusCode.OK, "{}");
            },
            interceptHandshakeResponses: false),
            options: options,
            timeProvider: timeProvider);

        Task<ImmutableArray<RunnerResource>> discover = context.Provider.DiscoverAsync(
            DockerProviderTestContext.CreateScope(),
            TestContext.Current.CancellationToken);
        await started.Task;
        timeProvider.Advance(TimeSpan.FromSeconds(29));
        Assert.False(discover.IsCompleted);

        timeProvider.Advance(TimeSpan.FromSeconds(1));
        DockerProviderException exception = await Assert.ThrowsAsync<DockerProviderException>(() => discover);

        Assert.Equal("request-timeout", exception.FailureCode);
        Assert.True(timeoutObserved);
        Assert.DoesNotContain(context.Handler.Requests, request => request.Uri.AbsolutePath.EndsWith("/containers/json", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Constructor_SnapshotsMutableProfileAndCommandOptions()
    {
        RunnerProfileOptions profile = DockerProviderTestContext.CreateProfile();
        List<string> command = ["/home/runner/run.sh"];
        DockerProviderOptions options = new() { Command = command };
        DockerTestHttpMessageHandler handler = new((request, _) => Task.FromResult(request.Uri.AbsolutePath switch
        {
            var path when path.Contains("/images/", StringComparison.Ordinal) && path.EndsWith("/json", StringComparison.Ordinal) =>
                DockerTestHttpMessageHandler.Json(HttpStatusCode.OK, "{\"Id\":\"sha256:test\"}"),
            "/v1.47/containers/create" => DockerTestHttpMessageHandler.Json(HttpStatusCode.Created, $"{{\"Id\":\"{new string('c', 64)}\"}}"),
            _ => DockerTestHttpMessageHandler.Json(HttpStatusCode.NotFound, "{}"),
        }));
        using DockerProviderTestContext context = new(handler, profile, options);
        profile.Image = "ghcr.io/example/changed:9.0";
        profile.Cpu = 8;
        profile.Memory = 4096;
        command[0] = "/tmp/changed-command";

        _ = await context.Provider.CreateAsync(new RunnerCreateRequest(
            DockerProviderTestContext.CreateIdentity(),
            "synthetic-jit"),
            TestContext.Current.CancellationToken);

        RecordedDockerRequest imageRequest = Assert.Single(
            handler.Requests,
            request => request.Uri.AbsolutePath.Contains("/images/", StringComparison.Ordinal));
        Assert.Contains("ghcr.io%2Fexample%2Frunner%3A2.324.0", imageRequest.Uri.AbsolutePath, StringComparison.OrdinalIgnoreCase);
        RecordedDockerRequest createRequest = Assert.Single(
            handler.Requests,
            request => request.Uri.AbsolutePath == "/v1.47/containers/create");
        using JsonDocument body = JsonDocument.Parse(createRequest.Body);
        Assert.Equal("ghcr.io/example/runner:2.324.0", body.RootElement.GetProperty("Image").GetString());
        Assert.Equal(1_500_000_000L, body.RootElement.GetProperty("HostConfig").GetProperty("NanoCpus").GetInt64());
        Assert.Equal(268_435_456L, body.RootElement.GetProperty("HostConfig").GetProperty("Memory").GetInt64());
        Assert.Equal("/home/runner/run.sh", body.RootElement.GetProperty("Cmd")[0].GetString());
    }

    [Fact]
    public void Constructor_RejectsInvalidProfilesAndTransportOptions()
    {
        RunnerProfileOptions profile = DockerProviderTestContext.CreateProfile();
        profile.Image = "ghcr.io/example/runner:latest";
        Assert.Throws<ArgumentException>(() =>
        {
            using HttpClient httpClient = new(new DockerTestHttpMessageHandler());
            using DockerRunnerProvider _ = new(profile, httpClient: httpClient);
        });

        profile = DockerProviderTestContext.CreateProfile();
        profile.DockerBuildMode = DockerBuildMode.DockerInDocker;
        Assert.Throws<ArgumentException>(() =>
        {
            using HttpClient httpClient = new(new DockerTestHttpMessageHandler());
            using DockerRunnerProvider _ = new(profile, httpClient: httpClient);
        });

        DockerProviderOptions invalidOptions = new() { WorkingDirectory = "../runner" };
        Assert.Throws<ArgumentException>(() =>
        {
            using HttpClient httpClient = new(new DockerTestHttpMessageHandler());
            using DockerRunnerProvider _ = new(
                DockerProviderTestContext.CreateProfile(),
                invalidOptions,
                httpClient);
        });
    }

    [Fact]
    public async Task ResponseContent_IsDisposedAfterSuccessfulOperation()
    {
        TrackingStringContent? content = null;
        using DockerProviderTestContext context = new(new DockerTestHttpMessageHandler(
            (request, _) =>
            {
                if (request.Uri.AbsolutePath == "/v1.47/containers/json")
                {
                    content = new TrackingStringContent("[]");
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
                }

                return Task.FromResult(DockerTestHttpMessageHandler.Json(HttpStatusCode.NotFound, "{}"));
            }));

        ImmutableArray<RunnerResource> resources = await context.Provider.DiscoverAsync(
            DockerProviderTestContext.CreateScope(),
            TestContext.Current.CancellationToken);

        Assert.Empty(resources);
        Assert.NotNull(content);
        Assert.True(content.IsDisposed);
    }

    private static Dictionary<string, string[]> ReadContainerFilters(Uri uri)
    {
        string query = uri.Query;
        int start = query.IndexOf("filters=", StringComparison.Ordinal);
        Assert.True(start >= 0);
        string json = Uri.UnescapeDataString(query[(start + "filters=".Length)..]);
        return JsonSerializer.Deserialize<Dictionary<string, string[]>>(json)
            ?? throw new InvalidOperationException("The filters query was empty.");
    }

    private static string ReadQueryValue(Uri uri, string name)
    {
        string prefix = $"{name}=";
        string pair = uri.Query.TrimStart('?').Split('&').Single(value => value.StartsWith(prefix, StringComparison.Ordinal));
        return Uri.UnescapeDataString(pair[prefix.Length..]);
    }
}
