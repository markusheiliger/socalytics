CREATE TABLE socalytics.password_recoveries (
    id uuid PRIMARY KEY,
    account_id uuid NOT NULL REFERENCES socalytics.member_account (id),
    address_hash text NOT NULL,
    security_stamp text NOT NULL,
    state text NOT NULL CHECK (state IN ('pending', 'issued', 'used', 'superseded', 'invalidated', 'expired')),
    token_hash bytea NULL,
    requested_at timestamptz NOT NULL,
    issued_at timestamptz NULL,
    expires_at timestamptz NULL,
    used_at timestamptz NULL,
    CONSTRAINT ck_password_recoveries_issued CHECK (
        (state = 'pending' AND token_hash IS NULL AND issued_at IS NULL AND expires_at IS NULL)
        OR (state IN ('issued', 'used', 'expired') AND token_hash IS NOT NULL AND issued_at IS NOT NULL AND expires_at IS NOT NULL)
        OR (state IN ('superseded', 'invalidated')
            AND ((token_hash IS NULL AND issued_at IS NULL AND expires_at IS NULL)
                OR (token_hash IS NOT NULL AND issued_at IS NOT NULL AND expires_at IS NOT NULL)))),
    CONSTRAINT ck_password_recoveries_used CHECK ((state = 'used') = (used_at IS NOT NULL))
);

CREATE UNIQUE INDEX ux_password_recoveries_token_hash ON socalytics.password_recoveries (token_hash)
    WHERE token_hash IS NOT NULL;
CREATE INDEX ix_password_recoveries_open ON socalytics.password_recoveries (account_id)
    WHERE state IN ('pending', 'issued');

ALTER TABLE socalytics.email_messages
    ADD COLUMN password_recovery_id uuid NULL REFERENCES socalytics.password_recoveries (id);

CREATE INDEX ix_member_email_addresses_address_normalized ON socalytics.member_email_addresses (address_normalized);
