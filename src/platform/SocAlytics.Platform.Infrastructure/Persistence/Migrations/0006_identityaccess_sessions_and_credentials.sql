CREATE TABLE socalytics.member_session (
    id uuid PRIMARY KEY,
    member_account_id uuid NOT NULL REFERENCES socalytics.member_account (id),
    token_hash bytea NOT NULL UNIQUE,
    security_stamp text NOT NULL,
    created_at timestamptz NOT NULL,
    last_seen_at timestamptz NOT NULL,
    idle_expires_at timestamptz NOT NULL,
    absolute_expires_at timestamptz NOT NULL,
    ended_at timestamptz NULL,
    end_reason text NULL CHECK (end_reason IN (
        'sign-out', 'password-changed', 'password-reset', 'deactivated',
        'ended-by-admin', 'replaced', 'break-glass-recovery'))
);

CREATE INDEX ix_member_session_live ON socalytics.member_session (member_account_id) WHERE ended_at IS NULL;

CREATE TABLE socalytics.one_time_credential (
    id uuid PRIMARY KEY,
    member_account_id uuid NOT NULL REFERENCES socalytics.member_account (id),
    purpose text NOT NULL CHECK (purpose IN ('set-password', 'password-reset')),
    credential_hash bytea NOT NULL UNIQUE,
    issued_at timestamptz NOT NULL,
    issued_by_account_id uuid NOT NULL,
    expires_at timestamptz NOT NULL,
    consumed_at timestamptz NULL,
    revoked_at timestamptz NULL,
    revocation_reason text NULL CHECK (revocation_reason IN (
        'superseded', 'issuer-lost-authority', 'target-deactivated', 'break-glass-recovery')),
    CONSTRAINT ck_one_time_credential_revocation CHECK ((revoked_at IS NULL) = (revocation_reason IS NULL))
);

CREATE INDEX ix_one_time_credential_open ON socalytics.one_time_credential (member_account_id)
    WHERE consumed_at IS NULL AND revoked_at IS NULL;
CREATE INDEX ix_one_time_credential_issuer_open ON socalytics.one_time_credential (issued_by_account_id)
    WHERE consumed_at IS NULL AND revoked_at IS NULL;

CREATE TABLE socalytics.recovery_directive_use (
    recovery_id text PRIMARY KEY CHECK (char_length(recovery_id) BETWEEN 8 AND 128),
    member_account_id uuid NOT NULL REFERENCES socalytics.member_account (id),
    applied_at timestamptz NOT NULL,
    correlation_id text NOT NULL
);

REVOKE UPDATE, DELETE, TRUNCATE ON socalytics.recovery_directive_use FROM socalytics_app;
