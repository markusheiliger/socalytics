CREATE FUNCTION socalytics.reject_immutable_change() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION '% on socalytics.% is not allowed: the table is immutable', TG_OP, TG_TABLE_NAME;
END;
$$;

CREATE TABLE socalytics.recording_upload_sessions (
    id uuid PRIMARY KEY,
    match_id uuid NOT NULL REFERENCES socalytics.match (id),
    team_id uuid NOT NULL,
    state text NOT NULL DEFAULT 'pending',
    display_name text NOT NULL,
    description text NULL,
    content_type text NOT NULL,
    total_size_bytes bigint NOT NULL,
    part_size_bytes bigint NOT NULL,
    part_count integer NOT NULL,
    part_digests bytea NOT NULL,
    content_digest text NOT NULL,
    object_key text NOT NULL UNIQUE,
    multipart_upload_id text NOT NULL UNIQUE,
    created_by uuid NOT NULL REFERENCES socalytics.member_account (id),
    created_at timestamptz NOT NULL,
    expires_at timestamptz NOT NULL,
    completed_at timestamptz NULL,
    expired_at timestamptz NULL,
    storage_released_at timestamptz NULL,
    version bigint NOT NULL DEFAULT 1,
    CONSTRAINT ck_recording_upload_sessions_state CHECK (state IN ('pending', 'completed', 'expired')),
    CONSTRAINT ck_recording_upload_sessions_part_digests CHECK (octet_length(part_digests) = 32 * part_count),
    CONSTRAINT ck_recording_upload_sessions_content_digest CHECK (
        content_digest ~ '^sha-256-parts:[1-9][0-9]*:[1-9][0-9]*:[0-9a-f]{64}$'
        AND split_part(content_digest, ':', 2) = part_size_bytes::text
        AND split_part(content_digest, ':', 3) = part_count::text),
    CONSTRAINT ck_recording_upload_sessions_completed CHECK ((state = 'completed') = (completed_at IS NOT NULL)),
    CONSTRAINT ck_recording_upload_sessions_expired CHECK ((state = 'expired') = (expired_at IS NOT NULL)),
    CONSTRAINT ck_recording_upload_sessions_released CHECK (storage_released_at IS NULL OR state = 'expired')
);

CALL socalytics.attach_version_trigger('socalytics.recording_upload_sessions');

CREATE INDEX ix_recording_upload_sessions_match ON socalytics.recording_upload_sessions (match_id, created_at);
CREATE INDEX ix_recording_upload_sessions_pending_expiry ON socalytics.recording_upload_sessions (expires_at) WHERE state = 'pending';
CREATE INDEX ix_recording_upload_sessions_unreleased ON socalytics.recording_upload_sessions (expired_at)
    WHERE state = 'expired' AND storage_released_at IS NULL;

CREATE TABLE socalytics.recording_versions (
    id uuid PRIMARY KEY,
    match_id uuid NOT NULL REFERENCES socalytics.match (id),
    team_id uuid NOT NULL,
    upload_session_id uuid NOT NULL UNIQUE REFERENCES socalytics.recording_upload_sessions (id),
    object_key text NOT NULL,
    total_size_bytes bigint NOT NULL,
    part_size_bytes bigint NOT NULL,
    part_count integer NOT NULL,
    content_digest text NOT NULL,
    display_name text NOT NULL,
    description text NULL,
    content_type text NOT NULL,
    storage_etag text NULL,
    created_by uuid NOT NULL REFERENCES socalytics.member_account (id),
    created_at timestamptz NOT NULL,
    CONSTRAINT uq_recording_versions_id_match UNIQUE (id, match_id),
    CONSTRAINT ck_recording_versions_content_digest CHECK (
        content_digest ~ '^sha-256-parts:[1-9][0-9]*:[1-9][0-9]*:[0-9a-f]{64}$'
        AND split_part(content_digest, ':', 2) = part_size_bytes::text
        AND split_part(content_digest, ':', 3) = part_count::text)
);

CREATE INDEX ix_recording_versions_match ON socalytics.recording_versions (match_id, created_at);

CREATE TABLE socalytics.recording_timeline_mappings (
    id uuid PRIMARY KEY,
    recording_version_id uuid NOT NULL,
    match_id uuid NOT NULL,
    spans jsonb NOT NULL,
    mapping_digest text NOT NULL,
    created_by uuid NOT NULL REFERENCES socalytics.member_account (id),
    created_at timestamptz NOT NULL,
    CONSTRAINT fk_recording_timeline_mappings_version FOREIGN KEY (recording_version_id, match_id)
        REFERENCES socalytics.recording_versions (id, match_id),
    CONSTRAINT ck_recording_timeline_mappings_digest CHECK (mapping_digest ~ '^sha-256:[0-9a-f]{64}$'),
    CONSTRAINT uq_recording_timeline_mappings_content UNIQUE (recording_version_id, mapping_digest),
    CONSTRAINT uq_recording_timeline_mappings_id_version UNIQUE (id, recording_version_id)
);

