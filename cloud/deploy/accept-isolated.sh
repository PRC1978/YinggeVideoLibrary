#!/usr/bin/env bash
# Isolated acceptance for the cloud account service on an Ubuntu 22.04 host.
#
# 首次使用前，先在开发机发布并解压到 /var/lib/yingge-cloud/accept/cloud：
#   dotnet publish cloud/src/Yingge.Cloud.Accounts -c Release -o publish
#   dotnet test  cloud/tests/Yingge.Cloud.Tests   # 也可在服务器上跑，见 docs/ACCEPTANCE
#
#
#   * creates a throwaway database and two throwaway roles in the local
#     PostgreSQL 14 cluster (nothing else in that cluster is touched)
#   * runs migrations, starts the service on 127.0.0.1:15080 only
#   * exercises register / login / logout / disable / redacted upload /
#     duplicate / quota / path rejection through real HTTP
#   * rehearses pg_dump + restore into another throwaway database
#   * removes every object it created, even on failure
#
# It never binds a public port, never touches nginx, never creates a
# production account and never writes outside /var/lib/yingge-cloud/accept.
set -uo pipefail
cd /tmp || exit 9

STAMP="$(date +%Y%m%d%H%M%S)"
DB="yingge_accept_${STAMP}"
RESTORE_DB="yingge_accept_restore_${STAMP}"
OWNER="yingge_acc_owner_${STAMP}"
APP="yingge_acc_app_${STAMP}"
OWNER_PW="$(openssl rand -hex 18)"
APP_PW="$(openssl rand -hex 18)"
PORT=15080
BASE="http://127.0.0.1:${PORT}"
APP_ROOT=/var/lib/yingge-cloud/accept
DLL="${APP_ROOT}/cloud/src/Yingge.Cloud.Accounts/bin/Release/net9.0/Yingge.Cloud.Accounts.dll"
STORAGE="/var/lib/yingge-cloud/accept-reports-${STAMP}"
OWNER_CS="Host=127.0.0.1;Port=5432;Database=${DB};Username=${OWNER};Password=${OWNER_PW}"
APP_CS="Host=127.0.0.1;Port=5432;Database=${DB};Username=${APP};Password=${APP_PW}"

PASS=0
FAIL=0
SERVER_PID=""

log() { printf '%s\n' "$*"; }
check() { # check <name> <actual> <expected>
  if [[ "$2" == "$3" ]]; then
    PASS=$((PASS + 1)); log "PASS  $1  ($2)"
  else
    FAIL=$((FAIL + 1)); log "FAIL  $1  expected=$3 actual=$2"
  fi
}

cleanup() {
  pkill -f "Yingge.Cloud.Accounts.dll" 2>/dev/null
  [[ -n "$SERVER_PID" ]] && kill "$SERVER_PID" 2>/dev/null
  sudo -u postgres psql -qAt -c "drop database if exists ${RESTORE_DB};" >/dev/null 2>&1
  sudo -u postgres psql -qAt -c "drop database if exists ${DB};" >/dev/null 2>&1
  sudo -u postgres psql -qAt -c "drop role if exists ${APP};" >/dev/null 2>&1
  sudo -u postgres psql -qAt -c "drop role if exists ${OWNER};" >/dev/null 2>&1
  rm -rf "$STORAGE"
}
trap cleanup EXIT

log "== 0. host =="
. /etc/os-release; log "os=${PRETTY_NAME}"
log "pg=$(psql --version | awk '{print $3}')  dotnet=$(dotnet --version)"
ss -ltn | awk 'NR==1 || /:5432 |:15080 /' | sed 's/^/  /'

log "== 1. isolated database =="
sudo -u postgres psql -v ON_ERROR_STOP=1 -q \
  -c "create role ${OWNER} login password '${OWNER_PW}' nosuperuser nocreatedb nocreaterole;" \
  -c "create role ${APP} login password '${APP_PW}' nosuperuser nocreatedb nocreaterole;" || exit 1
