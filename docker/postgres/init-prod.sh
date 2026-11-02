#!/bin/sh
# Production-like variant of init.sql: passwords come from the environment.
# PostgreSQL runs this only when the data volume is first created.
set -eu

create_service_database() {
    psql -v ON_ERROR_STOP=1 -U "$POSTGRES_USER" -v name="$1" -v password="$2" <<'SQL'
CREATE USER :"name" WITH PASSWORD :'password';
CREATE DATABASE :"name" OWNER :"name";
REVOKE ALL ON DATABASE :"name" FROM PUBLIC;
SQL
}

create_service_database booking "$BOOKING_DB_PASSWORD"
create_service_database payment "$PAYMENT_DB_PASSWORD"
create_service_database notification "$NOTIFICATION_DB_PASSWORD"
