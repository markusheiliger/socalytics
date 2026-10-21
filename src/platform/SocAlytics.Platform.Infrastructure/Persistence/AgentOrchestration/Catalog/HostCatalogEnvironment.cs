using Microsoft.Extensions.Hosting;
using SocAlytics.Platform.Application.AgentOrchestration.Catalog.Enablement;

namespace SocAlytics.Platform.Infrastructure.Persistence.AgentOrchestration.Catalog;

internal sealed class HostCatalogEnvironment(IHostEnvironment environment) : ICatalogEnvironment
{
    public bool AllowsSimulatedProvider => environment.IsDevelopment() || environment.IsEnvironment("Test");
}
