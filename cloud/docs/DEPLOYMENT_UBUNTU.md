# 部署与回滚 · Ubuntu 22.04

目标机：Ubuntu 22.04 LTS。已在本机验证的部分用「已实测」标注；服务器侧步骤尚未执行（见文末）。

## 组件与端口

| 组件 | 位置 | 端口 |
| --- | --- | --- |
| Kestrel（本服务） | `/opt/yingge-cloud/app` | `127.0.0.1:5080` |
| PostgreSQL 14 | 系统包，`listen_addresses=localhost` | `127.0.0.1:5432`（不对外） |
| nginx + certbot | 系统包 | `443`（`80` 仅 ACME 与跳转） |
| 日志文件 | `/var/lib/yingge-cloud/reports` | — |

PostgreSQL 保持 Ubuntu 默认的 `listen_addresses = 'localhost'`。服务通过 `127.0.0.1:5432` 以 scram 口令连接：
Ubuntu 的 pg_hba 对 Unix socket 使用 peer 认证（进程用户必须与角色同名），socket + 口令在默认配置下会失败。
`setup-database.sh` 不修改监听配置。**不要**为方便远程管理把 5432 暴露到公网。

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
把 `public` schema 转交 owner（PG14 的 public 归 postgres，不转交会导致迁移报 3F000）、撤销默认建表权，并为 owner 之后新建的表配置默认授权。

确认 5432 没有对公网开放（在本机执行）：

```bash
sudo ss -ltnp | grep -E ':5432|:5080'      # 期望：5432/5080 都只绑定 127.0.0.1
sudo ufw status                              # 期望：没有 allow 5432
# 在外部机器执行，期望超时或被拒绝：
#   nc -vz 45.207.215.78 5432
```

## 3. 发布（开发机）

```bash
dotnet publish cloud/src/Yingge.Cloud.Accounts -c Release -o publish
scp -r publish cloud/deploy user@45.207.215.78:/tmp/yingge-cloud-install/
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
Database__AdminConnectionString=Host=127.0.0.1;Port=5432;Database=yingge_cloud;Username=yingge_cloud_owner;Password=...
EOF
```

安装脚本会：建系统用户 `yinggecloud`、布置 `/opt/yingge-cloud/app`（旧版本保留为 `app.old.<时间戳>`）、
执行 `admin migrate`、安装并启动 systemd 单元、用 `X-Forwarded-Proto: https` 做本机健康检查。

## 5. nginx 与证书

首次部署时证书文件不存在，不能直接启用引用证书的 443 配置。先只开放
ACME 验证路径，签发成功后再切换到完整 HTTPS 配置：

```bash
sudo install -d -m 0755 /var/www/html/.well-known/acme-challenge
sudo cp deploy/nginx-acme-bootstrap.conf.example /etc/nginx/sites-available/yingge-cloud-accounts
sudo ln -s /etc/nginx/sites-available/yingge-cloud-accounts /etc/nginx/sites-enabled/yingge-cloud-accounts
sudo systemctl enable --now nginx
sudo nginx -t && sudo systemctl reload nginx
sudo certbot certonly --webroot -w /var/www/html -d reports.yingevideo.top
sudo test -s /etc/letsencrypt/live/reports.yingevideo.top/fullchain.pem
sudo cp deploy/nginx-reports.conf.example /etc/nginx/sites-available/yingge-cloud-accounts
sudo nginx -t && sudo systemctl reload nginx
sudo install -d -m 0755 /etc/letsencrypt/renewal-hooks/deploy
printf '#!/bin/sh\nnginx -t && systemctl reload nginx\n' | sudo tee /etc/letsencrypt/renewal-hooks/deploy/reload-yingge-nginx.sh >/dev/null
sudo chmod 0755 /etc/letsencrypt/renewal-hooks/deploy/reload-yingge-nginx.sh
sudo systemctl list-timers | grep certbot
sudo certbot renew --dry-run
```

执行前先确认公网 80 转发到这台主机。以上步骤只供正式部署使用，本轮隔离验收未执行。

`Security__KnownProxies__0=127.0.0.1` 是必需的：只有来自本机 nginx 的 `X-Forwarded-Proto`
会被信任，其他来源伪造该头无效。

## 6. 验证

```bash
sudo BASE_URL=https://reports.yingevideo.top ./deploy/smoke-local.sh
```

脚本依次验证：注册 → 登录 → 上传 → 列表 → 删除 → 退出，全部走真实 TLS（curl 默认校验证书）。
任何一步失败都会打印 HTTP 状态并退出非零。

```bash
curl -sS https://reports.yingevideo.top/api/v1/health
curl -sSI https://reports.yingevideo.top/api/v1/auth/login   # 只看证书与状态码，不发凭据
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

## 10. 当前状态与尚未验证

已在 `43.226.58.187` 上完成隔离验收（2026-11-04，见 `ACCEPTANCE_SERVER_2026-11-04.md`）：

- PostgreSQL **14.24** 已安装并仅监听 `127.0.0.1:5432`；.NET SDK 9.0.320 已安装。
- 临时库迁移 + 64 项测试通过，功能验收 23/23，备份恢复比对通过；临时对象已全部清理。
- 验证中修正了 PG14 的 `public` schema 所有权、socket/peer 认证、日志目录位置、退出限速四个问题。

仍未完成：

1. **证书尚未签发**：`reports.yingevideo.top` 已解析到 `43.226.58.187`，本机 80/443 空闲，但未安装
   nginx/certbot；HTTP-01 还需要公网 80 确实转发到本机（工作站经代理，无法可靠验证）。
2. **未创建生产库与服务**：没有创建 `yingge_cloud` 库、没有安装 systemd 单元、没有生成正式邀请码。
3. 公网 443 是否转发到本机、5432 是否可达，需要在干净出口的网络里复核。
4. 真实客户端只做过离线自检（`--self-test-cloud-account`）；联网登录与上传需服务端上线后补测。
