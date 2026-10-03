CREATE SCHEMA IF NOT EXISTS "analysis" AUTHORIZATION "analysis_owner";
REVOKE ALL ON SCHEMA "analysis" FROM PUBLIC;
GRANT USAGE ON SCHEMA "analysis" TO "analysis_runtime";
ALTER DEFAULT PRIVILEGES FOR ROLE "analysis_owner" IN SCHEMA "analysis"
    GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO "analysis_runtime";
ALTER DEFAULT PRIVILEGES FOR ROLE "analysis_owner" IN SCHEMA "analysis"
    GRANT USAGE, SELECT ON SEQUENCES TO "analysis_runtime";
ALTER DEFAULT PRIVILEGES FOR ROLE "analysis_owner" IN SCHEMA "analysis"
    GRANT EXECUTE ON FUNCTIONS TO "analysis_runtime";
