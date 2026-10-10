CREATE TABLE socalytics.security_audit_event (
    id uuid PRIMARY KEY,
    occurred_at timestamptz NOT NULL,
    event_type text NOT NULL,
    action text NOT NULL,
    outcome text NOT NULL,
    actor_kind text NOT NULL,
    actor_account_id uuid NULL,
    session_id uuid NULL,
    resource_type text NOT NULL,
    resource_id text NULL,
    team_id uuid NULL,
    data_class text NOT NULL DEFAULT 'DAT-001',
    reason_code text NULL,
    details jsonb NOT NULL DEFAULT '{}',
    correlation_id text NOT NULL,
    CONSTRAINT ck_security_audit_event_outcome CHECK (outcome IN ('succeeded', 'unchanged', 'denied', 'failed', 'refused')),
    CONSTRAINT ck_security_audit_event_actor_kind CHECK (actor_kind IN ('member', 'system', 'anonymous'))
);

CREATE INDEX ix_audit_occurred ON socalytics.security_audit_event (occurred_at);
CREATE INDEX ix_audit_actor ON socalytics.security_audit_event (actor_account_id);
CREATE INDEX ix_audit_resource ON socalytics.security_audit_event (resource_type, resource_id);

CREATE FUNCTION socalytics.reject_audit_mutation() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION 'socalytics.security_audit_event is append-only';
END;
$$;

CREATE TRIGGER trg_security_audit_event_reject_row_mutation
    BEFORE UPDATE OR DELETE ON socalytics.security_audit_event
    FOR EACH ROW EXECUTE FUNCTION socalytics.reject_audit_mutation();

CREATE TRIGGER trg_security_audit_event_reject_truncate
    BEFORE TRUNCATE ON socalytics.security_audit_event
    FOR EACH STATEMENT EXECUTE FUNCTION socalytics.reject_audit_mutation();

REVOKE UPDATE, DELETE, TRUNCATE ON socalytics.security_audit_event FROM socalytics_app;
