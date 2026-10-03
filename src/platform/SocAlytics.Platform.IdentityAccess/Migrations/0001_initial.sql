CREATE SCHEMA identity_access;
REVOKE ALL ON SCHEMA identity_access FROM PUBLIC;
GRANT USAGE ON SCHEMA identity_access TO identity_access_runtime;
