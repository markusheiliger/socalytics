using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.CompilerServices;
using System.Text;
using System.Xml.Linq;
using NetArchTest.Rules;
using Shouldly;
using SocAlytics.Platform.AgentOrchestration;
using SocAlytics.Platform.Analysis;
using SocAlytics.Platform.Club;
using SocAlytics.Platform.IdentityAccess;
using SocAlytics.Platform.Persistence;
using SocAlytics.Platform.Persistence.Connections;
using SocAlytics.Platform.Recordings;
using SocAlytics.Platform.Registry;
using Xunit;

namespace SocAlytics.Platform.Architecture.Tests;

public sealed class PlatformArchitectureTests
{
    private static readonly CapabilityBoundary[] CapabilityBoundaries =
    [
        CreateBoundary(typeof(AgentOrchestrationServiceCollectionExtensions), nameof(AgentOrchestrationServiceCollectionExtensions.AddAgentOrchestrationModule), ModuleKey.AgentOrchestration),
        CreateBoundary(typeof(AnalysisServiceCollectionExtensions), nameof(AnalysisServiceCollectionExtensions.AddAnalysisModule), ModuleKey.Analysis),
        CreateBoundary(typeof(ClubServiceCollectionExtensions), nameof(ClubServiceCollectionExtensions.AddClubModule), ModuleKey.Club),
        CreateBoundary(typeof(IdentityAccessServiceCollectionExtensions), nameof(IdentityAccessServiceCollectionExtensions.AddIdentityAccessModule), ModuleKey.IdentityAccess),
        CreateBoundary(typeof(RecordingsServiceCollectionExtensions), nameof(RecordingsServiceCollectionExtensions.AddRecordingsModule), ModuleKey.Recordings),
        CreateBoundary(typeof(RegistryServiceCollectionExtensions), nameof(RegistryServiceCollectionExtensions.AddRegistryModule), ModuleKey.Registry)
    ];

    private static readonly Type[] SharedPersistencePublicTypes =
    [
        typeof(PersistenceServiceCollectionExtensions),
        typeof(MigrationChecksum),
        typeof(ModuleKey),
        typeof(IModuleMigrationContributor),
        typeof(ModuleKeyExtensions),
        typeof(MigrationOrchestrator),
        typeof(MigrationConflictException),
        typeof(MigrationCatalog),
        typeof(IModuleTransactionExecutor),
        typeof(PersistenceOptions),
        typeof(MigrationDescriptor),
        typeof(OptimisticConcurrency),
        typeof(OptimisticConcurrencyConflictException),
        typeof(IModuleConnectionFactory)
    ];

    private static readonly string[] SharedPersistencePublicMemberNames =
    [
        $"{typeof(PersistenceServiceCollectionExtensions).FullName}.{nameof(PersistenceServiceCollectionExtensions.AddPlatformPersistence)}",
        $"{typeof(PersistenceServiceCollectionExtensions).FullName}.{nameof(PersistenceServiceCollectionExtensions.AddModuleMigrationContributor)}",
        $"{typeof(MigrationChecksum).FullName}.{nameof(MigrationChecksum.Compute)}",
        $"{typeof(IModuleMigrationContributor).FullName}.{nameof(IModuleMigrationContributor.ModuleKey)}",
        $"{typeof(IModuleMigrationContributor).FullName}.{nameof(IModuleMigrationContributor.GetMigrations)}",
        $"{typeof(ModuleKeyExtensions).FullName}.{nameof(ModuleKeyExtensions.ToSchemaName)}",
        $"{typeof(MigrationOrchestrator).FullName}.{nameof(MigrationOrchestrator.Run)}",
        $"{typeof(MigrationCatalog).FullName}.{nameof(MigrationCatalog.Migrations)}",
        $"{typeof(MigrationCatalog).FullName}.{nameof(MigrationCatalog.Create)}",
        $"{typeof(IModuleTransactionExecutor).FullName}.{nameof(IModuleTransactionExecutor.ExecuteAsync)}",
        $"{typeof(PersistenceOptions).FullName}.{nameof(PersistenceOptions.BootstrapConnectionString)}",
        $"{typeof(PersistenceOptions).FullName}.{nameof(PersistenceOptions.RuntimeConnectionString)}",
        $"{typeof(MigrationDescriptor).FullName}.{nameof(MigrationDescriptor.ModuleKey)}",
        $"{typeof(MigrationDescriptor).FullName}.{nameof(MigrationDescriptor.Sequence)}",
        $"{typeof(MigrationDescriptor).FullName}.{nameof(MigrationDescriptor.ScriptIdentity)}",
        $"{typeof(MigrationDescriptor).FullName}.{nameof(MigrationDescriptor.Content)}",
        $"{typeof(MigrationDescriptor).FullName}.{nameof(MigrationDescriptor.Checksum)}",
        $"{typeof(MigrationDescriptor).FullName}.{nameof(MigrationDescriptor.FromEmbeddedResource)}",
        $"{typeof(OptimisticConcurrency).FullName}.{nameof(OptimisticConcurrency.EnsureSingleRowAffected)}",
        $"{typeof(IModuleConnectionFactory).FullName}.{nameof(IModuleConnectionFactory.CreateConnection)}"
    ];

