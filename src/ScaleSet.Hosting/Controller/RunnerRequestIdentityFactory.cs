using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using ScaleSet.Hosting.Providers;

namespace ScaleSet.Hosting.Controller;

/// <summary>Creates deterministic provider identities for GitHub runner requests.</summary>
public static class RunnerRequestIdentityFactory
{
    /// <summary>
    /// Creates the same operation ID and safe runner name for redelivery of a
    /// request within the same ownership scope.
    /// </summary>
    /// <param name="scope">The controller and scale-set ownership scope.</param>
    /// <param name="runnerRequestId">The positive GitHub runner-request ID.</param>
    public static RunnerIdentity Create(RunnerOwnershipScope scope, long runnerRequestId)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(runnerRequestId);

        string material = string.Concat(
            "scale-set-runner-request-v1|",
            scope.ControllerId.Length.ToString(CultureInfo.InvariantCulture), ":", scope.ControllerId,
            scope.ScaleSetKey.Length.ToString(CultureInfo.InvariantCulture), ":", scope.ScaleSetKey,
            scope.ScaleSetId.ToString(CultureInfo.InvariantCulture), ":",
            runnerRequestId.ToString(CultureInfo.InvariantCulture));
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(material));
        Guid operationId = new(hash.AsSpan(0, 16));
        if (operationId == Guid.Empty)
        {
            hash[0] = 1;
            operationId = new Guid(hash.AsSpan(0, 16));
        }

        string runnerName = $"ss-{operationId:N}";
        return new RunnerIdentity(scope, operationId, runnerName);
    }
}
