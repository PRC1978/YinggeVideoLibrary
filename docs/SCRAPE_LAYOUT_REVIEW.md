# 1.6.0 搜刮页整理与隔离目录修复

基线：`1306406`；工作树 `codex/release-1.6.0-verify`。未安装、推送或发布。

## 页面调整

- 搜刮页先显示批量入队、单部完整资料搜刮、入队预览和队列状态。
- 队列为空时隐藏不可用的“继续轮询／重试失败／取消待处理”；有待处理或失败项时恢复相应按钮。
- 保留完整资料来源下拉与结果提示；筛选和跳过条件、来源与凭据折叠展示，审核队列保持可见。
- 删除展开筛选时重复的标题，缩短说明文字。所有原控件及事件处理保留，没有删除来源、筛选、队列或审核功能。

## 隔离目录问题

首次用全新的 `--data-dir` 临时目录启动窗口时，页面意外显示 16,076 部视频。查明 `TryMigrateLegacyCaches` 对空的重定向目录仍读取并复制旧缓存，导致隔离目录混入私人索引。发现后立即关闭窗口，未启动扫描、搜刮或打开视频；原始库未写入。这个临时目录中留下了迁入的配置、索引与缩略图缓存，后续验收**不再使用它**。

现在重定向数据目录直接跳过旧缓存迁移。以新的空临时目录复测：启动显示 0 部视频，搜刮页离屏诊断退出码 0；目录内没有 `config.json`、`library-index.json` 或迁移标记。功能自检新增 `redirectedLibraryDoesNotImportLegacyCache=true`，同时 `realLibraryRead=false`。常规未重定向目录的旧缓存迁移逻辑未改。

## 验证

- Release 构建：0 警告、0 错误。
- `--diagnose-ui-startup ... --scrape --data-dir <隔离目录>`：退出码 0，`scrape ui verified`。
- `--self-test-features ... --data-dir <隔离目录>`：`passed=true`、`redirectedLibraryDoesNotImportLegacyCache=true`、`realLibraryRead=false`。
- 真窗口 1224×768：确认空库为 0 部、搜刮页默认布局及“筛选与跳过条件”“来源与高级设置”能展开。截图：`SCRAPE_LAYOUT_1.6.0.png`。

125%／150% DPI、大库完整备份恢复仍未验证；本轮没有改变发布清单的结论。
