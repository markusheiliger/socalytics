CREATE TABLE socalytics.agent_conversations (
    id uuid PRIMARY KEY,
    team_id uuid NOT NULL REFERENCES socalytics.team (id),
    match_id uuid NOT NULL REFERENCES socalytics.match (id),
    analysis_run_id uuid NOT NULL REFERENCES socalytics.analysis_runs (id),
    owner_member_id uuid NOT NULL REFERENCES socalytics.member_account (id),
    state text NOT NULL,
    idempotency_key text NOT NULL,
    request_hash text NOT NULL,
    created_at timestamptz NOT NULL,
    version bigint NOT NULL DEFAULT 1,
    CONSTRAINT ck_agent_conversations_state CHECK (state = 'active'),
    CONSTRAINT uq_agent_conversations_idempotency UNIQUE (owner_member_id, idempotency_key)
);

CREATE TABLE socalytics.agent_conversation_participants (
    conversation_id uuid NOT NULL REFERENCES socalytics.agent_conversations (id),
    agent_id text NOT NULL,
    added_by_member_id uuid NOT NULL REFERENCES socalytics.member_account (id),
    added_at timestamptz NOT NULL,
    PRIMARY KEY (conversation_id, agent_id)
);

CREATE TABLE socalytics.agent_invocations (
    id uuid PRIMARY KEY,
    conversation_id uuid NOT NULL REFERENCES socalytics.agent_conversations (id),
    team_id uuid NOT NULL REFERENCES socalytics.team (id),
    match_id uuid NOT NULL REFERENCES socalytics.match (id),
    analysis_run_id uuid NOT NULL REFERENCES socalytics.analysis_runs (id),
    owner_member_id uuid NOT NULL REFERENCES socalytics.member_account (id),
    agent_id text NOT NULL,
    route text NOT NULL,
    question text NOT NULL,
    idempotency_key text NOT NULL,
    request_hash text NOT NULL,
    correlation_id uuid NOT NULL,
    policy_version text NOT NULL,
    execution_policy jsonb NOT NULL,
    state text NOT NULL,
    attempts_used integer NOT NULL DEFAULT 0,
    current_attempt_id uuid NULL,
    ready_occurrence integer NOT NULL DEFAULT 1,
    last_notified_at timestamptz NULL,
    failure_reason text NULL,
    submitted_at timestamptz NOT NULL,
    ended_at timestamptz NULL,
    progress_version bigint NOT NULL DEFAULT 1,
    version bigint NOT NULL DEFAULT 1,
    CONSTRAINT ck_agent_invocations_route CHECK (route = 'user-to-specialist'),
    CONSTRAINT ck_agent_invocations_state CHECK (state IN ('queued', 'running', 'succeeded', 'failed')),
    CONSTRAINT ck_agent_invocations_failure_reason CHECK (
        failure_reason IS NULL OR failure_reason IN ('attempts-exhausted', 'execution-failed', 'scope-lost')),
    CONSTRAINT ck_agent_invocations_attempts_used CHECK (attempts_used >= 0),
    CONSTRAINT ck_agent_invocations_ready_occurrence CHECK (ready_occurrence > 0),
    CONSTRAINT uq_agent_invocations_idempotency UNIQUE (owner_member_id, conversation_id, idempotency_key)
);

CREATE INDEX ix_agent_invocations_owner_unfinished
    ON socalytics.agent_invocations (owner_member_id) WHERE state IN ('queued', 'running');
CREATE INDEX ix_agent_invocations_state_notified
    ON socalytics.agent_invocations (state, last_notified_at);
CREATE INDEX ix_agent_invocations_conversation_submitted
    ON socalytics.agent_invocations (conversation_id, submitted_at);

CREATE TABLE socalytics.agent_attempts (
    id uuid PRIMARY KEY,
    invocation_id uuid NOT NULL REFERENCES socalytics.agent_invocations (id),
    attempt_number integer NOT NULL,
    workload_client_id text NOT NULL,
    fencing_token bigint NOT NULL,
    state text NOT NULL,
    claimed_at timestamptz NOT NULL,
    lease_expires_at timestamptz NOT NULL,
    deadline_at timestamptz NOT NULL,
    ended_at timestamptz NULL,
    end_reason text NULL,
    version bigint NOT NULL DEFAULT 1,
    CONSTRAINT uq_agent_attempts_number UNIQUE (invocation_id, attempt_number),
    CONSTRAINT ck_agent_attempts_number CHECK (attempt_number > 0),
    CONSTRAINT ck_agent_attempts_token CHECK (fencing_token BETWEEN 1 AND 9007199254740991),
    CONSTRAINT ck_agent_attempts_state CHECK (state IN ('claimed', 'active', 'completed', 'failed', 'expired', 'fenced')),
    CONSTRAINT ck_agent_attempts_end_reason CHECK (
        end_reason IS NULL OR end_reason IN
            ('completed', 'failed-retryable', 'failed-permanent', 'lease-expired', 'deadline-reached', 'scope-lost'))
);

