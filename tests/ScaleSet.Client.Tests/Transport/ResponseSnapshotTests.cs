using System.Net;
using System.Text;
using ScaleSet.Client;
using Xunit;

namespace ScaleSet.Client.Tests.Transport;

public sealed class ResponseSnapshotTests
{
    private static readonly GitHubTarget Target = GitHubTarget.Parse("https://github.com/example-org/example-repo");

    [Fact]
    public async Task HttpErrorResponsePopulatesSnapshotWithStatusHeadersAndBoundedBody()
    {
        using HttpClient httpClient = new(CreateConnectedHandler((_, _) =>
        {
            HttpResponseMessage response = new(HttpStatusCode.BadRequest)
            {
                ReasonPhrase = "Bad Request",
                Content = new StringContent("{\"message\":\"invalid scale set\"}", Encoding.UTF8, "application/json"),
            };
            response.Headers.TryAddWithoutValidation("X-GitHub-Request-Id", "req-999");
            response.Headers.TryAddWithoutValidation("ActivityId", "act-111");
            return Task.FromResult(response);
        }));
        using ScaleSetClient client = CreateClient(httpClient);

        ScaleSetApiException exception = await Assert.ThrowsAsync<ScaleSetApiException>(
            () => client.GetScaleSetAsync(1, TestContext.Current.CancellationToken));

        Assert.NotNull(exception.Response);
        Assert.Equal(400, exception.Response.StatusCode);
        Assert.Equal("Bad Request", exception.Response.ReasonPhrase);
        Assert.Equal("req-999", exception.Response.GetHeaderValue("x-github-request-id"));
        Assert.Equal("act-111", exception.Response.GetHeaderValue("ACTIVITYID"));
        Assert.Contains("application/json", exception.Response.GetHeaderValue("Content-Type"), StringComparison.Ordinal);
        Assert.False(exception.Response.IsBodyIncomplete);
        Assert.False(exception.Response.IsBodyTruncated);
        Assert.Equal("{\"message\":\"invalid scale set\"}", Encoding.UTF8.GetString(exception.Response.Body.Span));

        string oversized = new('a', 70_000);
        using HttpClient truncatedHttp = new(CreateConnectedHandler((_, _) =>
            Task.FromResult(TestResponses.Json(HttpStatusCode.BadRequest, oversized))));
        using ScaleSetClient truncatedClient = CreateClient(truncatedHttp);

        ScaleSetApiException truncatedEx = await Assert.ThrowsAsync<ScaleSetApiException>(
            () => truncatedClient.GetScaleSetAsync(1, TestContext.Current.CancellationToken));
        Assert.NotNull(truncatedEx.Response);
        Assert.True(truncatedEx.Response.IsBodyTruncated);
        Assert.Equal(65536, truncatedEx.Response.Body.Length);
    }

    [Fact]
    public async Task DecodeFailureOnSuccessResponsePreservesUtf8BomInSnapshotWithSecretFreeToString()
    {
        const string secretValue = "sensitive-jit-or-token-sentinel";
        byte[] rawPayload = [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes($"{{\"token\":\"{secretValue}\"}} {{trailing}}")];
        using HttpClient httpClient = new(CreateConnectedHandler((_, _) =>
        {
            HttpResponseMessage response = new(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(rawPayload),
            };
            response.Headers.TryAddWithoutValidation("X-Custom-Header", "header-secret-value");
            return Task.FromResult(response);
        }));
        using ScaleSetClient client = CreateClient(httpClient);

        ScaleSetApiException exception = await Assert.ThrowsAsync<ScaleSetApiException>(
            () => client.GetScaleSetAsync(15, TestContext.Current.CancellationToken));

        Assert.Equal(200, exception.StatusCode);
        Assert.Null(exception.InnerException);
        Assert.NotNull(exception.Response);
        Assert.Equal(rawPayload, exception.Response.Body.ToArray());
        Assert.False(exception.Response.IsBodyIncomplete);
        Assert.DoesNotContain(secretValue, exception.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(secretValue, exception.Response.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("header-secret-value", exception.Response.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TransportFailureBeforeResponseLeavesSnapshotNull()
    {
        using HttpClient httpClient = new(CreateConnectedHandler((_, _) =>
            Task.FromException<HttpResponseMessage>(new HttpRequestException("dns failure"))));
        using ScaleSetClient client = CreateClient(httpClient);

        ScaleSetApiException exception = await Assert.ThrowsAsync<ScaleSetApiException>(
            () => client.GetScaleSetAsync(1, TestContext.Current.CancellationToken));

        Assert.Null(exception.StatusCode);
        Assert.Null(exception.Response);
    }

    [Fact]
    public async Task PartialBodyReadFailurePopulatesSnapshotWithIncompleteFlagAndReceivedBytes()
    {
        byte[] prefix = Encoding.UTF8.GetBytes("{\"partial\":\"secret-token-value");
        using HttpClient httpClient = new(CreateConnectedHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadGateway)
        {
            Content = new StreamContent(new PartialReadStream(prefix)),
        })));
        using ScaleSetClient client = CreateClient(httpClient);

        ScaleSetApiException exception = await Assert.ThrowsAsync<ScaleSetApiException>(
            () => client.GetScaleSetAsync(1, TestContext.Current.CancellationToken));

        Assert.Equal(502, exception.StatusCode);
        Assert.NotNull(exception.Response);
        Assert.True(exception.Response.IsBodyIncomplete);
        Assert.Equal(prefix, exception.Response.Body.ToArray());
        Assert.DoesNotContain("secret-token-value", exception.BodyPreview ?? string.Empty, StringComparison.Ordinal);
    }

    private static ScaleSetClient CreateClient(HttpClient httpClient) =>
        new(
            Target,
            ScaleSetCredentials.FromPersonalAccessToken("synthetic-pat"),
            new ScaleSetClientOptions { MaxReadOnlyRetries = 0 },
            httpClient);

    private static RecordingHttpMessageHandler CreateConnectedHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> actionsHandler) =>
        TestResponses.CreateConnectedHandler(DateTimeOffset.UtcNow.AddMinutes(10), actionsHandler);
}
