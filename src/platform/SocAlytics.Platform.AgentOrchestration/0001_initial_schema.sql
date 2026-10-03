CREATE SCHEMA IF NOT EXISTS "agent_orchestration" AUTHORIZATION "agent_orchestration_owner";
REVOKE ALL ON SCHEMA "agent_orchestration" FROM PUBLIC;
GRANT USAGE ON SCHEMA "agent_orchestration" TO "agent_orchestration_runtime";
ALTER DEFAULT PRIVILEGES FOR ROLE "agent_orchestration_owner" IN SCHEMA "agent_orchestration"
    GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO "agent_orchestration_runtime";
ALTER DEFAULT PRIVILEGES FOR ROLE "agent_orchestration_owner" IN SCHEMA "agent_orchestration"
    GRANT USAGE, SELECT ON SEQUENCES TO "agent_orchestration_runtime";
ALTER DEFAULT PRIVILEGES FOR ROLE "agent_orchestration_owner" IN SCHEMA "agent_orchestration"
    GRANT EXECUTE ON FUNCTIONS TO "agent_orchestration_runtime";
