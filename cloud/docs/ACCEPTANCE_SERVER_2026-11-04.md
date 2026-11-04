# 服务器隔离验收（Ubuntu 22.04 / 43.226.58.187）

日期：2026-11-04 21:55–22:15（CST）。执行者：AI（root，SSH 端口 43602）。
**未部署、未启用公网服务、未创建正式账号、未签发证书、未安装 nginx、未推送代码。**

## 1. 范围

做了：

- 确认操作系统、资源、现有监听端口与防火墙状态（只读）。
- 安装 PostgreSQL 14 与 .NET 9 SDK（它们是运行与测试的前置条件，服务器原本两者都没有）。
- 用临时数据库 + 临时角色跑完整迁移；用临时集群跑 64 项集成测试。
- 在本机回环端口 15080 启动服务，用真实 HTTP 完成注册/登录/退出/停用/脱敏上传/配额/备份恢复。
- 采集 HTTPS 签发前置条件。

没有做：

- 没有连接、迁移或删除任何已有业务数据库（服务器上原本只有 `postgres`、`template0`、`template1`）。
- 没有安装或改动 nginx、没有申请证书、没有绑定 80/443。
- 没有创建 `yingge_cloud` 生产库、没有创建 systemd 服务、没有生成正式邀请码。

## 2. 环境事实

| 项 | 值 |
| --- | --- |
| 系统 | Ubuntu 22.04 LTS，内核 5.15.0-30-generic |
| 资源 | 4 vCPU / 3.9 GB 内存 / 44 GB 磁盘（验收后剩余 30 GB） |
| 验收前监听 | 仅 `0.0.0.0:22`（sshd）与 `127.0.0.53:53`（systemd-resolved） |
| 验收前软件 | 无 dotnet、无 psql、无 nginx、无 certbot |
| 防火墙 | `ufw` inactive；`iptables -S INPUT` 仅 `-P INPUT ACCEPT` |
| 主机名 | NAThb6980（外部 43602 → 本机 22，说明存在端口映射） |

安装后：

| 组件 | 版本 | 安装方式 | 状态 |
| --- | --- | --- | --- |
| PostgreSQL | 14.24-0ubuntu0.22.04.1 | `apt-get install postgresql-14 postgresql-client-14` | 运行中，仅 `127.0.0.1:5432` |
| .NET SDK | 9.0.320（运行时 9.0.22） | `dotnet-install.sh --channel 9.0 --install-dir /usr/share/dotnet` | 仅命令行使用，未注册服务 |

## 3. 实际命令与结果

```bash
# 环境（只读）
. /etc/os-release; uname -r; nproc; free -m; df -h /
ss -ltnp; ufw status verbose; iptables -S INPUT

# 依赖
export DEBIAN_FRONTEND=noninteractive
apt-get install -y postgresql-14 postgresql-client-14
curl -sSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh
bash /tmp/dotnet-install.sh --channel 9.0 --install-dir /usr/share/dotnet --no-path
ln -sf /usr/share/dotnet/dotnet /usr/bin/dotnet

# 隔离测试（以非 root 的 yinggecloud 运行，临时集群由夹具自建）
su -s /bin/bash yinggecloud -c \
  'YINGGE_TEST_PG_BIN=/usr/lib/postgresql/14/bin dotnet test tests/Yingge.Cloud.Tests -c Release'
# 结果：Passed: 64, Failed: 0

# 临时库功能验收（脚本见 cloud/deploy/accept-isolated.sh）
/var/lib/yingge-cloud/accept/... # 已清理；脚本内容已入库
# 结果：passed=23 failed=0
```

`accept-isolated.sh` 每次运行会新建 `yingge_accept_<时间戳>` 库与两个一次性角色，退出时
（含失败路径）全部 `drop`，并在结束时核对为空。

## 4. 测试结果

### 4.1 集成测试（PostgreSQL 14.24）

```
Passed: 64, Failed: 0, Skipped: 0   (~2 分钟)
```

与 Windows + PostgreSQL 18.6 的 64/64 一致。覆盖：邀请一次性、注册冲突不作废邀请码、
登录锁定与限速、会话过期/撤销、停用账号、脱敏拒绝、重复提交、配额、越权删除、迁移幂等、
整库/部分回滚、`pg_dump` 恢复比对、应用角色无 DDL/非超级用户、只监听回环。

### 4.2 功能验收（真实 HTTP，回环 15080）

| 检查 | 结果 |
| --- | --- |
| 迁移 0001/0002 应用 | PASS |
| 应用角色非超级用户 | PASS |
| 健康检查 / 只监听 127.0.0.1:15080 | PASS |
| 邀请码生成 | PASS |
| 注册 201 / 重复使用邀请码 403 | PASS |
| 登录 200 / 错误口令 401 | PASS |
| 脱敏日志上传 201 | PASS |
| 重复提交 409 / 含路径 422 | PASS |
| 超出 5 分钟配额 429 | PASS |
| 被拒请求不写库（行数=2，文件数=2） | PASS |
| 退出 204 / 退出后上传 401 | PASS |
| 停用后旧会话 401、登录 403 | PASS |
| 重新启用后可登录 200 | PASS |
| 备份 `pg_dump` → 新库恢复，行数一致 | PASS |

## 5. 验收中发现并修复的问题