CREATE TABLE socalytics.recording_set_versions (
    id uuid PRIMARY KEY,
    match_id uuid NOT NULL REFERENCES socalytics.match (id),
    team_id uuid NOT NULL,
    member_count integer NOT NULL,
    created_by uuid NOT NULL REFERENCES socalytics.member_account (id),
    finalized_at timestamptz NOT NULL,
    CONSTRAINT ck_recording_set_versions_member_count CHECK (member_count BETWEEN 1 AND 1000),
    CONSTRAINT uq_recording_set_versions_id_match UNIQUE (id, match_id)
);

CREATE INDEX ix_recording_set_versions_match ON socalytics.recording_set_versions (match_id, finalized_at);

CREATE TABLE socalytics.recording_set_members (
    recording_set_version_id uuid NOT NULL,
    position integer NOT NULL,
    match_id uuid NOT NULL,
    recording_version_id uuid NOT NULL,
    timeline_mapping_id uuid NOT NULL,
    PRIMARY KEY (recording_set_version_id, position),
    CONSTRAINT uq_recording_set_members_version UNIQUE (recording_set_version_id, recording_version_id),
    CONSTRAINT fk_recording_set_members_set FOREIGN KEY (recording_set_version_id, match_id)
        REFERENCES socalytics.recording_set_versions (id, match_id),
    CONSTRAINT fk_recording_set_members_recording FOREIGN KEY (recording_version_id, match_id)
        REFERENCES socalytics.recording_versions (id, match_id),
    CONSTRAINT fk_recording_set_members_mapping FOREIGN KEY (timeline_mapping_id, recording_version_id)
        REFERENCES socalytics.recording_timeline_mappings (id, recording_version_id)
);

CREATE TABLE socalytics.recording_retry_outcomes (
    operation text NOT NULL,
    match_id uuid NOT NULL REFERENCES socalytics.match (id),
    idempotency_key text NOT NULL,
    request_digest text NOT NULL,
    status_code smallint NOT NULL,
    result jsonb NOT NULL,
    created_by uuid NOT NULL REFERENCES socalytics.member_account (id),
    created_at timestamptz NOT NULL,
    PRIMARY KEY (operation, match_id, idempotency_key),
    CONSTRAINT ck_recording_retry_outcomes_operation CHECK (
        operation IN ('start-upload', 'complete-upload', 'revise-timeline-mapping', 'finalize-recording-set'))
);

CREATE TABLE socalytics.recording_finalized_events (
    event_id uuid PRIMARY KEY,
    event_type text NOT NULL,
    contract_version text NOT NULL,
    recording_set_version_id uuid NOT NULL UNIQUE REFERENCES socalytics.recording_set_versions (id),
    match_id uuid NOT NULL,
    team_id uuid NOT NULL,
    occurred_at timestamptz NOT NULL,
    payload jsonb NOT NULL
);

CREATE TRIGGER trg_recording_versions_immutable
    BEFORE UPDATE OR DELETE ON socalytics.recording_versions
    FOR EACH ROW EXECUTE FUNCTION socalytics.reject_immutable_change();
CREATE TRIGGER trg_recording_timeline_mappings_immutable
    BEFORE UPDATE OR DELETE ON socalytics.recording_timeline_mappings
    FOR EACH ROW EXECUTE FUNCTION socalytics.reject_immutable_change();
CREATE TRIGGER trg_recording_set_versions_immutable
    BEFORE UPDATE OR DELETE ON socalytics.recording_set_versions
    FOR EACH ROW EXECUTE FUNCTION socalytics.reject_immutable_change();
CREATE TRIGGER trg_recording_set_members_immutable
    BEFORE UPDATE OR DELETE ON socalytics.recording_set_members
    FOR EACH ROW EXECUTE FUNCTION socalytics.reject_immutable_change();
CREATE TRIGGER trg_recording_retry_outcomes_immutable
    BEFORE UPDATE OR DELETE ON socalytics.recording_retry_outcomes
    FOR EACH ROW EXECUTE FUNCTION socalytics.reject_immutable_change();
CREATE TRIGGER trg_recording_finalized_events_immutable
    BEFORE UPDATE OR DELETE ON socalytics.recording_finalized_events
    FOR EACH ROW EXECUTE FUNCTION socalytics.reject_immutable_change();

REVOKE UPDATE, DELETE, TRUNCATE ON
    socalytics.recording_versions,
    socalytics.recording_timeline_mappings,
    socalytics.recording_set_versions,
    socalytics.recording_set_members,
    socalytics.recording_retry_outcomes,
    socalytics.recording_finalized_events
FROM socalytics_app;
