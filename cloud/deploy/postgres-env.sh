#!/usr/bin/env bash
# Load the owner's Npgsql-style connection string into libpq environment vars.
# Source this file from a privileged maintenance script; never print the values.
set -euo pipefail

ADMIN_FILE="${ADMIN_FILE:-/etc/yingge-cloud/admin.env}"
ADMIN_CS="$(grep -m1 '^Database__AdminConnectionString=' "$ADMIN_FILE" | cut -d= -f2- || true)"
[[ -n "$ADMIN_CS" ]] || { echo "Missing admin connection string." >&2; exit 4; }

PGHOST= PGPORT= PGDATABASE= PGUSER= PGPASSWORD=
IFS=';' read -ra PARTS <<< "$ADMIN_CS"
for PART in "${PARTS[@]}"; do
  KEY="${PART%%=*}"
  VALUE="${PART#*=}"
  case "$KEY" in
    Host) PGHOST="$VALUE" ;;
    Port) PGPORT="$VALUE" ;;
    Database) PGDATABASE="$VALUE" ;;
    Username) PGUSER="$VALUE" ;;
    Password) PGPASSWORD="$VALUE" ;;
  esac
done

[[ "$PGHOST" == 127.0.0.1 && "$PGPORT" == 5432 && -n "$PGDATABASE" && -n "$PGUSER" && -n "$PGPASSWORD" ]] || {
  echo "Invalid local admin connection string." >&2
  exit 4
}
export PGHOST PGPORT PGDATABASE PGUSER PGPASSWORD
unset ADMIN_CS PARTS PART KEY VALUE
