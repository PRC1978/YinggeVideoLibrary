#!/usr/bin/env bash
# Nightly backup: database (custom format) + report files + checksums.
# Install as a systemd timer or cron job; run as root.
set -euo pipefail
umask 077

BACKUP_ROOT="${BACKUP_ROOT:-/var/backups/yingge-cloud}"
KEEP_DAYS="${KEEP_DAYS:-30}"
STAMP="$(date -u +%Y%m%dT%H%M%SZ)"
ADMIN_FILE="${ADMIN_FILE:-/etc/yingge-cloud/admin.env}"
STORAGE_ROOT="${STORAGE_ROOT:-/var/lib/yingge-cloud}"

if [[ $EUID -ne 0 ]]; then echo "请以 root 运行。" >&2; exit 2; fi
install -d -m 0700 -o root -g root "$BACKUP_ROOT"

source "$(dirname "${BASH_SOURCE[0]}")/postgres-env.sh"

DB_TMP="$BACKUP_ROOT/.db-$STAMP.partial"
REPORTS_TMP="$BACKUP_ROOT/.reports-$STAMP.partial"
HASH_TMP="$BACKUP_ROOT/.sha256-$STAMP.partial"
trap 'rm -f "$DB_TMP" "$REPORTS_TMP" "$HASH_TMP"' EXIT

pg_dump --format=custom --no-owner --no-privileges \
  --file="$DB_TMP" --dbname="$PGDATABASE"

tar -C "$STORAGE_ROOT" -czf "$REPORTS_TMP" reports

printf '%s  %s\n' "$(sha256sum "$DB_TMP" | cut -d' ' -f1)" "db-$STAMP.dump" \
  > "$HASH_TMP"
printf '%s  %s\n' "$(sha256sum "$REPORTS_TMP" | cut -d' ' -f1)" "reports-$STAMP.tar.gz" \
  >> "$HASH_TMP"

mv "$DB_TMP" "$BACKUP_ROOT/db-$STAMP.dump"
mv "$REPORTS_TMP" "$BACKUP_ROOT/reports-$STAMP.tar.gz"
mv "$HASH_TMP" "$BACKUP_ROOT/sha256-$STAMP.txt"

find "$BACKUP_ROOT" -maxdepth 1 -type f -mtime "+$KEEP_DAYS" -delete
echo "备份完成：$BACKUP_ROOT/db-$STAMP.dump"
