# 部署与回滚 · Ubuntu 22.04

目标机：Ubuntu 22.04 LTS。已在本机验证的部分用「已实测」标注；服务器侧步骤尚未执行（见文末）。

## 组件与端口

| 组件 | 位置 | 端口 |
| --- | --- | --- |
| Kestrel（本服务） | `/opt/yingge-cloud/app` | `127.0.0.1:5080` |
| PostgreSQL 14 | 系统包，Unix socket `/var/run/postgresql` | 不监听 TCP |
| nginx + certbot | 系统包 | `443`（`80` 仅 ACME 与跳转） |
| 日志文件 | `/var/lib/yingge-cloud/reports` | — |

数据库只走 Unix socket，`postgresql.conf` 保持 `listen_addresses = 'localhost'`；`setup-database.sh`
不修改监听配置。**不要**为方便远程管理把 5432 暴露到公网。

## 1. 依赖

```bash
sudo apt update
sudo apt install -y postgresql-14 nginx certbot python3-certbot-nginx curl python3
# .NET 9 ASP.NET Core 运行时（微软源）
sudo apt install -y aspnetcore-runtime-9.0
dotnet --list-runtimes | grep 'Microsoft.AspNetCore.App 9\.'
```

代码要求 PostgreSQL ≥ 14；启动时会读取 `server_version_num`，低于 14 直接拒绝启动。

## 2. 数据库

```bash
export OWNER_PASSWORD="$(openssl rand -base64 24)"
export APP_PASSWORD="$(openssl rand -base64 24)"
sudo -E ./deploy/setup-database.sh
```

脚本创建两个非超级用户：`yingge_cloud_owner`（建表/迁移）与 `yingge_cloud_app`（仅 DML），
撤销 `public` 的默认建表权，并为 owner 之后新建的表配置默认授权。

确认 5432 没有对公网开放（在本机执行）：

```bash
sudo ss -ltnp | grep -E ':5432|:5080'      # 期望：5432 只出现 127.0.0.1 或 unix socket
sudo ufw status                              # 期望：没有 allow 5432
# 在外部机器执行，期望超时或被拒绝：
#   nc -vz 43.226.58.187 5432
```

## 3. 发布（开发机）

```bash
dotnet publish cloud/src/Yingge.Cloud.Accounts -c Release -o publish
scp -r publish cloud/deploy user@43.226.58.187:/tmp/yingge-cloud-install/
```

发布目录不包含任何连接串、口令或证书。配置只在服务器上。

## 4. 安装

```bash
sudo /tmp/yingge-cloud-install/deploy/install-ubuntu.sh /tmp/yingge-cloud-install/publish
```

首次运行会因缺少 `/etc/yingge-cloud/accounts.env` 而停下，填写后重跑：

```bash
sudo install -m 0640 -o root -g yinggecloud deploy/accounts.env.example /etc/yingge-cloud/accounts.env
sudo nano /etc/yingge-cloud/accounts.env          # 填入 Database__AppConnectionString

sudo install -m 0600 -o root -g root /dev/null /etc/yingge-cloud/admin.env
sudo tee /etc/yingge-cloud/admin.env >/dev/null <<'EOF'
Database__AdminConnectionString=Host=/var/run/postgresql;Database=yingge_cloud;Username=yingge_cloud_owner;Password=...
EOF
```

安装脚本会：建系统用户 `yinggecloud`、布置 `/opt/yingge-cloud/app`（旧版本保留为 `app.old.<时间戳>`）、
执行 `admin migrate`、安装并启动 systemd 单元、用 `X-Forwarded-Proto: https` 做本机健康检查。

## 5. nginx 与证书

```bash
sudo cp deploy/nginx-reports.conf.example /etc/nginx/sites-available/yingge-cloud-accounts
sudo sed -i 's/reports.example.com/<你的域名>/g' /etc/nginx/sites-available/yingge-cloud-accounts
sudo ln -s /etc/nginx/sites-available/yingge-cloud-accounts /etc/nginx/sites-enabled/
sudo nginx -t && sudo systemctl reload nginx
sudo certbot --nginx -d <你的域名>          # 自动写入证书路径并配置续期
sudo systemctl list-timers | grep certbot   # 确认自动续期
```

`Security__KnownProxies__0=127.0.0.1` 是必需的：只有来自本机 nginx 的 `X-Forwarded-Proto`
会被信任，其他来源伪造该头无效。

## 6. 验证

