namespace ScaleSet.Hosting.Scaling;

/// <summary>Supported desired-capacity policies.</summary>
public enum ScalingPolicy
{
    /// <summary>Keep the configured minimum number of runners spare above assigned jobs.</summary>
    SpareCapacity = 0,

    /// <summary>Keep at least the configured minimum total number of runners.</summary>
    TotalFloor = 1,
}

/// <summary>Calculates desired runner capacity from the latest assigned-job count.</summary>
public static class ScalingDecision
{
    /// <summary>
    /// Calculates desired total runners, capped by the maximum. The maximum may be zero
    /// to pause provisioning.
    /// </summary>
    /// <param name="policy">The selected scaling policy.</param>
    /// <param name="assignedJobs">The latest observed number of assigned jobs.</param>
    /// <param name="minimumRunners">The configured minimum runner count.</param>
    /// <param name="maximumRunners">The configured maximum runner count.</param>
    /// <returns>The desired total number of runners.</returns>
    /// <exception cref="ArgumentOutOfRangeException">A count is negative or the minimum exceeds the maximum.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The policy is not supported.</exception>
    public static int GetDesiredRunnerCount(
        ScalingPolicy policy,
        int assignedJobs,
        int minimumRunners,
        int maximumRunners)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(assignedJobs);
        ArgumentOutOfRangeException.ThrowIfNegative(minimumRunners);
        ArgumentOutOfRangeException.ThrowIfNegative(maximumRunners);

        ArgumentOutOfRangeException.ThrowIfGreaterThan(minimumRunners, maximumRunners, nameof(maximumRunners));

        return policy switch
        {
            ScalingPolicy.SpareCapacity => CalculateSpareCapacity(assignedJobs, minimumRunners, maximumRunners),
            ScalingPolicy.TotalFloor => Math.Min(maximumRunners, Math.Max(assignedJobs, minimumRunners)),
            _ => throw new ArgumentOutOfRangeException(nameof(policy)),
        };
    }

    private static int CalculateSpareCapacity(int assignedJobs, int minimumRunners, int maximumRunners)
    {
        long desired = (long)assignedJobs + minimumRunners;
        return (int)Math.Min(maximumRunners, desired);
    }
}
