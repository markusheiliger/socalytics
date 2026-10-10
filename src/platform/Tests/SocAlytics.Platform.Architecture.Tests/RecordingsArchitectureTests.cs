using System.Reflection;
using System.Xml.Linq;
using NetArchTest.Rules;
using Shouldly;
using Xunit;

namespace SocAlytics.Platform.Architecture.Tests;

public sealed class RecordingsArchitectureTests
{
    private const string InfrastructureAssemblyName = "SocAlytics.Platform.Infrastructure";

    [Theory]
    [InlineData("SocAlytics.Platform.Domain")]
    [InlineData("SocAlytics.Platform.Application")]
    [InlineData("SocAlytics.Platform.Api")]
    public void Inner_layers_and_api_do_not_depend_on_amazon_namespaces(string assemblyName)
    {
        var result = Types.InAssembly(Assembly.Load(new AssemblyName(assemblyName)))
            .ShouldNot()
            .HaveDependencyOn("Amazon")
            .GetResult();

        result.IsSuccessful.ShouldBeTrue(
            $"{assemblyName} must not depend on Amazon namespaces: {string.Join(", ", result.FailingTypeNames ?? [])}");
    }

    [Fact]
    public void The_apphost_references_no_aws_sdk_package()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "InspectedProjects", "SocAlytics.Platform.AppHost.csproj");
        var packages = XDocument.Load(path)
            .Descendants()
            .Where(element => element.Name.LocalName is "PackageReference" or "PackageVersion")
            .Select(element => element.Attribute("Include")?.Value ?? string.Empty)
            .Where(include => include.StartsWith("AWSSDK.", StringComparison.OrdinalIgnoreCase));

        packages.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("S3ObjectStorage")]
    [InlineData("ObjectStorageOptions")]
    public void Object_storage_adapter_types_are_not_public(string typeName)
    {
        var type = Assembly.Load(new AssemblyName(InfrastructureAssemblyName))
            .GetTypes()
            .Single(t => t.Name == typeName);

        type.IsPublic.ShouldBeFalse();
    }
}
