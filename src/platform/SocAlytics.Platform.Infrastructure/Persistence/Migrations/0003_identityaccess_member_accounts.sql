CREATE TABLE socalytics.member_account (
    id uuid PRIMARY KEY,
    account_name text NOT NULL,
    normalized_account_name text NOT NULL UNIQUE,
    password_hash text NULL,
    security_stamp text NOT NULL,
    lockout_enabled boolean NOT NULL DEFAULT true,
    lockout_end timestamptz NULL,
    access_failed_count integer NOT NULL DEFAULT 0 CHECK (access_failed_count >= 0),
    two_factor_enabled boolean NOT NULL DEFAULT false,
    password_change_required boolean NOT NULL DEFAULT false,
    membership_status text NOT NULL DEFAULT 'active' CHECK (membership_status IN ('active', 'deactivated')),
    membership_changed_at timestamptz NOT NULL,
    created_at timestamptz NOT NULL,
    created_by_account_id uuid NULL REFERENCES socalytics.member_account (id),
    version bigint NOT NULL DEFAULT 1
);

CALL socalytics.attach_version_trigger('socalytics.member_account');
