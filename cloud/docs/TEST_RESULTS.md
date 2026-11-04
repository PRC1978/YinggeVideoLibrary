# 隔离测试结果（2026-11-04）

## 环境

| 项 | 值 |
| --- | --- |
| 主机 | Windows（win32），.NET SDK 9.0.315 |
| 数据库 | PostgreSQL 18.6（theseus-rs 官方 Windows 构建），独立临时集群 |
| 集群 | `initdb` 新建数据目录，`listen_addresses=127.0.0.1`，随机端口，仅本次测试期间存在 |
| 认证 | `initdb -A trust`（回环 + 临时目录，仅测试用；生产为 scram + socket） |
| 隔离 | 每个测试前 `truncate ... restart identity cascade`；每个测试类独立 `WebApplicationFactory` 与独立存储目录 |
| 命令 | `YINGGE_TEST_PG_BIN=<pg bin> dotnet test tests/Yingge.Cloud.Tests` |
| 跨平台 | 夹具自动按平台查找 `postgres`/`postgres.exe`，Linux 下扫描 `/usr/lib/postgresql/*/bin`，可在目标服务器上复跑 |

```
已通过! - 失败: 0，通过: 64，已跳过: 0，总计: 64，持续时间: 1 m 36 s
```

## 验收点 → 测试映射

| 验收 | 覆盖 |
| --- | --- |
| 邀请→注册→登录→退出→过期/撤销→重新登录 | `RegistrationTests`（9）、`LoginSessionTests`（10）、`ListingAndDeletionTests.Deleted_report_can_be_uploaded_again` |
| 未邀请不能注册 | `Registration_without_a_valid_invite_is_refused`、`Expired_invite_is_refused`、`Revoked_invite_is_refused`、`Invite_works_only_once` |
| 未登录/过期/越权/超限/畸形/重复被拒且不写库 | `UploadTests`（11）、`UploadQuotaTests`、`ListingAndDeletionTests`、`LoginSessionTests.Expired_session_is_rejected` |
| 服务异常不丢本地记录 | `ClientTests.Offline_upload_keeps_the_report_in_the_outbox`、`Outbox_refuses_to_grow_without_limit` |
| 迁移/备份恢复/回滚实测 | `DatabaseTests`（6）：幂等应用、校验和、整库回滚、部分回滚、pg_dump+恢复比对、最小权限、只监听回环 |
| 5432 不对公网开放 | `DatabaseTests.Database_listens_on_loopback_only`（`listen_addresses=127.0.0.1`） |
| 客户端离线可用 | 应用内 `--self-test-cloud-account`，6/6 项通过（见下） |

## 关键实测证据

- **一次性邀请**：同一邀请码第二次注册返回 403，账号数保持 1；用户名冲突返回 409 且**不作废**该邀请码。
- **会话**：登录返回 43 字符令牌，库中 `octet_length(token_hash)=32`（SHA-256）；退出后上传返回 401；
  手动把 `created_at/expires_at` 前移后请求返回 401。
- **锁定与限速**：连续 5 次错误口令后，即使密码正确也返回 429 `account_locked` 并带 `Retry-After`；
  单独配置 `LoginPerMinute=2` 的实例第 3 次请求返回 429 `rate_limited`。
- **私密数据**：含 `D:\Videos\movie.mkv`、`\\nas\share\...`、`/home/user/...`、`movie.mkv`、
  `api_key=sk-...`、`magnet:?` 的报告全部 422，且 `error_reports` 与磁盘文件数均为 0。
- **畸形请求**：101 条、150 KB 正文、未知字段、错误 `os/arch`、未来时间分别返回 422/413/400/422/422，
  均不写库。
- **重复提交**：同一内容第二次上传返回 409，行数保持 1；用户删除后可重新上传（201）。
- **删除越权**：删除他人日志返回 404，文件与行保持不动。
- **最小权限**：应用角色 `rolsuper=false`；`create table` 与 `select pg_authid` 均报 `42501`。
- **迁移回滚**：整库回滚后 `accounts/error_reports` 表不存在，重新应用后恢复；`--to 0001` 只回滚 0002。
- **备份恢复**：`pg_dump`（custom 未用，测试用 plain）→ 新库 `psql -f` 恢复，`invites` 行数与
  `schema_migrations`（2 条）一致。

## 客户端离线自检

```
映格视频库.exe --self-test-cloud-account <out.json>
exit=0，ok=true，6/6
```

| 检查 | 结果 |
| --- | --- |
| `private_data_masked` | 路径、凭据、磁力链接被替换 |
| `draft_validated_locally` | 脱敏 1 处，同一套校验在本地通过 |
| `plain_http_refused` | `http://reports.example.com` 构造即被拒绝 |
| `outbox_roundtrip` | 落盘→读取→删除，临时目录内完成 |
| `offline_failure_keeps_local_copy` | 连接失败后 `Remaining=1`，本地文件保留 |
| `session_not_in_settings_file` | 令牌不落配置文件 |

自检输出 `realLibraryRead=false`、`networkTargets=["127.0.0.1:<closed-port>"]`，未读取真实视频库、
未连接真实服务器。

## 过程中发现并修复的问题

| 问题 | 处理 |
| --- | --- |
| Npgsql 10 不把命名参数绑定到 `$1` 占位符（协议层 0 参数错误） | 全部 SQL 改为 `@name`，并保留集成测试覆盖 |
| 迁移文件资源名解析错误（`a.b.up.sql` 被当成 4 段） | 修正为 3 段解析，迁移才能真正建表 |
| 应用角色缺少 `usage on schema public`，注册返回 500 | 补授权，并在部署脚本中用 `alter default privileges` 固化 |
| 未知字段未被拒绝（全局 JSON 选项漏配） | `UnmappedMemberHandling.Disallow` 加到全局序列化配置 |
| 会话过期无法用 `update expires_at` 模拟（CHECK 约束） | 测试同时前移 `created_at`，约束保留 |

## 尚未验证

1. Ubuntu 22.04 + PostgreSQL 14 上的真实运行：本机用 PG 18.6 验证，代码限制 ≥14、未使用 15+ 特性，
   但**没有在 PG 14 上跑过**。
2. 真实 HTTPS：`reports.yingvideo.top` 仍 NXDOMAIN，证书未签发，`smoke-local.sh` 未对生产执行。
3. 服务器网络：443 是否转发到 Kestrel、公网能否访问 5432，均未实测。
4. 桌面端联网登录/上传：只做了离线自检，未连真实服务端。
