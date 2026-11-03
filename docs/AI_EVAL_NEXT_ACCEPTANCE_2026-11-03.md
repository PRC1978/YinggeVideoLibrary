# 搜刮准确率离线评测扩展验收

基线 `216618c`，独立工作树 `codex/scrape-eval-next`。本轮没有读取真实视频库、联网搜刮或调用付费模型。

## 已实现

- 增加人工标注样本模板；未填写 `goldSameWork` 的行会被拒绝。样本文件应保存在仓库外。
- 报告按固定来源组给出批准数、误批准数、精确率、负例误批准率及各自 95% Wilson 区间；未知来源归入 `Other / plugin`。
- 单例报告只保留匿名 ID；包含路径的 ID 被拒绝。报告还统计来源字段冲突样本及其中被自动批准的数量。
- 阈值建议要求人工样本正负各至少 30 条，且两项误判率的 95% 区间上限均不超过 1%；小样本零错误不会触发建议。

## 证据

- `dotnet build -c Release`：0 警告、0 错误。
- `--self-test-features`：退出码 0，`passed=true`；原始报告 `%TEMP%\yingge-ai-eval-next-features-final.json`。新增测试覆盖区间、来源脱敏、路径 ID 拒绝、冲突阻断和大/小人工样本阈值建议。
- 合成样本 `--self-test-ai-scrape-eval`：退出码 0，6 例，2 个来源组，报告无 `localTitle`、`sourceUrl` 或合成作品标题；原始报告 `%TEMP%\yingge-ai-eval-next-report.json`。

## 仍未验证

真实来源和真实模型的匹配准确率、实际账单、人工标注质量。当前只有合成样本，不能据此调生产阈值。
