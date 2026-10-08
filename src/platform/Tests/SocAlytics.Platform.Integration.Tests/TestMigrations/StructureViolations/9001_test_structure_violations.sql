CREATE TABLE socalytics.bad_unadvanced (
    id uuid PRIMARY KEY,
    version bigint NOT NULL DEFAULT 1
);

CREATE TABLE socalytics.bad_child_no_touch (
    id uuid PRIMARY KEY,
    unadvanced_id uuid NOT NULL REFERENCES socalytics.bad_unadvanced (id)
);

CREATE TABLE socalytics.bad_child_versioned (
    id uuid PRIMARY KEY,
    unadvanced_id uuid NOT NULL REFERENCES socalytics.bad_unadvanced (id),
    version bigint NOT NULL DEFAULT 1
);

CALL socalytics.attach_version_trigger('socalytics.bad_child_versioned');
CALL socalytics.attach_aggregate_child_triggers('socalytics.bad_child_versioned', 'socalytics.bad_unadvanced', 'unadvanced_id');

CREATE TABLE socalytics.bad_discriminator (
    id uuid PRIMARY KEY,
    club_id uuid NOT NULL
);
