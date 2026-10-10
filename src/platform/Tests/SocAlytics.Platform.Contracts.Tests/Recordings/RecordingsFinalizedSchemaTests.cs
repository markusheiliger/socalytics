using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;
using Shouldly;
using Xunit;

namespace SocAlytics.Platform.Contracts.Tests.Recordings;

public class RecordingsFinalizedSchemaTests
{
    private const string SchemaPath = "contracts/recordings/recordings-finalized/v1/recordings-finalized.schema.json";
    private const string PlainDigest = "sha-256:2c26b46b68ffc68ff99b453c1d30413413422d706483bfa0f98a5e886266e7ae";
    private const string PartsDigest = "sha-256-parts:5242880:3:8e41531920a37a7ca28af60841220d3147df4c1c06170b60dfca248e6f662770";

    private static ContractSchema Schema => ContractCatalog.Schemas.Single(s => s.RepositoryPath == SchemaPath);

    private static JsonNode Example() => Schema.Document["examples"]![0]!.DeepClone();

    private static bool IsValid(JsonNode instance)
    {
        var compiled = ((JsonSchema?)ContractCatalog.LoadRegistry().Get(new Uri(Schema.Id!))).ShouldNotBeNull();
        return compiled.Evaluate(JsonSerializer.SerializeToElement(instance), new EvaluationOptions { RequireFormatValidation = true }).IsValid;
    }

    [Fact]
    public void Embedded_example_validates()
    {
        IsValid(Example()).ShouldBeTrue($"the embedded example of {SchemaPath} must validate");
    }

    [Fact]
    public void Plain_digest_as_recording_content_digest_is_rejected()
    {
        var instance = Example();
        instance["members"]![0]!["recordingContentDigest"] = PlainDigest;
        IsValid(instance).ShouldBeFalse($"{SchemaPath} must reject a plain sha-256 recordingContentDigest");
    }

    [Fact]
    public void Parts_digest_as_timeline_mapping_digest_is_rejected()
    {
        var instance = Example();
        instance["members"]![0]!["timelineMappingDigest"] = PartsDigest;
        IsValid(instance).ShouldBeFalse($"{SchemaPath} must reject a sha-256-parts timelineMappingDigest");
    }

    [Fact]
    public void Empty_members_are_rejected()
    {
        var instance = Example();
        instance["members"] = new JsonArray();
        IsValid(instance).ShouldBeFalse($"{SchemaPath} must reject an empty members array");
    }

    [Fact]
    public void Unknown_top_level_property_is_rejected()
    {
        var instance = Example();
        instance["unexpected"] = true;
        IsValid(instance).ShouldBeFalse($"{SchemaPath} must reject unknown top-level properties");
    }
}
