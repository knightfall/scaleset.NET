using System.Runtime.InteropServices;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using ScaleSet.Hosting.Configuration;
using ScaleSet.Hosting.Providers;
using ScaleSet.Hosting.State;
using ScaleSet.Providers.Docker;
using Xunit;

namespace ScaleSet.Docker.IntegrationTests;

public sealed class DockerRunnerProviderIntegrationTests
{
    private const string Image = "busybox@sha256:bdf57e528e45e4433820e045b29b4597825a1c9e38353532d90a01445013f82e";
    private const string SyntheticJit = "docker-integration-jit-sentinel-no-github-credential";

    public static bool DockerIntegrationEnabled =>
        string.Equals(Environment.GetEnvironmentVariable("SCALESET_DOCKER_INTEGRATION"), "1", StringComparison.Ordinal);

    [Fact(Skip = "Set SCALESET_DOCKER_INTEGRATION=1 with a Linux Docker daemon.", SkipUnless = nameof(DockerIntegrationEnabled), Timeout = 90000)]
    public async Task OwnedRunnerSurvivesProviderDisposalAndIsDiscoveredWithoutAllowingForeignMutation()
    {
        RunnerOwnershipScope scope = NewScope();
        var identity = new RunnerIdentity(scope, Guid.NewGuid(), $"fixture-{Guid.NewGuid():N}", 5000000001);
        RunnerResource? resource = null;
        using DockerRunnerProvider cleanup = CreateProvider("sleep 300");
        try
        {
            using (DockerRunnerProvider first = CreateProvider("sleep 300"))
            {
                resource = await first.CreateAsync(new RunnerCreateRequest(identity, SyntheticJit), TestContext.Current.CancellationToken);
                Assert.Equal(RunnerResourceStatus.Created, resource.Status);
                resource = await first.StartAsync(resource, SyntheticJit, TestContext.Current.CancellationToken);
                Assert.Equal(RunnerResourceStatus.Running, resource.Status);
            }

            // Disposing the controller transport must not remove its independent runner.
            RunnerResource? survivor = await cleanup.InspectAsync(resource, TestContext.Current.CancellationToken);
            Assert.NotNull(survivor);
            Assert.Equal(RunnerResourceStatus.Running, survivor.Status);
            var discovered = await cleanup.DiscoverAsync(scope, TestContext.Current.CancellationToken);
            Assert.Equal(resource.ProviderResourceId, Assert.Single(discovered).ProviderResourceId);

            var otherScope = new RunnerOwnershipScope(scope.ControllerId, "foreign-set", scope.ScaleSetId);
            Assert.Empty(await cleanup.DiscoverAsync(otherScope, TestContext.Current.CancellationToken));
            var forged = new RunnerResource(
                resource.ProviderResourceId,
                new RunnerIdentity(otherScope, identity.OperationId, identity.RunnerName, identity.GitHubRunnerId),
                RunnerResourceStatus.Running);
            await Assert.ThrowsAsync<DockerProviderException>(
                () => cleanup.RemoveAsync(forged, force: true, TestContext.Current.CancellationToken));
            await Assert.ThrowsAsync<DockerProviderException>(
                () => cleanup.RemoveAsync(resource, force: false, TestContext.Current.CancellationToken));
            RunnerResource? protectedRunner = await cleanup.InspectAsync(resource, TestContext.Current.CancellationToken);
            Assert.Equal(RunnerResourceStatus.Running, protectedRunner?.Status);

            await cleanup.RemoveAsync(resource, force: true, TestContext.Current.CancellationToken);
            Assert.Null(await cleanup.InspectAsync(resource, TestContext.Current.CancellationToken));
            await cleanup.RemoveAsync(resource, force: false, TestContext.Current.CancellationToken);
        }
        finally
        {
            await CleanupOwnedAsync(cleanup, scope, resource);
        }
    }

