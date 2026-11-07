#!/usr/bin/env bash
# Installs or upgrades the service on Ubuntu 22.04.
#
#   1) 在开发机发布:  dotnet publish cloud/src/Yingge.Cloud.Accounts -c Release -o publish
#   2) 上传 publish/ 与 cloud/deploy/ 到服务器
#   3) sudo ./install-ubuntu.sh ./publish
#
# 首次运行会在写入 /etc/yingge-cloud/accounts.env 后停止，等填好口令再跑一次。
set -euo pipefail

APP_SOURCE="${1:-}"
DEPLOY_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

if [[ -z "$APP_SOURCE" || ! -f "$APP_SOURCE/Yingge.Cloud.Accounts.dll" ]]; then
  echo "用法: sudo ./install-ubuntu.sh <publish 目录>" >&2
  exit 2
fi
if [[ $EUID -ne 0 ]]; then
  echo "请以 root 运行（sudo）。" >&2
  exit 2
fi

command -v dotnet >/dev/null || { echo "缺少 dotnet，请安装 .NET 9 运行时。" >&2; exit 3; }
dotnet --list-runtimes | grep -q 'Microsoft.AspNetCore.App 9\.' || {
  echo "需要 ASP.NET Core 9 运行时（apt install aspnetcore-runtime-9.0）。" >&2; exit 3; }

id -u yinggecloud >/dev/null 2>&1 || \
  useradd --system --home /var/lib/yingge-cloud --shell /usr/sbin/nologin yinggecloud
install -d -o yinggecloud -g yinggecloud -m 0700 /var/lib/yingge-cloud /var/lib/yingge-cloud/reports
install -d -o root -g yinggecloud -m 0750 /opt/yingge-cloud /etc/yingge-cloud

# Stage next to the running app so an upgrade never leaves a half-copied tree.
rm -rf /opt/yingge-cloud/app.new
install -d -o root -g yinggecloud -m 0750 /opt/yingge-cloud/app.new
cp -a "$APP_SOURCE"/. /opt/yingge-cloud/app.new/
chown -R root:yinggecloud /opt/yingge-cloud/app.new
chmod -R u=rwX,g=rX,o= /opt/yingge-cloud/app.new

if [[ ! -f /etc/yingge-cloud/accounts.env ]]; then
  install -m 0640 -o root -g yinggecloud "$DEPLOY_DIR/accounts.env.example" \
    /etc/yingge-cloud/accounts.env
  cat >&2 <<'EOF'
已写入 /etc/yingge-cloud/accounts.env 模板。
请填入数据库口令，并创建 /etc/yingge-cloud/admin.env（仅 root 可读）:

  Database__AdminConnectionString=Host=127.0.0.1;Port=5432;Database=yingge_cloud;Username=yingge_cloud_owner;Password=...

然后重新运行本脚本。
EOF
  exit 4
fi

if [[ ! -f /etc/yingge-cloud/admin.env ]]; then
  echo "缺少 /etc/yingge-cloud/admin.env（迁移用的 owner 连接串）。" >&2
  exit 4
fi
chmod 0600 /etc/yingge-cloud/admin.env

if [[ -d /opt/yingge-cloud/app ]]; then
  mv /opt/yingge-cloud/app "/opt/yingge-cloud/app.old.$(date +%Y%m%d%H%M%S)"
fi
mv /opt/yingge-cloud/app.new /opt/yingge-cloud/app

ADMIN_CS="$(grep -m1 '^Database__AdminConnectionString=' /etc/yingge-cloud/admin.env \
  | cut -d= -f2- || true)"
if [[ -z "$ADMIN_CS" ]]; then
  echo "admin.env 中缺少 Database__AdminConnectionString。" >&2
  exit 4
fi

# Migrate before the new build serves traffic. The migrator takes an advisory
# lock and verifies checksums, so a second run is a no-op.
dotnet /opt/yingge-cloud/app/Yingge.Cloud.Accounts.dll admin migrate --admin-db "$ADMIN_CS"

install -m 0644 "$DEPLOY_DIR/yingge-cloud-accounts.service" \
  /etc/systemd/system/yingge-cloud-accounts.service
systemctl daemon-reload
systemctl enable yingge-cloud-accounts >/dev/null
systemctl restart yingge-cloud-accounts

for _ in $(seq 1 20); do
  if curl -fsS -H 'Host: reports.yingevideo.top' -H 'X-Forwarded-Proto: https' http://127.0.0.1:5080/api/v1/health \
       >/dev/null 2>&1; then
    echo "服务已启动：http://127.0.0.1:5080 (仅本机)"
    echo "下一步：配置 nginx + certbot，再运行 smoke-local.sh 验证 HTTPS 链路。"
    exit 0
  fi
  sleep 1
done

echo "服务未在 20 秒内通过健康检查，请查看 journalctl -u yingge-cloud-accounts。" >&2
exit 1
