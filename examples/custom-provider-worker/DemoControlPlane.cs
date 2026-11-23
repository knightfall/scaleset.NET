using System.Threading.Channels;
using ScaleSet.Client;
using ScaleSet.Hosting.Providers;
using ScaleSet.Hosting.Runtime;

namespace ScaleSet.CustomProvider.Example;

internal sealed class DemoControlPlaneFactory(DemoRunnerBackend backend, DemoSession session) : IScaleSetRuntimeClientFactory
{
    public IScaleSetRuntimeClient Create(GitHubTarget target, ScaleSetCredentials credentials, ScaleSetClientOptions options) =>
        new DemoControlPlane(backend, session);
}

internal sealed class DemoControlPlane(DemoRunnerBackend backend, DemoSession session) : IScaleSetRuntimeClient
{
    private const long RunnerGroupId = 20;
    private const long ScaleSetId = 10;

    public const long RunnerId = 30;

    public Task<RunnerGroup> GetRunnerGroupByNameAsync(string name, CancellationToken cancellationToken) =>
        Task.FromResult(new RunnerGroup { Id = RunnerGroupId, Name = name });

    public Task<RunnerScaleSet?> FindScaleSetAsync(long runnerGroupId, string name, CancellationToken cancellationToken) =>
        Task.FromResult<RunnerScaleSet?>(new RunnerScaleSet
        {
            Id = ScaleSetId,
            Name = name,
            RunnerGroupId = runnerGroupId,
            RunnerGroupName = "Default",
        });

    public Task<RunnerScaleSet> CreateScaleSetAsync(RunnerScaleSet scaleSet, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("The demo scale set already exists.");

    public Task<RunnerReference?> GetRunnerAsync(long runnerId, CancellationToken cancellationToken) =>
        Task.FromResult(backend.Current is { Status: RunnerResourceStatus.Running } runner && runnerId == RunnerId
            ? new RunnerReference { Id = runnerId, Name = runner.Identity.RunnerName }
            : null);

    public Task RemoveRunnerAsync(long runnerId, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("The demo registration disappears when the runner completes.");

    public Task<RunnerScaleSetJitRunnerConfig?> GenerateJitConfigAsync(
        long scaleSetId,
        RunnerScaleSetJitRunnerSetting runnerSetting,
        CancellationToken cancellationToken) =>
        Task.FromResult<RunnerScaleSetJitRunnerConfig?>(new RunnerScaleSetJitRunnerConfig
        {
            Runner = new RunnerReference { Id = RunnerId, Name = runnerSetting.Name },
            EncodedJitConfig = "example-jit",
        });

    public Task<IMessageSession> CreateMessageSessionAsync(long scaleSetId, string ownerName, CancellationToken cancellationToken) =>
        Task.FromResult<IMessageSession>(session);

    public void Dispose()
    {
        session.RecordClientDisposal();
    }
}

internal sealed class DemoSession : IMessageSession
{
    private readonly TaskCompletionSource _polled = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Channel<RunnerScaleSetMessage> _messages = Channel.CreateUnbounded<RunnerScaleSetMessage>();
    private readonly Channel<long> _acknowledgments = Channel.CreateUnbounded<long>();

    public RunnerScaleSetSession Session { get; } = new() { SessionId = Guid.NewGuid(), Statistics = new RunnerScaleSetStatistic() };

    public bool Closed { get; private set; }

    public bool ClientDisposedAfterClose { get; private set; }

    public Task Polled => _polled.Task;

    public void RecordClientDisposal() => ClientDisposedAfterClose = Closed;

    public async Task SendAsync(RunnerScaleSetMessage message, CancellationToken cancellationToken)
    {
        await _messages.Writer.WriteAsync(message, cancellationToken).ConfigureAwait(false);
        long acknowledged = await _acknowledgments.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        if (acknowledged != message.MessageId)
        {
            throw new InvalidDataException("The controller acknowledged a different message.");
        }
    }

    public async Task<RunnerScaleSetMessage?> GetMessageAsync(long lastMessageId, int maxCapacity, CancellationToken cancellationToken)
    {
        _polled.TrySetResult();
        return await _messages.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task DeleteMessageAsync(long messageId, CancellationToken cancellationToken) =>
        _acknowledgments.Writer.WriteAsync(messageId, cancellationToken).AsTask();

    public Task<IReadOnlyList<long>> AcquireJobsAsync(IReadOnlyList<long> runnerRequestIds, CancellationToken cancellationToken) =>
        Task.FromResult(runnerRequestIds);

    public Task CloseAsync(CancellationToken cancellationToken)
    {
        Closed = true;
        return Task.CompletedTask;
    }
}
