using System.Net;
using System.Text;

namespace ScaleSet.Client;

/// <summary>GitHub Actions scale-set and runner administration operations.</summary>
public sealed partial class ScaleSetClient
{
    private const string AdministrationScaleSetsEndpoint = "_apis/runtime/runnerscalesets";
    private const string AdministrationRunnerGroupsEndpoint = "/_apis/runtime/runnergroups/";
    private const string AdministrationRunnersEndpoint = "_apis/distributedtask/pools/0/agents";

    /// <summary>Finds a scale set by name within a runner group.</summary>
    /// <param name="runnerGroupId">A non-negative runner group identifier, passed to the service unchanged.</param>
    /// <param name="name">The scale-set name.</param>
    /// <param name="cancellationToken">A token used to cancel the request.</param>
    /// <returns>The matching scale set, or <see langword="null"/> when none exists.</returns>
    /// <exception cref="ScaleSetApiException">The service returned multiple matches or an invalid response.</exception>
    public async Task<RunnerScaleSet?> FindScaleSetAsync(
        long runnerGroupId,
        string name,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(runnerGroupId);
        ArgumentNullException.ThrowIfNull(name);

        Dictionary<string, string> query = new(StringComparer.Ordinal)
        {
            ["runnerGroupId"] = runnerGroupId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["name"] = name,
        };
        return await SendActionsAsync<RunnerScaleSet?>(
            HttpMethod.Get,
            AdministrationScaleSetsEndpoint,
            null,
            query,
            (int)HttpStatusCode.OK,
            cancellationToken,
            DecodeScaleSetByName).ConfigureAwait(false);
    }

    /// <summary>Lists the scale sets in a runner group.</summary>
    /// <param name="runnerGroupId">A non-negative runner group identifier, passed to the service unchanged.</param>
    /// <param name="cancellationToken">A token used to cancel the request.</param>
    /// <returns>The scale sets; a missing or null wire value is returned as an empty collection.</returns>
    public async Task<IReadOnlyList<RunnerScaleSet>> ListScaleSetsAsync(
        long runnerGroupId,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(runnerGroupId);

        Dictionary<string, string> query = new(StringComparer.Ordinal)
        {
            ["runnerGroupId"] = runnerGroupId.ToString(System.Globalization.CultureInfo.InvariantCulture),
        };
        return await SendActionsAsync<IReadOnlyList<RunnerScaleSet>>(
            HttpMethod.Get,
            AdministrationScaleSetsEndpoint,
            null,
            query,
            (int)HttpStatusCode.OK,
            cancellationToken,
            DecodeScaleSetList).ConfigureAwait(false);
    }

    /// <summary>Gets a scale set by its identifier.</summary>
    /// <param name="scaleSetId">The positive scale-set identifier.</param>
    /// <param name="cancellationToken">A token used to cancel the request.</param>
    /// <returns>The scale set, or <see langword="null"/> when the service returns a JSON null body.</returns>
    public async Task<RunnerScaleSet?> GetScaleSetAsync(long scaleSetId, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(scaleSetId);

        return await SendActionsAsync<RunnerScaleSet?>(
            HttpMethod.Get,
            $"/{AdministrationScaleSetsEndpoint}/{FormatIdentifier(scaleSetId)}",
            null,
            null,
            (int)HttpStatusCode.OK,
            cancellationToken,
            DeserializeAdministrationResponse<RunnerScaleSet>).ConfigureAwait(false);
    }

    /// <summary>Gets the unique runner group with the specified name.</summary>
    /// <param name="name">The runner-group name.</param>
    /// <param name="cancellationToken">A token used to cancel the request.</param>
    /// <returns>The matching runner group.</returns>
    /// <exception cref="ScaleSetApiException">The service returns no group, multiple groups, or an invalid response.</exception>
    public async Task<RunnerGroup> GetRunnerGroupByNameAsync(string name, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(name);

        Dictionary<string, string> query = new(StringComparer.Ordinal)
        {
            ["groupName"] = name,
        };
        return await SendActionsAsync<RunnerGroup>(
            HttpMethod.Get,
            AdministrationRunnerGroupsEndpoint,
            null,
            query,
            (int)HttpStatusCode.OK,
            cancellationToken,
            DecodeRunnerGroupByName).ConfigureAwait(false);
    }

    /// <summary>Creates a runner scale set.</summary>
    /// <param name="runnerScaleSet">The scale-set model to create.</param>
    /// <param name="cancellationToken">A token used to cancel the request.</param>
    /// <returns>The created scale set.</returns>
    /// <exception cref="ArgumentException">No labels were supplied and the scale-set name is empty.</exception>
    public async Task<RunnerScaleSet> CreateScaleSetAsync(
        RunnerScaleSet runnerScaleSet,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(runnerScaleSet);

        RunnerScaleSet request = PrepareCreateScaleSet(runnerScaleSet);
        byte[] body = Encoding.UTF8.GetBytes(ScaleSetJson.Serialize(request));
        return await SendActionsAsync<RunnerScaleSet>(
            HttpMethod.Post,
            AdministrationScaleSetsEndpoint,
            body,
            null,
            (int)HttpStatusCode.OK,
            cancellationToken,
            DeserializeCreatedScaleSet).ConfigureAwait(false);
    }

