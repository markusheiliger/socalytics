CREATE TABLE socalytics.test_failing (id integer);
INSERT INTO socalytics.test_failing (id) VALUES (1);
DO $$ BEGIN RAISE EXCEPTION 'intentional test failure'; END $$;
