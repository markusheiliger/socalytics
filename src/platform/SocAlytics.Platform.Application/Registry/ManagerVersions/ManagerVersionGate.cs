using Microsoft.Extensions.Logging;
using SocAlytics.Platform.Application.Abstractions;
using SocAlytics.Platform.Application.Registry.StatusReporting;
using SocAlytics.Platform.Application.Telemetry;
using SocAlytics.Platform.Domain.Registry;

namespace SocAlytics.Platform.Application.Registry.ManagerVersions;

public sealed class ManagerVersionGate(
    MinimumManagerVersionProvider minimumVersion, IManagerStatusStore statuses, ILogger<ManagerVersionGate> logger)
{
    public const string RefusalCode = "analyst-manager-version-below-minimum";
    public const string QueueCredentialsOperation = "queue-credentials";
    public const string ClaimOperation = "claim";

    // Returns null when no minimum applies or the effective version meets it.
    public async Task<OperationFailure?> CheckAsync(
        AnalystManagerRegistration registration, string operation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(registration);
        var minimum = minimumVersion.Current;
        if (minimum is null)
        {
            return null;
        }

        var effective = await statuses.FindVersionAsync(registration.Id, cancellationToken)
            ?? registration.DeviceMetadata.ManagerVersion;
        if (!ManagerVersion.IsBelow(effective, minimum))
        {
            return null;
        }

        logger.LogWarning(
            "Refused {Operation} for registration {RegistrationId}: Manager version {EffectiveVersion} is below the minimum {MinimumVersion}.",
            operation, registration.Id, effective, minimum.Value);
        AnalysisMetrics.ManagerVersionRefused(operation);
        return OperationFailure.Forbidden(RefusalCode) with
        {
            Extensions = new Dictionary<string, string> { ["minimumManagerVersion"] = minimum.Value },
        };
    }
}
