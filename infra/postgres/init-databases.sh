#!/usr/bin/env bash
set -euo pipefail

for db in "${POSTGRES_ORDERS_DB:-orders}" "${POSTGRES_INVENTORY_DB:-inventory}"; do
  if [ "$db" != "${POSTGRES_DB:-postgres}" ]; then
    psql -v ON_ERROR_STOP=1 --username "$POSTGRES_USER" --dbname "$POSTGRES_DB" -tc "SELECT 1 FROM pg_database WHERE datname = '$db'" | grep -q 1 || \
      psql -v ON_ERROR_STOP=1 --username "$POSTGRES_USER" --dbname "$POSTGRES_DB" -c "CREATE DATABASE \"$db\""
  fi
done
