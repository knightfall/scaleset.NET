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
}
