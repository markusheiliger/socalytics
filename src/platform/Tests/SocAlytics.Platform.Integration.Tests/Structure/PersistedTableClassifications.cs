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
        new Dictionary<string, TableClassification>(StringComparer.Ordinal)
        {
            ["club_role_assignment"] = TableClassification.ChildOf("member_account", "member_account_id"),
            ["team_role_assignment"] = TableClassification.ChildOf("member_account", "member_account_id"),
            ["member_session"] = TableClassification.Unversioned("operational session state; must not advance the account version"),
            ["one_time_credential"] = TableClassification.Unversioned("single-use credential state; consumption is the guard"),
            ["recovery_directive_use"] = TableClassification.Immutable,
            ["security_audit_event"] = TableClassification.Immutable,
            ["recording_versions"] = TableClassification.Immutable,
            ["recording_timeline_mappings"] = TableClassification.Immutable,
            ["recording_set_versions"] = TableClassification.Immutable,
            ["recording_set_members"] = TableClassification.Immutable,
            ["recording_retry_outcomes"] = TableClassification.Immutable,
            ["recording_finalized_events"] = TableClassification.Immutable,
        };
}
