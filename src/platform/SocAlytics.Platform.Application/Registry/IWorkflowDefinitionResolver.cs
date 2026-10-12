namespace SocAlytics.Platform.Application.Registry;

public interface IWorkflowDefinitionResolver
{
    Task<WorkflowResolution> ResolveDefaultAsync(CancellationToken cancellationToken);
}
