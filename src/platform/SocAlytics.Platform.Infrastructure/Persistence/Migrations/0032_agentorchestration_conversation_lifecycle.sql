ALTER TABLE socalytics.agent_conversations DROP CONSTRAINT ck_agent_conversations_state;
ALTER TABLE socalytics.agent_conversations ADD CONSTRAINT ck_agent_conversations_state
    CHECK (state IN ('active', 'archived', 'deleted'));
ALTER TABLE socalytics.agent_conversations ADD COLUMN archived_at timestamptz NULL;

CREATE TABLE socalytics.agent_conversation_tombstones (
    conversation_id uuid PRIMARY KEY REFERENCES socalytics.agent_conversations (id),
    team_id uuid NOT NULL REFERENCES socalytics.team (id),
    deleted_by_member_id uuid NOT NULL REFERENCES socalytics.member_account (id),
    deleted_at timestamptz NOT NULL,
    invocations_suppressed integer NOT NULL,
    CONSTRAINT ck_agent_conversation_tombstones_suppressed CHECK (invocations_suppressed >= 0)
);

CREATE TRIGGER trg_agent_conversation_tombstones_immutable
    BEFORE UPDATE OR DELETE ON socalytics.agent_conversation_tombstones
    FOR EACH ROW EXECUTE FUNCTION socalytics.reject_immutable_change();

REVOKE UPDATE, DELETE ON socalytics.agent_conversation_tombstones FROM socalytics_app;

CREATE TABLE socalytics.agent_conversation_purges (
    conversation_id uuid NOT NULL REFERENCES socalytics.agent_conversations (id),
    copy text NOT NULL,
    state text NOT NULL,
    outcome text NULL,
    attempts integer NOT NULL DEFAULT 0,
    next_attempt_at timestamptz NOT NULL,
    completed_at timestamptz NULL,
    updated_at timestamptz NOT NULL,
    version bigint NOT NULL DEFAULT 1,
    PRIMARY KEY (conversation_id, copy),
    CONSTRAINT ck_agent_conversation_purges_copy CHECK (copy IN ('primary', 'index')),
    CONSTRAINT ck_agent_conversation_purges_state CHECK (state IN ('pending', 'completed', 'unresolved')),
    CONSTRAINT ck_agent_conversation_purges_outcome CHECK (outcome IS NULL OR outcome IN ('purged', 'not-present')),
    CONSTRAINT ck_agent_conversation_purges_attempts CHECK (attempts >= 0)
);

CREATE INDEX ix_agent_conversation_purges_pending
    ON socalytics.agent_conversation_purges (next_attempt_at) WHERE state = 'pending';

CALL socalytics.attach_version_trigger('socalytics.agent_conversation_purges');

ALTER TABLE socalytics.agent_invocations DROP CONSTRAINT ck_agent_invocations_state;
ALTER TABLE socalytics.agent_invocations ADD CONSTRAINT ck_agent_invocations_state
    CHECK (state IN ('queued', 'running', 'succeeded', 'failed', 'lifecycle-suppressed'));
ALTER TABLE socalytics.agent_invocations ADD COLUMN purged_at timestamptz NULL;

ALTER TABLE socalytics.agent_attempts DROP CONSTRAINT ck_agent_attempts_end_reason;
ALTER TABLE socalytics.agent_attempts ADD CONSTRAINT ck_agent_attempts_end_reason CHECK (
    end_reason IS NULL OR end_reason IN
        ('completed', 'failed-retryable', 'failed-permanent', 'lease-expired', 'deadline-reached', 'scope-lost',
         'lifecycle-suppressed'));

ALTER TABLE socalytics.outbox_messages DROP CONSTRAINT ck_outbox_messages_message_type;
ALTER TABLE socalytics.outbox_messages ADD CONSTRAINT ck_outbox_messages_message_type CHECK (message_type IN (
    'matches.recordings-finalized', 'analysis.ready-work', 'analysis.run-state-changed', 'agent-invocation-ready',
    'agent-conversation-purge'));

-- Rows of a deleted conversation's invocations may be purged; any other change stays rejected.
CREATE FUNCTION socalytics.reject_agent_change_unless_tombstoned() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP = 'DELETE' AND EXISTS (
        SELECT 1
        FROM socalytics.agent_invocations i
        JOIN socalytics.agent_conversation_tombstones t ON t.conversation_id = i.conversation_id
        WHERE i.id = OLD.invocation_id) THEN
        RETURN OLD;
    END IF;

    RAISE EXCEPTION '% on socalytics.% is not allowed: the table is immutable', TG_OP, TG_TABLE_NAME;
END;
$$;

DROP TRIGGER trg_agent_lineage_entries_immutable ON socalytics.agent_lineage_entries;
DROP TRIGGER trg_agent_accepted_advice_immutable ON socalytics.agent_accepted_advice;
DROP TRIGGER trg_agent_workload_operations_immutable ON socalytics.agent_workload_operations;

CREATE TRIGGER trg_agent_lineage_entries_immutable
    BEFORE UPDATE OR DELETE ON socalytics.agent_lineage_entries
    FOR EACH ROW EXECUTE FUNCTION socalytics.reject_agent_change_unless_tombstoned();
CREATE TRIGGER trg_agent_accepted_advice_immutable
    BEFORE UPDATE OR DELETE ON socalytics.agent_accepted_advice
    FOR EACH ROW EXECUTE FUNCTION socalytics.reject_agent_change_unless_tombstoned();
CREATE TRIGGER trg_agent_workload_operations_immutable
    BEFORE UPDATE OR DELETE ON socalytics.agent_workload_operations
    FOR EACH ROW EXECUTE FUNCTION socalytics.reject_agent_change_unless_tombstoned();

GRANT DELETE ON
    socalytics.agent_lineage_entries,
    socalytics.agent_accepted_advice,
    socalytics.agent_workload_operations
TO socalytics_app;

-- The question text may only be blanked by a purge that stamps purged_at.
CREATE FUNCTION socalytics.reject_agent_invocation_question_change() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
    IF NEW.question IS DISTINCT FROM OLD.question
        AND NOT (NEW.question = '' AND NEW.purged_at IS NOT NULL) THEN
        RAISE EXCEPTION 'UPDATE of question on socalytics.% is not allowed: only a purge may blank it', TG_TABLE_NAME;
    END IF;

    IF OLD.purged_at IS NOT NULL AND NEW.purged_at IS DISTINCT FROM OLD.purged_at THEN
        RAISE EXCEPTION 'UPDATE of purged_at on socalytics.% is not allowed: it is set once', TG_TABLE_NAME;
    END IF;

    RETURN NEW;
END;
$$;

CREATE TRIGGER trg_agent_invocations_question
    BEFORE UPDATE ON socalytics.agent_invocations
    FOR EACH ROW EXECUTE FUNCTION socalytics.reject_agent_invocation_question_change();
