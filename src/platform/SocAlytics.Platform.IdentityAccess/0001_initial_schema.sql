CREATE SCHEMA IF NOT EXISTS "identity_access" AUTHORIZATION "identity_access_owner";
REVOKE ALL ON SCHEMA "identity_access" FROM PUBLIC;
GRANT USAGE ON SCHEMA "identity_access" TO "identity_access_runtime";
ALTER DEFAULT PRIVILEGES FOR ROLE "identity_access_owner" IN SCHEMA "identity_access"
    GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO "identity_access_runtime";
ALTER DEFAULT PRIVILEGES FOR ROLE "identity_access_owner" IN SCHEMA "identity_access"
    GRANT USAGE, SELECT ON SEQUENCES TO "identity_access_runtime";
ALTER DEFAULT PRIVILEGES FOR ROLE "identity_access_owner" IN SCHEMA "identity_access"
    GRANT EXECUTE ON FUNCTIONS TO "identity_access_runtime";
