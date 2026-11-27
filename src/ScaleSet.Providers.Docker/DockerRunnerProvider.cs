using System.Collections.Immutable;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using ScaleSet.Hosting.Capacity;
using ScaleSet.Hosting.Configuration;
using ScaleSet.Hosting.Providers;
using ScaleSet.Hosting.Telemetry;

namespace ScaleSet.Providers.Docker;

/// <summary>Manages GitHub Actions runner containers through the Docker Engine API.</summary>
public sealed partial class DockerRunnerProvider : IRunnerProvider, IDisposable
{
    /// <summary>Activity source used for Docker Engine request spans.</summary>
    public const string ActivitySourceName = ScaleSetTelemetrySources.DockerProvider;

    /// <summary>Meter used for Docker Engine request counts and durations.</summary>
    public const string MeterName = ActivitySourceName;

    private const string ProductLabelValue = "scaleset.net";
    private const string ContainerSocketPath = "/var/run/docker.sock";
    private const int MaximumJitConfigurationBytes = 96 * 1024;
    private const string JitBootstrapLabel = "io.scaleset.jit-bootstrap";
    private const string JitMountPath = "/run/scaleset-jit";
    private const string JitBootstrapVersion = "2";
    private const int MaximumHandoffCentiseconds = 3000;
    private const string HandoffClock = """
        monotonic_now() {
            IFS=' .' read -r seconds centiseconds _ < /proc/uptime || exit 125
            now=$((seconds * 100 + 1$centiseconds - 100))
        }
        within_deadline() {
            monotonic_now
            [ "$now" -lt "$deadline" ]
        }
        """;
    private const string BootstrapScript = HandoffClock + "\n" + """
        umask 077
        handoff=/run/scaleset-jit/handoff
        monotonic_now
        deadline=$((now + $1))
        shift
        mkdir "$handoff" || exit 125
        offer="$handoff/offer"
        clean_bootstrap() {
            rm -f "$handoff/.payload" "$handoff/claimed" "$handoff/accepted" "$handoff/.deadline" "$handoff/deadline" || return 1
            if [ -d "$offer" ]; then
                rm -f "$offer/.incoming" "$offer/config" && rmdir "$offer" || return 1
            fi
            rmdir "$handoff"
        }
        trap 'clean_bootstrap' 0
        trap 'exit 125' HUP INT TERM
        printf '%s\n' "$deadline" > "$handoff/.deadline" &&
            mv "$handoff/.deadline" "$handoff/deadline" || exit 125
        while [ ! -f "$offer/config" ]; do
            within_deadline || exit 125
            sleep 0.1
        done
        within_deadline || exit 125
        mv "$offer/config" "$handoff/.payload" || exit 125
        {
            IFS= read -r sender && IFS= read -r ACTIONS_RUNNER_INPUT_JITCONFIG
        } < "$handoff/.payload" || exit 125
        rm -f "$handoff/.payload" || exit 125
        case "$sender" in ''|*[!0-9]*|0|1) exit 125 ;; esac
        : > "$handoff/claimed" || exit 125
        # The sender checks the deadline before accepting. Acceptance plus sender exit completes the handoff.
        while [ ! -f "$handoff/accepted" ] || kill -0 "$sender" 2>/dev/null; do
            within_deadline || exit 125
            sleep 0.1
        done
        clean_bootstrap || exit 125
        trap - 0 HUP INT TERM
        export ACTIONS_RUNNER_INPUT_JITCONFIG
        exec "$@"
        """;
    private const string JitHandoffScript = HandoffClock + "\n" + """
        umask 077
        handoff=/run/scaleset-jit/handoff
        monotonic_now
        deadline=$((now + $1))
        while [ ! -f "$handoff/deadline" ]; do
            within_deadline || exit 125
            sleep 0.1
        done
        IFS= read -r bootstrap_deadline < "$handoff/deadline" || exit 125
        case "$bootstrap_deadline" in ''|*[!0-9]*) exit 125 ;; esac
        [ "$bootstrap_deadline" -ge "$deadline" ] || deadline=$bootstrap_deadline
        within_deadline || exit 125
        offer="$handoff/offer"
        mkdir "$offer" || exit 125
        trap 'rm -f "$offer/.incoming" "$offer/config"' 0
        trap 'exit 125' HUP INT TERM
        printf '%s\n%s\n' "$$" "$SCALESET_JIT_CONFIG" > "$offer/.incoming" &&
            mv "$offer/.incoming" "$offer/config" || exit 125
        while [ ! -f "$handoff/claimed" ]; do
            within_deadline || exit 125
            sleep 0.1
        done
        within_deadline || exit 125
        : > "$handoff/accepted"
        """;
    private const int MaximumContainerListBytes = 16 * 1024 * 1024;
    private readonly DockerProviderOptionsSnapshot _options;
    private readonly DockerRunnerProfileSnapshot _profile;
    private readonly int _handoffCentiseconds;
    private readonly DockerEngineClient _engine;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<DockerRunnerProvider>? _logger;
    private int _disposed;

