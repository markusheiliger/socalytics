using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.CompilerServices;
using System.Xml.Linq;
using NetArchTest.Rules;
using Shouldly;
using SocAlytics.Platform.AgentOrchestration;
using SocAlytics.Platform.Analysis;
using SocAlytics.Platform.Club;
using SocAlytics.Platform.IdentityAccess;
using SocAlytics.Platform.Recordings;
using SocAlytics.Platform.Registry;
using Xunit;

namespace SocAlytics.Platform.Architecture.Tests;

public sealed class PlatformArchitectureTests
{
    private static readonly CapabilityBoundary[] CapabilityBoundaries =
    [
        CreateBoundary(typeof(AgentOrchestrationServiceCollectionExtensions), nameof(AgentOrchestrationServiceCollectionExtensions.AddAgentOrchestrationModule)),
        CreateBoundary(typeof(AnalysisServiceCollectionExtensions), nameof(AnalysisServiceCollectionExtensions.AddAnalysisModule)),
        CreateBoundary(typeof(ClubServiceCollectionExtensions), nameof(ClubServiceCollectionExtensions.AddClubModule)),
        CreateBoundary(typeof(IdentityAccessServiceCollectionExtensions), nameof(IdentityAccessServiceCollectionExtensions.AddIdentityAccessModule)),
        CreateBoundary(typeof(RecordingsServiceCollectionExtensions), nameof(RecordingsServiceCollectionExtensions.AddRecordingsModule)),
        CreateBoundary(typeof(RegistryServiceCollectionExtensions), nameof(RegistryServiceCollectionExtensions.AddRegistryModule))
    ];

    [Fact]
    public void Capability_assemblies_do_not_depend_on_other_capabilities()
    {
        foreach (var capability in CapabilityBoundaries)
        {
            var prohibitedNamespaces = CapabilityBoundaries
                .Where(other => other != capability)
                .Select(other => other.Namespace)
                .ToArray();

            var result = Types.InAssembly(capability.Assembly)
                .ShouldNot()
                .HaveDependencyOnAny(prohibitedNamespaces)
                .GetResult();

            result.IsSuccessful.ShouldBeTrue(
                $"{capability.AssemblyName} must not depend on another capability namespace.");
        }
    }

    [Fact]
    public void Capability_projects_do_not_reference_other_capability_projects()
    {
        var capabilityProjectNames = CapabilityBoundaries
            .Select(capability => capability.AssemblyName)
            .ToHashSet(StringComparer.Ordinal);
        var projectDirectory = Path.Combine(AppContext.BaseDirectory, "InspectedProjects");

        foreach (var projectFile in Directory.EnumerateFiles(projectDirectory, "*.csproj"))
        {
            var referencedCapabilityProjects = XDocument.Load(projectFile)
                .Descendants()
                .Where(element => element.Name.LocalName == "ProjectReference")
                .Select(element => element.Attribute("Include")?.Value)
                .Where(include => !string.IsNullOrWhiteSpace(include))
                .Select(include => Path.GetFileNameWithoutExtension(include!))
                .Where(capabilityProjectNames.Contains)
                .ToArray();

            referencedCapabilityProjects.ShouldBeEmpty(
                $"{Path.GetFileName(projectFile)} must not reference another capability project.");
        }
    }

    [Fact]
    public void Api_uses_only_public_capability_composition_members()
    {
        var allowedBoundaries = CapabilityBoundaries.ToDictionary(
            capability => capability.AssemblyName,
            StringComparer.Ordinal);
        var apiAssemblyPath = Path.Combine(AppContext.BaseDirectory, "SocAlytics.Platform.Api.dll");
        using var stream = File.OpenRead(apiAssemblyPath);
        using var peReader = new PEReader(stream);
        var metadata = peReader.GetMetadataReader();
        var violations = new List<string>();

        foreach (var typeReferenceHandle in metadata.TypeReferences)
        {
            var typeReference = metadata.GetTypeReference(typeReferenceHandle);
            if (!TryGetCapabilityBoundary(metadata, typeReference, allowedBoundaries, out var capability))
            {
                continue;
            }

            var referencedType = $"{metadata.GetString(typeReference.Namespace)}.{metadata.GetString(typeReference.Name)}";
            if (!string.Equals(referencedType, capability.CompositionType.FullName, StringComparison.Ordinal))
            {
                violations.Add($"{capability.AssemblyName}: type {referencedType}");
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
            if (!TryGetCapabilityBoundary(metadata, typeReference, allowedBoundaries, out var capability))
            {
                continue;
            }

            var memberName = metadata.GetString(memberReference.Name);
            if (!string.Equals(memberName, capability.CompositionMethodName, StringComparison.Ordinal))
            {
                violations.Add($"{capability.AssemblyName}: member {memberName}");
            }
        }

        violations.ShouldBeEmpty("The API may access capabilities only through their public composition methods.");
    }

    [Fact]
    public void Capability_assemblies_expose_only_their_composition_surface()
    {
        foreach (var capability in CapabilityBoundaries)
        {
            capability.Assembly.GetExportedTypes().ShouldBe([capability.CompositionType], ignoreOrder: true);

            var publicDeclaredMembers = capability.CompositionType.GetMembers(
                BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly);
            publicDeclaredMembers.Select(member => member.Name)
                .ShouldBe([capability.CompositionMethodName], ignoreOrder: true);

            publicDeclaredMembers.Single().MemberType.ShouldBe(MemberTypes.Method);
            var compositionMethod = (MethodInfo)publicDeclaredMembers.Single();
            compositionMethod.IsDefined(typeof(ExtensionAttribute), inherit: false).ShouldBeTrue();
            compositionMethod.ReturnType.FullName.ShouldBe("Microsoft.Extensions.DependencyInjection.IServiceCollection");
            compositionMethod.GetParameters().Select(parameter => parameter.ParameterType.FullName)
                .ShouldBe(["Microsoft.Extensions.DependencyInjection.IServiceCollection"]);

            capability.Assembly.GetTypes()
                .Where(type => type.Name.EndsWith("ModuleMarker", StringComparison.Ordinal))
                .ShouldAllBe(type => !type.IsPublic && !type.IsNestedPublic);
        }
    }

    private static CapabilityBoundary CreateBoundary(Type compositionType, string compositionMethodName)
    {
        var assembly = compositionType.Assembly;
        return new CapabilityBoundary(
            assembly,
            assembly.GetName().Name!,
            compositionType.Namespace!,
            compositionType,
            compositionMethodName);
    }

    private static bool TryGetCapabilityBoundary(
        MetadataReader metadata,
        TypeReference typeReference,
        IReadOnlyDictionary<string, CapabilityBoundary> allowedBoundaries,
        out CapabilityBoundary capability)
    {
        capability = null!;
        if (typeReference.ResolutionScope.Kind != HandleKind.AssemblyReference)
        {
            return false;
        }

        var assemblyReference = metadata.GetAssemblyReference((AssemblyReferenceHandle)typeReference.ResolutionScope);
        return allowedBoundaries.TryGetValue(metadata.GetString(assemblyReference.Name), out capability!);
    }

    private sealed record CapabilityBoundary(
        Assembly Assembly,
        string AssemblyName,
        string Namespace,
        Type CompositionType,
        string CompositionMethodName);
}