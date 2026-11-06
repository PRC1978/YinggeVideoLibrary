# 本地 PostgreSQL 正常运行切换门槛

状态：截至 2026-11-06，数据库导入、私有运行时、备份和恢复已在隔离库验证；**正常桌面程序仍是文件模式**。`App.xaml.cs` 对 `storage-postgresql.active` 的拦截必须保留，直到以下写入路径全部迁移并完成端到端测试。

## 必须同批切换的权威数据

| 数据 | 当前正常程序入口 | 已有数据库适配器 | 剩余工作 |
| --- | --- | --- | --- |
| 配置、标签、分类、收藏、审核记录 | `MainWindow.LoadConfig` / `SaveConfig`、`MainWindow.ReviewWorkflow` | `PostgresLibraryConfigStore`、`PostgresReviewTransactionStore` | 主窗口已增加数据库会话的配置、审核事务、历史和保存视图分支；需完成全部直接文件入口及真实窗口验收。同步设置保存仍会等待数据库，后续改为异步 |
| 视频索引与缺失状态 | `MainWindow.SaveIndexAsync`、增量扫描和缺失清理 | `PostgresLibraryIndexStore`、`PostgresLibraryRuntimeSession.SaveScannedIndexAsync`、`PostgresMissingCleanupStore` | 数据库会话下的缺失清理和撤销已用同一事务写索引及历史；激活前拒绝未完成的旧清理。仍需真实窗口操作及大库验收。隔离库已验证空扫描不覆盖不同根目录的旧索引 |
| 搜刮资料 | `MetadataStore.Load/Save` 与审核事务 | `PostgresLibraryMetadataStore`、`PostgresReviewTransactionStore` | 所有资料写入、审核历史和撤销同事务 |
| 搜刮队列与来源冷却 | `MainWindow.ScrapeQueue` / `TitleScrapeQueue` | `ScrapeQueueBridge`、`PostgresScrapeQueueStore`、`PostgresScrapeSourceBackoffStore`、`PostgresScrapeWorkerLease` | 主窗口队列的加入、取消、重试、轮询和来源冷却已通过模式入口；仍需数据库模式的真实窗口操作、重启恢复与故障注入验收。诊断探针保持文件模式 |
| 人物与关系 | `RoleProfileStore` 文件 | `PostgresRoleProfileStore` | 自动搜刮、手工编辑、作品关联与撤销接线 |
| 插件、候选、AI 作业与用户状态 | 多个独立 JSON 文件 | `PostgresLibraryDocumentStore`、`PostgresUserStateStore` | 逐个消除正常程序的文件权威写入；只允许图片缓存继续落文件 |
| 备份和恢复 | 设置页 `LibraryBackupService` | `PostgresLibraryBackup` / `PostgresLibraryRestore` | 数据库模式的预览、进度、离线恢复入口与回退副本保留策略 |

## 激活顺序

1. 在隔离库中启动私有 PostgreSQL，验证数据库版本、配置、索引、资料和全部文档；旧文件保留作回退副本。`PostgresLibraryRuntimeSession` 已完成配置、索引、资料、队列、审核历史和人物档案的重启读取与校验，并验证核心数据及独立资料的数据库写入、版本冲突拒绝和专用事务门禁；1 部和 3,000 部隔离库通过。主窗口已接入可选数据库会话，但正常启动仍不传入会话。
2. 一次性切换上述全部正常写入入口，禁止数据库模式运行任何旧文件日志或 JSON 权威写入；启动恢复先于窗口展示。
3. 用 30,000 部合成库测试扫描、搜刮、审核、撤销、人物、插件、AI、备份、恢复及重启；对每项做写入失败注入和崩溃恢复。
4. 用隔离安装路径验证升级和卸载。所有检查通过后才启用 `storage-postgresql.active`，修改版本号并发布。

数据库模式和文件模式不得在同一视频库上同时写入。单独放开 `App.xaml.cs` 的标记拦截不构成切换。