    [Fact(Skip = "Set SCALESET_DOCKER_INTEGRATION=1 with a Linux Docker daemon.", SkipUnless = nameof(DockerIntegrationEnabled), Timeout = 90000)]
    public async Task TerminalSurvivorIsAdoptedAsUnknownThenRemovedWithDurableNonSecretEvidence()
    {
        RunnerOwnershipScope scope = NewScope();
        var identity = new RunnerIdentity(scope, Guid.NewGuid(), $"fixture-{Guid.NewGuid():N}", 5000000002);
        RunnerResource? resource = null;
        string directory = Path.Combine(Path.GetTempPath(), $"scaleset-docker-state-{Guid.NewGuid():N}");
        using DockerRunnerProvider provider = CreateProvider(
            """test "$ACTIONS_RUNNER_INPUT_JITCONFIG" = docker-integration-jit-sentinel-no-github-credential && test ! -e /run/scaleset-jit/handoff""");
        try
        {
            resource = await provider.CreateAsync(new RunnerCreateRequest(identity, SyntheticJit), TestContext.Current.CancellationToken);
            resource = await provider.StartAsync(resource, SyntheticJit, TestContext.Current.CancellationToken);
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            wait.CancelAfter(TimeSpan.FromSeconds(20));
            while (resource.Status is not (RunnerResourceStatus.Exited or RunnerResourceStatus.Dead))
            {
                await Task.Delay(TimeSpan.FromMilliseconds(100), wait.Token);
                resource = await provider.InspectAsync(resource, wait.Token)
                    ?? throw new InvalidOperationException("The owned fixture disappeared before terminal inspection.");
            }

            Assert.Equal(0, resource.ExitCode);
            await using (FileRunnerStateJournal journal = await FileRunnerStateJournal.OpenAsync(directory, scope, TestContext.Current.CancellationToken))
            {
                using var lifecycle = new RunnerLifecycleManager(journal, TimeProvider.System, NullLogger<RunnerLifecycleManager>.Instance);
                RunnerOperationState adopted = await lifecycle.AdoptDiscoveredAsync(resource, TestContext.Current.CancellationToken);
                Assert.Equal(RunnerLifecycleStage.Unknown, adopted.Stage);
                RunnerOperationState removing = await lifecycle.BeginRemovalAsync(resource, provider.Capabilities, TestContext.Current.CancellationToken);
                Assert.Equal(RunnerLifecycleStage.Removing, removing.Stage);
                await provider.RemoveAsync(resource, force: false, TestContext.Current.CancellationToken);
                Assert.Null(await provider.InspectAsync(resource, TestContext.Current.CancellationToken));
                RunnerOperationState gone = await lifecycle.ConfirmGoneAsync(identity, TestContext.Current.CancellationToken);
                Assert.Equal(RunnerLifecycleStage.Gone, gone.Stage);
            }

            await using FileRunnerStateJournal reopened = await FileRunnerStateJournal.OpenAsync(directory, scope, TestContext.Current.CancellationToken);
            RunnerOperationState persisted = Assert.Single(await reopened.ReadAllAsync(TestContext.Current.CancellationToken));
            Assert.Equal(RunnerLifecycleStage.Gone, persisted.Stage);
            Assert.Equal(resource.ProviderResourceId, persisted.ProviderResourceId);
            await using var persistedDb = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = Path.Combine(directory, "runner-state.sqlite3"),
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false,
            }.ToString());
            await persistedDb.OpenAsync(TestContext.Current.CancellationToken);
            await using var selectPayload = persistedDb.CreateCommand();
            selectPayload.CommandText = "SELECT payload FROM operations";
            string json = Assert.IsType<string>(await selectPayload.ExecuteScalarAsync(TestContext.Current.CancellationToken));
            Assert.Contains(identity.RunnerName, json, StringComparison.Ordinal);
            Assert.DoesNotContain(SyntheticJit, json, StringComparison.Ordinal);
            Assert.DoesNotContain("EncodedJitConfig", json, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            await CleanupOwnedAsync(provider, scope, resource);

            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    private static RunnerOwnershipScope NewScope() =>
        new($"scaleset-integration-{Guid.NewGuid():N}", "docker-integration", 5000000000);

    private static async Task CleanupOwnedAsync(DockerRunnerProvider provider, RunnerOwnershipScope scope, RunnerResource? resource)
    {
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        // Force is confined to this explicitly owned harmless fixture. Discovery
        // also cleans a create that succeeded before its response was lost.
        if (resource is not null)
        {
            await provider.RemoveAsync(resource, force: true, budget.Token);
        }

        foreach (RunnerResource owned in await provider.DiscoverAsync(scope, budget.Token))
        {
            await provider.RemoveAsync(owned, force: true, budget.Token);
        }
    }

    private static DockerRunnerProvider CreateProvider(string command) => new(
        new RunnerProfileOptions
        {
            Image = Image,
            Architecture = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? RunnerArchitecture.Arm64 : RunnerArchitecture.X64,
            Cpu = 0.25m,
            Memory = 32,
            Network = "none",
        },
        new DockerProviderOptions
        {
            User = "0",
            Command = ["/bin/sh", "-c", command],
            WorkingDirectory = "/",
            RequestTimeout = TimeSpan.FromSeconds(10),
            ImagePullTimeout = TimeSpan.FromSeconds(60),
        });
}
