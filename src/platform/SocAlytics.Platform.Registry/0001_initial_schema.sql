CREATE SCHEMA IF NOT EXISTS "registry" AUTHORIZATION "registry_owner";
REVOKE ALL ON SCHEMA "registry" FROM PUBLIC;
GRANT USAGE ON SCHEMA "registry" TO "registry_runtime";
ALTER DEFAULT PRIVILEGES FOR ROLE "registry_owner" IN SCHEMA "registry"
    GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO "registry_runtime";
ALTER DEFAULT PRIVILEGES FOR ROLE "registry_owner" IN SCHEMA "registry"
    GRANT USAGE, SELECT ON SEQUENCES TO "registry_runtime";
ALTER DEFAULT PRIVILEGES FOR ROLE "registry_owner" IN SCHEMA "registry"
    GRANT EXECUTE ON FUNCTIONS TO "registry_runtime";
