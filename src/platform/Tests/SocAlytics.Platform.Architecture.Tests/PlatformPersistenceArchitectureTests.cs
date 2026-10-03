using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.Extensions.DependencyInjection;
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

public sealed class PlatformPersistenceArchitectureTests
{
    private const BindingFlags AllMembers =
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly;

    private static readonly Module[] Modules =
    [
        new(ModuleKey.Club, typeof(ClubServiceCollectionExtensions), s => s.AddClubModule()),
        new(ModuleKey.IdentityAccess, typeof(IdentityAccessServiceCollectionExtensions), s => s.AddIdentityAccessModule()),
        new(ModuleKey.Recordings, typeof(RecordingsServiceCollectionExtensions), s => s.AddRecordingsModule()),
        new(ModuleKey.Registry, typeof(RegistryServiceCollectionExtensions), s => s.AddRegistryModule()),
        new(ModuleKey.Analysis, typeof(AnalysisServiceCollectionExtensions), s => s.AddAnalysisModule()),
        new(ModuleKey.AgentOrchestration, typeof(AgentOrchestrationServiceCollectionExtensions), s => s.AddAgentOrchestrationModule())
    ];

    private static Assembly PersistenceAssembly => typeof(ModuleKey).Assembly;

    [Fact]
    public void Shared_persistence_does_not_depend_on_capabilities()
    {
        var namespaces = Modules.Select(m => m.Assembly.GetName().Name!).ToArray();

        Types.InAssembly(PersistenceAssembly)
            .ShouldNot()
            .HaveDependencyOnAny(namespaces)
            .GetResult()
            .IsSuccessful.ShouldBeTrue("Shared persistence must not depend on any capability.");

        PersistenceAssembly.GetReferencedAssemblies()
            .Select(a => a.Name)
            .ShouldNotContain(name => namespaces.Contains(name));

        var project = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "InspectedProjects", "SocAlytics.Platform.Persistence.csproj"));
        project.Descendants()
            .Where(e => e.Name.LocalName == "ProjectReference")
            .Select(e => Path.GetFileNameWithoutExtension(e.Attribute("Include")!.Value))
            .ShouldBeEmpty("Shared persistence must not reference other projects.");
    }

    [Fact]
    public void Api_does_not_use_shared_persistence_internals_or_module_connections()
    {
        var api = Assembly.Load("SocAlytics.Platform.Api");
        var persistenceTypes = PersistenceAssembly.GetTypes()
            .Where(t => t.IsPublic)
            .Where(t => t.Name is "IModuleConnectionFactory`1" or "ModuleTransactions" or "OptimisticConcurrency" or "IModuleMigrationContributor" or "MigrationDescriptor")
            .Select(t => t.Name)
            .ToArray();

        var referenced = api.GetTypes()
            .SelectMany(t => t.GetMembers(AllMembers))
            .Select(m => m.ToString() ?? string.Empty)
            .Where(signature => persistenceTypes.Any(name => signature.Contains(name.Split('`')[0], StringComparison.Ordinal)))
            .ToArray();

        referenced.ShouldBeEmpty("The API may not touch module connections, transactions, or migrations directly.");
    }

    [Fact]
    public void Shared_persistence_exposes_only_the_restricted_public_surface()
    {
        PersistenceAssembly.GetExportedTypes().Select(t => t.Name).ShouldBe(
            [
                nameof(ConcurrencyConflictException),
                "IModuleConnectionFactory`1",
                nameof(IModuleMigrationContributor),
                nameof(MigrationChecksum),
                nameof(MigrationDescriptor),
                nameof(MigrationException),
                nameof(ModuleKey),
                nameof(ModuleTransactions),
                nameof(OptimisticConcurrency),
                nameof(PersistenceOptions),
                nameof(PersistenceServiceCollectionExtensions)
            ],
            ignoreOrder: true);

        var prohibited = new[] { "NpgsqlDataSource", "PersistenceDataSource", "MigrationRunner", "MigrationCatalog", "MigrationJournal", "RoleBootstrap" };
        var leaks = PersistenceAssembly.GetExportedTypes()
            .SelectMany(t => t.GetMembers(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Where(IsVisibleOutsideAssembly)
                .Select(m => $"{t.Name}.{m.Name}: {m}"))
            .Where(description => prohibited.Any(name => description.Contains(name, StringComparison.Ordinal)))
            .ToArray();

        leaks.ShouldBeEmpty("Bootstrap, orchestration, and data-source types must not be visible through the public surface.");
    }

    [Fact]
    public void Each_module_contributes_migrations_for_exactly_its_own_identity()
    {
        var owners = new Dictionary<ModuleKey, string>();

        foreach (var module in Modules)
        {
            var contributor = module.ResolveContributor();
            contributor.Module.ShouldBe(module.Key);
            contributor.GetType().Assembly.ShouldBe(module.Assembly);
            contributor.GetType().IsPublic.ShouldBeFalse();
            owners.TryAdd(contributor.Module, module.Assembly.GetName().Name!).ShouldBeTrue(
                $"{contributor.Module} must have exactly one owning assembly.");

            contributor.Migrations.ShouldNotBeEmpty();
            contributor.Migrations.ShouldAllBe(m => m.Module == module.Key);
            contributor.Migrations.Select(m => m.Sequence).ShouldBeUnique();
            contributor.Migrations.Select(m => m.ScriptName).ShouldBeUnique();
        }

        owners.Keys.ShouldBe(ModuleKey.All, ignoreOrder: true);
    }

    [Fact]
    public void Sql_resources_belong_to_exactly_one_module_and_one_migration()
    {
        var seenResources = new HashSet<string>(StringComparer.Ordinal);

        foreach (var module in Modules)
        {
            var assemblyName = module.Assembly.GetName().Name!;
            var sqlResources = module.Assembly.GetManifestResourceNames()
                .Where(name => name.EndsWith(".sql", StringComparison.OrdinalIgnoreCase))
                .ToArray();

            sqlResources.ShouldNotBeEmpty();
            foreach (var resource in sqlResources)
            {
                resource.ShouldStartWith(assemblyName + ".Migrations.");
                seenResources.Add(resource).ShouldBeTrue($"{resource} must be owned by one assembly.");
            }

            var contributorChecksums = module.ResolveContributor().Migrations.Select(m => m.Checksum).ToArray();
            var resourceChecksums = sqlResources
                .Select(r => MigrationChecksum.Compute(MigrationChecksum.ReadEmbeddedResource(module.Assembly, r)))
                .ToArray();

            contributorChecksums.ShouldBe(resourceChecksums, ignoreOrder: true,
                $"Every SQL resource of {assemblyName} must back exactly one of its migrations and vice versa.");
        }
    }

    [Fact]
    public void Sql_resources_do_not_reference_peer_schemas_or_roles()
    {
        foreach (var module in Modules)
        {
            var peers = Modules.Where(other => other.Key != module.Key).Select(other => other.Key).ToArray();

            foreach (var resource in module.Assembly.GetManifestResourceNames().Where(n => n.EndsWith(".sql", StringComparison.OrdinalIgnoreCase)))
            {
                var sql = ReadResource(module.Assembly, resource);

                foreach (var peer in peers)
                {
                    ContainsIdentifier(sql, peer.Name).ShouldBeFalse(
                        $"{resource} must not reference peer schema or role '{peer.Name}'.");
                }

                ContainsIdentifier(sql, "socalytics_migrations").ShouldBeFalse(
                    $"{resource} must not reference the shared migration schema.");

                var ownedNames = new[] { module.Key.Schema, module.Key.OwnerRole, module.Key.RuntimeRole };
                foreach (Match match in Regex.Matches(sql, "\"([A-Za-z0-9_]+)\"\\s*\\.\\s*\"?[A-Za-z0-9_]+\"?"))
                {
                    ownedNames.ShouldContain(match.Groups[1].Value, $"{resource} qualifies an object with a foreign schema.");
                }
            }
        }
    }

    [Fact]
    public void Persistence_types_and_resources_do_not_contain_club_id()
    {
        var assemblies = Modules.Select(m => m.Assembly).Append(PersistenceAssembly).Distinct().ToArray();
        var findings = new List<string>();

        foreach (var assembly in assemblies)
        {
            foreach (var type in assembly.GetTypes())
            {
                Check(type.FullName!, $"type {type.FullName}");
                foreach (var member in type.GetMembers(AllMembers))
                {
                    Check(member.Name, $"{type.FullName}.{member.Name}");
                    if (member is MethodBase method)
                    {
                        foreach (var parameter in method.GetParameters())
                        {
                            Check(parameter.Name ?? string.Empty, $"{type.FullName}.{member.Name}({parameter.Name})");
                        }
                    }
                }
            }

            foreach (var resource in assembly.GetManifestResourceNames())
            {
                Check(resource, $"resource name {resource}");
                if (resource.EndsWith(".sql", StringComparison.OrdinalIgnoreCase))
                {
                    Check(ReadResource(assembly, resource), $"resource content {resource}");
                }
            }
        }

        findings.ShouldBeEmpty("Persistence artifacts must not introduce a club_id discriminator.");

        void Check(string text, string location)
        {
            if (text.Replace("_", string.Empty, StringComparison.Ordinal).Contains("clubid", StringComparison.OrdinalIgnoreCase))
            {
                findings.Add(location);
            }
        }
    }

    private static bool IsVisibleOutsideAssembly(MemberInfo member) => member switch
    {
        MethodBase m => m.IsPublic || m.IsFamily || m.IsFamilyOrAssembly,
        FieldInfo f => f.IsPublic || f.IsFamily || f.IsFamilyOrAssembly,
        PropertyInfo p => (p.GetMethod?.IsPublic ?? false) || (p.GetMethod?.IsFamily ?? false),
        _ => false
    };

    private static string ReadResource(Assembly assembly, string resource) =>
        Encoding.UTF8.GetString(MigrationChecksum.ReadEmbeddedResource(assembly, resource));

    private static bool ContainsIdentifier(string sql, string identifier) =>
        Regex.IsMatch(sql, $"(?<![A-Za-z0-9_]){Regex.Escape(identifier)}(?![A-Za-z0-9_])", RegexOptions.IgnoreCase);

    private sealed record Module(ModuleKey Key, Type CompositionType, Func<IServiceCollection, IServiceCollection> Register)
    {
        public Assembly Assembly => CompositionType.Assembly;

        public IModuleMigrationContributor ResolveContributor()
        {
            var services = new ServiceCollection();
            Register(services);
            return services
                .Where(d => d.ServiceType == typeof(IModuleMigrationContributor))
                .Select(d => (IModuleMigrationContributor)d.ImplementationInstance!)
                .Single();
        }
    }
}
