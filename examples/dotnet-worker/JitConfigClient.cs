using ScaleSet.Client;
using ScaleSet.Hosting.Controller;

namespace ScaleSet.Worker.Example;

internal sealed class JitConfigClient(ScaleSetClient client) : IRunnerJitConfigClient
{
    public async Task<RunnerScaleSetJitRunnerConfig?> GenerateJitConfigAsync(
        long scaleSetId,
        RunnerScaleSetJitRunnerSetting runnerSetting,
        CancellationToken cancellationToken = default) =>
        await client.GenerateJitConfigAsync(scaleSetId, runnerSetting, cancellationToken);
}
