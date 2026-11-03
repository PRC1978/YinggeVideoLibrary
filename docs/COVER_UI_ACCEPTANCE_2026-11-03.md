# 搜刮封面界面隔离验收

基线：`codex/cover-compare`，本轮没有读取真实视频库、私人媒体或联网下载封面。使用 `--data-dir` 指向 `%TEMP%\yingge-cover-ui-accept-20261103-v2\library`，测试图由程序在该目录内生成。

## 已验证

- `--diagnose-ui-cover` 在真实 WPF 窗口实例上打开标题候选选择、封面比较与放大窗口。候选页有列表、封面和放大按钮；比较页显示两张实际解码的合成封面、各自来源和操作按钮。
- 比较页“用作视频封面”通过 WPF 按钮事件执行后，`config.json` 保存第二张封面的路径；审核记录的来源与状态仍未变。
- 单独启动新进程执行 `--diagnose-ui-cover-reload`，新窗口从同一个隔离库读取到封面选择。
- 外部路径不会作为搜刮缓存封面显示；清除自定义封面时只删除本程序为该视频创建的 `thumbs/<id>-custom.*`，不会删除 `posters/` 内的审核共享缓存。后者也有 `--self-test-features` 的回归断言。
- Release 构建 0 警告、0 错误。界面诊断与重启诊断退出码均为 0，报告 `passed=true`、`realLibraryRead=false`。

证据：`%TEMP%\yingge-cover-ui-accept-20261103-v2\cover-ui.json`、`cover-reload.json`、`cover-candidate-selection.png`、`cover-comparison.png`、`cover-zoom.png`。

## 未验证

- 真实鼠标点击、键盘焦点顺序与高 DPI；当前按钮验收使用 WPF 事件注入。
- 真实网站封面下载、失效链接与限流；本轮完全离线，未点“加载封面”。
- 大尺寸海报和大量候选时的解码、滚动性能。
- 真实视频库升级后的旧封面路径兼容；没有读取真实库。

复现：先用隔离目录执行 `映格视频库.exe --diagnose-ui-cover <报告路径> --data-dir <隔离库>`，再用同一隔离库执行 `--diagnose-ui-cover-reload <重启报告路径> --data-dir <隔离库>`。