    private static readonly HashSet<string> ApiAllowedPersistenceTypes =
    [
        typeof(PersistenceServiceCollectionExtensions).FullName!,
        typeof(PersistenceOptions).FullName!,
        typeof(MigrationOrchestrator).FullName!
    ];

    private static readonly HashSet<(string TypeName, string MemberName)> ApiAllowedPersistenceMembers =
    [
        (typeof(PersistenceServiceCollectionExtensions).FullName!, nameof(PersistenceServiceCollectionExtensions.AddPlatformPersistence)),
        (typeof(PersistenceOptions).FullName!, $"set_{nameof(PersistenceOptions.BootstrapConnectionString)}"),
        (typeof(PersistenceOptions).FullName!, $"set_{nameof(PersistenceOptions.RuntimeConnectionString)}"),
        (typeof(MigrationOrchestrator).FullName!, nameof(MigrationOrchestrator.Run))
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
            if (TryGetCapabilityBoundary(metadata, typeReference, allowedBoundaries, out var capability))
            {
                var referencedType = $"{metadata.GetString(typeReference.Namespace)}.{metadata.GetString(typeReference.Name)}";
                if (!string.Equals(referencedType, capability.CompositionType.FullName, StringComparison.Ordinal))
                {
                    violations.Add($"{capability.AssemblyName}: type {referencedType}");
                }
            }
            else if (TryGetPersistenceType(metadata, typeReference, out var persistenceType) &&
                     !ApiAllowedPersistenceTypes.Contains(persistenceType))
            {
                violations.Add($"SocAlytics.Platform.Persistence: type {persistenceType}");
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
            var memberName = metadata.GetString(memberReference.Name);
            if (TryGetCapabilityBoundary(metadata, typeReference, allowedBoundaries, out var capability))
            {
                if (!string.Equals(memberName, capability.CompositionMethodName, StringComparison.Ordinal))
                {
                    violations.Add($"{capability.AssemblyName}: member {memberName}");
                }
            }
            else if (TryGetPersistenceType(metadata, typeReference, out var persistenceType) &&
                     !ApiAllowedPersistenceMembers.Contains((persistenceType, memberName)))
            {
                violations.Add($"SocAlytics.Platform.Persistence: {persistenceType}.{memberName}");
            }
        }

        violations.ShouldBeEmpty("The API may access capabilities only through their public composition methods.");
    }

