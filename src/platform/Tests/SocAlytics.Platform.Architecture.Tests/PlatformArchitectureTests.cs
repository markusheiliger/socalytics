using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.CompilerServices;
using System.Xml.Linq;
using NetArchTest.Rules;
using Shouldly;
using SocAlytics.Platform.Application;
using SocAlytics.Platform.Infrastructure;
using Xunit;

namespace SocAlytics.Platform.Architecture.Tests;

public sealed class PlatformArchitectureTests
{
    private const string DomainAssemblyName = "SocAlytics.Platform.Domain";
    private const string ApplicationAssemblyName = "SocAlytics.Platform.Application";
    private const string InfrastructureAssemblyName = "SocAlytics.Platform.Infrastructure";
    private const string ApiAssemblyName = "SocAlytics.Platform.Api";

    // Allowed project references per layer project; anything else violates the layer dependency direction.
    private static readonly Dictionary<string, string[]> AllowedProjectReferences = new(StringComparer.Ordinal)
    {
        [DomainAssemblyName] = [],
        [ApplicationAssemblyName] = [DomainAssemblyName],
        [InfrastructureAssemblyName] = [ApplicationAssemblyName, DomainAssemblyName],
        [ApiAssemblyName] = [ApplicationAssemblyName, InfrastructureAssemblyName, "SocAlytics.Platform.ServiceDefaults"],
    };

    private static readonly CompositionBoundary[] CompositionBoundaries =
    [
        new(typeof(ApplicationServiceCollectionExtensions), nameof(ApplicationServiceCollectionExtensions.AddApplication)),
        new(typeof(InfrastructureServiceCollectionExtensions), nameof(InfrastructureServiceCollectionExtensions.AddInfrastructure)),
    ];

    [Fact]
    public void Layer_projects_reference_only_inner_layers()
    {
        var projectDirectory = Path.Combine(AppContext.BaseDirectory, "InspectedProjects");

        foreach (var (project, allowed) in AllowedProjectReferences)
        {
            var references = XDocument.Load(Path.Combine(projectDirectory, $"{project}.csproj"))
                .Descendants()
                .Where(element => element.Name.LocalName == "ProjectReference")
                .Select(element => element.Attribute("Include")?.Value)
                .Where(include => !string.IsNullOrWhiteSpace(include))
                .Select(include => Path.GetFileNameWithoutExtension(include!.Replace('\\', '/')))
                .ToArray();

            references.ShouldBeSubsetOf(allowed, $"{project} may reference only {string.Join(", ", allowed)}.");
        }
    }

    [Fact]
    public void Domain_depends_on_no_other_layer_and_no_framework()
    {
        var result = Types.InAssembly(LoadAssembly(DomainAssemblyName))
            .ShouldNot()
            .HaveDependencyOnAny(
                ApplicationAssemblyName,
                InfrastructureAssemblyName,
                ApiAssemblyName,
                "Microsoft.AspNetCore",
                "Microsoft.Extensions",
                "Npgsql",
                "Dapper",
                "DbUp")
            .GetResult();

        result.IsSuccessful.ShouldBeTrue("The Domain layer must not depend on other layers or on frameworks.");
    }

    [Fact]
    public void Application_depends_only_on_the_domain()
    {
        var result = Types.InAssembly(typeof(ApplicationServiceCollectionExtensions).Assembly)
            .ShouldNot()
            .HaveDependencyOnAny(
                InfrastructureAssemblyName,
                ApiAssemblyName,
                "Microsoft.AspNetCore",
                "Npgsql",
                "Dapper",
                "DbUp")
            .GetResult();

        result.IsSuccessful.ShouldBeTrue("The Application layer must not depend on Infrastructure, the API, or data-access libraries.");
    }

    [Fact]
    public void Infrastructure_does_not_depend_on_the_api()
    {
        var result = Types.InAssembly(typeof(InfrastructureServiceCollectionExtensions).Assembly)
            .ShouldNot()
            .HaveDependencyOnAny(ApiAssemblyName, "Microsoft.AspNetCore.Mvc", "Microsoft.AspNetCore.Routing")
            .GetResult();

        result.IsSuccessful.ShouldBeTrue("The Infrastructure layer must not depend on the API.");
    }

