namespace SocAlytics.Platform.Integration.Tests.Structure;

internal abstract record TableClassification
{
    public static TableClassification ChildOf(string root, string childKey, string rootKey = "id") =>
        new ChildOfClassification(root, childKey, rootKey);

    public static TableClassification Immutable { get; } = new ImmutableClassification();

    public static TableClassification Unversioned(string reason) => new UnversionedClassification(reason);
}

internal sealed record ChildOfClassification(string Root, string ChildKey, string RootKey) : TableClassification;

internal sealed record ImmutableClassification : TableClassification;

internal sealed record UnversionedClassification(string Reason) : TableClassification;

// Lists every table in schema socalytics that has no version column; versioned tables are implicit.
internal static class PersistedTableClassifications
{
    public static IReadOnlyDictionary<string, TableClassification> Platform { get; } =
        new Dictionary<string, TableClassification>(StringComparer.Ordinal);
}
