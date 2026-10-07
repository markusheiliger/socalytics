DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'socalytics_app') THEN
        RAISE EXCEPTION 'Role socalytics_app does not exist. Create the database roles before running migrations.';
    END IF;
END
$$;

CREATE SCHEMA socalytics;

REVOKE ALL ON SCHEMA socalytics FROM PUBLIC;
GRANT USAGE ON SCHEMA socalytics TO socalytics_app;

ALTER DEFAULT PRIVILEGES FOR ROLE socalytics_migrator IN SCHEMA socalytics
    GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO socalytics_app;

ALTER DEFAULT PRIVILEGES FOR ROLE socalytics_migrator IN SCHEMA socalytics
    GRANT USAGE, SELECT, UPDATE ON SEQUENCES TO socalytics_app;
