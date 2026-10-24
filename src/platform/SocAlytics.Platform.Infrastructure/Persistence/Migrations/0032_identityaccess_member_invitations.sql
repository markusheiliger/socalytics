CREATE TABLE socalytics.member_invitations (
    id uuid PRIMARY KEY,
    address text NOT NULL CHECK (char_length(address) BETWEEN 3 AND 254 AND address = btrim(address)),
    address_normalized text NOT NULL CHECK (char_length(address_normalized) BETWEEN 3 AND 254),
    address_hash text NOT NULL,
    account_name text NOT NULL CHECK (char_length(account_name) BETWEEN 3 AND 64),
    account_name_normalized text NOT NULL,
    state text NOT NULL CHECK (state IN ('pending', 'accepted', 'revoked')),
    invited_by uuid NOT NULL REFERENCES socalytics.member_account (id),
    created_at timestamptz NOT NULL,
    resend_count integer NOT NULL DEFAULT 0 CHECK (resend_count >= 0),
    revoked_by uuid NULL REFERENCES socalytics.member_account (id),
    revoked_at timestamptz NULL,
    accepted_member_id uuid NULL REFERENCES socalytics.member_account (id),
    accepted_at timestamptz NULL,
    version bigint NOT NULL DEFAULT 1,
    CONSTRAINT ck_member_invitations_revoked CHECK (
        (state = 'revoked') = (revoked_by IS NOT NULL AND revoked_at IS NOT NULL)
        AND (state = 'revoked' OR (revoked_by IS NULL AND revoked_at IS NULL))),
    CONSTRAINT ck_member_invitations_accepted CHECK (
        (state = 'accepted') = (accepted_member_id IS NOT NULL AND accepted_at IS NOT NULL)
        AND (state = 'accepted' OR (accepted_member_id IS NULL AND accepted_at IS NULL)))
);

CALL socalytics.attach_version_trigger('socalytics.member_invitations');

CREATE UNIQUE INDEX ux_member_invitations_pending_account_name ON socalytics.member_invitations (account_name_normalized)
    WHERE state = 'pending';
CREATE UNIQUE INDEX ux_member_invitations_pending_address ON socalytics.member_invitations (address_normalized)
    WHERE state = 'pending';
CREATE INDEX ix_member_invitations_created_at ON socalytics.member_invitations (created_at DESC);

CREATE TABLE socalytics.member_invitation_team_roles (
    invitation_id uuid NOT NULL REFERENCES socalytics.member_invitations (id),
    team_id uuid NOT NULL REFERENCES socalytics.team (id),
    role text NOT NULL CHECK (role IN ('coach', 'viewer')),
    PRIMARY KEY (invitation_id, team_id)
);

CREATE INDEX ix_member_invitation_team_roles_team ON socalytics.member_invitation_team_roles (team_id);

CALL socalytics.attach_aggregate_child_triggers('socalytics.member_invitation_team_roles', 'socalytics.member_invitations', 'invitation_id');

CREATE TABLE socalytics.member_invitation_links (
    id uuid PRIMARY KEY,
    invitation_id uuid NOT NULL REFERENCES socalytics.member_invitations (id),
    state text NOT NULL CHECK (state IN ('pending', 'issued', 'used', 'superseded')),
    token_hash bytea NULL,
    created_at timestamptz NOT NULL,
    issued_at timestamptz NULL,
    expires_at timestamptz NULL,
    used_at timestamptz NULL,
    CONSTRAINT ck_member_invitation_links_issued CHECK (
        (state = 'pending' AND token_hash IS NULL AND issued_at IS NULL AND expires_at IS NULL)
        OR (state IN ('issued', 'used') AND token_hash IS NOT NULL AND issued_at IS NOT NULL AND expires_at IS NOT NULL)
        OR (state = 'superseded'
            AND ((token_hash IS NULL AND issued_at IS NULL AND expires_at IS NULL)
                OR (token_hash IS NOT NULL AND issued_at IS NOT NULL AND expires_at IS NOT NULL)))),
    CONSTRAINT ck_member_invitation_links_used CHECK ((state = 'used') = (used_at IS NOT NULL))
);

CREATE UNIQUE INDEX ux_member_invitation_links_token_hash ON socalytics.member_invitation_links (token_hash)
    WHERE token_hash IS NOT NULL;
CREATE INDEX ix_member_invitation_links_open ON socalytics.member_invitation_links (invitation_id)
    WHERE state IN ('pending', 'issued');

ALTER TABLE socalytics.email_messages
    ALTER COLUMN account_id DROP NOT NULL,
    ADD COLUMN invitation_ref uuid NULL REFERENCES socalytics.member_invitation_links (id),
    ADD CONSTRAINT ck_email_messages_invitation CHECK (
        (notification_type = 'member-invitation') = (invitation_ref IS NOT NULL AND account_id IS NULL)
        AND (notification_type = 'member-invitation' OR account_id IS NOT NULL));
