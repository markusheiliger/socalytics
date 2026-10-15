namespace SocAlytics.Platform.Application.Registry;

public sealed class RegistryOptions
{
    public const string SectionName = "Registry";

    public bool AllowSoftwareKeys { get; set; }

    public PairingOptions Pairing { get; set; } = new();

    public TokenOptions Tokens { get; set; } = new();

    public RateLimitOptions RateLimits { get; set; } = new();

    public ExpiryWorkerOptions ExpiryWorker { get; set; } = new();

    public sealed class PairingOptions
    {
        public int MaxFingerprintMismatches { get; set; } = 3;

        public TimeSpan Window { get; set; } = TimeSpan.FromMinutes(10);

        public TimeSpan ApprovalWindow { get; set; } = TimeSpan.FromHours(24);

        public TimeSpan ActivationWindow { get; set; } = TimeSpan.FromMinutes(10);

        public TimeSpan ChallengeLifetime { get; set; } = TimeSpan.FromSeconds(60);

        public TimeSpan PollingInterval { get; set; } = TimeSpan.FromSeconds(5);

        public string VerificationPath { get; set; } = "/analyst-managers/pair";

        // Absolute origin of the page where a person enters the pairing code; Stamp:PublicBaseUri when unset.
        public string? VerificationBaseUri { get; set; }
    }

    public sealed class TokenOptions
    {
        public TimeSpan Lifetime { get; set; } = TimeSpan.FromMinutes(5);

        public TimeSpan MaxAssertionLifetime { get; set; } = TimeSpan.FromSeconds(60);

        public TimeSpan ProofClockSkew { get; set; } = TimeSpan.FromSeconds(60);
    }

    public sealed class RateLimitOptions
    {
        public int PairingCreationPerMinute { get; set; } = 10;

        public int StatusPerMinute { get; set; } = 60;

        public int ActivationPerMinute { get; set; } = 60;

        public int CodeSubmissionPerMinute { get; set; } = 10;

        public int TokenPerMinute { get; set; } = 30;
    }

    public sealed class ExpiryWorkerOptions
    {
        public TimeSpan Interval { get; set; } = TimeSpan.FromSeconds(30);
    }
}
