using System.Collections.Immutable;
using ScaleSet.Hosting.Configuration;

namespace ScaleSet.Providers.Docker;

internal sealed record DockerProviderOptionsSnapshot(
    Uri EngineEndpoint,
    string SocketPath,
    string HostDockerSocketPath,
    bool PullImageIfMissing,
    TimeSpan RequestTimeout,
    TimeSpan ImagePullTimeout,
    int MaximumPullResponseBytes,
    int MaximumPullLineBytes,
    bool AllowHostSocket,
    bool HardenUnprivilegedContainers,
    long PidsLimit,
    string User,
    ImmutableArray<string> Command,
    string? WorkingDirectory)
{
    public static DockerProviderOptionsSnapshot Capture(DockerProviderOptions? options, bool callerOwnsHttpClient)
    {
        options ??= new DockerProviderOptions();
        Uri endpoint = options.EngineEndpoint;
        ArgumentNullException.ThrowIfNull(endpoint);
        if (!endpoint.IsAbsoluteUri
            || (endpoint.Scheme != Uri.UriSchemeHttp && endpoint.Scheme != Uri.UriSchemeHttps)
            || !string.IsNullOrEmpty(endpoint.UserInfo)
            || !string.IsNullOrEmpty(endpoint.Query)
            || !string.IsNullOrEmpty(endpoint.Fragment)
            || endpoint.AbsolutePath != "/")
        {
            throw new ArgumentException("EngineEndpoint must be an HTTP or HTTPS origin without credentials, path, query, or fragment.", nameof(options));
        }

        if (!callerOwnsHttpClient && endpoint.Scheme != Uri.UriSchemeHttp)
        {
            throw new ArgumentException("A caller-owned HttpClient is required for a TLS Docker Engine endpoint.", nameof(options));
        }

        ValidateAbsoluteUnixPath(options.SocketPath, nameof(options.SocketPath));
        ValidateAbsoluteUnixPath(options.HostDockerSocketPath, nameof(options.HostDockerSocketPath));
        if (options.RequestTimeout <= TimeSpan.Zero || options.RequestTimeout > TimeSpan.FromHours(1))
        {
            throw new ArgumentOutOfRangeException(nameof(options), "RequestTimeout must be greater than zero and no more than one hour.");
        }

        if (options.ImagePullTimeout <= TimeSpan.Zero || options.ImagePullTimeout > TimeSpan.FromHours(24))
        {
            throw new ArgumentOutOfRangeException(nameof(options), "ImagePullTimeout must be greater than zero and no more than one day.");
        }

        if (options.MaximumPullResponseBytes is < 1024 * 1024 or > 64 * 1024 * 1024)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "MaximumPullResponseBytes must be between one and sixty-four mebibytes.");
        }

        if (options.MaximumPullLineBytes is < 1024 or > 1024 * 1024
            || options.MaximumPullLineBytes > options.MaximumPullResponseBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "MaximumPullLineBytes must be between one kibibyte and one mebibyte, and no greater than MaximumPullResponseBytes.");
        }

        ArgumentOutOfRangeException.ThrowIfNegative(options.PidsLimit);

        string user = ValidateText(options.User, nameof(options.User), 128);
        if (options.Command is null || options.Command.Count is < 1 or > 64)
        {
            throw new ArgumentException("Command must contain between one and sixty-four arguments.", nameof(options));
        }

        ImmutableArray<string>.Builder command = ImmutableArray.CreateBuilder<string>(options.Command.Count);
        foreach (string? argument in options.Command)
        {
            command.Add(ValidateText(argument, nameof(options.Command), 4096));
        }

        string? workingDirectory = options.WorkingDirectory is null
            ? null
            : ValidateWorkingDirectory(options.WorkingDirectory);

        return new DockerProviderOptionsSnapshot(
            endpoint,
            options.SocketPath,
            options.HostDockerSocketPath,
            options.PullImageIfMissing,
            options.RequestTimeout,
            options.ImagePullTimeout,
            options.MaximumPullResponseBytes,
            options.MaximumPullLineBytes,
            options.AllowHostSocket,
            options.HardenUnprivilegedContainers,
            options.PidsLimit,
            user,
            command.ToImmutable(),
            workingDirectory);
    }

    private static void ValidateAbsoluteUnixPath(string? path, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(path)
            || !path.StartsWith('/')
            || path.Contains(':')
            || path.Contains('\\')
            || path.Any(char.IsControl))
        {
            throw new ArgumentException("The configured Unix socket path must be an absolute POSIX path without colon or control characters.", parameterName);
        }
    }

    internal static string ValidateText(string? value, string parameterName, int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Length > maximumLength
            || value.Any(char.IsControl))
        {
            throw new ArgumentException("The configured value is empty, too long, or contains a control character.", parameterName);
        }

        return value;
    }

    private static string ValidateWorkingDirectory(string? value)
    {
        string directory = ValidateText(value, nameof(value), 512);
        if (!directory.StartsWith('/')
            || directory.Split('/').Any(static segment => segment == ".."))
        {
            throw new ArgumentException("WorkingDirectory must be an absolute POSIX path without parent-directory segments.", nameof(value));
        }

        return directory;
    }
}

