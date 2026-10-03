using System.Reflection;
using System.Text.RegularExpressions;
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
    private static readonly (Assembly Assembly, PersistenceModule Module)[] Owners =
    [
        (typeof(ClubServiceCollectionExtensions).Assembly, PersistenceModule.Club),
        (typeof(IdentityAccessServiceCollectionExtensions).Assembly, PersistenceModule.IdentityAccess),
        (typeof(RecordingsServiceCollectionExtensions).Assembly, PersistenceModule.Recordings),
        (typeof(RegistryServiceCollectionExtensions).Assembly, PersistenceModule.Registry),
        (typeof(AnalysisServiceCollectionExtensions).Assembly, PersistenceModule.Analysis),
        (typeof(AgentOrchestrationServiceCollectionExtensions).Assembly, PersistenceModule.AgentOrchestration),
    ];

    private static readonly string[] AllowedPersistencePublicTypes =
    [
        nameof(ConcurrencyConflictException),
        nameof(IMigrationContributor),
        nameof(IMigrationRunner),
        nameof(IModuleConnectionFactory),
        nameof(MigrationCatalog),
        nameof(MigrationDescriptor),
        nameof(MigrationException),
        nameof(MigrationFailureKind),
        nameof(ModuleTransactionExtensions),
        nameof(OptimisticConcurrency),
        nameof(PersistenceModule),
        nameof(PersistenceServiceCollectionExtensions),
    ];

    [Fact]
    public void Adopted_modules_map_one_to_one_to_capability_assemblies()
    {
        Owners.Select(owner => owner.Module).ShouldBe(PersistenceModule.All, ignoreOrder: true);
        Owners.Select(owner => owner.Assembly).Distinct().Count().ShouldBe(Owners.Length);
    }

    [Fact]
    public void Each_capability_has_one_contributor_for_its_own_module()
    {
        foreach (var (assembly, module) in Owners)
        {
            var contributorTypes = assembly.GetTypes()
                .Where(type => typeof(IMigrationContributor).IsAssignableFrom(type))
                .ToArray();

            var contributorType = contributorTypes.ShouldHaveSingleItem();
            contributorType.IsPublic.ShouldBeFalse($"{assembly.GetName().Name} must keep its migration contributor internal.");

            var contributor = (IMigrationContributor)Activator.CreateInstance(contributorType, nonPublic: true)!;
            contributor.Module.ShouldBe(module);
            contributor.GetMigrations().ShouldAllBe(migration => migration.Module == module);
        }
    }

    [Fact]
    public void Embedded_resources_are_owned_by_exactly_one_module_migration_namespace()
    {
        var owners = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var (assembly, _) in Owners)
        {
            var assemblyName = assembly.GetName().Name!;
            var resources = assembly.GetManifestResourceNames();
            resources.ShouldNotBeEmpty($"{assemblyName} must embed its migrations.");

            foreach (var resource in resources)
            {
                resource.ShouldStartWith(assemblyName + ".Migrations.");
                resource.ShouldEndWith(".sql");
                owners.TryAdd(resource, assemblyName).ShouldBeTrue($"Resource {resource} is embedded by more than one assembly.");
            }
        }
    }

    [Fact]
    public void Migration_descriptors_resolve_only_to_resources_of_their_own_assembly()
    {
        foreach (var (assembly, _) in Owners)
        {
            var contributorType = assembly.GetTypes().Single(type => typeof(IMigrationContributor).IsAssignableFrom(type));
            var contributor = (IMigrationContributor)Activator.CreateInstance(contributorType, nonPublic: true)!;
            var resources = assembly.GetManifestResourceNames().ToHashSet(StringComparer.Ordinal);
            var migrations = contributor.GetMigrations().ToArray();

            migrations.ShouldNotBeEmpty();
            migrations.Select(migration => migration.Identity + ".sql")
                .ShouldAllBe(name => resources.Contains(assembly.GetName().Name + ".Migrations." + name));
        }
    }

    [Fact]
    public void Migration_sql_touches_only_the_owning_module_schema_and_roles()
    {
        foreach (var (assembly, module) in Owners)
        {
            var foreignNames = PersistenceModule.All
                .Where(other => other != module)
                .SelectMany(other => new[] { other.Schema, other.OwnerRole, other.RuntimeRole })
                .ToArray();

            foreach (var (resource, sql) in ReadSql(assembly))
            {
                var statements = Normalize(sql);

                foreach (var name in foreignNames)
                {
                    Regex.IsMatch(statements, $@"(?<![\w]){Regex.Escape(name)}(?![\w])", RegexOptions.IgnoreCase)
                        .ShouldBeFalse($"{resource} must not reference '{name}', which belongs to another module.");
                }

                foreach (Match match in CreateSchemaPattern().Matches(statements))
                {
                    match.Groups["name"].Value.Trim('"').ShouldBe(module.Schema, StringComparer.OrdinalIgnoreCase,
                        $"{resource} may create only the {module.Schema} schema.");
                }

                Regex.IsMatch(statements, @"(?<![\w])(public|socalytics_migrations)\s*\.", RegexOptions.IgnoreCase)
                    .ShouldBeFalse($"{resource} must not reference shared schemas.");
            }
        }
    }

    [Fact]
    public void Shared_persistence_exposes_only_the_restricted_public_surface()
    {
        var exported = typeof(PersistenceModule).Assembly.GetExportedTypes().Select(type => type.Name);

        exported.ShouldBe(AllowedPersistencePublicTypes, ignoreOrder: true);

        typeof(PersistenceModule).GetConstructors(BindingFlags.Public | BindingFlags.Instance).ShouldBeEmpty();
        typeof(PersistenceModule).GetProperties(BindingFlags.Public | BindingFlags.Static)
            .Select(property => property.Name)
            .Where(name => name != nameof(PersistenceModule.All))
            .ShouldBe(["Club", "IdentityAccess", "Recordings", "Registry", "Analysis", "AgentOrchestration"], ignoreOrder: true);

        var publicSurface = typeof(PersistenceModule).Assembly.GetExportedTypes()
            .SelectMany(type => type.GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            .SelectMany(MemberTypes)
            .ToArray();

        publicSurface.ShouldNotContain(type => type.Namespace == "Npgsql" && type.Name == "NpgsqlDataSource",
            "The shared persistence public surface must not expose the bootstrap data source.");
    }

    [Fact]
    public void Persistence_types_and_resources_do_not_use_club_id()
    {
        var assemblies = Owners.Select(owner => owner.Assembly).Append(typeof(PersistenceModule).Assembly).ToArray();
        var offenders = new List<string>();

        foreach (var assembly in assemblies)
        {
            const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance
                | BindingFlags.Static | BindingFlags.DeclaredOnly;

            foreach (var type in assembly.GetTypes())
            {
                if (IsClubId(type.Name))
                {
                    offenders.Add(type.FullName!);
                }

                foreach (var member in type.GetMembers(All))
                {
                    if (IsClubId(member.Name))
                    {
                        offenders.Add($"{type.FullName}.{member.Name}");
                    }

                    if (member is MethodBase method)
                    {
                        offenders.AddRange(method.GetParameters()
                            .Where(parameter => IsClubId(parameter.Name))
                            .Select(parameter => $"{type.FullName}.{member.Name}({parameter.Name})"));
                    }
                }
            }

            foreach (var (resource, sql) in ReadSql(assembly))
            {
                if (IsClubId(sql))
                {
                    offenders.Add(resource);
                }
            }
        }

        offenders.ShouldBeEmpty("Platform persistence must model the single-club stamp without a club_id discriminator.");
    }

    private static IEnumerable<Type> MemberTypes(MemberInfo member) => member switch
    {
        FieldInfo field => [field.FieldType],
        PropertyInfo property => [property.PropertyType],
        MethodBase method => method.GetParameters().Select(parameter => parameter.ParameterType)
            .Concat(method is MethodInfo info ? [info.ReturnType] : []),
        _ => [],
    };

    private static IEnumerable<(string Resource, string Sql)> ReadSql(Assembly assembly)
    {
        foreach (var resource in assembly.GetManifestResourceNames().Where(name => name.EndsWith(".sql", StringComparison.Ordinal)))
        {
            using var stream = assembly.GetManifestResourceStream(resource)!;
            using var reader = new StreamReader(stream);
            yield return (resource, reader.ReadToEnd());
        }
    }

    private static string Normalize(string sql)
    {
        var withoutBlockComments = BlockCommentPattern().Replace(sql, " ");
        var withoutLineComments = LineCommentPattern().Replace(withoutBlockComments, " ");
        return withoutLineComments;
    }

    private static bool IsClubId(string? value) =>
        value is not null && Regex.IsMatch(value, @"club[\s_]?id", RegexOptions.IgnoreCase);

    [GeneratedRegex(@"/\*.*?\*/", RegexOptions.Singleline)]
    private static partial Regex BlockCommentPattern();

    [GeneratedRegex(@"--[^\r\n]*")]
    private static partial Regex LineCommentPattern();

    [GeneratedRegex(@"create\s+schema\s+(?:if\s+not\s+exists\s+)?(?<name>""?\w+""?)", RegexOptions.IgnoreCase)]
    private static partial Regex CreateSchemaPattern();
}
