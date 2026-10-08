#!/usr/bin/env bash
# Provisions the SocAlytics database roles. Executed or sourced by the postgres image entrypoint.

psql -v ON_ERROR_STOP=1 --username "$POSTGRES_USER" \
  -v migrator_pw="$SOCALYTICS_MIGRATOR_PASSWORD" -v app_pw="$SOCALYTICS_APP_PASSWORD" \
  --dbname "$POSTGRES_DB" <<'EOSQL'
CREATE ROLE socalytics_migrator LOGIN PASSWORD :'migrator_pw';
CREATE ROLE socalytics_app LOGIN PASSWORD :'app_pw';
ALTER DATABASE socalytics OWNER TO socalytics_migrator;
REVOKE ALL ON DATABASE socalytics FROM PUBLIC;
GRANT CONNECT ON DATABASE socalytics TO socalytics_app;
EOSQL
