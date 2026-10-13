using SocAlytics.Platform.Domain.Registry;

namespace SocAlytics.Platform.Application.Registry.StatusReporting;

public enum ManagerConnectivity
{
    Online,
    Offline,
    NotApplicable,
}

public static class Connectivity
{
    public static ManagerConnectivity Derive(
        RegistrationState state, StoredManagerStatus? status, DateTimeOffset now, int missedIntervalsBeforeOffline)
    {
        if (state is not (RegistrationState.Active or RegistrationState.Unregistering))
        {
            return ManagerConnectivity.NotApplicable;
        }

        if (status is null)
        {
            return ManagerConnectivity.Offline;
        }

        var allowed = TimeSpan.FromSeconds((double)status.IntervalSeconds * missedIntervalsBeforeOffline);
        return now - status.ReceivedAt <= allowed ? ManagerConnectivity.Online : ManagerConnectivity.Offline;
    }

    public static string ToApiName(this ManagerConnectivity value) => value switch
    {
        ManagerConnectivity.Online => "online",
        ManagerConnectivity.Offline => "offline",
        ManagerConnectivity.NotApplicable => "not-applicable",
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };
}
