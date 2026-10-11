CREATE TABLE socalytics.analysis_logical_jobs (
    id uuid PRIMARY KEY,
    run_id uuid NOT NULL,
    node_id uuid NOT NULL,
    execution_scope text NOT NULL,
    recording_version_id uuid NULL,
    timeline_mapping_digest text NULL,
    segmentation_policy_digest text NULL,
    segment_number integer NULL,
    state text NOT NULL,
    attempts_used integer NOT NULL DEFAULT 0,
    current_readiness_occurrence_id uuid NULL,
    current_attempt_id uuid NULL,
    failure_reason text NULL,
    updated_at timestamptz NOT NULL,
    version bigint NOT NULL DEFAULT 1,
    CONSTRAINT fk_analysis_logical_jobs_node FOREIGN KEY (run_id, node_id)
        REFERENCES socalytics.analysis_workflow_nodes (run_id, id),
    CONSTRAINT ck_analysis_logical_jobs_scope CHECK (execution_scope IN ('segment', 'match')),
    CONSTRAINT ck_analysis_logical_jobs_segment_number CHECK (segment_number > 0),
    CONSTRAINT ck_analysis_logical_jobs_state CHECK (state IN ('Waiting', 'Ready', 'Claimed', 'Completed', 'Failed', 'Blocked')),
    CONSTRAINT ck_analysis_logical_jobs_attempts_used CHECK (attempts_used >= 0),
    CONSTRAINT ck_analysis_logical_jobs_failure_reason CHECK (failure_reason IS NULL OR failure_reason IN ('attempts-exhausted', 'upstream-required-failed')),
    CONSTRAINT ck_analysis_logical_jobs_segment_fields CHECK (
        (execution_scope = 'segment'
            AND recording_version_id IS NOT NULL AND timeline_mapping_digest IS NOT NULL
            AND segmentation_policy_digest IS NOT NULL AND segment_number IS NOT NULL)
        OR (execution_scope <> 'segment'
            AND recording_version_id IS NULL AND timeline_mapping_digest IS NULL
            AND segmentation_policy_digest IS NULL AND segment_number IS NULL)),
    CONSTRAINT uq_analysis_logical_jobs_identity UNIQUE NULLS NOT DISTINCT
        (node_id, recording_version_id, timeline_mapping_digest, segmentation_policy_digest, segment_number)
);

CREATE INDEX ix_analysis_logical_jobs_run ON socalytics.analysis_logical_jobs (run_id);

CREATE TABLE socalytics.analysis_logical_job_snapshots (
    logical_job_id uuid PRIMARY KEY REFERENCES socalytics.analysis_logical_jobs (id),
    max_attempts integer NOT NULL,
    lease_seconds integer NOT NULL,
    stale_timeout_seconds integer NOT NULL,
    execution_timeout_seconds integer NOT NULL,
    analyst_profile_digest text NOT NULL,
    image_digest text NOT NULL,
    model_digest text NULL,
    resources jsonb NOT NULL,
    schema_version text NOT NULL,
    CONSTRAINT ck_analysis_logical_job_snapshots_max_attempts CHECK (max_attempts > 0),
    CONSTRAINT ck_analysis_logical_job_snapshots_lease CHECK (lease_seconds > 0),
    CONSTRAINT ck_analysis_logical_job_snapshots_stale CHECK (stale_timeout_seconds > 0),
    CONSTRAINT ck_analysis_logical_job_snapshots_execution CHECK (execution_timeout_seconds > 0)
);

CREATE TABLE socalytics.analysis_logical_job_inputs (
    logical_job_id uuid PRIMARY KEY REFERENCES socalytics.analysis_logical_jobs (id),
    inputs jsonb NOT NULL,
    recorded_at timestamptz NOT NULL
);

CREATE TABLE socalytics.analysis_readiness_occurrences (
    id uuid PRIMARY KEY,
    logical_job_id uuid NOT NULL REFERENCES socalytics.analysis_logical_jobs (id),
    sequence integer NOT NULL,
    cause text NOT NULL,
    created_at timestamptz NOT NULL,
    CONSTRAINT uq_analysis_readiness_occurrences_sequence UNIQUE (logical_job_id, sequence),
    CONSTRAINT ck_analysis_readiness_occurrences_sequence CHECK (sequence > 0),
    CONSTRAINT ck_analysis_readiness_occurrences_cause CHECK (cause IN ('initial', 'attempt-stale', 'attempt-failed', 'attempt-rejected'))
);

