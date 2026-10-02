namespace ScaleSet.Client;

/// <summary>Supported GitHub target scopes.</summary>
public enum GitHubTargetScope
{
    /// <summary>Target is one repository.</summary>
    Repository = 0,

    /// <summary>Target is an organization.</summary>
    Organization = 1,
}

/// <summary>A validated GitHub.com organization or repository target.</summary>
public sealed record GitHubTarget
{
    private GitHubTarget(Uri sourceUri, GitHubTargetScope scope, string owner, string? repository)
    {
        SourceUri = sourceUri;
        Scope = scope;
        Owner = owner;
        Repository = repository;
    }

    /// <summary>The parsed GitHub.com URL.</summary>
    public Uri SourceUri { get; }

    /// <summary>The GitHub REST API base URL for GitHub.com.</summary>
    public Uri ApiBaseUri { get; } = new("https://api.github.com/");

    /// <summary>Organization or repository owner name.</summary>
    public string Owner { get; }

    /// <summary>Repository name, or <see langword="null"/> for an organization target.</summary>
    public string? Repository { get; }

    /// <summary>Whether this target is scoped to a repository or organization.</summary>
    public GitHubTargetScope Scope { get; }

    /// <summary>REST path used to create the scoped runner registration token.</summary>
    public string RegistrationTokenPath => Scope switch
    {
        GitHubTargetScope.Organization => $"/orgs/{EscapePathSegment(Owner)}/actions/runners/registration-token",
        GitHubTargetScope.Repository => $"/repos/{EscapePathSegment(Owner)}/{EscapePathSegment(Repository!)}/actions/runners/registration-token",
        _ => throw new InvalidOperationException($"Unsupported target scope '{Scope}'."),
    };

    /// <summary>Parses an absolute GitHub.com repository or organization URL.</summary>
    /// <param name="value">An HTTPS URL such as <c>https://github.com/org/repo</c>.</param>
    /// <returns>The validated target.</returns>
    /// <exception cref="ArgumentException">The value is not a valid supported target URL.</exception>
    /// <exception cref="NotSupportedException">The URL points to a deferred GHES or enterprise target.</exception>
    public static GitHubTarget Parse(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (string.IsNullOrWhiteSpace(value) || !Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            throw new ArgumentException("A valid absolute HTTPS GitHub.com URL is required.", nameof(value));
        }

        return Parse(uri);
    }

    /// <summary>Parses an absolute GitHub.com repository or organization URI.</summary>
    /// <param name="uri">An HTTPS URI such as <c>https://github.com/org/repo</c>.</param>
    /// <returns>The validated target.</returns>
    /// <exception cref="ArgumentException">The URI is not a valid supported target.</exception>
    /// <exception cref="NotSupportedException">The URI points to a deferred GHES or enterprise target.</exception>
    public static GitHubTarget Parse(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        if (!uri.IsAbsoluteUri || !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Only absolute HTTPS GitHub.com target URLs are supported.", nameof(uri));
        }

        if (!string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
        {
            throw new ArgumentException("GitHub target URLs cannot contain user information, a query, or a fragment.", nameof(uri));
        }

        if (uri.Port != 443)
        {
            throw new ArgumentException("GitHub target URLs must use the standard HTTPS port.", nameof(uri));
        }

        if (!IsGitHubComHost(uri.Host))
        {
            throw new NotSupportedException(
                $"Host '{uri.Host}' is not a supported GitHub.com host. GHES and enterprise hosts are deferred.");
        }

        var escapedPath = uri.AbsolutePath.Trim('/');
        var pathParts = escapedPath.Length == 0
            ? Array.Empty<string>()
            : escapedPath.Split('/');

        if (pathParts.Length == 2 &&
            string.Equals(DecodeSegment(pathParts[0], nameof(uri)), "enterprises", StringComparison.OrdinalIgnoreCase))
        {
            throw new NotSupportedException("GitHub Enterprise scope is deferred; only organization and repository targets are supported.");
        }

        if (pathParts.Length is not (1 or 2))
        {
            throw new ArgumentException("The URL path must identify one organization or one repository.", nameof(uri));
        }

        var owner = DecodeSegment(pathParts[0], nameof(uri));
        if (pathParts.Length == 1)
        {
            return new GitHubTarget(uri, GitHubTargetScope.Organization, owner, repository: null);
        }

        var repository = DecodeSegment(pathParts[1], nameof(uri));
        return new GitHubTarget(uri, GitHubTargetScope.Repository, owner, repository);
    }

    private static bool IsGitHubComHost(string host) =>
        string.Equals(host, "github.com", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(host, "www.github.com", StringComparison.OrdinalIgnoreCase);

    private static string DecodeSegment(string escapedSegment, string parameterName)
    {
        if (escapedSegment.Length == 0 || HasInvalidEscape(escapedSegment))
        {
            throw new ArgumentException("GitHub target path segments must be non-empty valid URL segments.", parameterName);
        }

        var segment = Uri.UnescapeDataString(escapedSegment);
        if (segment.Length == 0 ||
            segment is "." or ".." ||
            segment.Contains('/') ||
            segment.Contains('\\') ||
            segment.Any(char.IsControl))
        {
            throw new ArgumentException("GitHub target path segments cannot contain separators or control characters.", parameterName);
        }

        return segment;
    }

    private static bool HasInvalidEscape(string value)
    {
        for (var index = 0; index < value.Length; index++)
        {
            if (value[index] != '%')
            {
                continue;
            }

            if (index + 2 >= value.Length ||
                !Uri.IsHexDigit(value[index + 1]) ||
                !Uri.IsHexDigit(value[index + 2]))
            {
                return true;
            }

            index += 2;
        }

        return false;
    }

    private static string EscapePathSegment(string value) => Uri.EscapeDataString(value);
}
