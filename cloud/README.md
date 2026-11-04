# 映格云端账号与错误日志服务

邀请制的账号服务，只做两件事：**发账号**、**收错误日志**。它和本机视频库是两个独立系统，
不做隐式同步，也不接收任何媒体信息。

- 服务端：ASP.NET Core 9 Minimal API + PostgreSQL（≥14，Ubuntu 22.04 自带 14）
- 客户端库：`net9.0` 类库，供桌面端登录、脱敏预览、离线上传队列
- 数据：邀请、账号、会话、上传审计、日志元数据；日志正文存 Web 根目录之外的独立目录

## 不接收

视频路径、封面、文件名、API Key、完整配置、视频库索引、媒体文件。服务端按固定字段白名单二次
校验；仍含路径/文件名/分享链接/凭据的整份拒绝，不落盘。

## 目录

```
cloud/
  src/Yingge.Cloud.Accounts/     API 服务 + 运维 CLI（同一可执行文件）
  src/Yingge.Cloud.Client/       客户端库（登录、会话、脱敏、离线上传队列）
  src/Yingge.Cloud.Protocol/     共享契约、校验、私密数据扫描
  tests/Yingge.Cloud.Tests/      64 项隔离集成/单元测试（真实 PostgreSQL）
  Migrations/ (在 Accounts 内)   *_up.sql / *_down.sql，嵌入程序集
  deploy/                        Ubuntu 22.04：systemd、nginx、安装/备份/恢复/冒烟脚本
  docs/                          API、部署、安全、测试结果
```

## 本地开发

```bash
# 1) PostgreSQL：任何 ≥14 的实例，一个空的测试库即可
export YINGGE_TEST_PG_BIN=/path/to/postgresql/bin     # 测试会自建临时集群
dotnet test cloud/tests/Yingge.Cloud.Tests            # 64 项

# 2) 手工运行服务
export Database__AppConnectionString='Host=127.0.0.1;Database=yingge_cloud;Username=yingge_cloud_app;Password=...'
export Storage__RootPath=/var/lib/yingge-cloud/reports
export Security__RequireHttps=false                   # 仅本机调试
dotnet run --project cloud/src/Yingge.Cloud.Accounts

# 3) 运维 CLI（邀请、停用、清理）
dotnet run --project cloud/src/Yingge.Cloud.Accounts -- \
  admin invite-create --note local --admin-db 'Host=...;Username=yingge_cloud_owner;Password=...'
```

服务端不含任何默认口令或连接串；缺少配置会直接拒绝启动。

## 桌面端

`YinggeVideoLibrary.csproj` 引用 `cloud/src/Yingge.Cloud.Client`。设置 → 数据与备份 → 「云端账号
（错误日志）」提供登录、邀请码注册、上传预览、补传、查看/删除自己的上传。会话令牌写入 Windows
凭据管理器；上传失败只改变状态行，不影响视频库。

离线自检（不读真实视频库、不连真实服务器）：

```bash
bin/Debug/net9.0-windows/映格视频库.exe --self-test-cloud-account %TEMP%\cloud.json
```

## 文档

| 文档 | 内容 |
| --- | --- |
| [docs/API.md](docs/API.md) | 端点、请求/响应、错误码、限额 |
| [docs/DEPLOYMENT_UBUNTU.md](docs/DEPLOYMENT_UBUNTU.md) | Ubuntu 22.04 部署、证书、备份、升级回滚 |
| [docs/SECURITY.md](docs/SECURITY.md) | 数据边界、凭据、限速、残余风险、事故处置 |
| [docs/TEST_RESULTS.md](docs/TEST_RESULTS.md) | 64 项测试、备份恢复演练、未验证事项 |

## 状态

| 项 | 状态 |
| --- | --- |
| 代码与迁移 | 完成，`dotnet build` 0 警告 0 错误 |
| 隔离 PostgreSQL 测试 | 64/64 通过（PG 18.6，Windows） |
| 客户端离线自检 | 6/6 通过 |
| Ubuntu 22.04 + PG 14 实机 | **未执行** |
| 域名 / 证书 / 公网端口 | **未验证**（`reports.yingvideo.top` 仍 NXDOMAIN） |
| 生产部署 | **未执行**（需用户明确授权） |
