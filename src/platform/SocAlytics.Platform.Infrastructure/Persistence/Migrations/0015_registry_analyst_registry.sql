CREATE TABLE socalytics.analyst_capability_declaration (
    image_digest text PRIMARY KEY,
    repository text NOT NULL,
    manifest_digest text NOT NULL,
    capability_id text NOT NULL,
    capability_version text NOT NULL,
    tier text NOT NULL,
    execution_scope text NOT NULL,
    model_digest text NOT NULL,
    manifest jsonb NOT NULL,
    source text NOT NULL,
    registered_at timestamptz NOT NULL,
    registered_by uuid NULL,
    CONSTRAINT ck_analyst_capability_declaration_tier CHECK (tier IN ('low-level', 'high-level')),
    CONSTRAINT ck_analyst_capability_declaration_scope CHECK (execution_scope IN ('segment', 'match')),
    CONSTRAINT ck_analyst_capability_declaration_source CHECK (source IN ('image', 'seed'))
);

CREATE INDEX ix_analyst_capability_declaration_capability
    ON socalytics.analyst_capability_declaration (capability_id, capability_version);

CREATE TABLE socalytics.analyst_profile (
    profile_id text NOT NULL,
    profile_version integer NOT NULL,
    profile_digest text NOT NULL,
    image_digest text NOT NULL REFERENCES socalytics.analyst_capability_declaration (image_digest),
    capability_id text NOT NULL,
    capability_version text NOT NULL,
    model_digest text NOT NULL,
    content jsonb NOT NULL,
    state text NOT NULL,
    created_at timestamptz NOT NULL,
    created_by uuid NOT NULL,
    approved_at timestamptz NULL,
    approved_by uuid NULL,
    retired_at timestamptz NULL,
    retired_by uuid NULL,
    version bigint NOT NULL DEFAULT 1,
    PRIMARY KEY (profile_id, profile_version),
    CONSTRAINT uq_analyst_profile_digest UNIQUE (profile_digest),
    CONSTRAINT ck_analyst_profile_state CHECK (state IN ('draft', 'approved', 'retired')),
    CONSTRAINT ck_analyst_profile_version CHECK (profile_version > 0)
);

CREATE INDEX ix_analyst_profile_image ON socalytics.analyst_profile (image_digest);
CREATE INDEX ix_analyst_profile_capability ON socalytics.analyst_profile (capability_id, capability_version);

CALL socalytics.attach_version_trigger('socalytics.analyst_profile');

CREATE FUNCTION socalytics.guard_analyst_profile_change() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP = 'DELETE' THEN
        RAISE EXCEPTION 'DELETE on socalytics.% is not allowed: retire the profile instead', TG_TABLE_NAME;
    END IF;

    IF NEW.state IS DISTINCT FROM OLD.state AND NOT (
        (OLD.state = 'draft' AND NEW.state IN ('approved', 'retired'))
        OR (OLD.state = 'approved' AND NEW.state = 'retired')) THEN
        RAISE EXCEPTION 'A profile cannot move from % to %', OLD.state, NEW.state;
    END IF;

    IF OLD.state <> 'draft' AND (
        NEW.profile_id IS DISTINCT FROM OLD.profile_id
        OR NEW.profile_version IS DISTINCT FROM OLD.profile_version
        OR NEW.profile_digest IS DISTINCT FROM OLD.profile_digest
        OR NEW.image_digest IS DISTINCT FROM OLD.image_digest
        OR NEW.capability_id IS DISTINCT FROM OLD.capability_id
        OR NEW.capability_version IS DISTINCT FROM OLD.capability_version
        OR NEW.model_digest IS DISTINCT FROM OLD.model_digest
        OR NEW.content IS DISTINCT FROM OLD.content
        OR NEW.created_at IS DISTINCT FROM OLD.created_at
        OR NEW.created_by IS DISTINCT FROM OLD.created_by) THEN
        RAISE EXCEPTION 'The content of a % profile cannot change', OLD.state;
    END IF;

    RETURN NEW;
END;
$$;

CREATE TRIGGER trg_analyst_profile_guard
    BEFORE UPDATE OR DELETE ON socalytics.analyst_profile
    FOR EACH ROW EXECUTE FUNCTION socalytics.guard_analyst_profile_change();

CREATE TABLE socalytics.workflow_definition (
    workflow_id text NOT NULL,
    workflow_version text NOT NULL,
    definition_digest text NOT NULL,
    definition jsonb NOT NULL,
    created_at timestamptz NOT NULL,
    created_by uuid NULL,
    PRIMARY KEY (workflow_id, workflow_version),
    CONSTRAINT uq_workflow_definition_digest UNIQUE (definition_digest)
);

CREATE TABLE socalytics.default_workflow (
    singleton boolean PRIMARY KEY DEFAULT true,
    workflow_id text NOT NULL,
    workflow_version text NOT NULL,
    changed_at timestamptz NOT NULL,
    changed_by uuid NULL,
    version bigint NOT NULL DEFAULT 1,
    CONSTRAINT ck_default_workflow_singleton CHECK (singleton),
    CONSTRAINT fk_default_workflow_definition FOREIGN KEY (workflow_id, workflow_version)
        REFERENCES socalytics.workflow_definition (workflow_id, workflow_version)
);

CALL socalytics.attach_version_trigger('socalytics.default_workflow');

CREATE TRIGGER trg_analyst_capability_declaration_immutable
    BEFORE UPDATE OR DELETE ON socalytics.analyst_capability_declaration
    FOR EACH ROW EXECUTE FUNCTION socalytics.reject_immutable_change();
CREATE TRIGGER trg_workflow_definition_immutable
    BEFORE UPDATE OR DELETE ON socalytics.workflow_definition
    FOR EACH ROW EXECUTE FUNCTION socalytics.reject_immutable_change();

REVOKE UPDATE, DELETE, TRUNCATE ON
    socalytics.analyst_capability_declaration,
    socalytics.workflow_definition
FROM socalytics_app;
