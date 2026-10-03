CREATE SCHEMA agent_orchestration;
REVOKE ALL ON SCHEMA agent_orchestration FROM PUBLIC;
GRANT USAGE ON SCHEMA agent_orchestration TO agent_orchestration_runtime;
