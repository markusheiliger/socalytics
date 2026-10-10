CREATE TABLE socalytics.analyst_manager_registration (
    id uuid PRIMARY KEY,
    manager_id uuid NULL UNIQUE,
    stamp_id text NOT NULL,
    state text NOT NULL,
    public_jwk jsonb NOT NULL,
    key_thumbprint text NOT NULL,
    device_fingerprint text NOT NULL,
    fingerprint_mismatch_count integer NOT NULL DEFAULT 0,
    pairing_source_address text NULL,
    pairing_nonce text NOT NULL,
    device_metadata jsonb NOT NULL,
    pairing_code_hash bytea NULL UNIQUE,
    polling_handle_hash bytea NOT NULL UNIQUE,
    challenge_hash bytea NULL,
    challenge_expires_at timestamptz NULL,
    pairing_expires_at timestamptz NOT NULL,
    approval_expires_at timestamptz NULL,
    activation_expires_at timestamptz NULL,
    submitted_at timestamptz NULL,
    decided_at timestamptz NULL,
    activated_at timestamptz NULL,
    unregistration_started_at timestamptz NULL,
    ended_at timestamptz NULL,
    submitted_by uuid NULL REFERENCES socalytics.member_account (id),
    decided_by uuid NULL REFERENCES socalytics.member_account (id),
    revoked_by uuid NULL REFERENCES socalytics.member_account (id),
    decision_reason text NULL,
    end_reason text NULL,
    correlation_id text NOT NULL,
    created_at timestamptz NOT NULL,
    version bigint NOT NULL DEFAULT 1,
    CONSTRAINT ck_analyst_manager_registration_state CHECK (state IN (
        'pairing', 'pending_approval', 'approved', 'active', 'unregistering',
        'unregistered', 'rejected', 'expired', 'revoked')),
    CONSTRAINT ck_analyst_manager_registration_nonce CHECK (char_length(pairing_nonce) BETWEEN 16 AND 128),
    CONSTRAINT ck_analyst_manager_registration_decision_reason CHECK (decision_reason IS NULL OR char_length(decision_reason) BETWEEN 1 AND 500),
    CONSTRAINT ck_analyst_manager_registration_end_reason CHECK (end_reason IS NULL OR char_length(end_reason) BETWEEN 1 AND 500)
);

CALL socalytics.attach_version_trigger('socalytics.analyst_manager_registration');

CREATE UNIQUE INDEX ux_analyst_manager_registration_active_thumbprint
    ON socalytics.analyst_manager_registration (key_thumbprint)
    WHERE state IN ('pairing', 'pending_approval', 'approved', 'active', 'unregistering');
CREATE INDEX ix_analyst_manager_registration_state_created
    ON socalytics.analyst_manager_registration (state, created_at DESC, id DESC);

CREATE TABLE socalytics.analyst_manager_access_token (
    token_hash bytea PRIMARY KEY,
    registration_id uuid NOT NULL REFERENCES socalytics.analyst_manager_registration (id),
    jkt text NOT NULL,
    scope text NOT NULL,
    issued_at timestamptz NOT NULL,
    expires_at timestamptz NOT NULL,
    CONSTRAINT ck_analyst_manager_access_token_scope CHECK (scope = 'analyst-manager')
);

CREATE INDEX ix_analyst_manager_access_token_registration ON socalytics.analyst_manager_access_token (registration_id);
CREATE INDEX ix_analyst_manager_access_token_expires ON socalytics.analyst_manager_access_token (expires_at);

CREATE TABLE socalytics.analyst_manager_proof_replay (
    kind text NOT NULL,
    jti_hash bytea NOT NULL,
    expires_at timestamptz NOT NULL,
    PRIMARY KEY (kind, jti_hash),
    CONSTRAINT ck_analyst_manager_proof_replay_kind CHECK (kind IN ('client_assertion', 'dpop', 'activation'))
);

CREATE INDEX ix_analyst_manager_proof_replay_expires ON socalytics.analyst_manager_proof_replay (expires_at);

ALTER TABLE socalytics.security_audit_event DROP CONSTRAINT ck_security_audit_event_actor_kind;
ALTER TABLE socalytics.security_audit_event ADD CONSTRAINT ck_security_audit_event_actor_kind
    CHECK (actor_kind IN ('member', 'system', 'anonymous', 'analyst-manager'));
