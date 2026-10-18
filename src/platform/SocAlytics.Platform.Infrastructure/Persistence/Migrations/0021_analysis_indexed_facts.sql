CREATE TABLE socalytics.analysis_indexed_facts (
    accepted_result_id uuid NOT NULL REFERENCES socalytics.analysis_accepted_results (id),
    ordinal integer NOT NULL,
    run_id uuid NOT NULL,
    node_id uuid NOT NULL,
    team_id uuid NOT NULL,
    match_id uuid NOT NULL,
    capability_id text NOT NULL,
    capability_version text NOT NULL,
    schema_version text NOT NULL,
    output_type text NOT NULL,
    kind text NOT NULL,
    start_ms bigint NOT NULL,
    end_ms bigint NOT NULL,
    confidence double precision NOT NULL,
    payload jsonb NOT NULL,
    PRIMARY KEY (accepted_result_id, ordinal),
    CONSTRAINT ck_analysis_indexed_facts_ordinal CHECK (ordinal >= 0),
    CONSTRAINT ck_analysis_indexed_facts_output_type CHECK (output_type IN ('foundational-facts', 'soccer-concepts')),
    CONSTRAINT ck_analysis_indexed_facts_range CHECK (start_ms >= 0 AND end_ms > start_ms),
    CONSTRAINT ck_analysis_indexed_facts_confidence CHECK (confidence >= 0 AND confidence <= 1),
    CONSTRAINT ck_analysis_indexed_facts_payload CHECK (jsonb_typeof(payload) = 'object')
);

CREATE INDEX ix_analysis_indexed_facts_read
    ON socalytics.analysis_indexed_facts (run_id, node_id, start_ms, end_ms, accepted_result_id, ordinal);

CREATE TRIGGER trg_analysis_indexed_facts_immutable
    BEFORE UPDATE OR DELETE ON socalytics.analysis_indexed_facts
    FOR EACH ROW EXECUTE FUNCTION socalytics.reject_immutable_change();

REVOKE UPDATE, DELETE ON socalytics.analysis_indexed_facts FROM socalytics_app;
