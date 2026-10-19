using System.Reflection;
using System.Xml.Linq;
using NetArchTest.Rules;
using Shouldly;
using Xunit;

namespace SocAlytics.Platform.Architecture.Tests;

public sealed class AgentOrchestrationArchitectureTests
{
    private const string PersistenceNamespace = "SocAlytics.Platform.Infrastructure.Persistence.AgentOrchestration";

    [Theory]
    [InlineData("SocAlytics.Platform.Domain", "SocAlytics.Platform.Domain.AgentOrchestration",
        new[] { "SocAlytics.Platform.Application", "SocAlytics.Platform.Infrastructure", "SocAlytics.Platform.Api", "Microsoft.AspNetCore", "Microsoft.Extensions", "Npgsql", "Dapper", "NATS" })]
    [InlineData("SocAlytics.Platform.Application", "SocAlytics.Platform.Application.AgentOrchestration",
        new[] { "SocAlytics.Platform.Infrastructure", "SocAlytics.Platform.Api", "Microsoft.AspNetCore", "Npgsql", "Dapper", "NATS" })]
    [InlineData("SocAlytics.Platform.Infrastructure", "SocAlytics.Platform.Infrastructure.AgentOrchestration",
        new[] { "SocAlytics.Platform.Api", "Microsoft.AspNetCore.Mvc", "Microsoft.AspNetCore.Routing" })]
    [InlineData("SocAlytics.Platform.Infrastructure", PersistenceNamespace,
        new[] { "SocAlytics.Platform.Api", "Microsoft.AspNetCore.Mvc", "Microsoft.AspNetCore.Routing" })]
    [InlineData("SocAlytics.Platform.Api", "SocAlytics.Platform.Api.AgentOrchestration",
        new[] { "SocAlytics.Platform.Infrastructure", "Npgsql", "Dapper", "NATS" })]
    public void Agent_orchestration_namespaces_follow_the_layer_dependencies(string assemblyName, string namespacePrefix, string[] forbidden)
    {
        var types = Types.InAssembly(Assembly.Load(new AssemblyName(assemblyName)))
            .That()
            .ResideInNamespaceStartingWith(namespacePrefix);

        var result = types.ShouldNot().HaveDependencyOnAny(forbidden).GetResult();

        result.IsSuccessful.ShouldBeTrue(
            $"{namespacePrefix} must not depend on {string.Join(", ", forbidden)}: {string.Join(", ", result.FailingTypeNames ?? [])}");
    }

    [Theory]
    [InlineData("SocAlytics.Platform.Infrastructure.AgentOrchestration")]
    [InlineData(PersistenceNamespace)]
    public void Infrastructure_agent_orchestration_types_are_internal(string namespacePrefix)
    {
        var result = Types.InAssembly(Assembly.Load(new AssemblyName("SocAlytics.Platform.Infrastructure")))
            .That()
            .ResideInNamespaceStartingWith(namespacePrefix)
            .Should()
            .NotBePublic()
            .GetResult();

        result.IsSuccessful.ShouldBeTrue(
            $"Public types in {namespacePrefix}: {string.Join(", ", result.FailingTypeNames ?? [])}");
    }

    [Theory]
    [InlineData("SocAlytics.Platform.Domain")]
    [InlineData("SocAlytics.Platform.Application")]
    [InlineData("SocAlytics.Platform.Api")]
    [InlineData("SocAlytics.Platform.Migrator")]
    public void Other_layers_do_not_reference_agent_orchestration_persistence_types(string assemblyName)
    {
        var result = Types.InAssembly(Assembly.Load(new AssemblyName(assemblyName)))
            .ShouldNot()
            .HaveDependencyOn(PersistenceNamespace)
            .GetResult();

        result.IsSuccessful.ShouldBeTrue(
            $"{assemblyName} must not reference {PersistenceNamespace}: {string.Join(", ", result.FailingTypeNames ?? [])}");
    }

    [Fact]
    public void Other_infrastructure_areas_do_not_reference_agent_orchestration_persistence_types()
    {
        var infrastructure = Assembly.Load(new AssemblyName("SocAlytics.Platform.Infrastructure"));

        // The composition root registers the stores; no other functional area may use them.
        var result = Types.InAssembly(infrastructure)
            .That()
            .DoNotResideInNamespaceStartingWith(PersistenceNamespace)
            .And()
            .DoNotHaveName("InfrastructureServiceCollectionExtensions")
            .And()
            .DoNotHaveNameStartingWith("<>")
            .ShouldNot()
            .HaveDependencyOn(PersistenceNamespace)
            .GetResult();

        result.IsSuccessful.ShouldBeTrue(
            $"Only the composition method may reference {PersistenceNamespace}: {string.Join(", ", result.FailingTypeNames ?? [])}");
    }

    [Fact]
    public void Agent_orchestration_persistence_types_do_not_reference_other_persistence_areas()
    {
        var result = Types.InAssembly(Assembly.Load(new AssemblyName("SocAlytics.Platform.Infrastructure")))
            .That()
            .ResideInNamespaceStartingWith(PersistenceNamespace)
            .ShouldNot()
            .HaveDependencyOnAny(
                "SocAlytics.Platform.Infrastructure.Persistence.Analysis",
                "SocAlytics.Platform.Infrastructure.Persistence.IdentityAccess",
                "SocAlytics.Platform.Infrastructure.Persistence.Registry")
            .GetResult();

        result.IsSuccessful.ShouldBeTrue(
            $"Agent Orchestration persistence must not use another area's persistence types: {string.Join(", ", result.FailingTypeNames ?? [])}");
    }

    [Fact]
    public void The_simulated_agent_workload_references_no_production_project()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "InspectedProjects", "SocAlytics.Platform.SimulatedAgentWorkload.csproj");
        var references = XDocument.Load(path)
            .Descendants()
            .Where(element => element.Name.LocalName == "ProjectReference")
            .Select(element => Path.GetFileNameWithoutExtension((element.Attribute("Include")?.Value ?? string.Empty).Replace('\\', '/')))
            .ToArray();

        references.ShouldBeEmpty("The simulated agent workload talks to the platform only through its contracts and must reference no project.");
    }
}