internal sealed record DockerRunnerProfileSnapshot(
    string Image,
    RunnerArchitecture Architecture,
    long NanoCpus,
    long MemoryBytes,
    string WorkFolder,
    string? Network,
    DockerBuildMode DockerBuildMode,
    bool Privileged)
{
    public static DockerRunnerProfileSnapshot Capture(RunnerProfileOptions profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        string image = ValidateImage(profile.Image);
        if (profile.Architecture is not RunnerArchitecture architecture || !Enum.IsDefined(architecture))
        {
            throw new ArgumentException("RunnerProfile.Architecture must be X64 or Arm64.", nameof(profile));
        }

        if (profile.Cpu <= 0)
        {
            throw new ArgumentException("RunnerProfile.Cpu must be greater than zero.", nameof(profile));
        }

        decimal nanoCpus;
        try
        {
            nanoCpus = checked(profile.Cpu * 1_000_000_000m);
        }
        catch (OverflowException)
        {
            throw new ArgumentException("RunnerProfile.Cpu is outside the Docker Engine range.", nameof(profile));
        }

        if (decimal.Truncate(nanoCpus) != nanoCpus || nanoCpus > long.MaxValue)
        {
            throw new ArgumentException("RunnerProfile.Cpu must map to a whole number of Docker NanoCPUs.", nameof(profile));
        }

        if (profile.Memory <= 0)
        {
            throw new ArgumentException("RunnerProfile.Memory must be greater than zero.", nameof(profile));
        }

        long memoryBytes;
        try
        {
            memoryBytes = checked(profile.Memory * 1024 * 1024);
        }
        catch (OverflowException)
        {
            throw new ArgumentException("RunnerProfile.Memory is outside the Docker Engine range.", nameof(profile));
        }

        string workFolder = DockerProviderOptionsSnapshot.ValidateText(
            profile.WorkFolder,
            nameof(profile.WorkFolder),
            512);
        string? network = profile.Network is null
            ? null
            : DockerProviderOptionsSnapshot.ValidateText(profile.Network, nameof(profile.Network), 256);
        if (!Enum.IsDefined(profile.DockerBuildMode))
        {
            throw new ArgumentException("RunnerProfile.DockerBuildMode is not supported.", nameof(profile));
        }

        if (profile.DockerBuildMode == DockerBuildMode.DockerInDocker && !profile.Privileged)
        {
            throw new ArgumentException("RunnerProfile.Privileged must be true for DockerInDocker.", nameof(profile));
        }

        return new DockerRunnerProfileSnapshot(
            image,
            architecture,
            decimal.ToInt64(nanoCpus),
            memoryBytes,
            workFolder,
            network,
            profile.DockerBuildMode,
            profile.Privileged);
    }

    private static string ValidateImage(string? image)
    {
        if (!IsPinnedImage(image))
        {
            throw new ArgumentException("RunnerProfile.Image must be a pinned image tag or sha256 digest.", nameof(image));
        }

        return image!;
    }

    internal static bool IsPinnedImage(string? image)
    {
        if (string.IsNullOrWhiteSpace(image) || image.Any(char.IsWhiteSpace))
        {
            return false;
        }

        int digestSeparator = image.LastIndexOf("@sha256:", StringComparison.OrdinalIgnoreCase);
        if (digestSeparator >= 0)
        {
            string digest = image[(digestSeparator + "@sha256:".Length)..];
            return digestSeparator > 0 && digest.Length == 64 && digest.All(Uri.IsHexDigit);
        }

        int lastSlash = image.LastIndexOf('/');
        int tagSeparator = image.LastIndexOf(':');
        if (tagSeparator <= lastSlash + 1 || tagSeparator == image.Length - 1)
        {
            return false;
        }

        string tag = image[(tagSeparator + 1)..];
        return !string.Equals(tag, "latest", StringComparison.OrdinalIgnoreCase)
            && (char.IsLetterOrDigit(tag[0]) || tag[0] == '_')
            && tag.All(static character => char.IsLetterOrDigit(character) || character is '.' or '_' or '-');
    }
}
