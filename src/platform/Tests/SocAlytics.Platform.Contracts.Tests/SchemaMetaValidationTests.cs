using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Json.Pointer;
using Json.Schema;
using Shouldly;
using Xunit;

namespace SocAlytics.Platform.Contracts.Tests;

internal static class SchemaReferenceWalker
{
    private static readonly HashSet<string> InstanceKeywords = ["examples", "default", "const", "enum"];

    public static List<string> FindUnresolved(ContractSchema schema, IReadOnlyCollection<ContractSchema> catalog)
    {
        var problems = new List<string>();
        Walk(schema.Document, schema, catalog, problems);
        return problems;
    }

    private static void Walk(JsonNode? node, ContractSchema schema, IReadOnlyCollection<ContractSchema> catalog, List<string> problems)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var (key, value) in obj)
                {
                    if (InstanceKeywords.Contains(key))
                    {
                        continue;
                    }

                    if (key == "$ref" && value is JsonValue v && v.TryGetValue<string>(out var reference))
                    {
                        var problem = Resolve(reference, schema, catalog);
                        if (problem is not null)
                        {
                            problems.Add($"{schema.RepositoryPath}: $ref '{reference}' {problem}");
                        }

                        continue;
                    }

                    Walk(value, schema, catalog, problems);
                }

                break;
            case JsonArray array:
                foreach (var item in array)
                {
                    Walk(item, schema, catalog, problems);
                }

                break;
        }
    }

    private static string? Resolve(string reference, ContractSchema schema, IReadOnlyCollection<ContractSchema> catalog)
    {
        if (schema.Id is null || !Uri.TryCreate(schema.Id, UriKind.Absolute, out var baseUri))
        {
            return "cannot be resolved because the schema has no absolute $id";
        }

        if (!Uri.TryCreate(baseUri, reference, out var target))
        {
            return "is not a valid URI reference";
        }

        var documentId = target.GetComponents(UriComponents.AbsoluteUri & ~UriComponents.Fragment, UriFormat.UriEscaped);
        var targetSchema = catalog.FirstOrDefault(s => s.Id == documentId);
        if (targetSchema is null)
        {
            return $"targets '{documentId}', which is not the $id of a catalog schema";
        }

        var fragment = Uri.UnescapeDataString(target.Fragment);
        if (fragment is "" or "#")
        {
            return null;
        }

        var pointerText = fragment[1..];
        if (!JsonPointer.TryParse(pointerText, out var pointer))
        {
            return $"has fragment '{fragment}', which is not a JSON pointer";
        }

        return pointer.TryEvaluate(targetSchema.Document, out _)
            ? null
            : $"has pointer '{fragment}', which does not resolve within {targetSchema.RepositoryPath}";
    }
}

public class SchemaMetaValidationTests
{
    private const string Draft202012 = "https://json-schema.org/draft/2020-12/schema";

    public static TheoryData<string> SchemaPaths()
    {
        var data = new TheoryData<string>();
        foreach (var schema in ContractCatalog.Schemas)
        {
            data.Add(schema.RepositoryPath);
        }

        return data;
    }

    private static ContractSchema Get(string path) => ContractCatalog.Schemas.Single(s => s.RepositoryPath == path);

    [Theory]
    [MemberData(nameof(SchemaPaths))]
    public void Schema_validates_against_the_2020_12_meta_schema(string path)
    {
        var schema = Get(path);
        MetaSchemas.Draft202012.Evaluate(JsonSerializer.SerializeToElement(schema.Document)).IsValid
            .ShouldBeTrue($"{path} must validate against the JSON Schema 2020-12 meta-schema");
    }

