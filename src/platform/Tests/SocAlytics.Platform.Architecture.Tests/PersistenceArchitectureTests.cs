using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
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

public sealed partial class PersistenceArchitectureTests
{
    private static readonly (Assembly Assembly, PersistenceModuleKey Key)[] Modules =
    [
        (typeof(ClubServiceCollectionExtensions).Assembly, PersistenceModuleKey.Club),
        (typeof(IdentityAccessServiceCollectionExtensions).Assembly, PersistenceModuleKey.IdentityAccess),
        (typeof(RecordingsServiceCollectionExtensions).Assembly, PersistenceModuleKey.Recordings),
        (typeof(RegistryServiceCollectionExtensions).Assembly, PersistenceModuleKey.Registry),
        (typeof(AnalysisServiceCollectionExtensions).Assembly, PersistenceModuleKey.Analysis),
        (typeof(AgentOrchestrationServiceCollectionExtensions).Assembly, PersistenceModuleKey.AgentOrchestration)
    ];

    private static readonly Assembly PersistenceAssembly = typeof(PersistenceModuleKey).Assembly;

    private static readonly string[] ApiAllowedPersistenceTypes =
    [
        typeof(PersistenceServiceCollectionExtensions).FullName!,
        typeof(PlatformPersistenceOptions).FullName!,
        typeof(IMigrationRunner).FullName!,
        typeof(MigrationFailedException).FullName!,
        typeof(MigrationChecksumConflictException).FullName!
    ];

    private static readonly string[] SharedPublicSurface =
    [
        typeof(IMigrationContributor).FullName!,
        typeof(MigrationCatalog).FullName!,
        typeof(MigrationDescriptor).FullName!,
        typeof(MigrationFailedException).FullName!,
        typeof(MigrationChecksumConflictException).FullName!,
        typeof(IMigrationRunner).FullName!,
        typeof(IModuleConnectionFactory).FullName!,
        typeof(IModuleTransactionExecutor).FullName!,
        typeof(ConcurrencyConflictException).FullName!,
        typeof(OptimisticConcurrency).FullName!,
        typeof(PersistenceServiceCollectionExtensions).FullName!,
        typeof(PlatformPersistenceOptions).FullName!,
        typeof(PersistenceModuleKey).FullName!
    ];

    private const BindingFlags AllMembers =
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

    [Fact]
    public void Shared_persistence_does_not_depend_on_capabilities_and_capabilities_reference_only_persistence()
    {
        var capabilityNamespaces = Modules.Select(m => m.Assembly.GetName().Name!).ToArray();

        Types.InAssembly(PersistenceAssembly)
            .ShouldNot()
            .HaveDependencyOnAny(capabilityNamespaces)
            .GetResult()
            .IsSuccessful.ShouldBeTrue("Shared persistence must remain module-neutral.");

        foreach (var (assembly, _) in Modules)
        {
            var persistenceReferences = assembly.GetReferencedAssemblies()
                .Select(reference => reference.Name!)
                .Where(name => name.StartsWith("SocAlytics.Platform.", StringComparison.Ordinal))
                .ToArray();

            persistenceReferences.ShouldBe(["SocAlytics.Platform.Persistence"], $"{assembly.GetName().Name}");
        }
    }

    [Fact]
    public void Api_accesses_shared_persistence_only_through_the_composition_surface()
    {
        var apiAssemblyPath = Path.Combine(AppContext.BaseDirectory, "SocAlytics.Platform.Api.dll");
        using var stream = File.OpenRead(apiAssemblyPath);
        using var peReader = new PEReader(stream);
        var metadata = peReader.GetMetadataReader();
        var persistenceName = PersistenceAssembly.GetName().Name;
        var violations = new List<string>();

        foreach (var handle in metadata.TypeReferences)
        {
            var reference = metadata.GetTypeReference(handle);
            if (reference.ResolutionScope.Kind != HandleKind.AssemblyReference
                || metadata.GetString(metadata.GetAssemblyReference((AssemblyReferenceHandle)reference.ResolutionScope).Name) != persistenceName)
            {
                continue;
            }

            var fullName = $"{metadata.GetString(reference.Namespace)}.{metadata.GetString(reference.Name)}";
            if (!ApiAllowedPersistenceTypes.Contains(fullName, StringComparer.Ordinal))
            {
                violations.Add(fullName);
            }
        }

        violations.ShouldBeEmpty("The API may use only registration, migration orchestration, and options from shared persistence.");
    }

