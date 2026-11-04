#!/usr/bin/env bash
# Creates the least-privilege database roles and the database itself.
# Run as root on the Ubuntu 22.04 host:  sudo ./setup-database.sh
#
# The application connects as yingge_cloud_app (DML only). Migrations and the
# operator CLI connect as yingge_cloud_owner (DDL). Neither is a superuser and
# neither is the postgres account.
set -euo pipefail

DB_NAME="${DB_NAME:-yingge_cloud}"
OWNER_ROLE="${OWNER_ROLE:-yingge_cloud_owner}"
APP_ROLE="${APP_ROLE:-yingge_cloud_app}"

if [[ $EUID -ne 0 ]]; then
  echo "请以 root 运行（sudo）。" >&2
  exit 2
fi

if [[ -z "${OWNER_PASSWORD:-}" || -z "${APP_PASSWORD:-}" ]]; then
  cat >&2 <<'EOF'
缺少口令。请先设置环境变量，口令只写入 PostgreSQL，不要写进仓库：

  export OWNER_PASSWORD="$(openssl rand -base64 24)"
  export APP_PASSWORD="$(openssl rand -base64 24)"

然后把它们写入 /etc/yingge-cloud/*.env（权限 0600/0640）。
EOF
  exit 2
fi

run_sql() {
  sudo -u postgres psql -v ON_ERROR_STOP=1 --quiet -c "$1"
}

run_sql "do \$\$
begin
  if not exists (select 1 from pg_roles where rolname = '${OWNER_ROLE}') then
    create role ${OWNER_ROLE} login password '${OWNER_PASSWORD}' nosuperuser nocreatedb nocreaterole;
  else
    alter role ${OWNER_ROLE} with login password '${OWNER_PASSWORD}' nosuperuser nocreatedb nocreaterole;
  end if;
  if not exists (select 1 from pg_roles where rolname = '${APP_ROLE}') then
    create role ${APP_ROLE} login password '${APP_PASSWORD}' nosuperuser nocreatedb nocreaterole;
  else
    alter role ${APP_ROLE} with login password '${APP_PASSWORD}' nosuperuser nocreatedb nocreaterole;
  end if;
end
\$\$;"

if ! sudo -u postgres psql -tAc "select 1 from pg_database where datname = '${DB_NAME}'" | grep -q 1; then
  sudo -u postgres createdb --owner "${OWNER_ROLE}" "${DB_NAME}"
fi

# PUBLIC must not be able to create objects in the schema, and the app role
# needs USAGE before it can touch any table.
sudo -u postgres psql -v ON_ERROR_STOP=1 --quiet -d "${DB_NAME}" <<SQL
-- PostgreSQL 14 (Ubuntu 22.04) 的 public schema 归 postgres 所有；不转交 owner，
-- 撤销 PUBLIC 权限后迁移会报 3F000: no schema has been selected to create in。
alter schema public owner to ${OWNER_ROLE};
revoke all on schema public from public;
grant usage on schema public to ${APP_ROLE};
grant select, insert, update, delete on all tables in schema public to ${APP_ROLE};
grant usage, select on all sequences in schema public to ${APP_ROLE};
alter default privileges for role ${OWNER_ROLE} in schema public
  grant select, insert, update, delete on tables to ${APP_ROLE};
alter default privileges for role ${OWNER_ROLE} in schema public
  grant usage, select on sequences to ${APP_ROLE};
SQL

echo "数据库 ${DB_NAME} 与角色 ${OWNER_ROLE}/${APP_ROLE} 已就绪。"
echo "下一步：安装应用并执行 admin migrate（会创建表并补发权限）。"
