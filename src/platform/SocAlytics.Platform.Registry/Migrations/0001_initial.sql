CREATE SCHEMA registry;
REVOKE ALL ON SCHEMA registry FROM PUBLIC;
GRANT USAGE ON SCHEMA registry TO registry_runtime;
