#!/usr/bin/env bash
# Nightly backup: database (custom format) + report files + checksums.
# Install as a systemd timer or cron job; run as root.
set -euo pipefail

BACKUP_ROOT="${BACKUP_ROOT:-/var/backups/yingge-cloud}"
KEEP_DAYS="${KEEP_DAYS:-30}"
STAMP="$(date -u +%Y%m%dT%H%M%SZ)"
ADMIN_FILE="${ADMIN_FILE:-/etc/yingge-cloud/admin.env}"
STORAGE_ROOT="${STORAGE_ROOT:-/var/lib/yingge-cloud}"

if [[ $EUID -ne 0 ]]; then echo "请以 root 运行。" >&2; exit 2; fi
install -d -m 0700 -o root -g root "$BACKUP_ROOT"

ADMIN_CS="$(grep -m1 '^Database__AdminConnectionString=' "$ADMIN_FILE" | cut -d= -f2- || true)"
if [[ -z "$ADMIN_CS" ]]; then echo "admin.env 缺少连接串。" >&2; exit 4; fi

pg_dump --format=custom --no-owner --no-privileges \
  --file="$BACKUP_ROOT/db-$STAMP.dump" "$ADMIN_CS"

tar -C "$STORAGE_ROOT" -czf "$BACKUP_ROOT/reports-$STAMP.tar.gz" reports

( cd "$BACKUP_ROOT" && sha256sum "db-$STAMP.dump" "reports-$STAMP.tar.gz" > "sha256-$STAMP.txt" )

find "$BACKUP_ROOT" -maxdepth 1 -type f -mtime "+$KEEP_DAYS" -delete
echo "备份完成：$BACKUP_ROOT/db-$STAMP.dump"
