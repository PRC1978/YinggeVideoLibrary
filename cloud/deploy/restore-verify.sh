#!/usr/bin/env bash
# Rehearses a restore into a scratch database and compares row counts with the
# live database. It never writes to the live database.
#
#   sudo ./restore-verify.sh /var/backups/yingge-cloud/db-20261104T120000Z.dump
set -euo pipefail

DUMP="${1:-}"
SCRATCH="${2:-yingge_cloud_restore_check_$(openssl rand -hex 4)}"
ADMIN_FILE="${ADMIN_FILE:-/etc/yingge-cloud/admin.env}"
LIVE_DB="${LIVE_DB:-yingge_cloud}"

if [[ -z "$DUMP" || ! -f "$DUMP" ]]; then
  echo "用法: sudo ./restore-verify.sh <dump 文件> [临时数据库名]" >&2
  exit 2
fi
if [[ $EUID -ne 0 ]]; then echo "请以 root 运行。" >&2; exit 2; fi

source "$(dirname "${BASH_SOURCE[0]}")/postgres-env.sh"
[[ "$SCRATCH" =~ ^yingge_cloud_restore_check_[a-f0-9]{8}$ ]] || {
  echo "Invalid scratch database name." >&2
  exit 2
}
if sudo -u postgres psql -tAc "select 1 from pg_database where datname = '$SCRATCH'" | grep -q 1; then
  echo "Scratch database already exists; refusing to overwrite it." >&2
  exit 3
fi

sudo -u postgres createdb --owner "$PGUSER" "$SCRATCH"
trap 'sudo -u postgres dropdb --if-exists "$SCRATCH"' EXIT
pg_restore --no-owner --no-privileges --exit-on-error \
  --dbname="$SCRATCH" "$DUMP"

counts() {
  psql "$1" -At -F',' -c "
    select
      (select count(*) from accounts),
      (select count(*) from invites),
      (select count(*) from sessions),
      (select count(*) from error_reports),
      (select count(*) from schema_migrations)"
}

LIVE="$(counts "$PGDATABASE")"
RESTORED="$(counts "$SCRATCH")"

echo "live     : $LIVE"
echo "restored : $RESTORED"

if [[ "$LIVE" == "$RESTORED" ]]; then
  echo "恢复演练通过（账号/邀请/会话/日志/迁移记录行数一致）。"
  exit 0
fi

echo "恢复演练失败：行数不一致。" >&2
exit 1
