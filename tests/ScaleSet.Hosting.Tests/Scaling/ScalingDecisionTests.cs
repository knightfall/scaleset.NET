using ScaleSet.Hosting.Configuration;
using ScaleSet.Hosting.Scaling;

namespace ScaleSet.Hosting.Tests.Scaling;

public sealed class ScalingDecisionTests
{
    [Theory]
    [InlineData(0, 0, 10, 0)]
    [InlineData(1, 0, 10, 1)]
    [InlineData(7, 2, 10, 9)]
    [InlineData(9, 2, 10, 10)]
    [InlineData(100, 2, 10, 10)]
    public void SpareCapacity_UsesAssignedJobsPlusMinimumCappedAtMaximum(
        int assignedJobs,
        int minimumRunners,
        int maximumRunners,
        int expected)
    {
        int desired = ScalingDecision.GetDesiredRunnerCount(
            ScalingPolicy.SpareCapacity,
            assignedJobs,
            minimumRunners,
            maximumRunners);

        Assert.Equal(expected, desired);
    }

    [Theory]
    [InlineData(0, 0, 10, 0)]
    [InlineData(1, 3, 10, 3)]
    [InlineData(5, 3, 10, 5)]
    [InlineData(11, 3, 10, 10)]
    public void TotalFloor_UsesLargerOfAssignedJobsAndMinimumCappedAtMaximum(
        int assignedJobs,
        int minimumRunners,
        int maximumRunners,
        int expected)
    {
        int desired = ScalingDecision.GetDesiredRunnerCount(
            ScalingPolicy.TotalFloor,
            assignedJobs,
            minimumRunners,
            maximumRunners);

        Assert.Equal(expected, desired);
    }

    [Theory]
    [InlineData(ScalingPolicy.SpareCapacity)]
    [InlineData(ScalingPolicy.TotalFloor)]
    public void AnyPolicy_MaximumZeroPausesProvisioning(ScalingPolicy policy)
    {
        int desired = ScalingDecision.GetDesiredRunnerCount(policy, int.MaxValue, 0, 0);

        Assert.Equal(0, desired);
    }

    [Fact]
    public void SpareCapacity_MaximumAssignedJobsAndMinimum_DoesNotOverflow()
    {
        int desired = ScalingDecision.GetDesiredRunnerCount(
            ScalingPolicy.SpareCapacity,
            int.MaxValue,
            int.MaxValue,
            int.MaxValue);

        Assert.Equal(int.MaxValue, desired);
    }

    [Theory]
    [InlineData(-1, 0, 10, "assignedJobs")]
    [InlineData(0, -1, 10, "minimumRunners")]
    [InlineData(0, 2, -1, "maximumRunners")]
    [InlineData(0, 3, 2, "maximumRunners")]
    public void GetDesiredRunnerCount_NegativeOrInvertedCounts_Throws(
        int assignedJobs,
        int minimumRunners,
        int maximumRunners,
        string parameterName)
    {
        ArgumentOutOfRangeException exception = Assert.Throws<ArgumentOutOfRangeException>(
            () => ScalingDecision.GetDesiredRunnerCount(
                ScalingPolicy.SpareCapacity,
                assignedJobs,
                minimumRunners,
                maximumRunners));

        Assert.Equal(parameterName, exception.ParamName);
    }

    [Fact]
    public void GetDesiredRunnerCount_UnknownPolicy_Throws()
    {
        ArgumentOutOfRangeException exception = Assert.Throws<ArgumentOutOfRangeException>(
            () => ScalingDecision.GetDesiredRunnerCount((ScalingPolicy)99, 1, 0, 10));

        Assert.Equal("policy", exception.ParamName);
    }

    [Fact]
    public void ScalingOptions_DefaultsToTheAcceptedPolicyAndPerSetLimits()
    {
        ScalingOptions options = new();

        Assert.Equal(ScalingPolicy.SpareCapacity, options.Policy);
        Assert.Equal(0, options.MinRunners);
        Assert.Equal(10, options.MaxRunners);
    }
}
