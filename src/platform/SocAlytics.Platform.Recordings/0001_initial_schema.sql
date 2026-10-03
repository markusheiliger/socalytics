CREATE SCHEMA IF NOT EXISTS "recordings" AUTHORIZATION "recordings_owner";
REVOKE ALL ON SCHEMA "recordings" FROM PUBLIC;
GRANT USAGE ON SCHEMA "recordings" TO "recordings_runtime";
ALTER DEFAULT PRIVILEGES FOR ROLE "recordings_owner" IN SCHEMA "recordings"
    GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO "recordings_runtime";
ALTER DEFAULT PRIVILEGES FOR ROLE "recordings_owner" IN SCHEMA "recordings"
    GRANT USAGE, SELECT ON SEQUENCES TO "recordings_runtime";
ALTER DEFAULT PRIVILEGES FOR ROLE "recordings_owner" IN SCHEMA "recordings"
    GRANT EXECUTE ON FUNCTIONS TO "recordings_runtime";
