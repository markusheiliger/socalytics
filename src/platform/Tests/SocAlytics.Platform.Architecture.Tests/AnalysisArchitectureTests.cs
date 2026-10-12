using System.Reflection;
using NetArchTest.Rules;
using Shouldly;
using Xunit;

namespace SocAlytics.Platform.Architecture.Tests;

public sealed class AnalysisArchitectureTests
{
    private const string InfrastructureAssemblyName = "SocAlytics.Platform.Infrastructure";

    [Theory]
    [InlineData("SocAlytics.Platform.Infrastructure.Workers")]
    [InlineData("SocAlytics.Platform.Infrastructure.Messaging.Nats")]
    [InlineData("SocAlytics.Platform.Infrastructure.Contracts")]
    [InlineData("SocAlytics.Platform.Infrastructure.Registry")]
    [InlineData("SocAlytics.Platform.Infrastructure.Analysis")]
    [InlineData("SocAlytics.Platform.Infrastructure.Persistence.Analysis")]
    [InlineData("SocAlytics.Platform.Infrastructure.Persistence.Messaging")]
    public void Infrastructure_analysis_types_are_internal(string namespacePrefix)
    {
        var result = Types.InAssembly(Assembly.Load(new AssemblyName(InfrastructureAssemblyName)))
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
    public void Domain_and_application_do_not_reference_data_and_messaging_libraries(string assemblyName)
    {
        var result = Types.InAssembly(Assembly.Load(new AssemblyName(assemblyName)))
            .ShouldNot()
            .HaveDependencyOnAny("NATS", "Npgsql", "Dapper", "Json.Schema")
            .GetResult();

        result.IsSuccessful.ShouldBeTrue(
            $"{assemblyName} must not reference NATS, Npgsql, Dapper, or Json.Schema: {string.Join(", ", result.FailingTypeNames ?? [])}");
    }

    [Fact]
    public void Api_does_not_reference_nats()
    {
        var result = Types.InAssembly(Assembly.Load(new AssemblyName("SocAlytics.Platform.Api")))
            .ShouldNot()
            .HaveDependencyOn("NATS")
            .GetResult();

        result.IsSuccessful.ShouldBeTrue(
            $"The Api must not reference NATS: {string.Join(", ", result.FailingTypeNames ?? [])}");
    }
}
