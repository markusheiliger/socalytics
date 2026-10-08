using System.Reflection;
using System.Text;

namespace SocAlytics.Platform.Infrastructure.Persistence.Migrations;

internal sealed class MigrationCatalog
{
    private const string PlatformResourcePrefix = "SocAlytics.Platform.Infrastructure.Persistence.Migrations.";

    private static readonly Lazy<MigrationCatalog> PlatformCatalog = new(
        () => FromEmbeddedResources(typeof(MigrationCatalog).Assembly, PlatformResourcePrefix));

    private MigrationCatalog(IReadOnlyList<MigrationScript> scripts)
    {
        Scripts = scripts;
    }

    public IReadOnlyList<MigrationScript> Scripts { get; }

    public static MigrationCatalog Platform => PlatformCatalog.Value;

    public static MigrationCatalog FromEmbeddedResources(Assembly assembly, string resourcePrefix)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        ArgumentNullException.ThrowIfNull(resourcePrefix);

        var scripts = new List<MigrationScript>();
        foreach (var resourceName in assembly.GetManifestResourceNames())
        {
            if (!resourceName.StartsWith(resourcePrefix, StringComparison.Ordinal)
                || !resourceName.EndsWith(".sql", StringComparison.Ordinal))
            {
                continue;
            }

            var identity = resourceName[resourcePrefix.Length..^".sql".Length];
            using var stream = assembly.GetManifestResourceStream(resourceName)
                ?? throw new MigrationCatalogException($"Migration '{identity}' could not be read.");
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            scripts.Add(new MigrationScript(identity, reader.ReadToEnd()));
        }

        return Create(scripts);
    }

    public static MigrationCatalog Create(IEnumerable<MigrationScript> scripts)
    {
        ArgumentNullException.ThrowIfNull(scripts);

        var ordered = scripts.OrderBy(script => script.Sequence).ThenBy(script => script.Identity, StringComparer.Ordinal).ToList();
        var sequences = new HashSet<int>();
        var identities = new HashSet<string>(StringComparer.Ordinal);
        foreach (var script in ordered)
        {
            if (!identities.Add(script.Identity))
            {
                throw new MigrationCatalogException($"Migration identity '{script.Identity}' is duplicated.");
            }

            if (!sequences.Add(script.Sequence))
            {
                throw new MigrationCatalogException($"Migration '{script.Identity}' duplicates sequence {script.Sequence}.");
            }
        }

        return new MigrationCatalog(ordered);
    }
}
