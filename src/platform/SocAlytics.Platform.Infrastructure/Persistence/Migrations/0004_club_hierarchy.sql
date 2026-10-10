CREATE TABLE socalytics.club (
    id uuid PRIMARY KEY,
    singleton boolean NOT NULL DEFAULT true CHECK (singleton) UNIQUE,
    display_name text NOT NULL,
    bootstrap_admin_account_id uuid NOT NULL REFERENCES socalytics.member_account (id),
    created_at timestamptz NOT NULL,
    version bigint NOT NULL DEFAULT 1
);

CREATE TABLE socalytics.season (
    id uuid PRIMARY KEY,
    name text NOT NULL,
    state text NOT NULL DEFAULT 'draft' CHECK (state IN ('draft', 'active', 'archived')),
    created_at timestamptz NOT NULL,
    activated_at timestamptz NULL,
    archived_at timestamptz NULL,
    version bigint NOT NULL DEFAULT 1,
    CONSTRAINT ck_season_state_timestamps CHECK (
        (state = 'draft' AND activated_at IS NULL AND archived_at IS NULL)
        OR (state = 'active' AND activated_at IS NOT NULL AND archived_at IS NULL)
        OR (state = 'archived' AND activated_at IS NOT NULL AND archived_at IS NOT NULL))
);

CREATE UNIQUE INDEX ux_season_single_active ON socalytics.season ((true)) WHERE state = 'active';

CREATE TABLE socalytics.team (
    id uuid PRIMARY KEY,
    season_id uuid NOT NULL REFERENCES socalytics.season (id),
    name text NOT NULL,
    created_at timestamptz NOT NULL,
    version bigint NOT NULL DEFAULT 1
);

CREATE INDEX ix_team_season ON socalytics.team (season_id);

CREATE TABLE socalytics.match (
    id uuid PRIMARY KEY,
    team_id uuid NOT NULL REFERENCES socalytics.team (id),
    opponent_name text NOT NULL,
    kickoff_at timestamptz NOT NULL,
    home_away text NOT NULL CHECK (home_away IN ('home', 'away', 'neutral')),
    competition text NULL,
    created_at timestamptz NOT NULL,
    created_by_account_id uuid NOT NULL REFERENCES socalytics.member_account (id),
    version bigint NOT NULL DEFAULT 1
);

CREATE INDEX ix_match_team_kickoff ON socalytics.match (team_id, kickoff_at, id);

CALL socalytics.attach_version_trigger('socalytics.club');
CALL socalytics.attach_version_trigger('socalytics.season');
CALL socalytics.attach_version_trigger('socalytics.team');
CALL socalytics.attach_version_trigger('socalytics.match');
