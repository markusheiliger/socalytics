using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SocAlytics.Platform.Domain.Registry;

namespace SocAlytics.Platform.Application.Registry.ManagerVersions;

public sealed class MinimumManagerVersionProvider
{
    private readonly IOptionsMonitor<ManagerVersionOptions> monitor;
    private readonly ILogger<MinimumManagerVersionProvider> logger;
    private readonly Lock gate = new();
    private ManagerVersion? lastValid;
    private bool warned;

    public MinimumManagerVersionProvider(
        IOptionsMonitor<ManagerVersionOptions> monitor,
        ILogger<MinimumManagerVersionProvider> logger)
    {
        ArgumentNullException.ThrowIfNull(monitor);
        ArgumentNullException.ThrowIfNull(logger);
        this.monitor = monitor;
        this.logger = logger;
    }

    // The configured minimum, or the last valid one while a reloaded value is invalid.
    public ManagerVersion? Current
    {
        get
        {
            string? configured = null;
            var valid = true;
            try
            {
                configured = monitor.CurrentValue.Minimum;
            }
            catch (OptionsValidationException)
            {
                valid = false;
            }

            lock (gate)
            {
                if (valid && string.IsNullOrEmpty(configured))
                {
                    lastValid = null;
                    warned = false;
                }
                else if (valid && ManagerVersion.TryParse(configured, out var parsed))
                {
                    lastValid = parsed;
                    warned = false;
                }
                else if (!warned)
                {
                    warned = true;
                    logger.LogWarning(
                        "Ignoring invalid {Setting} after a reload; keeping the last valid minimum ({Minimum}).",
                        ManagerVersionOptions.SectionName + ":Minimum",
                        lastValid?.Value ?? "none");
                }

                return lastValid;
            }
        }
    }
}
