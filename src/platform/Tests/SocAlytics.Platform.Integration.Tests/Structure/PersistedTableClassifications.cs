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
            ["pending_external_sign_in"] = TableClassification.Unversioned("short-lived pending sign-in state consumed once and purged; must not advance the account version"),
            ["analyst_manager_access_token"] = TableClassification.Unversioned("operational token rows deleted on revocation; must not advance the registration version"),
            ["analyst_manager_proof_replay"] = TableClassification.Unversioned("proof replay cache purged after expiry"),
            ["analyst_manager_queue_credential"] = TableClassification.Unversioned("issued queue credential rows; revocation and push marks are guarded by the registration unit of work"),
            ["analyst_manager_status"] = TableClassification.Unversioned("latest self-reported status, overwritten per report; must not advance the registration version"),
            ["recovery_directive_use"] = TableClassification.Immutable,
            ["security_audit_event"] = TableClassification.Immutable,
            ["recording_versions"] = TableClassification.Immutable,
            ["recording_timeline_mappings"] = TableClassification.Immutable,
            ["recording_set_versions"] = TableClassification.Immutable,
            ["recording_set_members"] = TableClassification.Immutable,
            ["recording_retry_outcomes"] = TableClassification.Immutable,
            ["recording_finalized_events"] = TableClassification.Immutable,
            ["analysis_run_snapshots"] = TableClassification.ChildOf("analysis_runs", "run_id"),
            ["analysis_workflow_nodes"] = TableClassification.ChildOf("analysis_runs", "run_id"),
            ["analysis_node_snapshots"] = TableClassification.ChildOf("analysis_runs", "run_id"),
            ["analysis_dependency_edges"] = TableClassification.ChildOf("analysis_runs", "run_id"),
            ["analysis_logical_job_snapshots"] = TableClassification.ChildOf("analysis_logical_jobs", "logical_job_id"),
            ["analysis_logical_job_inputs"] = TableClassification.ChildOf("analysis_logical_jobs", "logical_job_id"),
            ["analysis_readiness_occurrences"] = TableClassification.ChildOf("analysis_logical_jobs", "logical_job_id"),
            ["analysis_execution_attempts"] = TableClassification.ChildOf("analysis_logical_jobs", "logical_job_id"),
            ["analysis_claim_keys"] = TableClassification.Immutable,
            ["analysis_accepted_results"] = TableClassification.Immutable,
            ["analysis_completion_outcomes"] = TableClassification.Immutable,
            ["analysis_result_object_declarations"] = TableClassification.Immutable,
            ["analysis_accepted_result_manifests"] = TableClassification.Immutable,
            ["analysis_indexed_facts"] = TableClassification.Immutable,
            ["analysis_notification_receipts"] = TableClassification.Immutable,
            ["analyst_capability_declaration"] = TableClassification.Immutable,
            ["workflow_definition"] = TableClassification.Immutable,
            ["email_confirmations"] = TableClassification.Unversioned("single-use confirmation state; the state transition is the guard"),
            ["notification_preferences"] = TableClassification.Unversioned("per-member preference rows upserted by key"),
            ["email_messages"] = TableClassification.Unversioned("operational email outbox; transitions guarded by row lock, lease, and state"),
            ["outbox_messages"] = TableClassification.Unversioned("operational publication record; transitions guarded by row lock and state"),
        };
}
