CREATE TABLE socalytics.analyst_manager_queue_credential (
    queue_credential_id uuid PRIMARY KEY,
    registration_id uuid NOT NULL REFERENCES socalytics.analyst_manager_registration (id),
    user_public_key text NOT NULL,
    issued_at timestamptz NOT NULL,
    expires_at timestamptz NOT NULL,
    revoked_at timestamptz NULL,
    revocation_pushed_at timestamptz NULL,
    CONSTRAINT ux_analyst_manager_queue_credential_user_public_key UNIQUE (user_public_key),
    CONSTRAINT ck_analyst_manager_queue_credential_user_public_key CHECK (user_public_key ~ '^U[A-Z2-7]{55}$'),
    CONSTRAINT ck_analyst_manager_queue_credential_lifetime CHECK (expires_at > issued_at)
);

CREATE INDEX ix_analyst_manager_queue_credential_registration_expires
    ON socalytics.analyst_manager_queue_credential (registration_id, expires_at);