    /// <summary>Creates a provider bound to a snapshot of one runner profile.</summary>
    /// <param name="runnerProfile">The immutable-at-construction runner execution profile.</param>
    /// <param name="options">Docker transport and runner process settings.</param>
    /// <param name="httpClient">An optional caller-owned HTTP client for tests or custom TLS transports; configure its handler not to follow redirects.</param>
    /// <param name="logger">An optional Microsoft logging abstraction.</param>
    /// <param name="timeProvider">An optional clock and timer source for deterministic request timeouts and durations.</param>
    public DockerRunnerProvider(
        RunnerProfileOptions runnerProfile,
        DockerProviderOptions? options = null,
        HttpClient? httpClient = null,
        ILogger<DockerRunnerProvider>? logger = null,
        TimeProvider? timeProvider = null)
    {
        _options = DockerProviderOptionsSnapshot.Capture(options, callerOwnsHttpClient: httpClient is not null);
        _profile = DockerRunnerProfileSnapshot.Capture(runnerProfile);
        if (_profile.DockerBuildMode == DockerBuildMode.HostSocket && !_options.AllowHostSocket)
        {
            throw new ArgumentException("The HostSocket profile requires explicit AllowHostSocket opt-in.", nameof(options));
        }

        _handoffCentiseconds = (int)Math.Min(MaximumHandoffCentiseconds, Math.Ceiling(_options.RequestTimeout.TotalMilliseconds / 10));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _engine = new DockerEngineClient(_options, httpClient, _timeProvider);
        _logger = logger;
        if (_profile.DockerBuildMode == DockerBuildMode.HostSocket && logger is not null)
        {
            LogHostSocketEnabled(logger);
        }
        Capabilities = new RunnerProviderCapabilities(
            "docker",
            ImmutableArray.Create(RunnerArchitecture.X64, RunnerArchitecture.Arm64),
            supportsOwnershipScopedDiscovery: true,
            providesTerminalStateEvidence: true)
        {
            RequiresJitConfigurationAtStart = true,
        };
    }

    /// <inheritdoc />
    public RunnerProviderCapabilities Capabilities { get; }

