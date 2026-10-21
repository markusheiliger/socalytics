CREATE TABLE socalytics.materialized_segments (
    id uuid PRIMARY KEY,
    recording_version_id uuid NOT NULL REFERENCES socalytics.recording_versions (id),
    timeline_mapping_digest text NOT NULL,
    segmentation_policy_digest text NOT NULL,
    segment_number integer NOT NULL,
    policy_version integer NOT NULL,
    duration_seconds integer NOT NULL,
    state text NOT NULL,
    demanded boolean NOT NULL DEFAULT false,
    candidate_id uuid NULL,
    lease_owner text NULL,
    lease_expires_at timestamptz NULL,
    attempts_used integer NOT NULL DEFAULT 0,
    next_attempt_at timestamptz NULL,
    failure_reason text NULL,
    media_object_key text NULL,
    manifest_object_key text NULL,
    media_digest text NULL,
    media_size_bytes bigint NULL,
    manifest_checksum text NULL,
    created_at timestamptz NOT NULL,
    updated_at timestamptz NOT NULL,
    available_at timestamptz NULL,
    purged_at timestamptz NULL,
    version bigint NOT NULL DEFAULT 1,
    CONSTRAINT uq_materialized_segments_identity UNIQUE (
        recording_version_id, timeline_mapping_digest, segmentation_policy_digest, segment_number),
    CONSTRAINT ck_materialized_segments_segment_number CHECK (segment_number > 0),
    CONSTRAINT ck_materialized_segments_duration CHECK (duration_seconds > 0),
    CONSTRAINT ck_materialized_segments_state CHECK (state IN ('absent', 'materializing', 'available', 'failed')),
    CONSTRAINT ck_materialized_segments_association CHECK (
        CASE WHEN state = 'available'
            THEN media_object_key IS NOT NULL
                AND manifest_object_key IS NOT NULL
                AND media_digest IS NOT NULL
                AND media_size_bytes IS NOT NULL
                AND manifest_checksum IS NOT NULL
            ELSE media_object_key IS NULL
                AND manifest_object_key IS NULL
                AND media_digest IS NULL
                AND media_size_bytes IS NULL
                AND manifest_checksum IS NULL
        END),
    CONSTRAINT ck_materialized_segments_lease CHECK (
        CASE WHEN state = 'materializing'
            THEN candidate_id IS NOT NULL AND lease_owner IS NOT NULL AND lease_expires_at IS NOT NULL
            ELSE candidate_id IS NULL AND lease_owner IS NULL AND lease_expires_at IS NULL
        END)
);

CALL socalytics.attach_version_trigger('socalytics.materialized_segments');

CREATE INDEX ix_materialized_segments_claim
    ON socalytics.materialized_segments (state, demanded, next_attempt_at, lease_expires_at);

CREATE INDEX ix_materialized_segments_recording_version
    ON socalytics.materialized_segments (recording_version_id);

ALTER TABLE socalytics.analysis_logical_jobs ADD COLUMN segment_rejection_reason text NULL;
