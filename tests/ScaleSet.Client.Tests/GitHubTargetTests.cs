using ScaleSet.Client;
using Xunit;

namespace ScaleSet.Client.Tests;

public sealed class GitHubTargetTests
{
    [Fact]
    public void ParseRepositoryTargetResolvesGitHubApiAndRegistrationPath()
    {
        var target = GitHubTarget.Parse("https://github.com/my-org/my-repo/");

        Assert.Equal(GitHubTargetScope.Repository, target.Scope);
        Assert.Equal("my-org", target.Owner);
        Assert.Equal("my-repo", target.Repository);
        Assert.Equal(new Uri("https://api.github.com/"), target.ApiBaseUri);
        Assert.Equal("/repos/my-org/my-repo/actions/runners/registration-token", target.RegistrationTokenPath);
    }

    [Fact]
    public void ParseOrganizationTargetAcceptsWwwHost()
    {
        var target = GitHubTarget.Parse(new Uri("https://www.github.com/my-org"));

        Assert.Equal(GitHubTargetScope.Organization, target.Scope);
        Assert.Equal("my-org", target.Owner);
        Assert.Null(target.Repository);
        Assert.Equal(new Uri("https://api.github.com/"), target.ApiBaseUri);
        Assert.Equal("/orgs/my-org/actions/runners/registration-token", target.RegistrationTokenPath);
    }

    [Fact]
    public void RegistrationTokenPathEncodesEachPathSegment()
    {
        var target = GitHubTarget.Parse("https://github.com/team%20ops/project%2Bname");

        Assert.Equal("team ops", target.Owner);
        Assert.Equal("project+name", target.Repository);
        Assert.Equal("/repos/team%20ops/project%2Bname/actions/runners/registration-token", target.RegistrationTokenPath);
    }

    [Theory]
    [InlineData("http://github.com/org/repo")]
    [InlineData("https://github.com")]
    [InlineData("https://github.com/org/repo/extra")]
    [InlineData("https://github.com/org//repo")]
    [InlineData("https://github.com/org/repo?query=1")]
    [InlineData("https://github.com/org/repo#fragment")]
    [InlineData("https://user@github.com/org/repo")]
    [InlineData("https://github.com:8443/org/repo")]
    [InlineData("https://github.com/org%2Frepo/name")]
    public void ParseRejectsMalformedOrUnsupportedUrlShapes(string value)
    {
        Assert.Throws<ArgumentException>(() => GitHubTarget.Parse(value));
    }

    [Theory]
    [InlineData("https://ghes.example/org/repo")]
    [InlineData("https://github.com/enterprises/acme")]
    public void ParseRejectsDeferredEnterpriseTargetsClearly(string value)
    {
        var exception = Assert.Throws<NotSupportedException>(() => GitHubTarget.Parse(value));

        Assert.Contains("deferred", exception.Message, StringComparison.OrdinalIgnoreCase);
    }
}