sudo -u postgres createdb -O "$OWNER" "$DB" || exit 1
sudo -u postgres psql -v ON_ERROR_STOP=1 -q -d "$DB" <<SQL
alter schema public owner to ${OWNER};
revoke all on schema public from public;
SQL
log "created db=${DB} owner=${OWNER} app=${APP}"

log "== 2. migrations (owner role) =="
su -s /bin/bash yinggecloud -c "dotnet '${DLL}' admin migrate --admin-db '${OWNER_CS}'" 2>&1 | sed 's/^/  /'
MIGRATIONS="$(sudo -u postgres psql -qAt -d "$DB" -c 'select string_agg(version, $q$,$q$ order by version) from schema_migrations;')"
check "migrations_applied" "$MIGRATIONS" "0001,0002"

log "== 3. grants for the application role =="
sudo -u postgres psql -v ON_ERROR_STOP=1 -q -d "$DB" <<SQL
grant usage on schema public to ${APP};
grant select, insert, update, delete on all tables in schema public to ${APP};
grant usage, select on all sequences in schema public to ${APP};
SQL
check "app_role_cannot_create_table" \
  "$(sudo -u postgres psql -qAt -d "$DB" -c "select count(*) from pg_roles where rolname='${APP}' and rolsuper;")" "0"

log "== 4. start service on loopback only =="
install -d -o yinggecloud -g yinggecloud -m 0700 "$STORAGE"
su -s /bin/bash yinggecloud -c "cd ${APP_ROOT} && \
  Database__AppConnectionString='${APP_CS}' \
  Storage__RootPath='${STORAGE}' \
  Security__RequireHttps=false \
  Reports__BurstLimit=2 \
  Reports__DailyLimit=10 \
  AllowedHosts='*' \
  ASPNETCORE_URLS='http://127.0.0.1:${PORT}' \
  ASPNETCORE_ENVIRONMENT=Production \
  DOTNET_NOLOGO=1 \
  setsid dotnet '${DLL}' >/tmp/yingge-accept-server.log 2>&1 < /dev/null &"
sleep 1
SERVER_PID="$(pgrep -f "Yingge.Cloud.Accounts.dll" | head -1)"
for _ in $(seq 1 40); do
  [[ "$(curl -s -o /dev/null -w '%{http_code}' "${BASE}/api/v1/health")" == "200" ]] && break
  sleep 0.5
done
check "health" "$(curl -s -o /dev/null -w '%{http_code}' "${BASE}/api/v1/health")" "200"
check "listener_loopback_only" \
  "$(ss -ltn | awk -v p=":${PORT}" '$4 ~ p {print $4}' | head -1)" "127.0.0.1:${PORT}"

invite() {
  su -s /bin/bash yinggecloud -c "dotnet '${DLL}' admin invite-create --note accept --admin-db '${OWNER_CS}'" \
    | sed -n 's/^invite_code=//p'
}
post() {
  local path="$1" token="${2:-}" body="${3:-}"
  if [[ -n "$token" ]]; then
    curl -s -o /tmp/yingge-body.json -w '%{http_code}' -X POST "${BASE}${path}" \
      -H 'Content-Type: application/json' -H "Authorization: Bearer ${token}" -d "${body}"
  else
    curl -s -o /tmp/yingge-body.json -w '%{http_code}' -X POST "${BASE}${path}" \
      -H 'Content-Type: application/json' -d "${body}"
  fi
}
token_of() { python3 -c 'import json;print(json.load(open("/tmp/yingge-body.json"))["token"])'; }
user="acc$(date +%s)"
pw="AcceptPass-1234"

log "== 5. register / login =="
INV="$(invite)"
check "invite_created" "$([[ -n "$INV" ]] && echo yes)" "yes"
check "register" "$(post /api/v1/auth/register "" "{\"inviteCode\":\"${INV}\",\"username\":\"${user}\",\"password\":\"${pw}\"}")" "201"
check "invite_single_use" "$(post /api/v1/auth/register "" "{\"inviteCode\":\"${INV}\",\"username\":\"other$(date +%s)\",\"password\":\"${pw}\"}")" "403"
check "login" "$(post /api/v1/auth/login "" "{\"username\":\"${user}\",\"password\":\"${pw}\"}")" "200"
TOKEN="$(token_of)"
check "wrong_password" "$(post /api/v1/auth/login "" "{\"username\":\"${user}\",\"password\":\"WrongPass-9999\"}")" "401"