CREATE TABLE socalytics.analysis_execution_attempts (
    id uuid PRIMARY KEY,
    logical_job_id uuid NOT NULL REFERENCES socalytics.analysis_logical_jobs (id),
    readiness_occurrence_id uuid NOT NULL UNIQUE REFERENCES socalytics.analysis_readiness_occurrences (id),
    attempt_number integer NOT NULL,
    manager_id uuid NOT NULL,
    fencing_token bigint NOT NULL UNIQUE,
    state text NOT NULL,
    claimed_at timestamptz NOT NULL,
    lease_expires_at timestamptz NOT NULL,
    last_heartbeat_at timestamptz NOT NULL,
    closed_at timestamptz NULL,
    close_reason text NULL,
    CONSTRAINT uq_analysis_execution_attempts_number UNIQUE (logical_job_id, attempt_number),
    CONSTRAINT ck_analysis_execution_attempts_number CHECK (attempt_number > 0),
    CONSTRAINT ck_analysis_execution_attempts_token CHECK (fencing_token BETWEEN 1 AND 9007199254740991),
    CONSTRAINT ck_analysis_execution_attempts_state CHECK (state IN ('Active', 'Stale', 'Failed', 'Accepted', 'Rejected'))
);

CREATE UNIQUE INDEX ux_analysis_execution_attempts_active
    ON socalytics.analysis_execution_attempts (logical_job_id) WHERE state = 'Active';

CREATE TABLE socalytics.analysis_claim_keys (
    manager_id uuid NOT NULL,
    idempotency_key text NOT NULL,
    logical_job_id uuid NOT NULL REFERENCES socalytics.analysis_logical_jobs (id),
    readiness_occurrence_id uuid NOT NULL REFERENCES socalytics.analysis_readiness_occurrences (id),
    attempt_id uuid NOT NULL UNIQUE REFERENCES socalytics.analysis_execution_attempts (id),
    recorded_at timestamptz NOT NULL,
    PRIMARY KEY (manager_id, idempotency_key)
);

CREATE SEQUENCE socalytics.analysis_fencing_token_seq AS bigint MINVALUE 1 MAXVALUE 9007199254740991 NO CYCLE;

CALL socalytics.attach_version_trigger('socalytics.analysis_logical_jobs');
CALL socalytics.attach_aggregate_child_triggers('socalytics.analysis_logical_job_snapshots', 'socalytics.analysis_logical_jobs', 'logical_job_id');
CALL socalytics.attach_aggregate_child_triggers('socalytics.analysis_logical_job_inputs', 'socalytics.analysis_logical_jobs', 'logical_job_id');
CALL socalytics.attach_aggregate_child_triggers('socalytics.analysis_readiness_occurrences', 'socalytics.analysis_logical_jobs', 'logical_job_id');
CALL socalytics.attach_aggregate_child_triggers('socalytics.analysis_execution_attempts', 'socalytics.analysis_logical_jobs', 'logical_job_id');

CREATE TRIGGER trg_analysis_logical_job_snapshots_immutable
    BEFORE UPDATE OR DELETE ON socalytics.analysis_logical_job_snapshots
    FOR EACH ROW EXECUTE FUNCTION socalytics.reject_immutable_change();
CREATE TRIGGER trg_analysis_logical_job_inputs_immutable
    BEFORE UPDATE OR DELETE ON socalytics.analysis_logical_job_inputs
    FOR EACH ROW EXECUTE FUNCTION socalytics.reject_immutable_change();
CREATE TRIGGER trg_analysis_readiness_occurrences_immutable
    BEFORE UPDATE OR DELETE ON socalytics.analysis_readiness_occurrences
    FOR EACH ROW EXECUTE FUNCTION socalytics.reject_immutable_change();
CREATE TRIGGER trg_analysis_claim_keys_immutable
    BEFORE UPDATE OR DELETE ON socalytics.analysis_claim_keys
    FOR EACH ROW EXECUTE FUNCTION socalytics.reject_immutable_change();

REVOKE UPDATE, DELETE ON
    socalytics.analysis_logical_job_snapshots,
    socalytics.analysis_logical_job_inputs,
    socalytics.analysis_readiness_occurrences,
    socalytics.analysis_claim_keys
FROM socalytics_app;