CREATE TABLE socalytics.agent_lineage_entries (
    invocation_id uuid NOT NULL REFERENCES socalytics.agent_invocations (id),
    sequence integer NOT NULL,
    kind text NOT NULL,
    attempt_id uuid NULL REFERENCES socalytics.agent_attempts (id),
    payload jsonb NOT NULL,
    recorded_at timestamptz NOT NULL,
    PRIMARY KEY (invocation_id, sequence),
    CONSTRAINT ck_agent_lineage_entries_sequence CHECK (sequence > 0),
    CONSTRAINT ck_agent_lineage_entries_kind CHECK (kind IN
        ('submitted', 'claimed', 'checkpoint', 'lease-expired', 'deadline-reached', 'failed', 'scope-lost', 'exhausted', 'completed'))
);

CREATE TABLE socalytics.agent_accepted_advice (
    invocation_id uuid PRIMARY KEY REFERENCES socalytics.agent_invocations (id),
    attempt_id uuid NOT NULL REFERENCES socalytics.agent_attempts (id),
    advice text NOT NULL,
    execution_references jsonb NOT NULL,
    accepted_at timestamptz NOT NULL
);

CREATE TABLE socalytics.agent_workload_operations (
    workload_client_id text NOT NULL,
    operation_id text NOT NULL,
    kind text NOT NULL,
    request_hash text NOT NULL,
    invocation_id uuid NOT NULL REFERENCES socalytics.agent_invocations (id),
    attempt_id uuid NOT NULL REFERENCES socalytics.agent_attempts (id),
    outcome jsonb NOT NULL,
    recorded_at timestamptz NOT NULL,
    PRIMARY KEY (workload_client_id, operation_id),
    CONSTRAINT ck_agent_workload_operations_id CHECK (operation_id ~ '^[A-Za-z0-9._\-]{1,128}$'),
    CONSTRAINT ck_agent_workload_operations_kind CHECK (kind IN ('claim', 'checkpoint', 'complete', 'fail'))
);

CREATE TABLE socalytics.agent_workload_access_tokens (
    token_hash text PRIMARY KEY,
    workload_client_id text NOT NULL,
    key_thumbprint text NOT NULL,
    issued_at timestamptz NOT NULL,
    expires_at timestamptz NOT NULL
);

CREATE INDEX ix_agent_workload_access_tokens_expires ON socalytics.agent_workload_access_tokens (expires_at);

CALL socalytics.attach_version_trigger('socalytics.agent_conversations');
CALL socalytics.attach_aggregate_child_triggers('socalytics.agent_conversation_participants', 'socalytics.agent_conversations', 'conversation_id');
CALL socalytics.attach_version_trigger('socalytics.agent_invocations');
CALL socalytics.attach_version_trigger('socalytics.agent_attempts');

CREATE TRIGGER trg_agent_lineage_entries_immutable
    BEFORE UPDATE OR DELETE ON socalytics.agent_lineage_entries
    FOR EACH ROW EXECUTE FUNCTION socalytics.reject_immutable_change();
CREATE TRIGGER trg_agent_accepted_advice_immutable
    BEFORE UPDATE OR DELETE ON socalytics.agent_accepted_advice
    FOR EACH ROW EXECUTE FUNCTION socalytics.reject_immutable_change();
CREATE TRIGGER trg_agent_workload_operations_immutable
    BEFORE UPDATE OR DELETE ON socalytics.agent_workload_operations
    FOR EACH ROW EXECUTE FUNCTION socalytics.reject_immutable_change();

REVOKE UPDATE, DELETE ON
    socalytics.agent_lineage_entries,
    socalytics.agent_accepted_advice,
    socalytics.agent_workload_operations
FROM socalytics_app;

ALTER TABLE socalytics.security_audit_event DROP CONSTRAINT ck_security_audit_event_actor_kind;
ALTER TABLE socalytics.security_audit_event ADD CONSTRAINT ck_security_audit_event_actor_kind
    CHECK (actor_kind IN ('member', 'system', 'anonymous', 'analyst-manager', 'agent-workload'));

ALTER TABLE socalytics.outbox_messages DROP CONSTRAINT ck_outbox_messages_message_type;
ALTER TABLE socalytics.outbox_messages ADD CONSTRAINT ck_outbox_messages_message_type CHECK (message_type IN (
    'matches.recordings-finalized', 'analysis.ready-work', 'analysis.run-state-changed', 'agent-invocation-ready'));
