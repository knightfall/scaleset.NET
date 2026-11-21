using ScaleSet.Client;
using Xunit;

namespace ScaleSet.Client.Tests;

public sealed class ScaleSetApiExceptionTests
{
    [Fact]
    public void ScaleSetApiExceptionPreservesStatusAndDomainClassificationIndependently()
    {
        var exception = new ScaleSetApiException(
            "runner is still busy",
            statusCode: 409,
            domainError: ScaleSetDomainError.JobStillRunning);

        Assert.Equal("runner is still busy", exception.Message);
        Assert.Equal(409, exception.StatusCode);
        Assert.Equal(ScaleSetDomainError.JobStillRunning, exception.DomainError);
    }

    [Fact]
    public void ScaleSetApiExceptionRejectsInvalidHttpStatusCodes()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ScaleSetApiException("bad status", statusCode: 99));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ScaleSetApiException("bad status", statusCode: 600));
    }

    [Fact]
    public void RateLimitAndMaxElapsedExceptionsSupportStandardConstructorsAndValidateInputs()
    {
        ScaleSetRateLimitException defaultRateLimit = new();
        Assert.Equal(ScaleSetRateLimitKind.Secondary, defaultRateLimit.Kind);
        Assert.Equal("bad rate limit", new ScaleSetRateLimitException("bad rate limit").Message);
        InvalidOperationException inner = new("boom");
        ScaleSetRateLimitException wrappedRateLimit = new("wrapped", inner);
        Assert.Same(inner, wrappedRateLimit.InnerException);

        DateTimeOffset reset = new(2026, 11, 21, 12, 0, 0, TimeSpan.Zero);
        ScaleSetRateLimitException richRateLimit = new(
            "rate limited",
            429,
            ScaleSetRateLimitKind.Primary,
            TimeSpan.FromSeconds(30),
            reset,
            retries: 2);
        Assert.Equal(ScaleSetRateLimitKind.Primary, richRateLimit.Kind);
        Assert.Equal(TimeSpan.FromSeconds(30), richRateLimit.RetryAfter);
        Assert.Equal(reset, richRateLimit.Reset);
        Assert.Equal(2, richRateLimit.Retries);

        Assert.Throws<ArgumentOutOfRangeException>(() => new ScaleSetRateLimitException(
            "invalid", 429, (ScaleSetRateLimitKind)99, TimeSpan.Zero, null, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ScaleSetRateLimitException(
            "invalid", 429, ScaleSetRateLimitKind.Primary, TimeSpan.FromSeconds(-1), null, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ScaleSetRateLimitException(
            "invalid", 429, ScaleSetRateLimitKind.Primary, TimeSpan.Zero, null, -1));

        ScaleSetMaxElapsedExceededException defaultBudget = new();
        Assert.NotEmpty(defaultBudget.Message);
        Assert.Equal("budget", new ScaleSetMaxElapsedExceededException("budget").Message);
        Assert.Same(inner, new ScaleSetMaxElapsedExceededException("wrapped", inner).InnerException);

        ScaleSetMaxElapsedExceededException richBudget = new("budget exceeded", TimeSpan.FromSeconds(15), statusCode: 503);
        Assert.Equal(TimeSpan.FromSeconds(15), richBudget.MaxElapsed);
        Assert.Equal(503, richBudget.StatusCode);

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ScaleSetMaxElapsedExceededException("invalid", TimeSpan.FromSeconds(-1)));
    }
}