    /// <summary>Updates a runner scale set.</summary>
    /// <param name="scaleSetId">The positive scale-set identifier.</param>
    /// <param name="runnerScaleSet">The fields to send in the update.</param>
    /// <param name="cancellationToken">A token used to cancel the request.</param>
    /// <returns>The updated scale set.</returns>
    public async Task<RunnerScaleSet> UpdateScaleSetAsync(
        long scaleSetId,
        RunnerScaleSet runnerScaleSet,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(scaleSetId);
        ArgumentNullException.ThrowIfNull(runnerScaleSet);

        RunnerScaleSet request = PrepareUpdateScaleSet(runnerScaleSet);
        byte[] body = Encoding.UTF8.GetBytes(ScaleSetJson.Serialize(request));
        return await SendActionsAsync<RunnerScaleSet>(
            HttpMethod.Patch,
            $"{AdministrationScaleSetsEndpoint}/{FormatIdentifier(scaleSetId)}",
            body,
            null,
            (int)HttpStatusCode.OK,
            cancellationToken,
            DeserializeCreatedScaleSet).ConfigureAwait(false);
    }

    /// <summary>Deletes a runner scale set by identifier.</summary>
    /// <param name="scaleSetId">The positive scale-set identifier.</param>
    /// <param name="cancellationToken">A token used to cancel the request.</param>
    public async Task DeleteScaleSetAsync(long scaleSetId, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(scaleSetId);

        _ = await SendActionsAsync(
            HttpMethod.Delete,
            $"/{AdministrationScaleSetsEndpoint}/{FormatIdentifier(scaleSetId)}",
            null,
            null,
            (int)HttpStatusCode.NoContent,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Generates a just-in-time runner configuration for a scale set.</summary>
    /// <param name="scaleSetId">The positive scale-set identifier.</param>
    /// <param name="runnerSetting">The runner name and work folder to send.</param>
    /// <param name="cancellationToken">A token used to cancel the request.</param>
    /// <returns>The JIT response, or <see langword="null"/> when the service returns a JSON null body.</returns>
    public async Task<RunnerScaleSetJitRunnerConfig?> GenerateJitConfigAsync(
        long scaleSetId,
        RunnerScaleSetJitRunnerSetting runnerSetting,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(scaleSetId);
        ArgumentNullException.ThrowIfNull(runnerSetting);

        byte[] body = Encoding.UTF8.GetBytes(ScaleSetJson.Serialize(runnerSetting));
        return await SendActionsAsync<RunnerScaleSetJitRunnerConfig?>(
            HttpMethod.Post,
            $"/{AdministrationScaleSetsEndpoint}/{FormatIdentifier(scaleSetId)}/generatejitconfig",
            body,
            null,
            (int)HttpStatusCode.OK,
            cancellationToken,
            DeserializeAdministrationResponse<RunnerScaleSetJitRunnerConfig>).ConfigureAwait(false);
    }

    /// <summary>Gets a registered runner by identifier.</summary>
    /// <param name="runnerId">The positive runner identifier.</param>
    /// <param name="cancellationToken">A token used to cancel the request.</param>
    /// <returns>The runner, or <see langword="null"/> when the service returns a JSON null body.</returns>
    public async Task<RunnerReference?> GetRunnerAsync(long runnerId, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(runnerId);

        return await SendActionsAsync<RunnerReference?>(
            HttpMethod.Get,
            $"/{AdministrationRunnersEndpoint}/{FormatIdentifier(runnerId)}",
            null,
            null,
            (int)HttpStatusCode.OK,
            cancellationToken,
            DeserializeAdministrationResponse<RunnerReference>).ConfigureAwait(false);
    }

    /// <summary>Finds a registered runner by name.</summary>
    /// <param name="name">The runner name.</param>
    /// <param name="cancellationToken">A token used to cancel the request.</param>
    /// <returns>The matching runner, or <see langword="null"/> when none exists.</returns>
    /// <exception cref="ScaleSetApiException">The service returns multiple matches or an invalid response.</exception>
    public async Task<RunnerReference?> FindRunnerByNameAsync(string name, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(name);

        Dictionary<string, string> query = new(StringComparer.Ordinal)
        {
            ["agentName"] = name,
        };
        return await SendActionsAsync<RunnerReference?>(
            HttpMethod.Get,
            AdministrationRunnersEndpoint,
            null,
            query,
            (int)HttpStatusCode.OK,
            cancellationToken,
            DecodeRunnerByName).ConfigureAwait(false);
    }

    /// <summary>Removes a registered runner by identifier.</summary>
    /// <param name="runnerId">The positive runner identifier.</param>
    /// <param name="cancellationToken">A token used to cancel the request.</param>
    /// <exception cref="ScaleSetApiException">The request failed, including when the runner is busy or not found.</exception>
    public async Task RemoveRunnerAsync(long runnerId, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(runnerId);

        _ = await SendActionsAsync(
            HttpMethod.Delete,
            $"/{AdministrationRunnersEndpoint}/{FormatIdentifier(runnerId)}",
            null,
            null,
            (int)HttpStatusCode.NoContent,
            cancellationToken).ConfigureAwait(false);
    }

    private static RunnerScaleSet PrepareCreateScaleSet(RunnerScaleSet runnerScaleSet)
    {
        IReadOnlyList<Label>? labels = runnerScaleSet.Labels;
        if (labels is null || labels.Count == 0)
        {
            if (string.IsNullOrEmpty(runnerScaleSet.Name))
            {
                throw new ArgumentException("A scale-set name or at least one label is required.", nameof(runnerScaleSet));
            }

            labels = [new Label { Name = runnerScaleSet.Name, Type = "System" }];
        }

        return runnerScaleSet with { Labels = WithDefaultLabelTypes(labels) };
    }

    private static RunnerScaleSet PrepareUpdateScaleSet(RunnerScaleSet runnerScaleSet) =>
        runnerScaleSet with
        {
            Labels = runnerScaleSet.Labels is null ? null : WithDefaultLabelTypes(runnerScaleSet.Labels),
        };

    private static Label[] WithDefaultLabelTypes(IReadOnlyList<Label> labels)
    {
        Label[] normalizedLabels = new Label[labels.Count];
        for (int index = 0; index < labels.Count; index++)
        {
            Label label = labels[index] ?? throw new ArgumentException("Runner scale-set labels cannot contain null entries.", nameof(labels));
            normalizedLabels[index] = string.IsNullOrEmpty(label.Type) ? label with { Type = "System" } : label;
        }

        return normalizedLabels;
    }

    private static RunnerScaleSet? DecodeScaleSetByName(byte[] responseBody)
    {
        RunnerScaleSetList? response = DeserializeAdministrationResponse<RunnerScaleSetList>(responseBody);
        if (response is null || response.Count == 0)
        {
            return null;
        }

        if (response.Count > 1)
        {
            throw new InvalidDataException("Multiple runner scale sets matched the requested name.");
        }

        return GetCountedItem(response.Count, response.Value, "runner scale set");
    }

    private static IReadOnlyList<RunnerScaleSet> DecodeScaleSetList(byte[] responseBody)
    {
        RunnerScaleSetList? response = DeserializeAdministrationResponse<RunnerScaleSetList>(responseBody);
        return response?.Value ?? Array.Empty<RunnerScaleSet>();
    }

    private static RunnerGroup DecodeRunnerGroupByName(byte[] responseBody)
    {
        RunnerGroupList? response = DeserializeAdministrationResponse<RunnerGroupList>(responseBody);
        if (response is null || response.Count == 0)
        {
            throw new InvalidDataException("No runner group matched the requested name.");
        }

        if (response.Count > 1)
        {
            throw new InvalidDataException("Multiple runner groups matched the requested name.");
        }

        return GetCountedItem(response.Count, response.Value, "runner group");
    }

    private static RunnerReference? DecodeRunnerByName(byte[] responseBody)
    {
        RunnerReferenceList? response = DeserializeAdministrationResponse<RunnerReferenceList>(responseBody);
        if (response is null || response.Count == 0)
        {
            return null;
        }

        if (response.Count > 1)
        {
            throw new InvalidDataException("Multiple runners matched the requested name.");
        }

        return GetCountedItem(response.Count, response.Value, "runner reference");
    }

    private static RunnerScaleSet DeserializeCreatedScaleSet(byte[] responseBody) =>
        DeserializeAdministrationResponse<RunnerScaleSet>(responseBody) ?? new RunnerScaleSet();

    private static string FormatIdentifier(long identifier) =>
        identifier.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static T? DeserializeAdministrationResponse<T>(byte[] responseBody)
        where T : class
    {
        return ScaleSetJson.Deserialize<T>(Encoding.UTF8.GetString(responseBody));
    }

    private static T GetCountedItem<T>(int count, IReadOnlyList<T>? values, string responseName)
        where T : class
    {
        if (count == 1 && values is { Count: > 0 } && values[0] is { } value)
        {
            return value;
        }

        throw new InvalidDataException($"The {responseName} response count did not match its value array.");
    }
}
