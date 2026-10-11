namespace SocAlytics.Platform.Domain.Registry;

public sealed record RegistrationWindows
{
    public RegistrationWindows(
        TimeSpan pairing, TimeSpan approval, TimeSpan activation, TimeSpan challengeLifetime)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(pairing, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(approval, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(activation, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(challengeLifetime, TimeSpan.Zero);
        Pairing = pairing;
        Approval = approval;
        Activation = activation;
        ChallengeLifetime = challengeLifetime;
    }

    public TimeSpan Pairing { get; }

    public TimeSpan Approval { get; }

    public TimeSpan Activation { get; }

    public TimeSpan ChallengeLifetime { get; }

    public static RegistrationWindows Default { get; } =
        new(TimeSpan.FromMinutes(10), TimeSpan.FromHours(24), TimeSpan.FromMinutes(10), TimeSpan.FromSeconds(60));
}
