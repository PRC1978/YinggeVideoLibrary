# 桌面端集成状态（2026-11-04）

集成分支 `codex/desktop-cloud-integration` 从 `codex/postgresql-local` 建立，依次合入 `codex/local-person-tag-discovery` 和 `codex/cloud-accounts-postgresql`。原工作树和分支未改动。

## 已集成

- 本地人物按姓名查找公开资料、已有同名档案提示、本地标签建议。
- 云端邀请制账号客户端和设置入口。只允许登录后通过 HTTPS 上传经过本机脱敏预览的错误日志。
- 本地 PostgreSQL 的隔离导入器及诊断入口；普通视频库读写仍使用 JSON。
- 冲突仅在项目依赖：同时保留 Npgsql 与云端客户端项目引用。云端服务端源码不参与桌面程序编译。

## 集成修正

- Windows 会话凭据与离线待上传队列按 HTTPS 服务器来源分开；更换服务器地址不会带上旧服务器令牌或待上传日志。
- 云端离线自检只检查序列化结果，不读取真实数据目录中的 `cloud-account.json`。
- 设置页移除无需账号的旧 SSH 上传入口，保留本机加密导出与解密查看。联网上传只能走云端账号入口。

## 验证

- Release 构建：0 警告、0 错误。
- 完整功能自检：`passed=true`、`realLibraryRead=false`、`localTagReuseRequiresExactEvidence=true`。
- 云端客户端离线自检：7/7，`realLibraryRead=false`。
- 隔离库的设置页离屏诊断：退出码 0，阶段为 `settings ui verified`。

## 尚未完成

- 本地 PostgreSQL 尚未成为日常读写后端，不能声称迁移完成。
- 云端服务未在 Ubuntu 22.04 / PostgreSQL 14 上实测，尚未部署；邀请登录和真实 HTTPS 上传未做端到端验收。
- 云端分支文档及服务端问题地址仍需从错拼域名统一为 `yingevideo.top` / `reports.yingevideo.top`；由云端服务线处理后再合入。
- 未安装、未推送、未发布。
