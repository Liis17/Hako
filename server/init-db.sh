#!/bin/sh
set -eu

: "${HAKO_AUTH_DB_PASSWORD:?HAKO_AUTH_DB_PASSWORD is required}"
: "${HAKO_PACK_DB_PASSWORD:?HAKO_PACK_DB_PASSWORD is required}"

psql --set=ON_ERROR_STOP=1 --username "${POSTGRES_USER}" --dbname "${POSTGRES_DB}" \
    <<'SQL'
\getenv auth_password HAKO_AUTH_DB_PASSWORD
\getenv pack_password HAKO_PACK_DB_PASSWORD

CREATE ROLE hako_auth LOGIN PASSWORD :'auth_password';
CREATE ROLE hako_pack LOGIN PASSWORD :'pack_password';

CREATE DATABASE hako_auth OWNER hako_auth;
REVOKE ALL ON DATABASE hako_auth FROM PUBLIC;
GRANT CONNECT ON DATABASE hako_auth TO hako_auth;

CREATE DATABASE hako_pack OWNER hako_pack;
REVOKE ALL ON DATABASE hako_pack FROM PUBLIC;
GRANT CONNECT ON DATABASE hako_pack TO hako_pack;

REVOKE CONNECT ON DATABASE postgres FROM PUBLIC;
SQL
