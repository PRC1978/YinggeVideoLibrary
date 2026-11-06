# PostgreSQL 切换阶段验收（2026-11-06）

本轮只使用 `%TEMP%` 合成视频库与独立 AppId 安装路径。真实视频库、正式安装目录、正式账号及云端服务均未读取或修改；未推送或发布。

## 已验证

- 设置页可排定下次启动转换；转换先拿独占库锁，再导入、逐项校验、创建并校验独立备份，最后写数据库启用标记。旧 `config.json`、索引和资料文件保留。
- 1 部与 3,000 部隔离库的待办转换通过；旧恢复任务存在时转换在数据库创建前被拒绝。转换后的数据库窗口能读取索引和人物页。
- 数据库模式可正常启动，备份计划支持日／周／月、保留轮数与自选地址；待办备份在启动时执行并校验。数据库备份恢复到独立新库，避免覆盖活动库。
- 数据库模式的选择性导入重新显示。旧版备份先与临时数据库导出预览，再由数据库文档保存待办；重启时在临时副本合并，校验后使用数据库恢复事务替换。成功后保留合并快照和恢复前回退包。隔离单视频标签测试验证了预览、排定、导入、重启后资料、索引不变、标记所指备份仍有效；确认后改动数据库的测试验证了拒绝旧预览和取消待办。`--self-test-postgres-selective-import` 为 `passed=true`、`realLibraryRead=false`。
- 同一流程已用 30,000 部合成库复测：`--self-test-postgres-selective-import-scale` 通过，包含预览、实际导入、重启、保留回退、失效预览拒绝和取消；`realLibraryRead=false`。报告在 `%TEMP%/yingge-pg-selective-scale-dadf53b1f7ba4b88942e9f94889dae5d.json`。
- 跨设备指纹迁移包进入数据库模式选择性导入的端到端测试通过：两套独立合成视频目录的路径 ID 不同但文件字节相同；预览按指纹关联，重启导入后标签落在目标视频 ID，来源 ID 不进入目标库，索引和视频字节不变，待办清除。`--self-test-postgres-selective-import-cross-device` 为 `passed=true`、`realLibraryRead=false`，报告在 `%TEMP%/yingge-pg-cross-device-2206afc19f6246268827ac4a7af35c62.json`。
- 最终代码重新编译了独立 AppId 测试安装包（81,306,704 字节），隔离安装后 `postgres/bin/postgres.exe` 与单文件程序存在，安装版数据库窗口诊断 `passed=true`，卸载成功。隔离包曾共用正式版开始菜单快捷方式名；卸载后已核对正式安装程序仍在，并把开始菜单快捷方式恢复为正式安装路径。安装脚本新增 `PackageGroupName`，后续隔离包可用独立分组名称。
- `--self-test-postgres-backup-scale`：1／3,000／30,000 部导入、备份、恢复和数据库运行时写入均通过；30,000 部导入 4,148 ms、备份 2,159 ms、恢复 11,820 ms。结果 `passed=true`、`realLibraryRead=false`，报告在 `%TEMP%/yingge-pg-scale-72b1df080653444989ae93ba2257a055.json`。
- 隔离安装包启用 `BundlePostgres`，独立 AppId `PRC1978.YinggeVideoLibrary.PGIsolatedCutover`；安装、同 AppId 覆盖升级、安装后数据库窗口诊断、卸载均退出码 0。正式安装版未动。
- Release 构建 0 警告 0 错误，文件模式完整自检 `passed=true`、`realLibraryRead=false`。
- 从独立 AppId 安装的程序，在 30 部合成视频库的真实设置窗口点击“下次启动转换为 PostgreSQL”，确认后关闭测试进程并重开。转换待办消失，`storage-postgresql.active` 记载 30 部视频、30 条资料和 PostgreSQL 18.6；界面重开仍显示 30 部。激活标记所指的独立备份目录存在，`database-export-manifest.json` 的 SHA-256 与标记逐字节一致；原 `config.json`、`library-index.json` 和 `metadata.json` 保留。测试安装包使用独立开始菜单分组，没有覆盖正式版快捷方式。
- 转换后的隔离安装程序再次执行 `--diagnose-postgres-window`，结果 `passed=true`、`videos=30`、`realLibraryRead=false`，报告在 `%TEMP%/yingge-pg-ui-after-5e532adc5564462582289f9c821e2711.json`。测试安装包随后卸载，退出码 0；正式版开始菜单快捷方式仍指向正式安装目录且目标存在。

## 打包问题与修正

首次隔离包虽然带有 PostgreSQL，却漏了 WPF 原生组件，安装版启动窗口时抛出 `DllNotFoundException`。项目现已固化 `IncludeNativeLibrariesForSelfExtract=true`，安装脚本默认带 PostgreSQL；单文件发布程序再次通过隔离数据库备份自检。修正参数后的隔离安装包此前通过安装后窗口诊断。`dotnet publish` 在本机从头构建曾无进度停滞；可先 Release 构建，再用 `publish --no-build --no-restore` 完成打包。

## 未完成的发布门槛

1. 选择性导入的数据库合并、资料变化拒绝、取消、30,000 部规模与跨设备指纹包已通过自动诊断；其真实鼠标点击尚未实测。当前仅接受旧版 `backup-manifest.json` 包；数据库完整备份继续走完整恢复入口。成功导入留下的合并快照尚无自动保留轮数策略。
2. 安装版设置页排定转换和重启成功路径已实测；中断后恢复、失败提示和完整审核／搜刮／人物／插件操作仍需验证。测试窗口点击一条视频的收藏操作后，旧 `config.json` 与索引 SHA-256 未变化；该单次点击尚不足以证明所有编辑入口持久化正确。
3. 当前仍是 1.6.8 的开发代码；正式版本号、Release 安装包和更新资产均未准备。当前未推送、未发布，也未迁移真实库。
4. PostgreSQL 主版本升级方案仍待实现；不得以新主版本运行旧数据库目录。

这份结果是阶段验收，不代表真实用户库已迁移，也不是发布许可。
