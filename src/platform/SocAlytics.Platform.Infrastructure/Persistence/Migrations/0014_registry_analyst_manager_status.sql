CREATE TABLE socalytics.analyst_manager_status (
    registration_id uuid PRIMARY KEY REFERENCES socalytics.analyst_manager_registration (id),
    operating_state text NOT NULL,
    paused_until timestamptz NULL,
    runtime_unavailable_reason text NULL,
    active_analysts integer NOT NULL,
    max_analysts integer NOT NULL,
    manager_version text NOT NULL,
    received_at timestamptz NOT NULL,
    interval_seconds integer NOT NULL,
    CONSTRAINT ck_analyst_manager_status_state CHECK (operating_state IN (
        'running', 'paused', 'runtime-unavailable', 'draining')),
    CONSTRAINT ck_analyst_manager_status_paused_until CHECK (paused_until IS NULL OR operating_state = 'paused'),
    CONSTRAINT ck_analyst_manager_status_reason CHECK (
        runtime_unavailable_reason IS NULL
        OR (operating_state = 'runtime-unavailable' AND char_length(runtime_unavailable_reason) BETWEEN 1 AND 128)),
    CONSTRAINT ck_analyst_manager_status_analysts CHECK (
        active_analysts >= 0 AND active_analysts <= max_analysts AND max_analysts <= 1024),
    CONSTRAINT ck_analyst_manager_status_version CHECK (char_length(manager_version) BETWEEN 1 AND 64),
    CONSTRAINT ck_analyst_manager_status_interval CHECK (interval_seconds > 0)
);
