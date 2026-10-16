CREATE TABLE socalytics.member_email_addresses (
    account_id uuid PRIMARY KEY REFERENCES socalytics.member_account (id),
    address text NOT NULL CHECK (char_length(address) BETWEEN 3 AND 254 AND address = btrim(address)),
    address_normalized text NOT NULL CHECK (char_length(address_normalized) BETWEEN 3 AND 254),
    state text NOT NULL CHECK (state IN ('unverified', 'verified')),
    verified_at timestamptz NULL,
    set_by uuid NOT NULL REFERENCES socalytics.member_account (id),
    updated_at timestamptz NOT NULL,
    version bigint NOT NULL DEFAULT 1,
    CONSTRAINT ck_member_email_addresses_verified CHECK ((state = 'verified') = (verified_at IS NOT NULL))
);

CALL socalytics.attach_version_trigger('socalytics.member_email_addresses');

CREATE TABLE socalytics.email_confirmations (
    id uuid PRIMARY KEY,
    account_id uuid NOT NULL REFERENCES socalytics.member_account (id),
    address_hash text NOT NULL,
    state text NOT NULL CHECK (state IN ('pending', 'issued', 'used', 'superseded', 'expired')),
    token_hash bytea NULL,
    issued_at timestamptz NULL,
    expires_at timestamptz NULL,
    used_at timestamptz NULL,
    CONSTRAINT ck_email_confirmations_issued CHECK (
        (state = 'pending' AND token_hash IS NULL)
        OR (state <> 'pending' AND state <> 'superseded' AND token_hash IS NOT NULL AND issued_at IS NOT NULL AND expires_at IS NOT NULL)
        OR state = 'superseded'),
    CONSTRAINT ck_email_confirmations_used CHECK ((state = 'used') = (used_at IS NOT NULL))
);

CREATE UNIQUE INDEX ux_email_confirmations_token_hash ON socalytics.email_confirmations (token_hash)
    WHERE token_hash IS NOT NULL;
CREATE INDEX ix_email_confirmations_open ON socalytics.email_confirmations (account_id)
    WHERE state IN ('pending', 'issued');

CREATE TABLE socalytics.notification_preferences (
    account_id uuid NOT NULL REFERENCES socalytics.member_account (id),
    notification_type text NOT NULL,
    enabled boolean NOT NULL,
    updated_at timestamptz NOT NULL,
    PRIMARY KEY (account_id, notification_type)
);

CREATE TABLE socalytics.email_messages (
    id uuid PRIMARY KEY,
    notification_type text NOT NULL,
    account_id uuid NOT NULL REFERENCES socalytics.member_account (id),
    address_hash text NOT NULL,
    confirmation_id uuid NULL REFERENCES socalytics.email_confirmations (id),
    state text NOT NULL CHECK (state IN ('queued', 'sending', 'sent', 'failed', 'discarded')),
    attempt_count integer NOT NULL DEFAULT 0 CHECK (attempt_count >= 0),
    next_attempt_at timestamptz NOT NULL,
    lease_until timestamptz NULL,
    last_error_category text NULL CHECK (last_error_category IN ('transient', 'permanent', 'ineligible')),
    created_at timestamptz NOT NULL,
    sent_at timestamptz NULL
);

CREATE INDEX ix_email_messages_type_account_created ON socalytics.email_messages (notification_type, account_id, created_at);
CREATE INDEX ix_email_messages_state_next_attempt ON socalytics.email_messages (state, next_attempt_at);
