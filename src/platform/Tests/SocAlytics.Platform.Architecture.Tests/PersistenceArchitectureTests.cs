using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using NetArchTest.Rules;
using Shouldly;
using Xunit;

namespace SocAlytics.Platform.Architecture.Tests;

public sealed partial class PersistenceArchitectureTests
{
    private const string MigratorAssemblyName = "SocAlytics.Platform.Migrator";
    private const string InfrastructureAssemblyName = "SocAlytics.Platform.Infrastructure";

    private static readonly string[] InspectedProductionProjects =
    [
        "SocAlytics.Platform.Api",
        "SocAlytics.Platform.Application",
        "SocAlytics.Platform.Domain",
        InfrastructureAssemblyName,
        "SocAlytics.Platform.ServiceDefaults",
    ];

    private static readonly string[] MigrationFreeAssemblies =
    [
        "SocAlytics.Platform.Api",
        "SocAlytics.Platform.Application",
        "SocAlytics.Platform.Domain",
        InfrastructureAssemblyName,
    ];

    private static readonly string[] ProductionAssemblies = [.. InspectedProductionProjects, MigratorAssemblyName];

    [GeneratedRegex(@"^[0-9]{4}_[a-z][a-z0-9]*_[a-z0-9]+(_[a-z0-9]+)*\.sql$", RegexOptions.CultureInvariant)]
    private static partial Regex MigrationFileNamePattern();

    [Fact]
    public void Only_the_migrator_project_references_dbup()
    {
        foreach (var project in InspectedProductionProjects.Append(MigratorAssemblyName))
        {
            var packages = LoadProject(project)
                .Descendants()
                .Where(element => element.Name.LocalName == "PackageReference")
                .Select(element => element.Attribute("Include")?.Value ?? string.Empty)
                .Where(include => include.StartsWith("dbup-", StringComparison.OrdinalIgnoreCase))
                .ToArray();

            if (project == MigratorAssemblyName)
            {
                packages.ShouldNotBeEmpty("The Migrator project owns the DbUp package reference.");
            }
            else
            {
                packages.ShouldBeEmpty($"{project} must not reference a dbup package.");
            }
        }
    }

    [Fact]
    public void No_production_project_references_the_migrator()
    {
        foreach (var project in InspectedProductionProjects)
        {
            var references = LoadProject(project)
                .Descendants()
                .Where(element => element.Name.LocalName == "ProjectReference")
                .Select(element => Path.GetFileNameWithoutExtension((element.Attribute("Include")?.Value ?? string.Empty).Replace('\\', '/')))
                .ToArray();

            references.ShouldNotContain(MigratorAssemblyName, $"{project} must not reference the Migrator.");
        }
    }

    [Fact]
    public void Layer_assemblies_do_not_depend_on_dbup_or_the_migrator()
    {
        foreach (var name in MigrationFreeAssemblies)
        {
            var result = Types.InAssembly(LoadAssembly(name))
                .ShouldNot()
                .HaveDependencyOnAny("DbUp", MigratorAssemblyName)
                .GetResult();

            result.IsSuccessful.ShouldBeTrue($"{name} must not depend on DbUp or the Migrator.");
        }
    }

    [Fact]
    public void Api_does_not_depend_on_data_access_libraries()
    {
        var result = Types.InAssembly(LoadAssembly("SocAlytics.Platform.Api"))
            .ShouldNot()
            .HaveDependencyOnAny("Npgsql", "Dapper")
            .GetResult();

        result.IsSuccessful.ShouldBeTrue("The Api must not depend on Npgsql or Dapper.");
    }

    [Fact]
    public void Infrastructure_exposes_internals_only_to_the_migrator_and_integration_tests()
    {
        var visibleTo = LoadAssembly(InfrastructureAssemblyName)
            .GetCustomAttributes<InternalsVisibleToAttribute>()
            .Select(attribute => attribute.AssemblyName)
            .ToArray();

        visibleTo.ShouldBe(
            [MigratorAssemblyName, "SocAlytics.Platform.Integration.Tests"],
            ignoreOrder: true);
    }

    [Fact]
    public void Only_infrastructure_embeds_well_named_sql_migrations()
    {
        foreach (var name in ProductionAssemblies.Where(name => name != InfrastructureAssemblyName))
        {
            LoadAssembly(name).GetManifestResourceNames()
                .Where(resource => resource.EndsWith(".sql", StringComparison.OrdinalIgnoreCase))
                .ShouldBeEmpty($"{name} must not embed .sql resources.");
        }

        var fileNames = LoadAssembly(InfrastructureAssemblyName).GetManifestResourceNames()
            .Where(resource => resource.EndsWith(".sql", StringComparison.OrdinalIgnoreCase))
            .Select(resource => resource[(resource.LastIndexOf('.', resource.Length - 5) + 1)..])
            .ToArray();

        fileNames.ShouldNotBeEmpty();
        foreach (var fileName in fileNames)
        {
            MigrationFileNamePattern().IsMatch(fileName).ShouldBeTrue($"'{fileName}' is not a valid migration file name.");
        }

        fileNames.Select(fileName => fileName[..4]).ShouldBeUnique("Migration sequences must be unique.");
    }

    [Fact]
    public void Domain_and_application_public_types_declare_no_club_id()
    {
        const BindingFlags AllDeclared = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        var violations = new List<string>();

        foreach (var name in new[] { "SocAlytics.Platform.Domain", "SocAlytics.Platform.Application" })
        {
            foreach (var type in LoadAssembly(name).GetExportedTypes())
            {
                violations.AddRange(type.GetProperties(AllDeclared).Where(property => property.Name == "ClubId").Select(property => $"{type.FullName}.{property.Name}"));
                violations.AddRange(type.GetFields(AllDeclared).Where(field => field.Name == "ClubId").Select(field => $"{type.FullName}.{field.Name}"));
            }
        }

        violations.ShouldBeEmpty("Club scoping is an Infrastructure concern; no public Domain or Application type may declare ClubId.");
    }

    private static XDocument LoadProject(string project) =>
        XDocument.Load(Path.Combine(AppContext.BaseDirectory, "InspectedProjects", $"{project}.csproj"));

    private static Assembly LoadAssembly(string name) => Assembly.Load(new AssemblyName(name));
}
