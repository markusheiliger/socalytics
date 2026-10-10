using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Json.Schema;

namespace SocAlytics.Platform.Contracts.Tests;

public sealed record ContractSchema(string RepositoryPath, string FullPath, JsonNode Document, string? Id);

public sealed record ContractRelease(string RepositoryPath, string FullPath, JsonNode Document, string Version, string SchemaRepositoryPath);

public sealed record ContractIndexRow(string ArtifactPath, string Owner, string Version, string Example, string ValidationCommand);

public static class ContractCatalog
{
    private const string SchemaSuffix = ".schema.json";
    private static readonly string Root = Path.Combine(AppContext.BaseDirectory, "contracts");

    public static IReadOnlyList<ContractSchema> Schemas
    {
        get
        {
            var schemas = Files()
                .Where(f => !IsRelease(f.Repo))
                .Select(f =>
                {
                    var doc = Parse(f.Full);
                    return new ContractSchema(f.Repo, f.Full, doc, doc["$id"]?.GetValue<string>());
                })
                .ToList();
            if (schemas.Count == 0)
            {
                throw new InvalidOperationException($"No contract schema (contracts/**/*{SchemaSuffix}) found below '{Root}'.");
            }

            return schemas;
        }
    }

    public static IReadOnlyList<ContractRelease> Releases
    {
        get
        {
            var schemas = Schemas;
            return Files()
                .Where(f => IsRelease(f.Repo))
                .Select(f =>
                {
                    var parts = f.Repo.Split('/');
                    var idx = Array.IndexOf(parts, "releases");
                    var parent = string.Join('/', parts.Take(idx));
                    var schema = schemas.SingleOrDefault(s =>
                        s.RepositoryPath.StartsWith(parent + "/", StringComparison.Ordinal)
                        && s.RepositoryPath.IndexOf('/', parent.Length + 1) < 0);
                    var version = parts[^1][..^SchemaSuffix.Length];
                    return new ContractRelease(f.Repo, f.Full, Parse(f.Full), version, schema?.RepositoryPath ?? string.Empty);
                })
                .ToList();
        }
    }

    public static IReadOnlyList<ContractIndexRow> IndexRows
    {
        get
        {
            var lines = File.ReadAllLines(Path.Combine(Root, "README.md"));
            var rows = new List<ContractIndexRow>();
            var inTable = false;
            foreach (var line in lines)
            {
                var trimmed = line.Trim();
                if (!trimmed.StartsWith('|'))
                {
                    if (inTable)
                    {
                        break;
                    }

                    continue;
                }

                var cells = Cells(trimmed);
                if (!inTable)
                {
                    inTable = string.Join(" | ", cells) == "Artifact | Owner | Version | Example | Validation command";
                    continue;
                }

                if (cells.All(c => Regex.IsMatch(c, "^:?-+:?$")) || cells.Length < 5)
                {
                    continue;
                }

                var link = Regex.Match(cells[0], @"\[[^\]]*\]\(([^)\s]+)\)");
                var target = link.Success ? link.Groups[1].Value : cells[0];
                var path = "contracts/" + Path.GetRelativePath("/r", Path.Combine("/r", target)).Replace('\\', '/');
                rows.Add(new ContractIndexRow(path, cells[1], cells[2], cells[3], cells[4]));
            }

            return rows;
        }
    }

    public static SchemaRegistry LoadRegistry()
    {
        var registry = new SchemaRegistry { Fetch = (_, _) => null };
        foreach (var schema in Schemas)
        {
            JsonSchema.FromText(File.ReadAllText(schema.FullPath), new BuildOptions { SchemaRegistry = registry });
        }

        return registry;
    }

    private static string[] Cells(string line) =>
        line.Trim('|').Split('|').Select(c => c.Trim()).ToArray();

    private static bool IsRelease(string repo) => repo.Split('/').Contains("releases");

    private static JsonNode Parse(string path) => JsonNode.Parse(File.ReadAllText(path))!;

    private static IEnumerable<(string Repo, string Full)> Files() =>
        Directory.EnumerateFiles(Root, "*" + SchemaSuffix, SearchOption.AllDirectories)
            .Select(full => (Repo: "contracts/" + Path.GetRelativePath(Root, full).Replace('\\', '/'), Full: full))
            .OrderBy(f => f.Repo, StringComparer.Ordinal);
}
