#!/usr/bin/env bash
# Rehearses a restore into a scratch database and compares row counts with the
# live database. It never writes to the live database.
#
#   sudo ./restore-verify.sh /var/backups/yingge-cloud/db-20261104T120000Z.dump
set -euo pipefail

DUMP="${1:-}"
SCRATCH="${2:-yingge_cloud_restore_check}"
ADMIN_FILE="${ADMIN_FILE:-/etc/yingge-cloud/admin.env}"
LIVE_DB="${LIVE_DB:-yingge_cloud}"

if [[ -z "$DUMP" || ! -f "$DUMP" ]]; then
  echo "用法: sudo ./restore-verify.sh <dump 文件> [临时数据库名]" >&2
  exit 2
fi
if [[ $EUID -ne 0 ]]; then echo "请以 root 运行。" >&2; exit 2; fi

ADMIN_CS="$(grep -m1 '^Database__AdminConnectionString=' "$ADMIN_FILE" | cut -d= -f2- || true)"
OWNER_ROLE="$(sed -n 's/.*Username=\([^;]*\).*/\1/p' <<<"$ADMIN_CS")"
[[ -n "$OWNER_ROLE" ]] || OWNER_ROLE="yingge_cloud_owner"

sudo -u postgres dropdb --if-exists "$SCRATCH"
sudo -u postgres createdb --owner "$OWNER_ROLE" "$SCRATCH"
SCRATCH_CS="$(sed "s/Database=[^;]*/Database=$SCRATCH/" <<<"$ADMIN_CS")"
pg_restore --no-owner --no-privileges --exit-on-error \
  --dbname="$SCRATCH_CS" "$DUMP"

counts() {
  psql "$1" -At -F',' -c "
    select
      (select count(*) from accounts),
      (select count(*) from invites),
      (select count(*) from sessions),
      (select count(*) from error_reports),
      (select count(*) from schema_migrations)"
}

LIVE="$(counts "$ADMIN_CS")"
RESTORED="$(counts "$SCRATCH_CS")"

echo "live     : $LIVE"
echo "restored : $RESTORED"

if [[ "$LIVE" == "$RESTORED" ]]; then
  echo "恢复演练通过（账号/邀请/会话/日志/迁移记录行数一致）。"
  sudo -u postgres dropdb --if-exists "$SCRATCH"
  exit 0
fi

echo "恢复演练失败：行数不一致，请勿删除临时数据库 $SCRATCH 以便排查。" >&2
exit 1
