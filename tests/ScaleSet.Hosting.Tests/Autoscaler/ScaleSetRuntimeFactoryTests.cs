using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using ScaleSet.Autoscaler.Runtime;
using ScaleSet.Client;
using ScaleSet.Hosting.Capacity;
using ScaleSet.Hosting.Configuration;
using ScaleSet.Hosting.Providers;
using ScaleSet.Hosting.Scaling;

namespace ScaleSet.Hosting.Tests.Autoscaler;

public sealed class ScaleSetRuntimeFactoryTests
{
    private const string CredentialSentinel = "synthetic-PAT-secret-6419";

    [Fact]
    public async Task InitializeAsync_CleanBootstrapCreatesOnceAndPersistsFinalNonSecretIdentity()
    {
        using TemporaryStateDirectory directory = new();
        (AutoscalerOptions options, ScaleSetOptions scaleSet) = CreateOptions(directory.Path);
        string setStateDirectory = GetSetStateDirectory(directory.Path, scaleSet.Key!);
        FakeScaleSetRuntimeClient client = new()
        {
            OnCreate = async (request, _) =>
            {
                Assert.True(request.RunnerSetting.DisableUpdate);
                string pendingJson = await File.ReadAllTextAsync(Path.Combine(setStateDirectory, "scale-set-identity.json"), TestContext.Current.CancellationToken);
                using JsonDocument pending = JsonDocument.Parse(pendingJson);
                Assert.True(pending.RootElement.GetProperty("CreationInProgress").GetBoolean());
                Assert.True(pending.RootElement.GetProperty("ScaleSetId").ValueKind == JsonValueKind.Null);
                Assert.Contains("org/repo", pending.RootElement.GetProperty("Target").GetString(), StringComparison.OrdinalIgnoreCase);
                return new RunnerScaleSet
                {
                    Id = 987,
                    Name = request.Name,
                    RunnerGroupId = request.RunnerGroupId,
                    RunnerGroupName = request.RunnerGroupName,
                    Labels = request.Labels,
                };
            },
        };
        FakeRunnerProvider provider = new();
        ScaleSetRuntimeFactory factory = CreateFactory(options, client, provider);

        IScaleSetRuntime runtime = await factory.CreateAsync(
            scaleSet,
            new HostCapacityCoordinator(options.HostCapacity, options.ScaleSets),
            TestContext.Current.CancellationToken);
        try
        {
            await runtime.InitializeAsync(TestContext.Current.CancellationToken);
        }
        finally
        {
            await runtime.DisposeAsync();
        }

        string identityJson = await File.ReadAllTextAsync(Path.Combine(setStateDirectory, "scale-set-identity.json"), TestContext.Current.CancellationToken);
        using JsonDocument identity = JsonDocument.Parse(identityJson);
        Assert.Equal(1, client.CreateCount);
        Assert.Equal(0, client.SessionCreateCount);
        Assert.Equal(987L, identity.RootElement.GetProperty("ScaleSetId").GetInt64());
        Assert.False(identity.RootElement.GetProperty("CreationInProgress").GetBoolean());
        Assert.Equal("https://github.com/org/repo", identity.RootElement.GetProperty("Target").GetString());
        Assert.Equal("Default", identity.RootElement.GetProperty("RunnerGroupName").GetString());
        Assert.Equal(scaleSet.Name, identity.RootElement.GetProperty("ScaleSetName").GetString());
        Assert.DoesNotContain(CredentialSentinel, identityJson, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(setStateDirectory, "runner-state.json")));
    }

    [Fact]
    public async Task InitializeAsync_PendingIdentityWithoutRemoteScaleSetFailsClosedWithoutSecondCreate()
    {
        using TemporaryStateDirectory directory = new();
        (AutoscalerOptions options, ScaleSetOptions scaleSet) = CreateOptions(directory.Path);
        string setStateDirectory = GetSetStateDirectory(directory.Path, scaleSet.Key!);
        FakeScaleSetRuntimeClient firstClient = new()
        {
            OnCreate = async (_, _) =>
            {
                string pendingJson = await File.ReadAllTextAsync(Path.Combine(setStateDirectory, "scale-set-identity.json"), TestContext.Current.CancellationToken);
                using JsonDocument pending = JsonDocument.Parse(pendingJson);
                Assert.True(pending.RootElement.GetProperty("CreationInProgress").GetBoolean());
                throw new HttpRequestException("synthetic lost create response");
            },
        };
        FakeRunnerProvider firstProvider = new();
        ScaleSetRuntimeFactory firstFactory = CreateFactory(options, firstClient, firstProvider);
        IScaleSetRuntime firstRuntime = await firstFactory.CreateAsync(
            scaleSet,
            new HostCapacityCoordinator(options.HostCapacity, options.ScaleSets),
            TestContext.Current.CancellationToken);

        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => firstRuntime.InitializeAsync(TestContext.Current.CancellationToken));
        }
        finally
        {
            await firstRuntime.DisposeAsync();
        }

        string pendingIdentityJson = await File.ReadAllTextAsync(
            Path.Combine(setStateDirectory, "scale-set-identity.json"),
            TestContext.Current.CancellationToken);
        Assert.DoesNotContain(CredentialSentinel, pendingIdentityJson, StringComparison.Ordinal);
        FakeScaleSetRuntimeClient restartClient = new();
        FakeRunnerProvider restartProvider = new();
        ScaleSetRuntimeFactory restartFactory = CreateFactory(options, restartClient, restartProvider);
        IScaleSetRuntime restartRuntime = await restartFactory.CreateAsync(
            scaleSet,
            new HostCapacityCoordinator(options.HostCapacity, options.ScaleSets),
            TestContext.Current.CancellationToken);

        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => restartRuntime.InitializeAsync(TestContext.Current.CancellationToken));
        }
        finally
        {
            await restartRuntime.DisposeAsync();
        }

        Assert.Equal(1, firstClient.CreateCount);
        Assert.Equal(0, restartClient.CreateCount);
        Assert.Equal(0, restartClient.SessionCreateCount);
    }

    private static ScaleSetRuntimeFactory CreateFactory(
        AutoscalerOptions options,
        FakeScaleSetRuntimeClient client,
        FakeRunnerProvider provider) =>
        new(
            options,
            NullLoggerFactory.Instance,
            TimeProvider.System,
            (_, _, _, _, _) => client,
            (_, _, _) => provider);

    private static (AutoscalerOptions Options, ScaleSetOptions ScaleSet) CreateOptions(string stateDirectory)
    {
        ScaleSetOptions scaleSet = new()
        {
            Key = "linux-x64",
            GithubUrl = "https://github.com/org/repo",
            Name = "ci-linux",
            CredentialsRef = "github",
            Labels = [],
            Scaling = new ScalingOptions { Policy = ScalingPolicy.SpareCapacity, MinRunners = 0, MaxRunners = 10 },
            Lifecycle = new LifecycleOptions
            {
                StartupTimeout = TimeSpan.FromSeconds(30),
                ShutdownDrainTimeout = TimeSpan.FromSeconds(2),
                CleanupTimeout = TimeSpan.FromSeconds(2),
            },
            RunnerProfile = new RunnerProfileOptions
            {
                Image = "example/runner:1",
                Architecture = RunnerArchitecture.X64,
                Cpu = 1,
                Memory = 1024,
                WorkFolder = "_work",
            },
        };
        AutoscalerOptions options = new()
        {
            Controller = new ControllerOptions
            {
                Identity = "controller-test",
                StateDirectory = stateDirectory,
                ControlOperationTimeout = TimeSpan.FromSeconds(2),
            },
            HostCapacity = new HostCapacityOptions { CreationConcurrency = 2 },
            ScaleSets = [scaleSet],
            Credentials = new Dictionary<string, CredentialOptions>(StringComparer.OrdinalIgnoreCase)
            {
                ["github"] = new CredentialOptions { Pat = CredentialSentinel },
            },
        };
        return (options, scaleSet);
    }

    private static string GetSetStateDirectory(string stateRoot, string scaleSetKey)
    {
        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(scaleSetKey.ToUpperInvariant()));
        return Path.Combine(Path.GetFullPath(stateRoot), Convert.ToHexString(digest).ToLowerInvariant());
    }

    private sealed class FakeScaleSetRuntimeClient : IScaleSetRuntimeClient
    {
        private int _createCount;
        private int _sessionCreateCount;

        public Func<RunnerScaleSet, CancellationToken, Task<RunnerScaleSet>>? OnCreate { get; init; }

        public int CreateCount => Volatile.Read(ref _createCount);

        public int SessionCreateCount => Volatile.Read(ref _sessionCreateCount);

        public Task<RunnerGroup> GetRunnerGroupByNameAsync(string name, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal("Default", name);
            return Task.FromResult(new RunnerGroup { Id = 44, Name = name });
        }

        public Task<RunnerScaleSet?> FindScaleSetAsync(long runnerGroupId, string name, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(44L, runnerGroupId);
            Assert.Equal("ci-linux", name);
            return Task.FromResult<RunnerScaleSet?>(null);
        }

        public Task<RunnerScaleSet> CreateScaleSetAsync(RunnerScaleSet scaleSet, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _createCount);
            return OnCreate?.Invoke(scaleSet, cancellationToken)
                ?? Task.FromResult(new RunnerScaleSet
                {
                    Id = 987,
                    Name = scaleSet.Name,
                    RunnerGroupId = scaleSet.RunnerGroupId,
                    RunnerGroupName = scaleSet.RunnerGroupName,
                    Labels = scaleSet.Labels,
                });
        }

        public Task<RunnerScaleSetJitRunnerConfig?> GenerateJitConfigAsync(
            long scaleSetId,
            RunnerScaleSetJitRunnerSetting runnerSetting,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IScaleSetRuntimeSession> CreateMessageSessionAsync(long scaleSetId, string ownerName, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _sessionCreateCount);
            throw new Xunit.Sdk.XunitException("Initialization must reconcile before creating a session.");
        }

        public void Dispose()
        {
        }
    }

    private sealed class FakeRunnerProvider : IRunnerProvider
    {
        public RunnerProviderCapabilities Capabilities { get; } = new(
            "fake",
            ImmutableArray.Create(RunnerArchitecture.X64),
            supportsOwnershipScopedDiscovery: true,
            providesTerminalStateEvidence: true);

        public Task<ImmutableArray<RunnerResource>> DiscoverAsync(RunnerOwnershipScope scope, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(ImmutableArray<RunnerResource>.Empty);
        }

        public Task<RunnerResource> CreateAsync(RunnerCreateRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<RunnerResource> StartAsync(RunnerResource resource, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<RunnerResource?> InspectAsync(RunnerResource resource, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task RemoveAsync(RunnerResource resource, bool force, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class TemporaryStateDirectory : IDisposable
    {
        public TemporaryStateDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"scaleset-runtime-factory-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
