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
    [InlineData("https://github.com/enterprises/acme")]
    [InlineData("https://github.com/enterprises/acme/")]
    public void ParseEnterpriseTargetOnGitHubComResolvesEnterpriseScopeAndPath(string url)
    {
        var target = GitHubTarget.Parse(url);

        Assert.Equal(GitHubTargetScope.Enterprise, target.Scope);
        Assert.Equal("acme", target.Enterprise);
        Assert.Equal("acme", target.Owner);
        Assert.Null(target.Repository);
        Assert.Equal(new Uri("https://api.github.com/"), target.ApiBaseUri);
        Assert.Equal("/enterprises/acme/actions/runners/registration-token", target.RegistrationTokenPath);
    }

    [Fact]
    public void ParseHostedGheOrganizationRepositoryAndEnterpriseTargetsResolveTenantApiBase()
    {
        var orgTarget = GitHubTarget.Parse("https://acme.ghe.com/my-org");
        Assert.Equal(GitHubTargetScope.Organization, orgTarget.Scope);
        Assert.Equal("my-org", orgTarget.Owner);
        Assert.Null(orgTarget.Repository);
        Assert.Null(orgTarget.Enterprise);
        Assert.Equal(new Uri("https://api.acme.ghe.com/"), orgTarget.ApiBaseUri);
        Assert.Equal("/orgs/my-org/actions/runners/registration-token", orgTarget.RegistrationTokenPath);

        var repoTarget = GitHubTarget.Parse("https://acme.ghe.com/my-org/my-repo");
        Assert.Equal(GitHubTargetScope.Repository, repoTarget.Scope);
        Assert.Equal("my-org", repoTarget.Owner);
        Assert.Equal("my-repo", repoTarget.Repository);
        Assert.Null(repoTarget.Enterprise);
        Assert.Equal(new Uri("https://api.acme.ghe.com/"), repoTarget.ApiBaseUri);
        Assert.Equal("/repos/my-org/my-repo/actions/runners/registration-token", repoTarget.RegistrationTokenPath);

        var enterpriseTarget = GitHubTarget.Parse("https://ACME.GHE.COM/enterprises/my%20ent");
        Assert.Equal(GitHubTargetScope.Enterprise, enterpriseTarget.Scope);
        Assert.Equal("my ent", enterpriseTarget.Enterprise);
        Assert.Equal("my ent", enterpriseTarget.Owner);
        Assert.Null(enterpriseTarget.Repository);
        Assert.Equal(new Uri("https://api.acme.ghe.com/"), enterpriseTarget.ApiBaseUri);
        Assert.Equal("/enterprises/my%20ent/actions/runners/registration-token", enterpriseTarget.RegistrationTokenPath);
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
    [InlineData("https://.ghe.com/org")]
    [InlineData("https://-bad.ghe.com/org")]
    [InlineData("https://bad-.ghe.com/org")]
    [InlineData("https://foo..ghe.com/org")]
    [InlineData("https://sub_domain.ghe.com/org")]
    public void ParseRejectsMalformedOrUnsupportedUrlShapes(string value)
    {
        Assert.Throws<ArgumentException>(() => GitHubTarget.Parse(value));
    }

    [Theory]
    [InlineData("https://ghes.example/org/repo")]
    [InlineData("https://ghe.com/org")]
    public void ParseRejectsDeferredGhesTargetsClearly(string value)
    {
        var exception = Assert.Throws<NotSupportedException>(() => GitHubTarget.Parse(value));

        Assert.Contains("deferred", exception.Message, StringComparison.OrdinalIgnoreCase);
    }
}
