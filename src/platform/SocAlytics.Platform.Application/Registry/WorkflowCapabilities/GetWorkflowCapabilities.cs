using System.Globalization;
using SocAlytics.Platform.Application.Abstractions;
using SocAlytics.Platform.Application.Abstractions.Persistence;
using SocAlytics.Platform.Application.Registry.Abstractions;
using SocAlytics.Platform.Application.Registry.RegistryCredentials;
using SocAlytics.Platform.Domain.Registry;

namespace SocAlytics.Platform.Application.Registry.WorkflowCapabilities;

public sealed record WorkflowReference(string Id, string Version);

public sealed record WorkflowCapability(
    string Id, string Version, IReadOnlyList<string> Accelerators, string Cpus, long MemoryBytes);

public sealed record WorkflowCapabilitiesView(WorkflowReference Workflow, IReadOnlyList<WorkflowCapability> Capabilities);

public sealed class GetWorkflowCapabilitiesHandler(
    IUnitOfWork unitOfWork,
    IRegistrationStore registrations,
    IAnalystProfileStore profiles,
    IWorkflowDefinitionResolver workflows)
{
    private const string DefaultCpus = "1";
    private const long DefaultMemoryBytes = 1024L * 1024 * 1024;

    public async Task<OperationResult<WorkflowCapabilitiesView>> HandleAsync(Guid registrationId, CancellationToken cancellationToken)
    {
        await using var scope = await unitOfWork.BeginAsync(cancellationToken);
        var registration = await registrations.FindByIdAsync(registrationId, cancellationToken);
        if (registration is null)
        {
            return OperationFailure.Unauthenticated();
        }

        if (registration.State != RegistrationState.Active)
        {
            return OperationFailure.Forbidden("manager-not-active");
        }

        var resolution = await workflows.ResolveDefaultAsync(cancellationToken);
        if (resolution is not WorkflowResolution.ResolvedResult resolved)
        {
            return IssueRegistryCredentialsHandler.WorkflowFailure(resolution);
        }

        var capabilities = new Dictionary<(string Id, string Version), WorkflowCapability>();
        foreach (var node in resolved.Definition.Nodes)
        {
            if (capabilities.ContainsKey((node.CapabilityId, node.CapabilityVersion)))
            {
                continue;
            }

            var stored = await profiles.FindByDigestAsync(node.AnalystProfileDigest.ToString(), cancellationToken);
            if (stored is null)
            {
                return OperationFailure.Conflict("workflow-not-configured");
            }

            var content = stored.Profile.Content;
            capabilities[(node.CapabilityId, node.CapabilityVersion)] = new WorkflowCapability(
                node.CapabilityId,
                node.CapabilityVersion,
                content.Accelerators.OrderBy(static a => a, StringComparer.Ordinal).ToList(),
                ParseCpus(content.Resources.GetValueOrDefault("cpu")),
                ParseMemoryBytes(content.Resources.GetValueOrDefault("memory")));
        }

        return new WorkflowCapabilitiesView(
            new WorkflowReference(resolved.Definition.WorkflowId, resolved.Definition.WorkflowVersion),
            capabilities.Values.ToList());
    }

    /// <summary>Accepts decimal cores ("1", "0.5") and millicores ("500m"); returns a canonical decimal string.</summary>
    internal static string ParseCpus(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return DefaultCpus;
        }

        decimal cores;
        if (value.EndsWith('m'))
        {
            if (!decimal.TryParse(value[..^1], NumberStyles.None, CultureInfo.InvariantCulture, out var milli))
            {
                return DefaultCpus;
            }

            cores = milli / 1000m;
        }
        else if (!decimal.TryParse(value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out cores))
        {
            return DefaultCpus;
        }

        var text = cores.ToString("0.###", CultureInfo.InvariantCulture);
        return cores > 0 && text != "0" ? text : DefaultCpus;
    }

    internal static long ParseMemoryBytes(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return DefaultMemoryBytes;
        }

        var digits = new string(value.TakeWhile(char.IsDigit).ToArray());
        if (!long.TryParse(digits, CultureInfo.InvariantCulture, out var number) || number <= 0)
        {
            return DefaultMemoryBytes;
        }

        var multiplier = value[digits.Length..] switch
        {
            "Ki" => 1024L,
            "Mi" or "M" => 1024L * 1024,
            "Gi" or "G" => 1024L * 1024 * 1024,
            "Ti" => 1024L * 1024 * 1024 * 1024,
            _ => 1L,
        };
        try
        {
            return checked(number * multiplier);
        }
        catch (OverflowException)
        {
            return long.MaxValue;
        }
    }
}
