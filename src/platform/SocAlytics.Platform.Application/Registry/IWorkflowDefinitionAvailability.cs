namespace SocAlytics.Platform.Application.Registry;

public interface IWorkflowDefinitionAvailability
{
    WorkflowDefinitionStatus Current { get; }
}

public sealed record WorkflowDefinitionStatus(string State, string? Category, DateTimeOffset Since)
{
    public const string Resolvable = "resolvable";
    public const string Unresolvable = "unresolvable";

    public bool IsResolvable => State == Resolvable;
}
