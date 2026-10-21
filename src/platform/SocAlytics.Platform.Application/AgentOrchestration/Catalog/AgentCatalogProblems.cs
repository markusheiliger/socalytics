namespace SocAlytics.Platform.Application.AgentOrchestration.Catalog;

/// <summary>Problem codes of the Agent Catalog API; the wire type is <c>urn:socalytics:problem:&lt;code&gt;</c>.</summary>
public static class AgentCatalogProblems
{
    public const string ValidationFailed = "agent-catalog-validation-failed";
    public const string VersionExists = "agent-catalog-version-exists";
    public const string AgentVersionNotDraft = "agent-version-not-draft";
    public const string DifferentiationReviewRequired = "differentiation-review-required";
    public const string AgentVersionNotApproved = "agent-version-not-approved";
    public const string AgentVersionEnabled = "agent-version-enabled";
    public const string AgentVersionNotEnableable = "agent-version-not-enableable";
    public const string CoachAgentEnablementUnsupported = "coach-agent-enablement-unsupported";
    public const string AgentNotAvailable = "agent-not-available";
    public const string ExecutionReferencesMismatch = "execution-references-mismatch";

    public const string ViolationsExtension = "violations";
    public const string ReasonExtension = "reason";

    public const string ReasonNotApproved = "not-approved";
    public const string ReasonRetired = "retired";
    public const string ReasonSimulatedProvider = "simulated-provider";
}