    [Theory]
    [MemberData(nameof(SchemaPaths))]
    public void Schema_declares_dialect_and_version_matching_its_path(string path)
    {
        var schema = Get(path);
        schema.Document["$schema"]?.GetValue<string>().ShouldBe(Draft202012, $"{path} must declare $schema");

        ContractPathRule.TryMatch(path, out var major).ShouldBeTrue($"{path} must follow the contract path rule");
        var version = schema.Document["x-socalytics-version"]?.GetValue<string>();
        version.ShouldNotBeNull($"{path} must declare x-socalytics-version");
        Regex.IsMatch(version, @"^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(-[0-9A-Za-z.-]+)?(\+[0-9A-Za-z.-]+)?$")
            .ShouldBeTrue($"{path}: x-socalytics-version '{version}' must be a semantic version");
        int.Parse(version.Split('.')[0]).ShouldBe(major, $"{path}: x-socalytics-version major must equal the v<major> path segment");
    }

    [Theory]
    [MemberData(nameof(SchemaPaths))]
    public void Every_ref_resolves_offline_within_the_catalog(string path)
    {
        SchemaReferenceWalker.FindUnresolved(Get(path), ContractCatalog.Schemas)
            .ShouldBeEmpty($"{path} has unresolved references");
    }

    [Theory]
    [MemberData(nameof(SchemaPaths))]
    public void Every_example_validates_against_its_schema(string path)
    {
        var schema = Get(path);
        schema.Id.ShouldNotBeNull($"{path} must declare $id");
        var registry = ContractCatalog.LoadRegistry();
        var compiled = ((JsonSchema?)registry.Get(new Uri(schema.Id))).ShouldNotBeNull($"{path} must be in the registry");
        var options = new EvaluationOptions { RequireFormatValidation = true };

        if (schema.Document["examples"] is not JsonArray examples)
        {
            return;
        }

        for (var i = 0; i < examples.Count; i++)
        {
            compiled.Evaluate(JsonSerializer.SerializeToElement(examples[i]), options).IsValid
                .ShouldBeTrue($"{path}: examples[{i}] must validate against the schema");
        }
    }

    [Fact]
    public void Reference_to_a_non_catalog_id_is_reported_with_path_and_reference()
    {
        var schema = Make("contracts/a/b/v1/b.schema.json", "https://socalytics.invalid/contracts/a/b/v1/b.schema.json",
            """{"properties":{"x":{"$ref":"https://example.com/other.schema.json"}}}""");

        var problems = SchemaReferenceWalker.FindUnresolved(schema, [schema]);

        problems.Count.ShouldBe(1);
        problems[0].ShouldContain("contracts/a/b/v1/b.schema.json");
        problems[0].ShouldContain("https://example.com/other.schema.json");
    }

    [Fact]
    public void Reference_with_an_unresolvable_pointer_is_reported_with_path_and_reference()
    {
        var schema = Make("contracts/a/b/v1/b.schema.json", "https://socalytics.invalid/contracts/a/b/v1/b.schema.json",
            """{"$defs":{"ok":{"type":"string"}},"properties":{"x":{"$ref":"#/$defs/missing"},"y":{"$ref":"#/$defs/ok"}}}""");

        var problems = SchemaReferenceWalker.FindUnresolved(schema, [schema]);

        problems.Count.ShouldBe(1);
        problems[0].ShouldContain("contracts/a/b/v1/b.schema.json");
        problems[0].ShouldContain("#/$defs/missing");
    }

    [Fact]
    public void Reference_walk_skips_instance_values_and_resolves_cross_schema_references()
    {
        var common = Make("contracts/common/v1/common.schema.json", "https://socalytics.invalid/contracts/common/v1/common.schema.json",
            """{"$defs":{"id":{"type":"string"}}}""");
        var schema = Make("contracts/a/b/v1/b.schema.json", "https://socalytics.invalid/contracts/a/b/v1/b.schema.json",
            """{"properties":{"x":{"$ref":"../../../common/v1/common.schema.json#/$defs/id"}},"examples":[{"$ref":"nowhere"}],"const":{"$ref":"nowhere"}}""");

        SchemaReferenceWalker.FindUnresolved(schema, [common, schema]).ShouldBeEmpty();
    }

    private static ContractSchema Make(string path, string id, string json)
    {
        var doc = JsonNode.Parse(json)!;
        doc["$id"] = id;
        return new ContractSchema(path, path, doc, id);
    }
}
