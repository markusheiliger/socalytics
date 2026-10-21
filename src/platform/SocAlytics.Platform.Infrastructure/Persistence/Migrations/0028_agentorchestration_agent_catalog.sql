CREATE TABLE socalytics.agent_catalog_artifacts (
    kind text NOT NULL,
    artifact_id text NOT NULL,
    artifact_version text NOT NULL,
    version_major integer NOT NULL,
    digest text NOT NULL,
    agent_group text NULL,
    document jsonb NOT NULL,
    registered_by text NOT NULL,
    registered_at timestamptz NOT NULL,
    PRIMARY KEY (kind, artifact_id, artifact_version),
    CONSTRAINT uq_agent_catalog_artifacts_digest UNIQUE (digest),
    CONSTRAINT ck_agent_catalog_artifacts_kind CHECK (kind IN (
        'prompt-template', 'model-selection', 'tool-policy', 'safety-policy',
        'dimension-set', 'philosophy-profile', 'modifier-policy')),
    CONSTRAINT ck_agent_catalog_artifacts_group CHECK (agent_group IS NULL OR agent_group IN ('coach', 'specialist')),
    CONSTRAINT ck_agent_catalog_artifacts_major CHECK (version_major >= 0)
);

CREATE INDEX ix_agent_catalog_artifacts_kind_id_major
    ON socalytics.agent_catalog_artifacts (kind, artifact_id, version_major);

CREATE TABLE socalytics.agent_versions (
    agent_id text NOT NULL,
    agent_version text NOT NULL,
    agent_group text NOT NULL,
    digest text NOT NULL,
    document jsonb NOT NULL,
    state text NOT NULL DEFAULT 'draft',
    registered_by text NOT NULL,
    registered_at timestamptz NOT NULL,
    approved_by text NULL,
    approved_at timestamptz NULL,
    self_approval boolean NULL,
    differentiation_acknowledged boolean NULL,
    retired_by text NULL,
    retired_at timestamptz NULL,
    version bigint NOT NULL DEFAULT 1,
    PRIMARY KEY (agent_id, agent_version),
    CONSTRAINT uq_agent_versions_digest UNIQUE (digest),
    CONSTRAINT ck_agent_versions_state CHECK (state IN ('draft', 'approved', 'retired')),
    CONSTRAINT ck_agent_versions_group CHECK (agent_group IN ('coach', 'specialist')),
    CONSTRAINT ck_agent_versions_agent_id CHECK (agent_id IN (
        'specialist-fitness', 'specialist-goalkeeping', 'specialist-tactics',
        'specialist-video-analysis', 'specialist-scouting', 'specialist-player-development',
        'coach-jurgen-klopp', 'coach-pep-guardiola', 'coach-jose-mourinho'))
);

CREATE FUNCTION socalytics.reject_agent_version_identity_change() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP = 'DELETE' THEN
        RAISE EXCEPTION 'DELETE on socalytics.% is not allowed: agent versions are never deleted', TG_TABLE_NAME;
    END IF;

    IF NEW.agent_id IS DISTINCT FROM OLD.agent_id
        OR NEW.agent_version IS DISTINCT FROM OLD.agent_version
        OR NEW.agent_group IS DISTINCT FROM OLD.agent_group
        OR NEW.digest IS DISTINCT FROM OLD.digest
        OR NEW.document IS DISTINCT FROM OLD.document
        OR NEW.registered_by IS DISTINCT FROM OLD.registered_by
        OR NEW.registered_at IS DISTINCT FROM OLD.registered_at THEN
        RAISE EXCEPTION 'UPDATE on socalytics.% is not allowed: the registered agent version is immutable', TG_TABLE_NAME;
    END IF;

    RETURN NEW;
END;
$$;

CREATE TABLE socalytics.agent_enablements (
    agent_id text PRIMARY KEY,
    agent_version text NULL,
    changed_by text NULL,
    changed_at timestamptz NULL,
    version bigint NOT NULL DEFAULT 1,
    CONSTRAINT ck_agent_enablements_agent_id CHECK (agent_id IN (
        'specialist-fitness', 'specialist-goalkeeping', 'specialist-tactics',
        'specialist-video-analysis', 'specialist-scouting', 'specialist-player-development',
        'coach-jurgen-klopp', 'coach-pep-guardiola', 'coach-jose-mourinho')),
    CONSTRAINT fk_agent_enablements_version FOREIGN KEY (agent_id, agent_version)
        REFERENCES socalytics.agent_versions (agent_id, agent_version)
);

INSERT INTO socalytics.agent_enablements (agent_id) VALUES
    ('specialist-fitness'),
    ('specialist-goalkeeping'),
    ('specialist-tactics'),
    ('specialist-video-analysis'),
    ('specialist-scouting'),
    ('specialist-player-development'),
    ('coach-jurgen-klopp'),
    ('coach-pep-guardiola'),
    ('coach-jose-mourinho');

ALTER TABLE socalytics.agent_invocations
    ADD COLUMN agent_version text NULL,
    ADD COLUMN agent_version_digest text NULL;

ALTER TABLE socalytics.agent_invocations DROP CONSTRAINT ck_agent_invocations_failure_reason;
ALTER TABLE socalytics.agent_invocations ADD CONSTRAINT ck_agent_invocations_failure_reason CHECK (
    failure_reason IS NULL OR failure_reason IN
        ('attempts-exhausted', 'execution-failed', 'scope-lost', 'agent-not-available'));

CREATE TABLE socalytics.agent_invocation_pins (
    invocation_id uuid NOT NULL REFERENCES socalytics.agent_invocations (id),
    kind text NOT NULL,
    artifact_id text NOT NULL,
    artifact_version text NOT NULL,
    digest text NOT NULL,
    PRIMARY KEY (invocation_id, kind),
    CONSTRAINT ck_agent_invocation_pins_kind CHECK (kind IN (
        'prompt-template', 'model-selection', 'tool-policy', 'safety-policy',
        'dimension-set', 'philosophy-profile', 'modifier-policy'))
);

CALL socalytics.attach_version_trigger('socalytics.agent_versions');
CALL socalytics.attach_version_trigger('socalytics.agent_enablements');

CREATE TRIGGER trg_agent_versions_identity
    BEFORE UPDATE OR DELETE ON socalytics.agent_versions
    FOR EACH ROW EXECUTE FUNCTION socalytics.reject_agent_version_identity_change();
CREATE TRIGGER trg_agent_catalog_artifacts_immutable
    BEFORE UPDATE OR DELETE ON socalytics.agent_catalog_artifacts
    FOR EACH ROW EXECUTE FUNCTION socalytics.reject_immutable_change();
CREATE TRIGGER trg_agent_invocation_pins_immutable
    BEFORE UPDATE OR DELETE ON socalytics.agent_invocation_pins
    FOR EACH ROW EXECUTE FUNCTION socalytics.reject_immutable_change();

REVOKE UPDATE, DELETE ON
    socalytics.agent_catalog_artifacts,
    socalytics.agent_invocation_pins
FROM socalytics_app;
REVOKE DELETE ON socalytics.agent_versions FROM socalytics_app;
