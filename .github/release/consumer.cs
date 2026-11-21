using System.Globalization;
using ScaleSet.Client;
using ScaleSet.Hosting.Scaling;
using ScaleSet.Listener;
using ScaleSet.Providers.Docker;

GitHubTarget target = GitHubTarget.Parse("https://github.com/knightfall/scaleset.NET");
ScaleSetCredentials credentials = ScaleSetCredentials.FromPersonalAccessToken("synthetic-consumer-token");
using var client = new ScaleSetClient(target, credentials);
ScaleSetRetryOptions retryOptions = ScaleSetRetryOptions.CreateUpstreamDefault();
ScaleSetListenerOptions listenerOptions = new() { ScaleSetId = 42, MaxRunners = 4 };
int desired = ScalingDecision.GetDesiredRunnerCount(
    ScalingPolicy.SpareCapacity,
    assignedJobs: 2,
    minimumRunners: 1,
    maximumRunners: listenerOptions.MaxRunners);
DockerProviderOptions dockerOptions = new();

if (desired != 3 || retryOptions.MaxRetries <= 0 || string.IsNullOrWhiteSpace(dockerOptions.SocketPath))
{
    throw new InvalidOperationException("Published package behavior differs.");
}

Console.WriteLine(string.Create(
    CultureInfo.InvariantCulture,
    $"Verified {target.ApiBaseUri} (desired={desired}, socket={dockerOptions.SocketPath}) across Client, Listener, Hosting, and Providers.Docker."));
