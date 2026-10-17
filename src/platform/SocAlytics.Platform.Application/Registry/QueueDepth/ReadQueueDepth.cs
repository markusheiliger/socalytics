using System.Text.RegularExpressions;
using SocAlytics.Platform.Application.Abstractions;
using SocAlytics.Platform.Application.Abstractions.Persistence;
using SocAlytics.Platform.Application.Registry.Abstractions;
using SocAlytics.Platform.Domain.Registry;

namespace SocAlytics.Platform.Application.Registry.QueueDepth;

public sealed record ReadQueueDepthCommand(Guid RegistrationId, IReadOnlyList<string>? Capabilities);

public sealed record CapabilityQueueDepth(string Id, long Waiting);

public sealed record QueueDepth(DateTimeOffset ObservedAt, IReadOnlyList<CapabilityQueueDepth> Capabilities, long Total);

/// <summary>Reads the number of ready jobs not yet delivered per capability; capabilities without a consumer are omitted.</summary>
public interface IQueueDepthReader
{
    /// <exception cref="MessageTransportException">The counts cannot be read from the broker.</exception>
    Task<IReadOnlyDictionary<string, long>> ReadWaitingAsync(
        IReadOnlyCollection<string> capabilityIds, CancellationToken cancellationToken);
}

public sealed partial class ReadQueueDepthHandler(
    IUnitOfWork unitOfWork,
    IRegistrationStore registrations,
    IWorkflowDefinitionResolver workflows,
    IQueueDepthReader reader,
    TimeProvider time)
{
    public const int MaxCapabilities = 64;

    public const string QueueUnavailable = "queue-unavailable";

    [GeneratedRegex("^[a-z0-9][a-z0-9.-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex CapabilityPattern();

    public async Task<OperationResult<QueueDepth>> HandleAsync(
        ReadQueueDepthCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var requested = command.Capabilities;
        if (requested is null or { Count: 0 } or { Count: > MaxCapabilities }
            || requested.Any(static c => string.IsNullOrEmpty(c) || !CapabilityPattern().IsMatch(c)))
        {
            return OperationFailure.Validation(new FieldViolation("capability", "invalid"));
        }

        await using (var scope = await unitOfWork.BeginAsync(cancellationToken))
        {
            var registration = await registrations.FindByIdAsync(command.RegistrationId, cancellationToken);
            if (registration is null)
            {
                return OperationFailure.Unauthenticated();
            }

            if (registration.State != RegistrationState.Active)
            {
                return OperationFailure.Forbidden("manager-not-active");
            }
        }

        var resolution = await workflows.ResolveDefaultAsync(cancellationToken);
        var known = resolution is WorkflowResolution.ResolvedResult resolved
            ? resolved.Definition.Nodes.Select(static n => n.CapabilityId).ToHashSet(StringComparer.Ordinal)
            : [];
        var ids = requested.Where(known.Contains).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();

        IReadOnlyDictionary<string, long> waiting = new Dictionary<string, long>();
        if (ids.Count > 0)
        {
            try
            {
                waiting = await reader.ReadWaitingAsync(ids, cancellationToken);
            }
            catch (MessageTransportException)
            {
                return OperationFailure.Unavailable(QueueUnavailable);
            }
        }

        var capabilities = ids
            .Where(waiting.ContainsKey)
            .Select(id => new CapabilityQueueDepth(id, waiting[id]))
            .ToList();
        return new QueueDepth(time.GetUtcNow(), capabilities, capabilities.Sum(static c => c.Waiting));
    }
}
