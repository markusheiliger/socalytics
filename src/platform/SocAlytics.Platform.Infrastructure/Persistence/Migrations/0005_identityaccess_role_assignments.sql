CREATE TABLE socalytics.club_role_assignment (
    member_account_id uuid NOT NULL REFERENCES socalytics.member_account (id),
    role text NOT NULL CHECK (role IN ('club-admin', 'registrar')),
    assigned_at timestamptz NOT NULL,
    assigned_by_account_id uuid NULL,
    PRIMARY KEY (member_account_id, role)
);

CREATE TABLE socalytics.team_role_assignment (
    member_account_id uuid NOT NULL REFERENCES socalytics.member_account (id),
    team_id uuid NOT NULL REFERENCES socalytics.team (id),
    role text NOT NULL CHECK (role IN ('coach', 'viewer')),
    assigned_at timestamptz NOT NULL,
    assigned_by_account_id uuid NOT NULL,
    PRIMARY KEY (member_account_id, team_id)
);

CREATE INDEX ix_team_role_team ON socalytics.team_role_assignment (team_id);

CALL socalytics.attach_aggregate_child_triggers('socalytics.club_role_assignment', 'socalytics.member_account', 'member_account_id');
CALL socalytics.attach_aggregate_child_triggers('socalytics.team_role_assignment', 'socalytics.member_account', 'member_account_id');
