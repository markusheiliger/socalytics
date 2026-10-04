using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using NetArchTest.Rules;
using Shouldly;
using SocAlytics.Platform.AgentOrchestration;
using SocAlytics.Platform.Analysis;
using SocAlytics.Platform.Club;
using SocAlytics.Platform.IdentityAccess;
using SocAlytics.Platform.Persistence;
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

    private static readonly string[] SharedPersistenceExports =
    [
        nameof(ConcurrencyConflictException),
        nameof(IMigrationContributor),
        nameof(IModuleConnectionFactory),
        nameof(MigrationCatalog),
        nameof(MigrationDescriptor),
        nameof(MigrationFailedException),
        nameof(MigrationOrchestrator),
        nameof(ModuleTransactionExtensions),
        nameof(OptimisticConcurrency),
        nameof(PersistenceModuleKey),
        nameof(PersistenceServiceCollectionExtensions)
    ];

    private static readonly string[] ApiAllowedPersistenceTypes =
    [
        nameof(PersistenceServiceCollectionExtensions),
        nameof(MigrationOrchestrator),
        nameof(MigrationFailedException)
    ];

    [Fact]
    public void Shared_persistence_does_not_depend_on_capabilities()
    {
        var result = Types.InAssembly(typeof(PersistenceModuleKey).Assembly)
            .ShouldNot()
            .HaveDependencyOnAny(CapabilityBoundaries.Select(capability => capability.Namespace).ToArray())
            .GetResult();

        result.IsSuccessful.ShouldBeTrue("Shared persistence must remain module-neutral.");
    }

    [Fact]
    public void Shared_persistence_exposes_only_the_restricted_public_surface()
    {
        var assembly = typeof(PersistenceModuleKey).Assembly;

        assembly.GetExportedTypes().Select(type => type.Name)
            .ShouldBe(SharedPersistenceExports, ignoreOrder: true);

        var privilegedTypes = new[] { "BootstrapConnectionSource", "RuntimeDataSource" };
        foreach (var privilegedName in privilegedTypes)
        {
            var privileged = assembly.GetTypes().Single(type => type.Name == privilegedName);
            privileged.IsPublic.ShouldBeFalse($"{privilegedName} must not be public.");
        }

        var exposedPrivilegedMembers = assembly.GetExportedTypes()
            .SelectMany(type => type.GetMembers(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
                .OfType<MethodBase>()
                .Where(method => method.IsPublic || method.IsFamily || method.IsFamilyOrAssembly)
                .Where(method => method.GetParameters().Select(parameter => parameter.ParameterType)
                    .Concat(method is MethodInfo info ? [info.ReturnType] : [])
                    .Any(parameterType => parameterType.Name is "BootstrapConnectionSource" or "RuntimeDataSource" or "NpgsqlDataSource"))
                .Select(method => $"{type.Name}.{method.Name}"))
            .ToArray();

        exposedPrivilegedMembers.ShouldBeEmpty("Public shared persistence members must not expose bootstrap or raw data sources.");
    }

    [Fact]
    public void Api_uses_only_permitted_shared_persistence_types()
    {
        var persistenceName = typeof(PersistenceModuleKey).Assembly.GetName().Name;
        using var stream = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "SocAlytics.Platform.Api.dll"));
        using var peReader = new PEReader(stream);
        var metadata = peReader.GetMetadataReader();

        var violations = metadata.TypeReferences
            .Select(handle => metadata.GetTypeReference(handle))
            .Where(reference => reference.ResolutionScope.Kind == HandleKind.AssemblyReference
                && metadata.GetString(metadata.GetAssemblyReference((AssemblyReferenceHandle)reference.ResolutionScope).Name) == persistenceName)
            .Select(reference => metadata.GetString(reference.Name))
            .Where(name => !ApiAllowedPersistenceTypes.Contains(name, StringComparer.Ordinal))
            .ToArray();

        violations.ShouldBeEmpty("The API may use shared persistence only for registration and startup orchestration.");
    }

    [Fact]
    public void Each_module_owns_its_embedded_sql_resources_exclusively()
    {
        var seenResources = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var capability in CapabilityBoundaries)
        {
            var module = GetModuleKey(capability);
            var resources = capability.Assembly.GetManifestResourceNames();

            resources.ShouldNotBeEmpty($"{capability.AssemblyName} must embed its migrations.");
            foreach (var resource in resources)
            {
                resource.ShouldStartWith($"{capability.Namespace}.Migrations.");
                resource.ShouldEndWith(".sql");
                seenResources.TryAdd(resource, capability.AssemblyName).ShouldBeTrue(
                    $"{resource} is embedded by more than one assembly.");
            }

            var contributors = capability.Assembly.GetTypes()
                .Where(type => typeof(IMigrationContributor).IsAssignableFrom(type))
                .ToArray();
            contributors.Length.ShouldBe(1);
            contributors[0].IsPublic.ShouldBeFalse();

            var contributor = (IMigrationContributor)Activator.CreateInstance(contributors[0], nonPublic: true)!;
            contributor.Module.ShouldBe(module);
            contributor.GetMigrations().ShouldAllBe(migration => migration.Module == module);
        }

        var migrationKeys = CapabilityBoundaries
            .Select(capability => GetContributor(capability))
            .SelectMany(contributor => contributor.GetMigrations())
            .Select(migration => (migration.Module.Key, migration.Sequence))
            .ToArray();
        migrationKeys.Distinct().Count().ShouldBe(migrationKeys.Length);
        PersistenceModuleKey.All.Select(module => module.Key)
            .ShouldBe(CapabilityBoundaries.Select(capability => GetModuleKey(capability).Key), ignoreOrder: true);
    }

    [Fact]
    public void Module_sql_never_references_peer_schemas_or_roles()
    {
        foreach (var capability in CapabilityBoundaries)
        {
            var module = GetModuleKey(capability);
            var peerIdentifiers = PersistenceModuleKey.All
                .Where(other => other != module)
                .SelectMany(other => new[] { other.Schema, other.OwnerRole, other.RuntimeRole })
                .ToArray();

            foreach (var (name, sql) in ReadSqlResources(capability))
            {
                var code = StripSqlComments(sql);
                foreach (var identifier in peerIdentifiers)
                {
                    Regex.IsMatch(code, $@"(?<![A-Za-z0-9_]){Regex.Escape(identifier)}(?![A-Za-z0-9_])", RegexOptions.IgnoreCase)
                        .ShouldBeFalse($"{name} must not reference peer identifier {identifier}.");
                }

                Regex.IsMatch(code, @"\b(SET\s+(SESSION\s+|LOCAL\s+)?ROLE|SET\s+SESSION\s+AUTHORIZATION|SET\s+search_path|CREATE\s+(ROLE|USER)|ALTER\s+(ROLE|USER)|GRANT\s+\S+\s+ON\s+ALL|TO\s+PUBLIC)\b", RegexOptions.IgnoreCase)
                    .ShouldBeFalse($"{name} must not change roles, search path, or grant broadly.");

                foreach (Match match in Regex.Matches(code, @"\bSCHEMA\s+(?:IF\s+NOT\s+EXISTS\s+)?([A-Za-z0-9_]+)", RegexOptions.IgnoreCase))
                {
                    match.Groups[1].Value.ToLowerInvariant().ShouldBe(module.Schema);
                }
            }
        }
    }

    [Fact]
    public void Product_persistence_types_and_resources_do_not_use_club_id()
    {
        var assemblies = CapabilityBoundaries.Select(capability => capability.Assembly)
            .Append(typeof(PersistenceModuleKey).Assembly)
            .ToArray();
        var pattern = new Regex("club_?id", RegexOptions.IgnoreCase);
        var violations = new List<string>();
        const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

        foreach (var assembly in assemblies)
        {
            foreach (var type in assembly.GetTypes())
            {
                if (pattern.IsMatch(type.Name))
                {
                    violations.Add($"type {type.FullName}");
                }

                foreach (var member in type.GetMembers(all))
                {
                    if (pattern.IsMatch(member.Name))
                    {
                        violations.Add($"{type.FullName}.{member.Name}");
                    }

                    if (member is MethodBase method
                        && method.GetParameters().Any(parameter => pattern.IsMatch(parameter.Name ?? string.Empty)))
                    {
                        violations.Add($"{type.FullName}.{member.Name} parameter");
                    }
                }
            }

            foreach (var resource in assembly.GetManifestResourceNames())
            {
                if (pattern.IsMatch(resource))
                {
                    violations.Add($"resource name {resource}");
                }

                using var stream = assembly.GetManifestResourceStream(resource)!;
                using var reader = new StreamReader(stream);
                if (pattern.IsMatch(reader.ReadToEnd()))
                {
                    violations.Add($"resource content {resource}");
                }
            }
        }

        violations.ShouldBeEmpty("Product persistence must not introduce club_id.");
    }

    private static PersistenceModuleKey GetModuleKey(CapabilityBoundary capability) =>
        GetContributor(capability).Module;

    private static IMigrationContributor GetContributor(CapabilityBoundary capability)
    {
        var type = capability.Assembly.GetTypes().Single(candidate => typeof(IMigrationContributor).IsAssignableFrom(candidate));
        return (IMigrationContributor)Activator.CreateInstance(type, nonPublic: true)!;
    }

    private static IEnumerable<(string Name, string Sql)> ReadSqlResources(CapabilityBoundary capability)
    {
        foreach (var resource in capability.Assembly.GetManifestResourceNames().Where(name => name.EndsWith(".sql", StringComparison.Ordinal)))
        {
            using var stream = capability.Assembly.GetManifestResourceStream(resource)!;
            using var reader = new StreamReader(stream);
            yield return (resource, reader.ReadToEnd());
        }
    }

    private static string StripSqlComments(string sql) =>
        Regex.Replace(Regex.Replace(sql, @"--[^\r\n]*", string.Empty), @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);

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