    [Fact]
    public void Infrastructure_exposes_only_its_composition_surface()
    {
        var boundary = CompositionBoundaries.Single(candidate => candidate.AssemblyName == InfrastructureAssemblyName);

        boundary.Assembly.GetExportedTypes().ShouldBe([boundary.CompositionType]);
    }

    [Fact]
    public void Layer_composition_methods_extend_the_service_collection()
    {
        foreach (var boundary in CompositionBoundaries)
        {
            var publicDeclaredMembers = boundary.CompositionType.GetMembers(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly);
            publicDeclaredMembers.Select(member => member.Name).ShouldBe([boundary.CompositionMethodName]);

            var compositionMethod = (MethodInfo)publicDeclaredMembers.Single();
            compositionMethod.IsDefined(typeof(ExtensionAttribute), inherit: false).ShouldBeTrue();
            compositionMethod.ReturnType.FullName.ShouldBe("Microsoft.Extensions.DependencyInjection.IServiceCollection");
            compositionMethod.GetParameters().Select(parameter => parameter.ParameterType.FullName)
                .ShouldBe(["Microsoft.Extensions.DependencyInjection.IServiceCollection"]);
        }
    }

    [Fact]
    public void Layer_markers_stay_internal()
    {
        var assemblies = CompositionBoundaries.Select(boundary => boundary.Assembly).Append(LoadAssembly(DomainAssemblyName));
        foreach (var assembly in assemblies)
        {
            assembly.GetTypes()
                .Where(type => type.Name.EndsWith("LayerMarker", StringComparison.Ordinal))
                .ShouldAllBe(type => !type.IsPublic && !type.IsNestedPublic);
        }
    }

    [Fact]
    public void Api_uses_infrastructure_only_through_its_composition_method()
    {
        var infrastructure = CompositionBoundaries.Single(candidate => candidate.AssemblyName == InfrastructureAssemblyName);
        var apiAssemblyPath = Path.Combine(AppContext.BaseDirectory, $"{ApiAssemblyName}.dll");
        using var stream = File.OpenRead(apiAssemblyPath);
        using var peReader = new PEReader(stream);
        var metadata = peReader.GetMetadataReader();
        var violations = new List<string>();

        foreach (var typeReferenceHandle in metadata.TypeReferences)
        {
            var typeReference = metadata.GetTypeReference(typeReferenceHandle);
            if (!IsFromAssembly(metadata, typeReference, InfrastructureAssemblyName))
            {
                continue;
            }

            var referencedType = $"{metadata.GetString(typeReference.Namespace)}.{metadata.GetString(typeReference.Name)}";
            if (!string.Equals(referencedType, infrastructure.CompositionType.FullName, StringComparison.Ordinal))
            {
                violations.Add($"type {referencedType}");
            }
        }

        foreach (var memberReferenceHandle in metadata.MemberReferences)
        {
            var memberReference = metadata.GetMemberReference(memberReferenceHandle);
            if (memberReference.Parent.Kind != HandleKind.TypeReference)
            {
                continue;
            }

            var typeReference = metadata.GetTypeReference((TypeReferenceHandle)memberReference.Parent);
            if (!IsFromAssembly(metadata, typeReference, InfrastructureAssemblyName))
            {
                continue;
            }

            var memberName = metadata.GetString(memberReference.Name);
            if (!string.Equals(memberName, infrastructure.CompositionMethodName, StringComparison.Ordinal))
            {
                violations.Add($"member {memberName}");
            }
        }

        violations.ShouldBeEmpty("The API may use Infrastructure only through its composition method.");
    }

    private static Assembly LoadAssembly(string name) => Assembly.Load(new AssemblyName(name));

    private static bool IsFromAssembly(MetadataReader metadata, TypeReference typeReference, string assemblyName)
    {
        if (typeReference.ResolutionScope.Kind != HandleKind.AssemblyReference)
        {
            return false;
        }

        var assemblyReference = metadata.GetAssemblyReference((AssemblyReferenceHandle)typeReference.ResolutionScope);
        return string.Equals(metadata.GetString(assemblyReference.Name), assemblyName, StringComparison.Ordinal);
    }

    private sealed record CompositionBoundary(Type CompositionType, string CompositionMethodName)
    {
        public Assembly Assembly => CompositionType.Assembly;

        public string AssemblyName => Assembly.GetName().Name!;
    }
}
