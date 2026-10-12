CREATE TABLE socalytics.analysis_accepted_results (
    id uuid PRIMARY KEY,
    logical_job_id uuid NOT NULL UNIQUE REFERENCES socalytics.analysis_logical_jobs (id),
    run_id uuid NOT NULL,
    node_id uuid NOT NULL,
    attempt_id uuid NOT NULL UNIQUE REFERENCES socalytics.analysis_execution_attempts (id),
    manifest_reference text NOT NULL,
    manifest_checksum text NOT NULL,
    capability_id text NOT NULL,
    capability_version text NOT NULL,
    schema_version text NOT NULL,
    analyst_profile_digest text NOT NULL,
    image_digest text NOT NULL,
    model_digest text NULL,
    policy_versions jsonb NOT NULL,
    executed_runtime jsonb NOT NULL,
    upstream_result_ids uuid[] NOT NULL DEFAULT '{}',
    accepted_at timestamptz NOT NULL,
    CONSTRAINT fk_analysis_accepted_results_node FOREIGN KEY (run_id, node_id)
        REFERENCES socalytics.analysis_workflow_nodes (run_id, id)
);

CREATE INDEX ix_analysis_accepted_results_run ON socalytics.analysis_accepted_results (run_id, accepted_at);

CREATE TABLE socalytics.analysis_completion_outcomes (
    logical_job_id uuid NOT NULL REFERENCES socalytics.analysis_logical_jobs (id),
    idempotency_key text NOT NULL,
    attempt_id uuid NOT NULL REFERENCES socalytics.analysis_execution_attempts (id),
    request_fingerprint text NOT NULL,
    outcome text NOT NULL,
    accepted_result_id uuid NULL REFERENCES socalytics.analysis_accepted_results (id),
    recorded_at timestamptz NOT NULL,
    PRIMARY KEY (logical_job_id, idempotency_key),
    CONSTRAINT ck_analysis_completion_outcomes_outcome CHECK (outcome IN ('accepted', 'failure-recorded'))
);

CREATE TABLE socalytics.analysis_notification_receipts (
    message_id uuid PRIMARY KEY,
    message_type text NOT NULL,
    recording_set_version_id uuid NULL,
    outcome text NOT NULL,
    rejection_category text NULL,
    run_id uuid NULL,
    received_at timestamptz NOT NULL,
    CONSTRAINT ck_analysis_notification_receipts_type CHECK (message_type IN ('recordings-finalized')),
    CONSTRAINT ck_analysis_notification_receipts_outcome CHECK (outcome IN ('run-created', 'run-exists', 'rejected')),
    CONSTRAINT ck_analysis_notification_receipts_category CHECK (
        rejection_category IS NULL OR rejection_category IN ('schema-invalid', 'lineage-not-found', 'lineage-mismatch', 'scope-mismatch'))
);

CREATE INDEX ix_analysis_notification_receipts_received ON socalytics.analysis_notification_receipts (received_at DESC, message_id);

CREATE TRIGGER trg_analysis_accepted_results_immutable
    BEFORE UPDATE OR DELETE ON socalytics.analysis_accepted_results
    FOR EACH ROW EXECUTE FUNCTION socalytics.reject_immutable_change();
CREATE TRIGGER trg_analysis_completion_outcomes_immutable
    BEFORE UPDATE OR DELETE ON socalytics.analysis_completion_outcomes
    FOR EACH ROW EXECUTE FUNCTION socalytics.reject_immutable_change();
CREATE TRIGGER trg_analysis_notification_receipts_immutable
    BEFORE UPDATE OR DELETE ON socalytics.analysis_notification_receipts
    FOR EACH ROW EXECUTE FUNCTION socalytics.reject_immutable_change();

REVOKE UPDATE, DELETE ON
    socalytics.analysis_accepted_results,
    socalytics.analysis_completion_outcomes,
    socalytics.analysis_notification_receipts
FROM socalytics_app;
