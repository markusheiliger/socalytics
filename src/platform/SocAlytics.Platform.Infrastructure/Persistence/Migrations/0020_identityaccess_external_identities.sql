CREATE TABLE socalytics.external_identity_link (
    id uuid PRIMARY KEY,
    account_id uuid NOT NULL REFERENCES socalytics.member_account (id),
    provider_id text NOT NULL CHECK (provider_id ~ '^[a-z0-9-]{1,32}$'),
    issuer text NOT NULL,
    subject text NOT NULL,
    linked_at timestamptz NOT NULL,
    linked_by uuid NOT NULL REFERENCES socalytics.member_account (id),
    version bigint NOT NULL DEFAULT 1,
    CONSTRAINT uq_external_identity_link_identity UNIQUE (issuer, subject),
    CONSTRAINT uq_external_identity_link_account_provider UNIQUE (account_id, provider_id)
);

CALL socalytics.attach_version_trigger('socalytics.external_identity_link');

ALTER TABLE socalytics.one_time_credential DROP CONSTRAINT one_time_credential_purpose_check;
ALTER TABLE socalytics.one_time_credential ADD CONSTRAINT one_time_credential_purpose_check
    CHECK (purpose IN ('set-password', 'password-reset', 'identity-link'));
ALTER TABLE socalytics.one_time_credential ADD COLUMN provider_id text NULL
    CHECK (provider_id ~ '^[a-z0-9-]{1,32}$');
ALTER TABLE socalytics.one_time_credential ADD CONSTRAINT ck_one_time_credential_provider
    CHECK ((purpose = 'identity-link') = (provider_id IS NOT NULL));

CREATE TABLE socalytics.pending_external_sign_in (
    id uuid PRIMARY KEY,
    state_hash bytea NOT NULL UNIQUE,
    browser_binding_hash bytea NOT NULL,
    nonce text NOT NULL,
    code_verifier text NOT NULL,
    provider_id text NOT NULL CHECK (provider_id ~ '^[a-z0-9-]{1,32}$'),
    link_credential_id uuid NULL REFERENCES socalytics.one_time_credential (id),
    created_at timestamptz NOT NULL,
    expires_at timestamptz NOT NULL,
    consumed_at timestamptz NULL
);

CREATE INDEX ix_pending_external_sign_in_expires ON socalytics.pending_external_sign_in (expires_at);
