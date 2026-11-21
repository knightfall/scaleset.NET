using ScaleSet.Client;
using Xunit;

namespace ScaleSet.Client.Tests;

public sealed class ScaleSetClientOptionsTests
{
    [Fact]
    public void DefaultTimeoutsUseSixtySecondControlAndSixtyFiveSecondPollBudgets()
    {
        var options = new ScaleSetClientOptions();

        Assert.Equal(TimeSpan.FromSeconds(60), options.ControlRequestTimeout);
        Assert.Equal(TimeSpan.FromSeconds(65), options.PollRequestTimeout);
        options.Validate();
    }

    [Fact]
    public void PollTimeoutMustExceedFiftySeconds()
    {
        var options = new ScaleSetClientOptions
        {
            PollRequestTimeout = TimeSpan.FromSeconds(50),
        };

        Assert.Throws<ArgumentOutOfRangeException>(options.Validate);
    }

    [Fact]
    public void ControlTimeoutCannotExceedTheCancellationTimerRange()
    {
        var options = new ScaleSetClientOptions
        {
            ControlRequestTimeout = TimeSpan.FromMilliseconds((double)uint.MaxValue),
        };

        Assert.Throws<ArgumentOutOfRangeException>(options.Validate);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(11)]
    public void ReadRetryCountIsBounded(int retryCount)
    {
        var options = new ScaleSetClientOptions { MaxReadOnlyRetries = retryCount };

        Assert.Throws<ArgumentOutOfRangeException>(options.Validate);
    }

    [Fact]
    public void RetryDelaysCannotExceedOneMinute()
    {
        var options = new ScaleSetClientOptions
        {
            RetryBaseDelay = TimeSpan.FromMinutes(2),
            RetryMaxDelay = TimeSpan.FromMinutes(2),
        };

        Assert.Throws<ArgumentOutOfRangeException>(options.Validate);
    }

    [Fact]
    public void CustomUserAgentMustUseValidHttpSyntax()
    {
        var options = new ScaleSetClientOptions { UserAgent = "bad user agent\r\nInjected: yes" };

        Assert.Throws<ArgumentException>(options.Validate);
    }

    [Fact]
    public void RetryOptionsPresetsAndValidationEnforceBounds()
    {
        ScaleSetRetryOptions upstream = ScaleSetRetryOptions.CreateUpstreamDefault();
        Assert.Equal(ScaleSetRetryOptions.DefaultMaxRetries, upstream.MaxRetries);
        Assert.Equal(ScaleSetRetryOptions.DefaultWaitMin, upstream.WaitMin);
        Assert.Equal(ScaleSetRetryOptions.DefaultWaitMax, upstream.WaitMax);
        Assert.True(upstream.RetryAllReplayableRequests);
        Assert.Null(upstream.MaxRetryAfterDelay);
        new ScaleSetClientOptions { Retry = upstream }.Validate();

        Assert.Equal(0, ScaleSetRetryOptions.Disabled.MaxRetries);
        new ScaleSetClientOptions { Retry = ScaleSetRetryOptions.Disabled }.Validate();

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ScaleSetClientOptions { Retry = new ScaleSetRetryOptions { MaxRetries = -1 } }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ScaleSetClientOptions { Retry = new ScaleSetRetryOptions { WaitMin = TimeSpan.FromSeconds(-1) } }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ScaleSetClientOptions { Retry = new ScaleSetRetryOptions { WaitMin = TimeSpan.FromSeconds(45) } }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ScaleSetClientOptions { Retry = new ScaleSetRetryOptions { MaxElapsed = TimeSpan.FromSeconds(-1) } }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ScaleSetClientOptions { Retry = new ScaleSetRetryOptions { MaxRetryAfterDelay = TimeSpan.FromSeconds(-1) } }.Validate());

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ScaleSetRateLimitDecision.RetryAfter(TimeSpan.FromSeconds(-1)));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ScaleSetRateLimitPolicies.Create(new ScaleSetRateLimitRule(-1, TimeSpan.Zero), new ScaleSetRateLimitRule()));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ScaleSetRateLimitPolicies.Create(new ScaleSetRateLimitRule(), new ScaleSetRateLimitRule(0, TimeSpan.FromSeconds(-1))));
    }
}
