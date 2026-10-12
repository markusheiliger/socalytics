CREATE TABLE socalytics.analysis_runs (
    id uuid PRIMARY KEY,
    team_id uuid NOT NULL,
    match_id uuid NOT NULL,
    recording_set_version_id uuid NOT NULL,
    workflow_id text NOT NULL,
    workflow_version text NOT NULL,
    requested_capabilities text[] NOT NULL,
    completion_policy text NOT NULL,
    state text NOT NULL,
    failure_reason text NULL,
    created_at timestamptz NOT NULL,
    running_at timestamptz NULL,
    terminated_at timestamptz NULL,
    updated_at timestamptz NOT NULL,
    version bigint NOT NULL DEFAULT 1,
    CONSTRAINT uq_analysis_runs_set_workflow UNIQUE (recording_set_version_id, workflow_id, workflow_version),
    CONSTRAINT ck_analysis_runs_completion_policy CHECK (completion_policy IN ('all-requested-capabilities')),
    CONSTRAINT ck_analysis_runs_state CHECK (state IN ('Requested', 'Running', 'PartiallyCompleted', 'Completed', 'Failed', 'Cancelled')),
    CONSTRAINT ck_analysis_runs_terminated CHECK (
        (terminated_at IS NOT NULL) = (state IN ('PartiallyCompleted', 'Completed', 'Failed', 'Cancelled')))
);

CREATE TABLE socalytics.analysis_run_snapshots (
    run_id uuid PRIMARY KEY REFERENCES socalytics.analysis_runs (id),
    snapshot jsonb NOT NULL,
    snapshot_digest text NOT NULL
);

CREATE TABLE socalytics.analysis_workflow_nodes (
    id uuid PRIMARY KEY,
    run_id uuid NOT NULL REFERENCES socalytics.analysis_runs (id),
    node_key text NOT NULL,
    capability_id text NOT NULL,
    capability_version text NOT NULL,
    tier text NOT NULL,
    execution_scope text NOT NULL,
    ordinal integer NOT NULL,
    state text NOT NULL,
    blocked_by_node_id uuid NULL,
    updated_at timestamptz NOT NULL,
    CONSTRAINT uq_analysis_workflow_nodes_key UNIQUE (run_id, node_key),
    CONSTRAINT uq_analysis_workflow_nodes_ordinal UNIQUE (run_id, ordinal),
    CONSTRAINT uq_analysis_workflow_nodes_run_id UNIQUE (run_id, id),
    CONSTRAINT ck_analysis_workflow_nodes_tier CHECK (tier IN ('low-level', 'high-level')),
    CONSTRAINT ck_analysis_workflow_nodes_scope CHECK (execution_scope IN ('segment', 'match')),
    CONSTRAINT ck_analysis_workflow_nodes_state CHECK (state IN ('Waiting', 'Active', 'Completed', 'Failed', 'Blocked'))
);

CREATE TABLE socalytics.analysis_node_snapshots (
    node_id uuid PRIMARY KEY REFERENCES socalytics.analysis_workflow_nodes (id),
    run_id uuid NOT NULL REFERENCES socalytics.analysis_runs (id),
    snapshot jsonb NOT NULL
);

CREATE TABLE socalytics.analysis_dependency_edges (
    run_id uuid NOT NULL,
    upstream_node_id uuid NOT NULL,
    dependent_node_id uuid NOT NULL,
    kind text NOT NULL,
    predicate jsonb NULL,
    PRIMARY KEY (run_id, upstream_node_id, dependent_node_id),
    CONSTRAINT ck_analysis_dependency_edges_distinct CHECK (upstream_node_id <> dependent_node_id),
    CONSTRAINT ck_analysis_dependency_edges_kind CHECK (kind IN ('required', 'optional', 'conditional')),
    CONSTRAINT ck_analysis_dependency_edges_predicate CHECK ((predicate IS NOT NULL) = (kind = 'conditional')),
    CONSTRAINT fk_analysis_dependency_edges_upstream FOREIGN KEY (run_id, upstream_node_id)
        REFERENCES socalytics.analysis_workflow_nodes (run_id, id),
    CONSTRAINT fk_analysis_dependency_edges_dependent FOREIGN KEY (run_id, dependent_node_id)
        REFERENCES socalytics.analysis_workflow_nodes (run_id, id)
);

CALL socalytics.attach_version_trigger('socalytics.analysis_runs');
CALL socalytics.attach_aggregate_child_triggers('socalytics.analysis_run_snapshots', 'socalytics.analysis_runs', 'run_id');
CALL socalytics.attach_aggregate_child_triggers('socalytics.analysis_workflow_nodes', 'socalytics.analysis_runs', 'run_id');
CALL socalytics.attach_aggregate_child_triggers('socalytics.analysis_node_snapshots', 'socalytics.analysis_runs', 'run_id');
CALL socalytics.attach_aggregate_child_triggers('socalytics.analysis_dependency_edges', 'socalytics.analysis_runs', 'run_id');

CREATE TRIGGER trg_analysis_run_snapshots_immutable
    BEFORE UPDATE OR DELETE ON socalytics.analysis_run_snapshots
    FOR EACH ROW EXECUTE FUNCTION socalytics.reject_immutable_change();
CREATE TRIGGER trg_analysis_node_snapshots_immutable
    BEFORE UPDATE OR DELETE ON socalytics.analysis_node_snapshots
    FOR EACH ROW EXECUTE FUNCTION socalytics.reject_immutable_change();
CREATE TRIGGER trg_analysis_dependency_edges_immutable
    BEFORE UPDATE OR DELETE ON socalytics.analysis_dependency_edges
    FOR EACH ROW EXECUTE FUNCTION socalytics.reject_immutable_change();

REVOKE UPDATE, DELETE ON
    socalytics.analysis_run_snapshots,
    socalytics.analysis_node_snapshots,
    socalytics.analysis_dependency_edges
FROM socalytics_app;
