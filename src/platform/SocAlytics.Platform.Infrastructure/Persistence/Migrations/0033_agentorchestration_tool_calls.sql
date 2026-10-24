CREATE TABLE socalytics.agent_tool_calls (
    attempt_id uuid NOT NULL REFERENCES socalytics.agent_attempts (id),
    call_id text NOT NULL,
    invocation_id uuid NOT NULL REFERENCES socalytics.agent_invocations (id),
    tool_id text NOT NULL,
    arguments_digest text NOT NULL,
    is_clip_resolution boolean NOT NULL,
    reserved_at timestamptz NOT NULL,
    PRIMARY KEY (attempt_id, call_id),
    CONSTRAINT ck_agent_tool_calls_call_id CHECK (call_id ~ '^[A-Za-z0-9._\-]{1,128}$'),
    CONSTRAINT ck_agent_tool_calls_tool_id CHECK (tool_id IN (
        'get_match', 'get_video_clip', 'get_player_stats', 'find_counter_attacks', 'find_turnovers',
        'get_heatmap', 'find_ball_actions', 'find_set_plays', 'get_formations', 'find_pressing_phases',
        'get_tactical_phases'))
);

CREATE INDEX ix_agent_tool_calls_invocation ON socalytics.agent_tool_calls (invocation_id);

CREATE TRIGGER trg_agent_tool_calls_immutable
    BEFORE UPDATE OR DELETE ON socalytics.agent_tool_calls
    FOR EACH ROW EXECUTE FUNCTION socalytics.reject_immutable_change();

REVOKE UPDATE, DELETE ON socalytics.agent_tool_calls FROM socalytics_app;

ALTER TABLE socalytics.agent_lineage_entries DROP CONSTRAINT ck_agent_lineage_entries_kind;
ALTER TABLE socalytics.agent_lineage_entries ADD CONSTRAINT ck_agent_lineage_entries_kind CHECK (kind IN
    ('submitted', 'claimed', 'checkpoint', 'lease-expired', 'deadline-reached', 'failed', 'scope-lost', 'exhausted',
     'completed', 'tool-call'));

CREATE UNIQUE INDEX uq_agent_lineage_entries_tool_call
    ON socalytics.agent_lineage_entries (attempt_id, (payload ->> 'callId'))
    WHERE kind = 'tool-call';
