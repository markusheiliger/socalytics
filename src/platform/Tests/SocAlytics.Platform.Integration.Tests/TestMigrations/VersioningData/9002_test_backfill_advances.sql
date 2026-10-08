UPDATE socalytics.test_widget SET name = name || '-backfilled';

UPDATE socalytics.test_widget_part
SET label = label || '-backfilled'
WHERE id = (SELECT id FROM socalytics.test_widget_part ORDER BY id LIMIT 1);