    [Fact]
    public void Shared_persistence_exposes_only_its_restricted_module_neutral_surface()
    {
        var persistenceAssembly = typeof(MigrationCatalog).Assembly;
        persistenceAssembly.GetExportedTypes()
            .Select(type => type.FullName)
            .ShouldBe(
                SharedPersistencePublicTypes.Select(type => type.FullName),
                ignoreOrder: true);

        SharedPersistencePublicTypes
            .SelectMany(type => type.GetMembers(
                    BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Where(member => member is PropertyInfo || member is MethodInfo method && !method.IsSpecialName)
                .Select(member => $"{type.FullName}.{member.Name}"))
            .Distinct(StringComparer.Ordinal)
            .ShouldBe(SharedPersistencePublicMemberNames, ignoreOrder: true);
        Enum.GetNames<ModuleKey>()
            .ShouldBe(["Club", "IdentityAccess", "Recordings", "Registry", "Analysis", "AgentOrchestration"], ignoreOrder: true);
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

    [Fact]
    public void Module_migration_resources_are_declared_and_embedded_only_by_their_owner()
    {
        var inspectedProjectsDirectory = Path.Combine(AppContext.BaseDirectory, "InspectedProjects");
        var allResources = new List<(CapabilityBoundary Capability, string ResourceName)>();

        foreach (var capability in CapabilityBoundaries)
        {
            var projectFile = Path.Combine(inspectedProjectsDirectory, $"{capability.AssemblyName}.csproj");
            var declaredResourcePaths = XDocument.Load(projectFile)
                .Descendants()
                .Where(element => element.Name.LocalName == "EmbeddedResource")
                .Select(element => element.Attribute("Include")?.Value)
                .Where(include => !string.IsNullOrWhiteSpace(include))
                .Select(include => include!.Replace('\\', '.').Replace('/', '.'))
                .Where(include => include.Contains("Migration", StringComparison.OrdinalIgnoreCase) ||
                                  include.EndsWith(".sql", StringComparison.OrdinalIgnoreCase))
                .ToArray();
            var embeddedResourceNames = capability.Assembly.GetManifestResourceNames()
                .Where(IsSqlResource)
                .ToArray();

            declaredResourcePaths.ShouldNotBeEmpty($"{capability.AssemblyName} must declare its migrations.");
            declaredResourcePaths.ShouldAllBe(name =>
                name.StartsWith("Migrations.", StringComparison.Ordinal) &&
                name.EndsWith(".sql", StringComparison.OrdinalIgnoreCase));
            declaredResourcePaths.Select(name => $"{capability.AssemblyName}.{name}")
                .ShouldBe(embeddedResourceNames, ignoreOrder: true);

            allResources.AddRange(embeddedResourceNames.Select(name => (capability, name)));
        }

        allResources.ShouldNotBeEmpty();
        allResources.Select(resource => resource.ResourceName)
            .Distinct(StringComparer.Ordinal)
            .Count()
            .ShouldBe(allResources.Count);
        typeof(MigrationCatalog).Assembly.GetManifestResourceNames()
            .ShouldBeEmpty("Shared persistence must not own module SQL or migrations.");

        foreach (var (capability, resourceName) in allResources)
        {
            resourceName.StartsWith(
                    $"{capability.AssemblyName}.Migrations.",
                    StringComparison.Ordinal)
                .ShouldBeTrue($"{resourceName} must be embedded by its owning module.");
        }
    }

    [Fact]
    public void Module_migration_sql_does_not_reference_peer_schemas_or_roles()
    {
        foreach (var (capability, resourceName) in GetModuleSqlResources())
        {
            using var stream = capability.Assembly.GetManifestResourceStream(resourceName);
            using var reader = new StreamReader(stream!, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            var sql = reader.ReadToEnd();

            ContainsIdentifier(sql, capability.ModuleKey.ToSchemaName())
                .ShouldBeTrue($"{resourceName} must reference its owning schema.");

            foreach (var peer in CapabilityBoundaries.Where(other => other.ModuleKey != capability.ModuleKey))
            {
                var peerSchema = peer.ModuleKey.ToSchemaName();
                ContainsIdentifier(sql, peerSchema)
                    .ShouldBeFalse($"{resourceName} must not reference peer schema '{peerSchema}'.");
                ContainsIdentifier(sql, $"socalytics_{peerSchema}_owner")
                    .ShouldBeFalse($"{resourceName} must not reference the peer owner role.");
                ContainsIdentifier(sql, $"socalytics_{peerSchema}_runtime")
                    .ShouldBeFalse($"{resourceName} must not reference the peer runtime role.");
            }
        }
    }

    [Fact]
    public void Product_persistence_types_and_resources_do_not_contain_club_id()
    {
        var productAssemblies = CapabilityBoundaries
            .Select(capability => capability.Assembly)
            .Append(typeof(MigrationCatalog).Assembly)
            .ToArray();
        var violations = new List<string>();

        foreach (var assembly in productAssemblies)
        {
            foreach (var type in assembly.GetTypes())
            {
                if (ContainsClubId(type.FullName) || ContainsClubId(type.ToString()))
                {
                    violations.Add($"{assembly.GetName().Name}: type {type.FullName}");
                }

                foreach (var member in type.GetMembers(
                             BindingFlags.Public | BindingFlags.NonPublic |
                             BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                {
                    if (ContainsClubId(member.Name) || ContainsClubId(member.ToString()))
                    {
                        violations.Add($"{assembly.GetName().Name}: member {type.FullName}.{member.Name}");
                    }

                    if (member is MethodBase method)
                    {
                        violations.AddRange(method.GetParameters()
                            .Where(parameter => ContainsClubId(parameter.Name))
                            .Select(parameter => $"{assembly.GetName().Name}: parameter {type.FullName}.{member.Name}({parameter.Name})"));
                    }
                }
            }

            foreach (var resourceName in assembly.GetManifestResourceNames())
            {
                if (ContainsClubId(resourceName))
                {
                    violations.Add($"{assembly.GetName().Name}: resource name {resourceName}");
                }

                using var stream = assembly.GetManifestResourceStream(resourceName);
                using var reader = new StreamReader(stream!, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                if (ContainsClubId(reader.ReadToEnd()))
                {
                    violations.Add($"{assembly.GetName().Name}: resource {resourceName}");
                }
            }
        }

        violations.ShouldBeEmpty("Product persistence types and resources must not introduce club_id.");
    }

    private static CapabilityBoundary CreateBoundary(
        Type compositionType,
        string compositionMethodName,
        ModuleKey moduleKey)
    {
        var assembly = compositionType.Assembly;
        return new CapabilityBoundary(
            assembly,
            assembly.GetName().Name!,
            compositionType.Namespace!,
            compositionType,
            compositionMethodName,
            moduleKey);
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

    private static bool TryGetPersistenceType(
        MetadataReader metadata,
        TypeReference typeReference,
        out string typeName)
    {
        typeName = string.Empty;
        if (typeReference.ResolutionScope.Kind != HandleKind.AssemblyReference)
        {
            return false;
        }

        var assemblyReference = metadata.GetAssemblyReference((AssemblyReferenceHandle)typeReference.ResolutionScope);
        if (!string.Equals(
                metadata.GetString(assemblyReference.Name),
                typeof(MigrationCatalog).Assembly.GetName().Name,
                StringComparison.Ordinal))
        {
            return false;
        }

        typeName = $"{metadata.GetString(typeReference.Namespace)}.{metadata.GetString(typeReference.Name)}";
        return true;
    }

    private static IEnumerable<(CapabilityBoundary Capability, string ResourceName)> GetModuleSqlResources() =>
        CapabilityBoundaries.SelectMany(capability => capability.Assembly.GetManifestResourceNames()
            .Where(IsSqlResource)
            .Select(resourceName => (capability, resourceName)));

    private static bool IsSqlResource(string resourceName) =>
        resourceName.EndsWith(".sql", StringComparison.OrdinalIgnoreCase);

    private static bool ContainsIdentifier(string text, string identifier)
    {
        var startIndex = 0;
        while ((startIndex = text.IndexOf(identifier, startIndex, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            var endIndex = startIndex + identifier.Length;
            var hasLeftBoundary = startIndex == 0 || !IsIdentifierCharacter(text[startIndex - 1]);
            var hasRightBoundary = endIndex == text.Length || !IsIdentifierCharacter(text[endIndex]);
            if (hasLeftBoundary && hasRightBoundary)
            {
                return true;
            }

            startIndex = endIndex;
        }

        return false;
    }

    private static bool ContainsClubId(string? text) =>
        text?.Contains("club_id", StringComparison.OrdinalIgnoreCase) is true;

    private static bool IsIdentifierCharacter(char value) =>
        char.IsLetterOrDigit(value) || value == '_';

    private sealed record CapabilityBoundary(
        Assembly Assembly,
        string AssemblyName,
        string Namespace,
        Type CompositionType,
        string CompositionMethodName,
        ModuleKey ModuleKey);
}