| 问题 | 触发条件 | 修复 |
| --- | --- | --- |
| 迁移报 `3F000 no schema has been selected to create in` | PG14 的 `public` schema 归 `postgres`；撤销 PUBLIC 权限后 owner 无法建表 | 建库后 `alter schema public owner to <owner>`；已写入 `setup-database.sh` 与测试夹具 |
| 服务连不上数据库 | 部署示例用 Unix socket + 口令；Ubuntu 的 socket 走 `peer`（进程用户须等于角色名） | 改用 `Host=127.0.0.1;Port=5432` + scram；`listen_addresses` 保持 localhost |
| 启动即退出：日志目录在 Web 根目录内 | 启动时 `cd` 到源码目录，存储目录放在其下 | 验收脚本把存储放到 content root 之外；启动检查按预期拦截 |
| 退出登录被上传限速拦截（429） | 退出复用了上传策略的令牌桶，配额耗尽后无法退出 | 新增 `session` 策略（30 次/分钟）；上传只由账号配额判定，认证失败返回 401 而非 429 |
| 测试夹具在 Linux 上无法启动 postmaster | 默认 socket 目录 `/var/run/postgresql` 仅 postgres 可写 | 夹具指定自有 `unix_socket_directories` |

前四项在 Windows + PG18 上都不会暴露，属于只在目标环境才会出现的缺陷。

## 6. 端口与防火墙现状

| 端口 | 本机监听 | 说明 |
| --- | --- | --- |
| 22 | `0.0.0.0:22` | SSH（外部 43602 → 22） |
| 5432 | `127.0.0.1:5432` | PostgreSQL 14，仅回环 |
| 80 / 443 | 未监听 | nginx 未安装，网站不在此主机 |
| 5080 / 15080 | 未监听 | 服务未常驻；验收实例已停止 |

- `ufw` 未启用，`iptables` 无过滤规则；安全性依赖“只绑定回环”。
- 从工作站探测 `43.226.58.187:80/443/5432` 会得到“可连接/空响应”，但本机工作在网络代理后
  （HTTPS 探测返回代理的 `200 Connection Established`），该结果**不能**作为公网可达性证据。
  公网暴露面必须在具备干净出口的网络里用 `nmap`/外部检测服务复核。

## 7. HTTPS 方案与前置条件

预检脚本 `cloud/deploy/https-precheck.sh` 在服务器上的输出：

```
DNS: PASS  (reports.yingevideo.top -> 43.226.58.187)
port80: free      port443: free
nginx: MISSING    certbot: MISSING
no local DNAT for 80/443
```

### 推荐路径（HTTP-01 + nginx）

1. 先在**外部网络**确认 `http://reports.yingevideo.top/` 能到达本机（预期返回 nginx 的 301/404；
   若超时，说明上游未把 80 转发到本机，本方案不成立）。
2. `apt-get install -y nginx certbot python3-certbot-nginx`。
3. 部署 `deploy/nginx-reports.conf.example`（域名已写死为 `reports.yingevideo.top`），
   `nginx -t && systemctl reload nginx`；此时 443 仍无证书，先只启用 80 的 ACME location。
4. `certbot --nginx -d reports.yingevideo.top`（自动写入证书路径并挂续期钩子）。
5. `systemctl list-timers | grep certbot` 确认续期定时器；`certbot renew --dry-run` 演练。
6. 启动服务（`systemd` 单元见 `deploy/yingge-cloud-accounts.service`，仅 127.0.0.1:5080），
   配 `Security__KnownProxies__0=127.0.0.1`，再跑 `deploy/smoke-local.sh` 走真实 TLS 验证。

### 若 80 不可达

- 改用 DNS-01：DNSPod 提供 API，可用 `acme.sh --dns dns_dp` 自动签发与续期；手工 TXT 每次续期
  需人工，不建议长期使用。
- 无论哪种方式，证书私钥只留在 `/etc/letsencrypt`，不进仓库、不进客户端。

### 续期条件

- 域名解析保持指向本机（当前 TTL 600）。
- 80（HTTP-01）或 DNS API（DNS-01）至少一条路径长期可用。
- certbot 定时器随系统启动；`renew --dry-run` 通过。

## 8. 回滚

| 变更 | 回滚命令 |
| --- | --- |
| 临时数据库 / 角色 | 已由脚本清理，核对为空：`select datname from pg_database` 仅剩 postgres/template0/template1 |
| 源码副本 `/var/lib/yingge-cloud/accept` | 已删除 |
| 临时文件 `/tmp/cloud-src*.tgz`、`/tmp/accept-server.sh` 等 | 已删除 |
| PostgreSQL 14 | `systemctl stop postgresql`（或 `apt-get purge postgresql-14 postgresql-client-14 postgresql-common`） |
| .NET SDK | `rm -rf /usr/share/dotnet /usr/bin/dotnet` |
| `yinggecloud` 系统用户 | `userdel yinggecloud; rm -rf /var/lib/yingge-cloud` |
| nginx / certbot / 证书 | 未安装、未申请，无需回滚 |

保留状态：PostgreSQL 14 运行在 `127.0.0.1:5432`（库为空）、SDK 在 `/usr/share/dotnet`、
`yinggecloud` 用户与空的 `/var/lib/yingge-cloud`。这些都是后续部署需要的，可按上表移除。

## 9. 未验证与阻塞

| 项 | 状态 | 说明 |
| --- | --- | --- |
| 公网 80/443 是否转发到本机 | **未验证** | 需要干净出口的探测或安全组/端口映射确认；工作站经代理，结果不可信 |
| 公网 5432 是否可达 | **未验证（本机仅回环，风险低）** | 同上 |
| 证书签发与续期 | **未执行** | 需先确认 80 可达并授权安装 nginx/certbot |
| `systemd` 常驻 + 生产库 | **未执行** | 需部署指令 |
| 桌面端联网登录/上传 | **未执行** | 需服务端上线后由你验证；客户端离线自检 6/6 已通过 |