    /// <inheritdoc />
    public async Task<ImmutableArray<RunnerResource>> DiscoverAsync(
        RunnerOwnershipScope scope,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ValidateScope(scope);
        await _engine.EnsureCompatibleEngineAsync(cancellationToken).ConfigureAwait(false);

        string[] scopeFilters = BuildScopeLabels(scope)
            .Pairs
            .Select(static pair => $"{pair.Key}={pair.Value}")
            .ToArray();
        string filters = JsonSerializer.Serialize(new Dictionary<string, string[]>
        {
            ["label"] = scopeFilters,
        });
        string path = $"v{_engine.ApiVersion}/containers/json?all=1&filters={Uri.EscapeDataString(filters)}";
        DockerApiResult<DockerContainerSummaryDto[]> result = await _engine.SendJsonAsync<DockerContainerSummaryDto[]>(
            HttpMethod.Get,
            path,
            "list-containers",
            _options.RequestTimeout,
            [HttpStatusCode.OK],
            parseStatuses: [HttpStatusCode.OK],
            maximumResponseBytes: MaximumContainerListBytes,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        DockerContainerSummaryDto[]? containers = result.Content;
        if (containers is null)
        {
            throw ProviderFailure("list-containers", "invalid-engine-response");
        }

        ImmutableArray<RunnerResource>.Builder resources = ImmutableArray.CreateBuilder<RunnerResource>();
        foreach (DockerContainerSummaryDto container in containers)
        {
            Dictionary<string, string>? labels = container.Labels;
            if (!HasScopeLabels(labels, scope))
            {
                continue;
            }

            string? containerId = container.Id;
            if (!IsValidContainerId(containerId))
            {
                throw ProviderFailure("list-containers", "invalid-owned-container-id");
            }

            RunnerIdentity? identity = CreateIdentityFromLabels(labels, scope);
            if (identity is null)
            {
                if (_logger is not null)
                {
                    LogInvalidContainerLabels(_logger);
                }

                continue;
            }

            RunnerResource? resource = await InspectResourceAsync(
                containerId!,
                identity,
                cancellationToken).ConfigureAwait(false);
            resources.Add(resource ?? new RunnerResource(containerId!, identity, RunnerResourceStatus.Missing));
        }

        return resources.ToImmutable();
    }

    /// <inheritdoc />
    public async Task<RunnerResource> CreateAsync(
        RunnerCreateRequest request,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(request);
        ValidateIdentity(request.Identity);
        await _engine.EnsureCompatibleEngineAsync(cancellationToken).ConfigureAwait(false);
        string platform = GetPlatform(_profile.Architecture);
        if (string.Equals(_engine.ApiVersion, "1.40", StringComparison.Ordinal)
            && !string.Equals(MapDaemonArchitecture(_engine.DaemonArchitecture), platform[("linux/".Length)..], StringComparison.Ordinal))
        {
            throw ProviderFailure("create-container", "api-version-cannot-enforce-platform");
        }

        DockerImageInspectResponse image = await EnsureImageAvailableAsync(platform, cancellationToken).ConfigureAwait(false);
        string containerName = CreateContainerName(request.Identity);
        string nameQuery = $"name={Uri.EscapeDataString(containerName)}";
        string platformQuery = string.Equals(_engine.ApiVersion, "1.40", StringComparison.Ordinal)
            ? string.Empty
            : $"&platform={Uri.EscapeDataString(platform)}";
        string path = $"v{_engine.ApiVersion}/containers/create?{nameQuery}{platformQuery}";
        DockerContainerCreateBody body = CreateBody(request.Identity, image.Config?.Entrypoint ?? []);
        DockerApiResult<DockerContainerCreateResponse> result = await _engine.SendJsonAsync<DockerContainerCreateResponse>(
            HttpMethod.Post,
            path,
            "create-container",
            _options.RequestTimeout,
            [HttpStatusCode.Created, HttpStatusCode.Conflict],
            parseStatuses: [HttpStatusCode.Created],
            requestBody: body,
            outcomeMayBeUnknown: true,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        if (result.StatusCode == HttpStatusCode.Conflict)
        {
            RunnerResource? existing = await InspectResourceAsync(
                containerName,
                request.Identity,
                cancellationToken).ConfigureAwait(false);
            if (existing is null)
            {
                throw ProviderFailure("create-container", "deterministic-name-conflict");
            }

            if (_logger is not null)
            {
                LogCreateReconciled(_logger);
            }
            return existing;
        }

        string? containerId = result.Content?.Id;
        if (!IsValidContainerId(containerId))
        {
            throw new DockerProviderException("create-container", "invalid-engine-response", outcomeMayBeUnknown: true);
        }

        if (_logger is not null)
        {
            LogCreateCompleted(_logger);
        }
        return new RunnerResource(containerId!, request.Identity, RunnerResourceStatus.Created);
    }

    /// <inheritdoc />
    public async Task<RunnerResource> StartAsync(
        RunnerResource resource, string? encodedJitConfig, CancellationToken cancellationToken = default)
    {
        if (encodedJitConfig is not null && (string.IsNullOrWhiteSpace(encodedJitConfig) || encodedJitConfig.Any(char.IsControl)
            || Encoding.UTF8.GetByteCount(encodedJitConfig) > MaximumJitConfigurationBytes))
        {
            throw new ArgumentException("JIT configuration must be nonempty, single-line, and at most 96 KiB.", nameof(encodedJitConfig));
        }

        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(resource);
        ValidateIdentity(resource.Identity);
        ValidateProviderResourceId(resource.ProviderResourceId);
        await _engine.EnsureCompatibleEngineAsync(cancellationToken).ConfigureAwait(false);

        RunnerResource? current = await InspectResourceAsync(
            resource.ProviderResourceId,
            resource.Identity,
            cancellationToken,
            requireJitBootstrap: encodedJitConfig is not null).ConfigureAwait(false);
        if (current is null)
        {
            throw ProviderFailure("start-container", "resource-missing");
        }

        if (current.Status == RunnerResourceStatus.Running)
        {
            if (encodedJitConfig is not null)
            {
                throw new DockerProviderException("start-container", "jit-handoff-unconfirmed", outcomeMayBeUnknown: true);
            }

            return current;
        }

        if (current.Status != RunnerResourceStatus.Created)
        {
            throw ProviderFailure("start-container", "resource-not-startable");
        }

        if (encodedJitConfig is null)
        {
            throw ProviderFailure("start-container", "jit-handoff-required");
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(_handoffCentiseconds * 10), _timeProvider);
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        try
        {
            return await StartWithHandoffAsync(current, encodedJitConfig, budget.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new DockerProviderException("start-container", "request-timeout", outcomeMayBeUnknown: true);
        }
    }

    private async Task<RunnerResource> StartWithHandoffAsync(
        RunnerResource current, string encodedJitConfig, CancellationToken cancellationToken)
    {
        string path = $"v{_engine.ApiVersion}/containers/{Uri.EscapeDataString(current.ProviderResourceId)}/start";
        DockerApiResult<JsonElement> start = await _engine.SendJsonAsync<JsonElement>(
            HttpMethod.Post,
            path,
            "start-container",
            _options.RequestTimeout,
            [HttpStatusCode.NoContent, HttpStatusCode.NotModified],
            parseStatuses: [],
            outcomeMayBeUnknown: true,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        if (start.StatusCode != HttpStatusCode.NoContent)
        {
            throw new DockerProviderException("start-container", "jit-handoff-unconfirmed", outcomeMayBeUnknown: true);
        }

        await SendJitToBootstrapAsync(current.ProviderResourceId, encodedJitConfig, cancellationToken).ConfigureAwait(false);
        RunnerResource? started;
        try
        {
            started = await InspectResourceAsync(
                current.ProviderResourceId,
                current.Identity,
                cancellationToken).ConfigureAwait(false);
        }
        catch (DockerProviderException exception) when (!exception.OutcomeMayBeUnknown)
        {
            throw new DockerProviderException(exception.Operation, exception.FailureCode, exception.StatusCode, outcomeMayBeUnknown: true);
        }
        catch (ArgumentException)
        {
            throw new DockerProviderException("start-container", "invalid-engine-response", outcomeMayBeUnknown: true);
        }

        if (started is null)
        {
            throw new DockerProviderException("start-container", "resource-disappeared-after-start", outcomeMayBeUnknown: true);
        }

        bool running = started is { Status: RunnerResourceStatus.Running, StartedAt: not null };
        bool terminal = started.Status is RunnerResourceStatus.Exited or RunnerResourceStatus.Dead
            && started is { StartedAt: not null, FinishedAt: not null, ExitCode: not null };
        if (!running && !terminal)
        {
            throw new DockerProviderException("start-container", "invalid-post-handoff-state", outcomeMayBeUnknown: true);
        }

        if (_logger is not null)
        {
            LogStartCompleted(_logger);
        }
        return started;
    }

    private async Task SendJitToBootstrapAsync(string containerId, string encodedJitConfig, CancellationToken cancellationToken)
    {
        string version = _engine.ApiVersion;
        string execEndpoint = $"v{version}/exec";
        var created = await _engine.SendJsonAsync<DockerContainerCreateResponse>(
            HttpMethod.Post, $"v{version}/containers/{containerId}/exec", "jit-exec-create", _options.RequestTimeout,
            [HttpStatusCode.Created], [HttpStatusCode.Created],
            requestBody: new DockerExecCreateBody(["/bin/sh", "-c", JitHandoffScript, "scaleset-handoff", HandoffBudgetArgument], [$"SCALESET_JIT_CONFIG={encodedJitConfig}"]),
            outcomeMayBeUnknown: true, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!IsValidContainerId(created.Content?.Id))
        {
            throw new DockerProviderException("jit-exec-create", "invalid-engine-response", outcomeMayBeUnknown: true);
        }

        string execId = created.Content!.Id!;
        _ = await _engine.SendJsonAsync<JsonElement>(
            HttpMethod.Post, $"{execEndpoint}/{execId}/start", "jit-exec-start", _options.RequestTimeout,
            [HttpStatusCode.OK], [], requestBody: new DockerExecStartBody(true), outcomeMayBeUnknown: true,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        while (true)
        {
            var inspected = await _engine.SendJsonAsync<DockerExecInspectResponse>(
                HttpMethod.Get, $"{execEndpoint}/{execId}/json", "jit-exec-inspect", _options.RequestTimeout,
                [HttpStatusCode.OK], [HttpStatusCode.OK], outcomeMayBeUnknown: true,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            if (inspected.Content is not { } status || status.ContainerId != containerId || status.Running is null)
            {
                throw new DockerProviderException("jit-exec-inspect", "invalid-engine-response", outcomeMayBeUnknown: true);
            }

            if (!status.Running.Value)
            {
                if (status.ExitCode is null)
                {
                    throw new DockerProviderException("jit-exec-inspect", "invalid-engine-response", outcomeMayBeUnknown: true);
                }

                if (status.ExitCode != 0)
                {
                    throw new DockerProviderException("jit-exec-inspect", "jit-bootstrap-handoff-failed", outcomeMayBeUnknown: true);
                }

                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(50), _timeProvider, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async Task<RunnerResource?> InspectAsync(
        RunnerResource resource,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(resource);
        ValidateIdentity(resource.Identity);
        ValidateProviderResourceId(resource.ProviderResourceId);
        await _engine.EnsureCompatibleEngineAsync(cancellationToken).ConfigureAwait(false);
        return await InspectResourceAsync(resource.ProviderResourceId, resource.Identity, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task RemoveAsync(
        RunnerResource resource,
        bool force,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(resource);
        ValidateIdentity(resource.Identity);
        ValidateProviderResourceId(resource.ProviderResourceId);
        await _engine.EnsureCompatibleEngineAsync(cancellationToken).ConfigureAwait(false);

        RunnerResource? current = await InspectResourceAsync(
            resource.ProviderResourceId,
            resource.Identity,
            cancellationToken).ConfigureAwait(false);
        if (current is null)
        {
            return;
        }

        if (!force && current.Status is RunnerResourceStatus.Running or RunnerResourceStatus.Unknown)
        {
            throw ProviderFailure("remove-container", "resource-may-be-active");
        }

        string forceQuery = force ? "?v=1&force=1" : "?v=1";
        string path = $"v{_engine.ApiVersion}/containers/{Uri.EscapeDataString(current.ProviderResourceId)}{forceQuery}";
        DockerApiResult<JsonElement> result = await _engine.SendJsonAsync<JsonElement>(
            HttpMethod.Delete,
            path,
            "remove-container",
            _options.RequestTimeout,
            [HttpStatusCode.NoContent, HttpStatusCode.NotFound],
            parseStatuses: [],
            cancellationToken: cancellationToken).ConfigureAwait(false);

        if (_logger?.IsEnabled(LogLevel.Debug) == true)
        {
            LogRemoveCompleted(_logger, (int)result.StatusCode);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _engine.Dispose();
    }

    private async Task<DockerImageInspectResponse> EnsureImageAvailableAsync(string platform, CancellationToken cancellationToken)
    {
        string imagePath = $"v{_engine.ApiVersion}/images/{Uri.EscapeDataString(_profile.Image)}/json";
        DockerApiResult<DockerImageInspectResponse> image = await _engine.SendJsonAsync<DockerImageInspectResponse>(
            HttpMethod.Get,
            imagePath,
            "inspect-image",
            _options.RequestTimeout,
            [HttpStatusCode.OK, HttpStatusCode.NotFound],
            parseStatuses: [HttpStatusCode.OK],
            cancellationToken: cancellationToken).ConfigureAwait(false);
        if (image.StatusCode == HttpStatusCode.OK && MatchesImagePlatform(image.Content, platform))
        {
            return image.Content!;
        }

        if (!_options.PullImageIfMissing)
        {
            throw ProviderFailure("inspect-image", image.StatusCode == HttpStatusCode.OK
                ? "pinned-image-platform-mismatch" : "pinned-image-not-available");
        }

        await _engine.PullImageAsync(_profile.Image, platform, _options, cancellationToken).ConfigureAwait(false);
        DockerApiResult<DockerImageInspectResponse> pulledImage = await _engine.SendJsonAsync<DockerImageInspectResponse>(
            HttpMethod.Get,
            imagePath,
            "inspect-image",
            _options.RequestTimeout,
            [HttpStatusCode.OK, HttpStatusCode.NotFound],
            parseStatuses: [HttpStatusCode.OK],
            cancellationToken: cancellationToken).ConfigureAwait(false);
        if (pulledImage.StatusCode != HttpStatusCode.OK || !MatchesImagePlatform(pulledImage.Content, platform))
        {
            throw ProviderFailure("inspect-image", "image-pull-not-confirmed");
        }

        return pulledImage.Content!;
    }

    private static bool MatchesImagePlatform(DockerImageInspectResponse? image, string platform) =>
        image is not null
        && string.Equals(image.Os, "linux", StringComparison.OrdinalIgnoreCase)
        && string.Equals(MapDaemonArchitecture(image.Architecture), platform["linux/".Length..], StringComparison.Ordinal);

    private async Task<RunnerResource?> InspectResourceAsync(
        string providerResourceId,
        RunnerIdentity expectedIdentity,
        CancellationToken cancellationToken,
        bool requireJitBootstrap = false)
    {
        string path = $"v{_engine.ApiVersion}/containers/{Uri.EscapeDataString(providerResourceId)}/json";
        DockerApiResult<DockerContainerInspectResponse> result = await _engine.SendJsonAsync<DockerContainerInspectResponse>(
            HttpMethod.Get,
            path,
            "inspect-container",
            _options.RequestTimeout,
            [HttpStatusCode.OK, HttpStatusCode.NotFound],
            parseStatuses: [HttpStatusCode.OK],
            cancellationToken: cancellationToken).ConfigureAwait(false);
        if (result.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        DockerContainerInspectResponse inspected = result.Content
            ?? throw ProviderFailure("inspect-container", "invalid-engine-response");
        Dictionary<string, string>? labels = inspected.Config?.Labels;
        if (!TryMatchIdentityLabels(labels, expectedIdentity, out RunnerIdentity actualIdentity))
        {
            throw ProviderFailure("inspect-container", "resource-ownership-mismatch");
        }

        string? id = inspected.Id;
        if (id is not { Length: 64 } || !IsValidContainerId(id))
        {
            throw ProviderFailure("inspect-container", "invalid-engine-response");
        }

        if (IsValidContainerId(providerResourceId) && !id.StartsWith(providerResourceId, StringComparison.OrdinalIgnoreCase))
        {
            throw ProviderFailure("inspect-container", "resource-id-mismatch");
        }

        RunnerResource resource = MapResource(id, actualIdentity, inspected.State, inspected.HostConfig);
        if (requireJitBootstrap && resource.Status == RunnerResourceStatus.Created
            && (labels?.TryGetValue(JitBootstrapLabel, out string? bootstrap) != true || bootstrap != JitBootstrapVersion))
        {
            throw ProviderFailure("start-container", "jit-bootstrap-required");
        }

        return resource;
    }

    private static RunnerResource MapResource(
        string id,
        RunnerIdentity identity,
        DockerContainerStateDto? state,
        DockerContainerHostConfigDto? hostConfig)
    {
        RunnerResourceStatus status = state?.Status?.ToLowerInvariant() switch
        {
            "created" => RunnerResourceStatus.Created,
            "running" => RunnerResourceStatus.Running,
            "exited" => RunnerResourceStatus.Exited,
            "dead" => RunnerResourceStatus.Dead,
            _ => RunnerResourceStatus.Unknown,
        };
        int? exitCode = (status is RunnerResourceStatus.Exited or RunnerResourceStatus.Dead)
            && state?.ExitCode is long code
            && code is >= int.MinValue and <= int.MaxValue
                ? (int)code
                : null;
        return new RunnerResource(
            id,
            identity,
            status,
            exitCode,
            ParseDockerTimestamp(state?.StartedAt),
            status is RunnerResourceStatus.Exited or RunnerResourceStatus.Dead
                ? ParseDockerTimestamp(state?.FinishedAt)
                : null,
            MapCapacityResources(hostConfig));
    }

    private static CapacityResourceQuantities? MapCapacityResources(DockerContainerHostConfigDto? hostConfig)
    {
        if (hostConfig is null)
        {
            return null;
        }

        decimal? cpuCores = hostConfig.NanoCpus is > 0
            ? hostConfig.NanoCpus.Value / 1_000_000_000m
            : null;
        long? memoryMiB = hostConfig.Memory is > 0
            ? (hostConfig.Memory.Value / 1_048_576) + (hostConfig.Memory.Value % 1_048_576 == 0 ? 0 : 1)
            : null;
        return new CapacityResourceQuantities(cpuCores, memoryMiB);
    }

    private static DateTimeOffset? ParseDockerTimestamp(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.StartsWith("0001-01-01T00:00:00", StringComparison.Ordinal))
        {
            return null;
        }

        return DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out DateTimeOffset timestamp)
                ? timestamp
                : null;
    }

    private static DockerIdentityScopeLabels BuildScopeLabels(RunnerOwnershipScope scope) => new(
        [
            new KeyValuePair<string, string>(DockerProviderLabels.Product, ProductLabelValue),
            new KeyValuePair<string, string>(DockerProviderLabels.ControllerId, scope.ControllerId),
            new KeyValuePair<string, string>(DockerProviderLabels.ScaleSetKey, scope.ScaleSetKey),
            new KeyValuePair<string, string>(DockerProviderLabels.ScaleSetId, scope.ScaleSetId.ToString(CultureInfo.InvariantCulture)),
        ]);

    private static Dictionary<string, string> BuildIdentityLabels(RunnerIdentity identity)
    {
        Dictionary<string, string> labels = BuildScopeLabels(identity.Scope).Pairs
            .ToDictionary(static pair => pair.Key, static pair => pair.Value, StringComparer.Ordinal);
        labels.Add(DockerProviderLabels.OperationId, identity.OperationId.ToString("D", CultureInfo.InvariantCulture));
        labels.Add(DockerProviderLabels.RunnerName, identity.RunnerName);
        if (identity.GitHubRunnerId is long runnerId)
        {
            labels.Add(DockerProviderLabels.GitHubRunnerId, runnerId.ToString(CultureInfo.InvariantCulture));
        }

        return labels;
    }

    private static bool HasScopeLabels(Dictionary<string, string>? labels, RunnerOwnershipScope scope)
    {
        if (labels is null)
        {
            return false;
        }

        foreach ((string key, string value) in BuildScopeLabels(scope).Pairs)
        {
            if (!labels.TryGetValue(key, out string? actual) || !string.Equals(actual, value, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static RunnerIdentity? CreateIdentityFromLabels(
        Dictionary<string, string>? labels,
        RunnerOwnershipScope expectedScope)
    {
        if (!HasScopeLabels(labels, expectedScope)
            || labels is null
            || !labels.TryGetValue(DockerProviderLabels.OperationId, out string? operationValue)
            || !Guid.TryParseExact(operationValue, "D", out Guid operationId)
            || operationId == Guid.Empty
            || !labels.TryGetValue(DockerProviderLabels.RunnerName, out string? runnerName)
            || !IsSafeIdentityText(runnerName))
        {
            return null;
        }

        long? runnerId = null;
        if (labels.TryGetValue(DockerProviderLabels.GitHubRunnerId, out string? runnerIdValue))
        {
            if (!long.TryParse(runnerIdValue, NumberStyles.None, CultureInfo.InvariantCulture, out long parsedRunnerId)
                || parsedRunnerId <= 0)
            {
                return null;
            }

            runnerId = parsedRunnerId;
        }

        return new RunnerIdentity(expectedScope, operationId, runnerName!, runnerId);
    }

    private static bool TryMatchIdentityLabels(
        Dictionary<string, string>? labels,
        RunnerIdentity expected,
        out RunnerIdentity actual)
    {
        actual = expected;
        if (!HasScopeLabels(labels, expected.Scope)
            || labels is null
            || !labels.TryGetValue(DockerProviderLabels.OperationId, out string? operationValue)
            || !string.Equals(operationValue, expected.OperationId.ToString("D", CultureInfo.InvariantCulture), StringComparison.Ordinal)
            || !labels.TryGetValue(DockerProviderLabels.RunnerName, out string? runnerName)
            || !string.Equals(runnerName, expected.RunnerName, StringComparison.Ordinal))
        {
            return false;
        }

        bool hasRunnerIdLabel = labels.TryGetValue(DockerProviderLabels.GitHubRunnerId, out string? runnerIdValue);
        if (!hasRunnerIdLabel)
        {
            return expected.GitHubRunnerId is null;
        }

        if (!long.TryParse(runnerIdValue, NumberStyles.None, CultureInfo.InvariantCulture, out long runnerId)
            || runnerId <= 0
            || (expected.GitHubRunnerId is long expectedRunnerId && expectedRunnerId != runnerId))
        {
            return false;
        }

        actual = expected.GitHubRunnerId is null
            ? new RunnerIdentity(
                expected.Scope,
                expected.OperationId,
                expected.RunnerName,
                runnerId,
                expected.CapacityResources)
            : expected;
        return true;
    }

    private static string CreateContainerName(RunnerIdentity identity)
    {
        StringBuilder input = new();
        AppendHashField(input, identity.Scope.ControllerId);
        AppendHashField(input, identity.Scope.ScaleSetKey);
        AppendHashField(input, identity.Scope.ScaleSetId.ToString(CultureInfo.InvariantCulture));
        AppendHashField(input, identity.OperationId.ToString("D", CultureInfo.InvariantCulture));
        AppendHashField(input, identity.RunnerName);
        AppendHashField(input, identity.GitHubRunnerId?.ToString(CultureInfo.InvariantCulture) ?? string.Empty);
        byte[] identityBytes = Encoding.UTF8.GetBytes(input.ToString());
        try
        {
            byte[] hash = SHA256.HashData(identityBytes);
            return $"scaleset-{Convert.ToHexString(hash.AsSpan(0, 16)).ToLowerInvariant()}";
        }
        finally
        {
            CryptographicOperations.ZeroMemory(identityBytes);
        }
    }

    private static void AppendHashField(StringBuilder builder, string value) =>
        builder.Append(value.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(value);

    private DockerContainerCreateBody CreateBody(RunnerIdentity identity, ImmutableArray<string> entrypoint)
    {
        Dictionary<string, string> labels = BuildIdentityLabels(identity);
        labels.Add(JitBootstrapLabel, JitBootstrapVersion);
        ImmutableArray<string>? binds = _profile.DockerBuildMode == DockerBuildMode.HostSocket
            ? ImmutableArray.Create($"{_options.HostDockerSocketPath}:{ContainerSocketPath}")
            : null;

        bool hardened = !_profile.Privileged && _options.HardenUnprivilegedContainers;
        DockerHostConfiguration hostConfiguration = new(
            _profile.NanoCpus,
            _profile.MemoryBytes,
            _profile.Privileged,
            false,
            binds,
            _profile.Network,
            hardened ? ["ALL"] : null,
            hardened ? ["no-new-privileges:true"] : null,
            !_profile.Privileged && _options.PidsLimit > 0 ? _options.PidsLimit : null,
            hardened ? _profile.MemoryBytes : null,
            new Dictionary<string, string>(StringComparer.Ordinal) { [JitMountPath] = "rw,nosuid,nodev,noexec,size=2m,mode=1777" });
        return new DockerContainerCreateBody(
            _profile.Image,
            _options.User,
            entrypoint.AddRange(_options.Command),
            ["/bin/sh", "-c", BootstrapScript, "scaleset-init", HandoffBudgetArgument],
            labels,
            _options.WorkingDirectory,
            hostConfiguration);
    }

    private string HandoffBudgetArgument => _handoffCentiseconds.ToString(CultureInfo.InvariantCulture);

    private static string GetPlatform(RunnerArchitecture architecture) => architecture switch
    {
        RunnerArchitecture.X64 => "linux/amd64",
        RunnerArchitecture.Arm64 => "linux/arm64",
        _ => throw new ArgumentOutOfRangeException(nameof(architecture)),
    };

    private static string? MapDaemonArchitecture(string? architecture) => architecture?.ToLowerInvariant() switch
    {
        "amd64" or "x86_64" or "x64" => "amd64",
        "arm64" or "aarch64" or "armv8" => "arm64",
        _ => null,
    };

    private static void ValidateScope(RunnerOwnershipScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        _ = DockerProviderOptionsSnapshot.ValidateText(scope.ControllerId, nameof(scope.ControllerId), 256);
        _ = DockerProviderOptionsSnapshot.ValidateText(scope.ScaleSetKey, nameof(scope.ScaleSetKey), 256);
        if (scope.ScaleSetId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(scope), "The scale-set ID must be positive.");
        }
    }

    private static void ValidateIdentity(RunnerIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ValidateScope(identity.Scope);
        if (identity.OperationId == Guid.Empty)
        {
            throw new ArgumentException("The operation ID must not be empty.", nameof(identity));
        }

        _ = DockerProviderOptionsSnapshot.ValidateText(identity.RunnerName, nameof(identity.RunnerName), 256);
        if (identity.GitHubRunnerId is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(identity), "The GitHub runner ID must be positive when present.");
        }
    }

    private static bool IsSafeIdentityText(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length <= 256
        && !value.Any(char.IsControl);

    private static bool IsValidContainerId(string? id) =>
        !string.IsNullOrWhiteSpace(id)
        && id.Length is >= 12 and <= 64
        && id.All(Uri.IsHexDigit);

    private static void ValidateProviderResourceId(string id)
    {
        if (!IsValidContainerId(id))
        {
            throw new ArgumentException("ProviderResourceId must be a Docker container ID.", nameof(id));
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
    }

    private static DockerProviderException ProviderFailure(string operation, string failureCode) =>
        new(operation, failureCode);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Ignored a Docker container whose immutable labels do not identify a ScaleSet runner.")]
    private static partial void LogInvalidContainerLabels(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The HostSocket runner profile gives jobs control of the host Docker daemon. Use this profile only for trusted jobs.")]
    private static partial void LogHostSocketEnabled(ILogger logger);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Docker runner container create reconciled an existing resource.")]
    private static partial void LogCreateReconciled(ILogger logger);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Docker runner container create completed.")]
    private static partial void LogCreateCompleted(ILogger logger);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Docker runner container acknowledged the credential handoff.")]
    private static partial void LogStartCompleted(ILogger logger);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Docker runner container remove completed with HTTP {StatusCode}.")]
    private static partial void LogRemoveCompleted(ILogger logger, int statusCode);

    private sealed record DockerIdentityScopeLabels(ImmutableArray<KeyValuePair<string, string>> Pairs);

    private sealed record DockerContainerCreateBody(
        [property: JsonPropertyName("Image")] string Image,
        [property: JsonPropertyName("User")] string User,
        [property: JsonPropertyName("Cmd")] ImmutableArray<string> Command,
        [property: JsonPropertyName("Entrypoint")] ImmutableArray<string> Entrypoint,
        [property: JsonPropertyName("Labels")] Dictionary<string, string> Labels,
        [property: JsonPropertyName("WorkingDir")] string? WorkingDirectory,
        [property: JsonPropertyName("HostConfig")] DockerHostConfiguration HostConfig);

    private sealed record DockerHostConfiguration(
        [property: JsonPropertyName("NanoCpus")] long NanoCpus,
        [property: JsonPropertyName("Memory")] long Memory,
        [property: JsonPropertyName("Privileged")] bool Privileged,
        [property: JsonPropertyName("AutoRemove")] bool AutoRemove,
        [property: JsonPropertyName("Binds")] ImmutableArray<string>? Binds,
        [property: JsonPropertyName("NetworkMode")] string? NetworkMode,
        [property: JsonPropertyName("CapDrop")] ImmutableArray<string>? CapDrop,
        [property: JsonPropertyName("SecurityOpt")] ImmutableArray<string>? SecurityOpt,
        [property: JsonPropertyName("PidsLimit")] long? PidsLimit,
        [property: JsonPropertyName("MemorySwap")] long? MemorySwap,
        [property: JsonPropertyName("Tmpfs")] Dictionary<string, string> Tmpfs);

    private sealed record DockerExecCreateBody(
        [property: JsonPropertyName("Cmd")] ImmutableArray<string> Command,
        [property: JsonPropertyName("Env")] ImmutableArray<string> Environment);

    private sealed record DockerExecStartBody([property: JsonPropertyName("Detach")] bool Detach);

    private sealed class DockerExecInspectResponse
    {
        [JsonPropertyName("ContainerID")]
        public string? ContainerId { get; init; }

        [JsonPropertyName("Running")]
        public bool? Running { get; init; }

        [JsonPropertyName("ExitCode")]
        public int? ExitCode { get; init; }
    }

    private sealed class DockerContainerCreateResponse
    {
        [JsonPropertyName("Id")]
        public string? Id { get; init; }
    }

    private sealed class DockerImageInspectResponse
    {
        [JsonPropertyName("Os")]
        public string? Os { get; init; }

        [JsonPropertyName("Architecture")]
        public string? Architecture { get; init; }

        [JsonPropertyName("Config")]
        public DockerImageConfigDto? Config { get; init; }
    }

    private sealed class DockerImageConfigDto
    {
        [JsonPropertyName("Entrypoint")]
        public ImmutableArray<string>? Entrypoint { get; init; }
    }

    private sealed class DockerContainerSummaryDto
    {
        [JsonPropertyName("Id")]
        public string? Id { get; init; }

        [JsonPropertyName("Labels")]
        public Dictionary<string, string>? Labels { get; init; }
    }

    private sealed class DockerContainerInspectResponse
    {
        [JsonPropertyName("Id")]
        public string? Id { get; init; }

        [JsonPropertyName("Config")]
        public DockerContainerConfigDto? Config { get; init; }

        [JsonPropertyName("State")]
        public DockerContainerStateDto? State { get; init; }

        [JsonPropertyName("HostConfig")]
        public DockerContainerHostConfigDto? HostConfig { get; init; }
    }

    private sealed class DockerContainerConfigDto
    {
        [JsonPropertyName("Labels")]
        public Dictionary<string, string>? Labels { get; init; }
    }

    private sealed class DockerContainerStateDto
    {
        [JsonPropertyName("Status")]
        public string? Status { get; init; }

        [JsonPropertyName("ExitCode")]
        public long? ExitCode { get; init; }

        [JsonPropertyName("StartedAt")]
        public string? StartedAt { get; init; }

        [JsonPropertyName("FinishedAt")]
        public string? FinishedAt { get; init; }
    }

    private sealed class DockerContainerHostConfigDto
    {
        [JsonPropertyName("NanoCpus")]
        public long? NanoCpus { get; init; }

        [JsonPropertyName("Memory")]
        public long? Memory { get; init; }
    }
}
