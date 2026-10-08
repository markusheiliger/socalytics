SET LOCAL socalytics.suppress_version = 'on';

UPDATE socalytics.test_widget SET name = name || '-suppressed';

INSERT INTO socalytics.test_widget_part (id, widget_id, label)
SELECT gen_random_uuid(), id, 'suppressed'
FROM socalytics.test_widget
ORDER BY id
LIMIT 1;