    [Fact]
    public void Shared_persistence_exposes_only_the_restricted_public_surface()
    {
        PersistenceAssembly.GetExportedTypes()
            .Select(type => type.FullName!)
            .ShouldBe(SharedPublicSurface, ignoreOrder: true);

        var exposedBootstrap = PersistenceAssembly.GetExportedTypes()
            .SelectMany(type => type.GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            .SelectMany(GetSignatureTypes)
            .Where(type => type.Name.Contains("Bootstrap", StringComparison.Ordinal) || type == typeof(Npgsql.NpgsqlDataSource))
            .Select(type => type.FullName)
            .ToArray();

        exposedBootstrap.ShouldBeEmpty("Bootstrap connections and data sources must not be publicly reachable.");
    }

    [Fact]
    public void Shared_persistence_carries_no_module_sql_resources()
    {
        PersistenceAssembly.GetManifestResourceNames()
            .Where(name => name.EndsWith(".sql", StringComparison.OrdinalIgnoreCase))
            .ShouldBeEmpty();
    }

    [Fact]
    public void Each_module_owns_exactly_its_own_migration_resources_and_contributor()
    {
        var allResources = new List<string>();

        foreach (var (assembly, key) in Modules)
        {
            var assemblyName = assembly.GetName().Name!;
            var sqlResources = assembly.GetManifestResourceNames()
                .Where(name => name.EndsWith(".sql", StringComparison.OrdinalIgnoreCase))
                .ToArray();

            sqlResources.ShouldNotBeEmpty(assemblyName);
            sqlResources.ShouldAllBe(name => name.StartsWith($"{assemblyName}.Migrations.", StringComparison.Ordinal));
            assembly.GetManifestResourceNames().ShouldBe(sqlResources, ignoreOrder: true);
            allResources.AddRange(sqlResources);

            var contributors = assembly.GetTypes()
                .Where(type => !type.IsAbstract && typeof(IMigrationContributor).IsAssignableFrom(type))
                .ToArray();
            contributors.Length.ShouldBe(1, assemblyName);
            contributors.Single().IsPublic.ShouldBeFalse();

            var contributor = (IMigrationContributor)Activator.CreateInstance(contributors.Single(), nonPublic: true)!;
            contributor.Module.ShouldBeSameAs(key);

            var migrations = contributor.GetMigrations();
            migrations.ShouldAllBe(migration => ReferenceEquals(migration.Module, key));
            migrations.Select(migration => migration.Sequence).ShouldBe(
                Enumerable.Range(1, migrations.Count), "Sequences must be contiguous per module.");
            migrations.Count.ShouldBe(sqlResources.Length, "Every embedded SQL resource must be a registered migration.");
        }

        allResources.ShouldBeUnique("Each SQL resource must belong to exactly one module.");
    }

    [Fact]
    public void Module_sql_touches_only_its_own_schema_and_roles()
    {
        foreach (var (assembly, key) in Modules)
        {
            foreach (var resource in assembly.GetManifestResourceNames())
            {
                var sql = StripComments(ReadResource(assembly, resource));
                var identifiers = IdentifierRegex().Matches(sql).Select(match => match.Value.ToLowerInvariant()).ToHashSet();

                identifiers.ShouldContain(key.SchemaName, $"{resource} must create its own schema.");

                var foreign = PersistenceModuleKey.All
                    .Where(other => !ReferenceEquals(other, key))
                    .SelectMany(other => new[] { other.SchemaName, other.OwnerRoleName, other.RuntimeRoleName })
                    .Append("socalytics_migrations")
                    .Where(identifiers.Contains)
                    .ToArray();

                foreign.ShouldBeEmpty($"{resource} must not reference another module's schema, roles, or the migration schema.");
                sql.ShouldNotContain("public.", Case.Insensitive, $"{resource} must not use the public schema.");
            }
        }
    }

    [Fact]
    public void Persistence_types_and_resources_do_not_use_club_id()
    {
        var violations = new List<string>();

        foreach (var assembly in Modules.Select(m => m.Assembly).Append(PersistenceAssembly))
        {
            foreach (var type in assembly.GetTypes())
            {
                if (IsClubId(type.Name))
                {
                    violations.Add($"type {type.FullName}");
                }

                foreach (var member in type.GetMembers(AllMembers))
                {
                    if (IsClubId(member.Name))
                    {
                        violations.Add($"member {type.FullName}.{member.Name}");
                    }

                    if (member is MethodBase method)
                    {
                        violations.AddRange(method.GetParameters()
                            .Where(parameter => parameter.Name is not null && IsClubId(parameter.Name))
                            .Select(parameter => $"parameter {type.FullName}.{member.Name}({parameter.Name})"));
                    }
                }
            }

            foreach (var resource in assembly.GetManifestResourceNames())
            {
                if (IsClubId(ReadResource(assembly, resource)) || IsClubId(resource))
                {
                    violations.Add($"resource {resource}");
                }
            }
        }

        violations.ShouldBeEmpty("The single-club stamp invariant forbids a club_id discriminator.");
    }

    [Fact]
    public void Persistence_projects_do_not_reference_capability_projects()
    {
        var projectFile = Path.Combine(AppContext.BaseDirectory, "InspectedProjects", "SocAlytics.Platform.Persistence.csproj");
        var capabilityNames = Modules.Select(m => m.Assembly.GetName().Name!).ToHashSet(StringComparer.Ordinal);

        XDocument.Load(projectFile)
            .Descendants()
            .Where(element => element.Name.LocalName == "ProjectReference")
            .Select(element => Path.GetFileNameWithoutExtension(element.Attribute("Include")!.Value))
            .Where(capabilityNames.Contains)
            .ShouldBeEmpty();
    }

    private static IEnumerable<Type> GetSignatureTypes(MemberInfo member) => member switch
    {
        MethodBase method => method.GetParameters().Select(p => p.ParameterType)
            .Concat(method is MethodInfo info ? [info.ReturnType] : []),
        PropertyInfo property => [property.PropertyType],
        FieldInfo field => [field.FieldType],
        EventInfo @event => @event.EventHandlerType is null ? [] : [@event.EventHandlerType],
        _ => []
    };

    private static bool IsClubId(string value) =>
        value.Replace("_", string.Empty, StringComparison.Ordinal).Contains("clubid", StringComparison.OrdinalIgnoreCase);

    private static string ReadResource(Assembly assembly, string name)
    {
        using var stream = assembly.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static string StripComments(string sql) =>
        BlockCommentRegex().Replace(LineCommentRegex().Replace(sql, string.Empty), string.Empty);

    [GeneratedRegex(@"[A-Za-z_][A-Za-z0-9_]*")]
    private static partial Regex IdentifierRegex();

    [GeneratedRegex(@"--[^\r\n]*")]
    private static partial Regex LineCommentRegex();

    [GeneratedRegex(@"/\*.*?\*/", RegexOptions.Singleline)]
    private static partial Regex BlockCommentRegex();
}
