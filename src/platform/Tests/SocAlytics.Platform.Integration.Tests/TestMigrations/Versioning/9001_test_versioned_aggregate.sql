CREATE TABLE socalytics.test_widget (
    id uuid PRIMARY KEY,
    name text NOT NULL,
    version bigint NOT NULL DEFAULT 1
);

CALL socalytics.attach_version_trigger('socalytics.test_widget');

CREATE TABLE socalytics.test_widget_part (
    id uuid PRIMARY KEY,
    widget_id uuid NOT NULL REFERENCES socalytics.test_widget (id),
    label text NOT NULL
);

CALL socalytics.attach_aggregate_child_triggers('socalytics.test_widget_part', 'socalytics.test_widget', 'widget_id');