log "== 6. redacted upload, duplicate, quota =="
check "upload_clean_1" "$(post /api/v1/reports "$TOKEN" '{"clientVersion":"accept","os":"linux","arch":"x64","messages":["验收：合成错误 A，不含私人信息。"]}')" "201"
check "duplicate_rejected" "$(post /api/v1/reports "$TOKEN" '{"clientVersion":"accept","os":"linux","arch":"x64","messages":["验收：合成错误 A，不含私人信息。"]}')" "409"
check "path_rejected" "$(post /api/v1/reports "$TOKEN" '{"clientVersion":"accept","os":"linux","arch":"x64","messages":["读取失败：D:\\Videos\\a.mkv"]}')" "422"
check "upload_clean_2" "$(post /api/v1/reports "$TOKEN" '{"clientVersion":"accept","os":"linux","arch":"x64","messages":["验收：合成错误 B。"]}')" "201"
check "quota_exceeded" "$(post /api/v1/reports "$TOKEN" '{"clientVersion":"accept","os":"linux","arch":"x64","messages":["验收：合成错误 C。"]}')" "429"
check "db_rows_after_rejections" "$(sudo -u postgres psql -qAt -d "$DB" -c 'select count(*) from error_reports;')" "2"
check "files_on_disk" "$(find "$STORAGE" -name '*.json' | wc -l)" "2"

log "== 7. session lifecycle =="
check "logout" "$(post /api/v1/auth/logout "$TOKEN" '')" "204"
check "upload_after_logout" "$(post /api/v1/reports "$TOKEN" '{"clientVersion":"accept","os":"linux","arch":"x64","messages":["验收：退出后上传。"]}')" "401"
check "login_again" "$(post /api/v1/auth/login "" "{\"username\":\"${user}\",\"password\":\"${pw}\"}")" "200"
TOKEN2="$(token_of)"

log "== 8. disable / enable =="
su -s /bin/bash yinggecloud -c "dotnet '${DLL}' admin account-disable --username '${user}' --admin-db '${OWNER_CS}'" >/dev/null
# account-disable also revokes sessions, so the old token is rejected as unauthenticated
check "disabled_session_revoked" "$(post /api/v1/reports "$TOKEN2" '{"clientVersion":"accept","os":"linux","arch":"x64","messages":["验收：停用后上传。"]}')" "401"
check "disabled_login" "$(post /api/v1/auth/login "" "{\"username\":\"${user}\",\"password\":\"${pw}\"}")" "403"
su -s /bin/bash yinggecloud -c "dotnet '${DLL}' admin account-enable --username '${user}' --admin-db '${OWNER_CS}'" >/dev/null
check "enabled_login" "$(post /api/v1/auth/login "" "{\"username\":\"${user}\",\"password\":\"${pw}\"}")" "200"

log "== 9. backup and restore rehearsal =="
sudo -u postgres pg_dump -Fc -f "/tmp/yingge-accept-${STAMP}.dump" "$DB"
sudo -u postgres createdb -O "$OWNER" "$RESTORE_DB"
sudo -u postgres pg_restore --no-owner --exit-on-error -d "$RESTORE_DB" "/tmp/yingge-accept-${STAMP}.dump"
counts() { sudo -u postgres psql -qAt -F',' -d "$1" -c \
  'select (select count(*) from accounts),(select count(*) from invites),(select count(*) from error_reports),(select count(*) from schema_migrations);'; }
check "restore_matches" "$(counts "$RESTORE_DB")" "$(counts "$DB")"
sudo -u postgres pg_dump --schema-only "$DB" | grep -c 'CREATE TABLE' | xargs -I{} echo "  tables_in_dump={}"

log "== 10. summary =="
log "passed=${PASS} failed=${FAIL}"
[[ "$FAIL" -eq 0 ]] || exit 1
