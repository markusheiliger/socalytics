CREATE TABLE socalytics.analysis_result_object_declarations (
    attempt_id uuid NOT NULL REFERENCES socalytics.analysis_execution_attempts (id),
    name text NOT NULL,
    size_bytes bigint NOT NULL,
    digest text NOT NULL,
    declared_at timestamptz NOT NULL,
    PRIMARY KEY (attempt_id, name),
    CONSTRAINT ck_analysis_result_object_declarations_name CHECK (name ~ '^[a-z0-9][a-z0-9._-]{0,127}$'),
    CONSTRAINT ck_analysis_result_object_declarations_size CHECK (size_bytes >= 1),
    CONSTRAINT ck_analysis_result_object_declarations_digest CHECK (digest ~ '^sha-256:[0-9a-f]{64}$')
);

CREATE TABLE socalytics.analysis_accepted_result_manifests (
    accepted_result_id uuid PRIMARY KEY REFERENCES socalytics.analysis_accepted_results (id),
    contract_version text NOT NULL,
    output_type text NOT NULL,
    validity_start_ms bigint NOT NULL,
    validity_end_ms bigint NOT NULL,
    fact_count integer NOT NULL,
    artifacts jsonb NOT NULL,
    upstream_lineage jsonb NOT NULL,
    CONSTRAINT ck_analysis_accepted_result_manifests_output_type CHECK (output_type IN ('foundational-facts', 'soccer-concepts')),
    CONSTRAINT ck_analysis_accepted_result_manifests_validity CHECK (validity_start_ms >= 0 AND validity_end_ms > validity_start_ms),
    CONSTRAINT ck_analysis_accepted_result_manifests_fact_count CHECK (fact_count >= 0)
);

CREATE TRIGGER trg_analysis_result_object_declarations_immutable
    BEFORE UPDATE OR DELETE ON socalytics.analysis_result_object_declarations
    FOR EACH ROW EXECUTE FUNCTION socalytics.reject_immutable_change();
CREATE TRIGGER trg_analysis_accepted_result_manifests_immutable
    BEFORE UPDATE OR DELETE ON socalytics.analysis_accepted_result_manifests
    FOR EACH ROW EXECUTE FUNCTION socalytics.reject_immutable_change();

REVOKE UPDATE, DELETE ON
    socalytics.analysis_result_object_declarations,
    socalytics.analysis_accepted_result_manifests
FROM socalytics_app;
