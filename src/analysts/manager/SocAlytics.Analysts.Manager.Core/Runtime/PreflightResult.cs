namespace SocAlytics.Analysts.Manager.Runtime;

public sealed record PreflightCheck(string Id, bool Passed, string? Detail, string? Remediation);

public sealed record PreflightResult(
    DateTimeOffset RanAt,
    string? Profile,
    string? RuntimeProduct,
    string? RuntimeVersion,
    string? Backend,
    bool Passed,
    IReadOnlyList<PreflightCheck> Checks,
    IReadOnlyList<string> ValidatedCapabilities);
