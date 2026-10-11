using Json.Schema;

namespace SocAlytics.Analysts.Manager.Tests.Support;

internal static class LocalControlSchema
{
    private static readonly Lazy<JsonSchema> Instance = new(
        () => JsonSchema.FromFile(Path.Combine(AppContext.BaseDirectory, "contracts", "local-control.schema.json")),
        LazyThreadSafetyMode.ExecutionAndPublication);

    public static JsonSchema Value => Instance.Value;
}