```bash
sudo BASE_URL=https://<你的域名> ./deploy/smoke-local.sh
```

脚本依次验证：注册 → 登录 → 上传 → 列表 → 删除 → 退出，全部走真实 TLS（curl 默认校验证书）。
任何一步失败都会打印 HTTP 状态并退出非零。

```bash
curl -sS https://<你的域名>/api/v1/health
curl -sSI https://<你的域名>/api/v1/auth/login   # 只看证书与状态码，不发凭据
```

在服务器上复跑集成测试，确认 PostgreSQL **14** 的行为（测试会另建临时集群，不碰生产库）：

```bash
sudo apt install -y dotnet-sdk-9.0 git
cd cloud
YINGGE_TEST_PG_BIN=/usr/lib/postgresql/14/bin dotnet test tests/Yingge.Cloud.Tests
```

## 7. 日常运维（CLI）

```bash
APP=/opt/yingge-cloud/app/Yingge.Cloud.Accounts.dll
ADMIN="$(grep -m1 '^Database__AdminConnectionString=' /etc/yingge-cloud/admin.env | cut -d= -f2-)"

sudo dotnet $APP admin invite-create --note "2026-11 内测" --ttl-hours 72 --count 3 --admin-db "$ADMIN"
sudo dotnet $APP admin status --admin-db "$ADMIN"
sudo dotnet $APP admin account-disable --username alice --admin-db "$ADMIN"   # 同时撤销全部会话
sudo dotnet $APP admin account-enable  --username alice --admin-db "$ADMIN"
sudo dotnet $APP admin session-revoke  --username alice --admin-db "$ADMIN"
sudo dotnet $APP admin purge-expired --admin-db "$ADMIN"
```

`invite-create` 只打印一次明文邀请码（库存 SHA-256）。已发放但未使用的邀请码可用
`invite-revoke --code` 或 `invites-revoke-all` 作废。

## 8. 备份与恢复

```bash
sudo ./deploy/backup.sh                    # db custom dump + reports tar + sha256
sudo ./deploy/restore-verify.sh /var/backups/yingge-cloud/db-<时间戳>.dump
```

`restore-verify.sh` 把备份恢复到一个临时库并比对 `accounts/invites/sessions/error_reports/schema_migrations`
行数，**不动生产库**；通过后自动删除临时库。真实恢复流程：停服务 → `pg_restore` 到生产库 →
恢复 `reports/` 目录 → 启动服务 → 跑冒烟脚本。

定时任务示例（`/etc/systemd/system/yingge-cloud-backup.service` + `.timer`，每天 03:30）：

```ini
[Service]
Type=oneshot
ExecStart=/opt/yingge-cloud/deploy/backup.sh
```

## 9. 升级与回滚

升级：发布新版本 → 再跑 `install-ubuntu.sh`。旧代码保留在 `/opt/yingge-cloud/app.old.<stamp>`，
数据库迁移在切换前完成（advisory lock + 校验和，重复执行无副作用）。

回滚：

```bash
sudo systemctl stop yingge-cloud-accounts
sudo dotnet /opt/yingge-cloud/app.old.<stamp>/Yingge.Cloud.Accounts.dll \
     admin migrate-down --to 0001 --admin-db "$ADMIN"      # 只在确实需要回退结构时执行
sudo mv /opt/yingge-cloud/app /opt/yingge-cloud/app.failed
sudo mv /opt/yingge-cloud/app.old.<stamp> /opt/yingge-cloud/app
sudo systemctl start yingge-cloud-accounts
```

`migrate-down` 会删除表；执行前先跑 `backup.sh`。只回滚代码、保留数据时，跳过 `migrate-down`。

## 10. 尚未验证（必须完成才能对公众开放）

1. **域名未生效**：2026-11-04 用 Cloudflare DoH 复查，`yingvideo.top` 与 `reports.yingvideo.top`
   仍返回 `Status=3`（NXDOMAIN），根域 NS 也查不到。域名未委派前，certbot 无法签发证书。
2. 本文件中的服务器步骤没有在 `43.226.58.187` 上执行过：本次只交付代码、脚本和隔离测试，
   未登录服务器、未改数据库、未部署。
3. 公网 443 是否真正转发到本机、证书能否自动续期、5432 是否意外可达，需要在服务器上按第 2、5 节实测。
4. 真实客户端只做过离线自检（`--self-test-cloud-account`）；联网登录与上传需要服务器就绪后补测。
