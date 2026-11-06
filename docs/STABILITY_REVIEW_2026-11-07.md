# 映格视频库稳定性复核（2026-11-07）

基线：`codex/ui-report-integration`，代码提交 `c576ad3`。本记录针对《映格视频库_独立复核_2026-11-06.md》指出的 1.6.11 稳定性阻断项。未安装、推送或发布；未读取真实视频库。

## 已修复

| 复核项 | 修改 | 证据 |
| --- | --- | --- |
| 首实例建窗前第二次启动误报占用 | 锁冲突后等待最多 10 秒唤回管道；旧进程退出时继续开库 | `artifacts/c576ad3-startup.json` 中 `delayedActivationPipeIsWaitedFor=true` |
| 关闭到托盘拦截关机/注销 | 系统会话结束与恢复程序主动退出时绕过托盘拦截 | 代码检查；真实关机/注销未做 |
| 强杀后遗留 PostgreSQL | 持有独占锁后识别并停止遗留数据库进程，再启动私有集群 | `artifacts/c576ad3-pg-full.json` 中 `orphanPostgresRecoveredOnFirstLaunch=true` |
| 退出时停库卡住或异常穿出 | 停库进程等待上限 15 秒；`OnExit` 捕获并记录异常且释放应用租约 | `artifacts/c576ad3-pg-full.json` 中 `stopped=true`；真实托盘退出未做 |
| PostgreSQL 标记丢失时回落文件库 | 数据目录已有 `postgres-data` 且标记缺失/损坏时阻断文件模式 | `artifacts/c576ad3-features.json`，`passed=true` |
| 云端服务未上线却显示登录入口 | 设置页隐藏云端登录/上传区 | `artifacts/c576ad3-settings.json`，`settings ui verified` |

## 提交后验证

- `dotnet build -c Release -p:EnableSourceLink=false -o artifacts/review-c576ad3`：0 警告、0 错误。
- `--self-test-bundled-postgres`：退出码 0，`passed=true`、`backupMatrixVerified=true`、`backupFailureInjectionVerified=true`、`realLibraryRead=false`。1 部和 3,000 部两组的 `RuntimeWritesVerified` 均为 true。
- `--self-test-features`：退出码 0、`passed=true`、`realLibraryRead=false`。
- `--self-test-startup-update`：退出码 0、`passed=true`。
- `--diagnose-ui-startup --settings`：退出码 0，隔离目录 `artifacts/perf-accuracy/fixture`。

完整数据库验收曾因自检中“来源限速到期时间”的比较基准错误出现假红。`a9833e8` 加入逐项诊断，`c576ad3` 修正为与首次预留时刻比较；最终两种规模所有运行时子检查均通过。失败记录 `artifacts/a9833e8-pg-full.json` 保留供复核。

## 仍需验证和后续处理

- 真实双击启动、系统关机/注销、托盘退出后的立即重启、更新退出时的停库：目前只有代码与隔离自检证据，不应写成真实窗口验收通过。
- 复核报告的 P1 项尚未全部处理：文件模式租约契约、应用内打开数据库恢复产物、分类检查器横向裁切，以及其余真实页面截图。
- 真实视频库迁移和恢复未运行。云端账号服务未上线，不应对用户开放上传入口。
- 当前工作仍是稳定性修复分支；未改版本号或打发布标签。发布前需在独立测试环境完成真实退出/重启路径与安装升级验收。
