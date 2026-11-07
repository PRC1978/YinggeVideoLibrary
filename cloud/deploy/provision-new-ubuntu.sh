#!/usr/bin/env bash
# First-time provisioning on a dedicated Ubuntu 22.04 host.
# Generates distinct database passwords locally on the host and never prints them.
set -euo pipefail
umask 077

DEPLOY_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PUBLISH_DIR="${1:?usage: provision-new-ubuntu.sh <publish-dir>}"

if [[ $EUID -ne 0 ]]; then
  echo "Run as root." >&2
  exit 2
fi
if [[ -e /etc/yingge-cloud/accounts.env || -e /etc/yingge-cloud/admin.env ]]; then
  echo "Existing service configuration detected; refusing to rotate credentials." >&2
  exit 3
fi
if ! pg_isready -h 127.0.0.1 -p 5432 >/dev/null; then
  echo "PostgreSQL is not ready on loopback." >&2
  exit 4
fi

id -u yinggecloud >/dev/null 2>&1 || \
  useradd --system --home /var/lib/yingge-cloud --shell /usr/sbin/nologin yinggecloud
install -d -o root -g yinggecloud -m 0750 /etc/yingge-cloud

OWNER_PASSWORD="$(openssl rand -hex 24)"
APP_PASSWORD="$(openssl rand -hex 24)"
export OWNER_PASSWORD APP_PASSWORD
bash "$DEPLOY_DIR/setup-database.sh"

printf '%s\n' \
  "Database__AppConnectionString=Host=127.0.0.1;Port=5432;Database=yingge_cloud;Username=yingge_cloud_app;Password=${APP_PASSWORD};Maximum Pool Size=10" \
  'Storage__RootPath=/var/lib/yingge-cloud/reports' \
  'Security__RequireHttps=true' \
  'Security__KnownProxies__0=127.0.0.1' \
  'AllowedHosts=reports.yingevideo.top' \
  'Reports__RetentionDays=30' \
  'Reports__DailyLimit=10' \
  'Reports__BurstLimit=3' \
  > /etc/yingge-cloud/accounts.env
chown root:yinggecloud /etc/yingge-cloud/accounts.env
chmod 0640 /etc/yingge-cloud/accounts.env

printf '%s\n' \
  "Database__AdminConnectionString=Host=127.0.0.1;Port=5432;Database=yingge_cloud;Username=yingge_cloud_owner;Password=${OWNER_PASSWORD}" \
  > /etc/yingge-cloud/admin.env
chmod 0600 /etc/yingge-cloud/admin.env
unset OWNER_PASSWORD APP_PASSWORD

bash "$DEPLOY_DIR/install-ubuntu.sh" "$PUBLISH_DIR"
