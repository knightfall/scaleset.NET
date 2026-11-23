namespace ScaleSet.Client;

/// <summary>Supported GitHub target scopes.</summary>
public enum GitHubTargetScope
{
    /// <summary>Target is one repository.</summary>
    Repository = 0,

    /// <summary>Target is an organization.</summary>
    Organization = 1,

    /// <summary>Target is an enterprise.</summary>
    Enterprise = 2,
}

/// <summary>A validated GitHub.com or GitHub Enterprise Cloud (*.ghe.com) enterprise, organization, or repository target.</summary>
public sealed record GitHubTarget
{
    private const string HostedGheSuffix = ".ghe.com";

    private GitHubTarget(
        Uri sourceUri,
        Uri apiBaseUri,
        GitHubTargetScope scope,
        string owner,
        string? repository,
        string? enterprise)
    {
        SourceUri = sourceUri;
        ApiBaseUri = apiBaseUri;
        Scope = scope;
        Owner = owner;
        Repository = repository;
        Enterprise = enterprise;
    }

    /// <summary>The parsed GitHub target URL.</summary>
    public Uri SourceUri { get; }

    /// <summary>The GitHub REST API base URL resolved for this target.</summary>
    public Uri ApiBaseUri { get; }

    /// <summary>Enterprise, organization, or repository owner name.</summary>
    public string Owner { get; }

    /// <summary>Repository name, or <see langword="null"/> for an organization or enterprise target.</summary>
    public string? Repository { get; }

    /// <summary>Enterprise slug, or <see langword="null"/> for an organization or repository target.</summary>
    public string? Enterprise { get; }

    /// <summary>Whether this target is scoped to a repository, organization, or enterprise.</summary>
    public GitHubTargetScope Scope { get; }

    /// <summary>REST path used to create the scoped runner registration token.</summary>
    /// <exception cref="InvalidOperationException">The target scope is unsupported.</exception>
    public string RegistrationTokenPath => Scope switch
    {
        GitHubTargetScope.Enterprise => $"/enterprises/{EscapePathSegment(Enterprise!)}/actions/runners/registration-token",
        GitHubTargetScope.Organization => $"/orgs/{EscapePathSegment(Owner)}/actions/runners/registration-token",
        GitHubTargetScope.Repository => $"/repos/{EscapePathSegment(Owner)}/{EscapePathSegment(Repository!)}/actions/runners/registration-token",
        _ => throw new InvalidOperationException($"Unsupported target scope '{Scope}'."),
    };

    /// <summary>Parses an absolute GitHub.com or GitHub Enterprise Cloud (*.ghe.com) enterprise, organization, or repository URL.</summary>
    /// <param name="value">An HTTPS URL such as <c>https://github.com/org/repo</c> or <c>https://acme.ghe.com/enterprises/acme</c>.</param>
    /// <returns>The validated target.</returns>
    /// <exception cref="ArgumentException">The value is not a valid supported target URL.</exception>
    /// <exception cref="NotSupportedException">The URL points to a deferred GHES host.</exception>
    public static GitHubTarget Parse(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (string.IsNullOrWhiteSpace(value) || !Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            throw new ArgumentException("A valid absolute HTTPS GitHub.com or *.ghe.com target URL is required.", nameof(value));
        }

        return Parse(uri);
    }

    /// <summary>Parses an absolute GitHub.com or GitHub Enterprise Cloud (*.ghe.com) enterprise, organization, or repository URI.</summary>
    /// <param name="uri">An HTTPS URI such as <c>https://github.com/org/repo</c> or <c>https://acme.ghe.com/enterprises/acme</c>.</param>
    /// <returns>The validated target.</returns>
    /// <exception cref="ArgumentException">The URI is not a valid supported target.</exception>
    /// <exception cref="NotSupportedException">The URI points to a deferred GHES host.</exception>
    public static GitHubTarget Parse(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        if (!uri.IsAbsoluteUri || !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Only absolute HTTPS GitHub target URLs are supported.", nameof(uri));
        }

        if (!string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
        {
            throw new ArgumentException("GitHub target URLs cannot contain user information, a query, or a fragment.", nameof(uri));
        }

        if (uri.Port != 443)
        {
            throw new ArgumentException("GitHub target URLs must use the standard HTTPS port.", nameof(uri));
        }

        var apiBaseUri = ResolveApiBaseUri(uri.Host);

        var escapedPath = uri.AbsolutePath.Trim('/');
        var pathParts = escapedPath.Length == 0
            ? Array.Empty<string>()
            : escapedPath.Split('/');

        if (pathParts.Length == 2 &&
            string.Equals(DecodeSegment(pathParts[0], nameof(uri)), "enterprises", StringComparison.OrdinalIgnoreCase))
        {
            var enterprise = DecodeSegment(pathParts[1], nameof(uri));
            return new GitHubTarget(
                uri,
                apiBaseUri,
                GitHubTargetScope.Enterprise,
                owner: enterprise,
                repository: null,
                enterprise: enterprise);
        }

        if (pathParts.Length is not (1 or 2))
        {
            throw new ArgumentException("The URL path must identify one enterprise, organization, or repository.", nameof(uri));
        }

        var owner = DecodeSegment(pathParts[0], nameof(uri));
        if (pathParts.Length == 1)
        {
            return new GitHubTarget(
                uri,
                apiBaseUri,
                GitHubTargetScope.Organization,
                owner,
                repository: null,
                enterprise: null);
        }

        var repository = DecodeSegment(pathParts[1], nameof(uri));
        return new GitHubTarget(
            uri,
            apiBaseUri,
            GitHubTargetScope.Repository,
            owner,
            repository,
            enterprise: null);
    }

    private static Uri ResolveApiBaseUri(string host)
    {
        if (IsGitHubComHost(host))
        {
            return new Uri("https://api.github.com/");
        }

        if (host.EndsWith(HostedGheSuffix, StringComparison.OrdinalIgnoreCase))
        {
            if (!IsValidHostedGheSubdomain(host))
            {
                throw new ArgumentException($"Host '{host}' is not a valid GitHub Enterprise Cloud (*.ghe.com) hostname.", nameof(host));
            }

            return new Uri($"https://api.{host.ToLowerInvariant()}/");
        }

        throw new NotSupportedException(
            $"Host '{host}' is not a supported GitHub.com or GitHub Enterprise Cloud (*.ghe.com) host. GHES hosts are deferred.");
    }

    private static bool IsGitHubComHost(string host) =>
        string.Equals(host, "github.com", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(host, "www.github.com", StringComparison.OrdinalIgnoreCase);

    private static bool IsValidHostedGheSubdomain(string host)
    {
        if (host.Length <= HostedGheSuffix.Length)
        {
            return false;
        }

        string prefix = host[..^HostedGheSuffix.Length];
        if (prefix.StartsWith('.') || prefix.EndsWith('.'))
        {
            return false;
        }

        foreach (string label in prefix.Split('.'))
        {
            if (label.Length == 0 || label.StartsWith('-') || label.EndsWith('-'))
            {
                return false;
            }

            foreach (char character in label)
            {
                if (!char.IsAsciiLetterOrDigit(character) && character != '-')
                {
                    return false;
                }
            }
        }

        return true;
    }

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
