# 插件接口（第一批：搜刮来源）

状态：**已实现第一批**，仅开放「搜刮来源」能力。AI 服务、工具命令、界面主题仍未实现，不要当作现有功能。

## 目录与状态

```
<数据目录>\
  plugins\                      每个插件一个子目录
    com.example.scrape\
      plugin.json               清单（必需）
      ExampleScrapePlugin.exe   入口（清单里的 entry.file）
  plugins.json                  启用状态、权限同意记录、最近错误
  plugins\logs\<插件ID>.log     脱敏后的调用日志（超过 64 KB 轮换）
```

`plugins.json` 与 `config.json` 分开保存：插件状态损坏不会影响视频库配置。权限集合的签名与用户同意时的签名不一致时，插件自动停用并要求重新确认。

启用/停用是**先写盘后改界面**：`plugins.json` 写失败（例如目录只读）时弹窗说明原因，插件状态和界面都不变，不会出现"界面已启用、重启后又变回去"。

## 清单格式（plugin.json）

```json
{
  "schemaVersion": 1,
  "id": "com.example.scrape",
  "name": "示例搜刮来源",
  "version": "1.0.0",
  "apiVersion": 1,
  "author": "示例作者",
  "description": "离线示例，不发网络请求。",
  "homepage": "",
  "capabilities": ["scrape.source"],
  "permissions": {
    "network": ["example.com"],
    "data": ["query.text", "query.code"]
  },
  "entry": { "file": "ExampleScrapePlugin.exe", "arguments": [] },
  "timeoutSeconds": 15
}
```

校验规则（任一不满足即不可用，页面上给出原因）：

| 字段 | 规则 |
| --- | --- |
| `id` | 小写字母、数字、`.` `_` `-`，长度 3–64；**重复 ID 会让两个目录都不可用** |
| `version` | `1.2.3` 形式 |
| `apiVersion` | 必须等于宿主支持的 `1`，否则提示"版本不兼容" |
| `capabilities` | 目前只支持 `scrape.source`；`ai.service` / `tool.command` / `ui.theme` 会显示"本版本尚未开放" |
| `permissions.network` | 只写域名，不含 `http://`、端口或路径，最多 16 个。**这是声明 + 链接校验，不是网络隔离**（见下） |
| `permissions.data` | 只支持 `query.text`、`query.code`、`query.year`；出现其它值即拒绝，避免多传字段 |
| `entry.file` | 必须是插件目录内的相对路径，禁止 `..`、绝对路径、驱动器前缀 |

`timeoutSeconds` 会被夹到 1–120 秒，默认 20 秒。

## 独立进程协议（版本 1）

宿主为**每次调用**启动一个新进程，标准输入输出各一行 JSON，按 `id` 配对。诊断信息写 stderr（最多读取 4 KB，写入日志前脱敏）。

请求：

```json
{"v":1,"id":"a1b2","method":"describe"}
{"v":1,"id":"c3d4","method":"search","params":{"token":"9f…","query":"作品名","kind":"title","limit":8,"year":null}}
```

成功响应：

```json
{"v":1,"id":"c3d4","ok":true,"result":{"candidates":[
  {"title":"作品名","originalTitle":"…","code":"EX-001","year":2024,
   "source":"示例来源","sourceUrl":"https://example.com/work/1",
   "posterUrl":"https://example.com/poster/1.jpg","summary":"…",
   "evidence":"依据…","confidenceHint":55}
]}}
```

失败响应：

```json
{"v":1,"id":"c3d4","ok":false,"error":{"code":"rate_limited","message":"…","retryAfterSeconds":900}}
```

宿主侧约束：

- `params.token` 是每次调用随机生成的不透明串，**插件拿不到视频 ID 和文件路径**，无法自行建立库映射。
- 候选最多 20 条；`title` 必需；`sourceUrl` 必需且必须是 http(s)、域名在清单声明内；含本地路径特征（`C:\`、`C:/`、`\\`、`/`、`file:`）的候选直接丢弃并计数。
- 单行响应超过 256 KB（262 144 字符）判为 `response_too_large` 并终止该次调用。上限是**边读边判**的：读取器逐字符消费，达到上限立即停止，不会先把整行读进内存再检查。自检 `boundedReaderStopsAtCap` 用 8 M 字符的输入证明它只读取了 262 145 个字符。
- 超时、取消或异常都会 `Kill(entireProcessTree)`；自检断言超时后没有残留进程。

错误码：`timeout`、`cancelled`、`plugin_exited`、`response_too_large`、`invalid_response`、`start_failed`、`missing_entry`，以及插件自定义（如 `rate_limited`）。

## 候选如何进入视频库

```
插件进程 → 候选 JSON → 宿主校验（域名/路径/长度）
        → MovieMetadata（DetailFetched=true, DetailIdentityVerified=false）
        → ScrapeIdentityPolicy.Apply（番号冲突、年份冲突、官方目录精确标题）
        → AiReviewRecord（status 强制 pending，decisionSource=plugin）
        → ReviewTransactionStore 事务 + 审核历史
        → 用户在「搜刮」页逐字段预览后通过
```

插件**不能**直接写 `metadata.json`：即使 `confidenceHint=100`，审核记录仍是 `pending`，且可信度被限制在人工阈值 80 以下。已有"人工已通过"的资料不会被插件候选覆盖。

### 同一视频的多个候选

候选本身保存在 `<数据目录>\plugin-candidates.json`（按视频 ID 分组，最多 8 条，按插件 + 来源链接 + 番号 + 标题去重）。审核记录只指向其中一条：

- 首次入队选第 1 条；再次搜索只追加候选，不会把用户已经选过的记录改回第 1 条。
- 审核行在选择器里列出"候选 1/N … N/N"，切换只改写这条待审核记录指向的候选，不改已保存资料。
- 界面显示的入队数量是**实际保存的条数**（去重后），重启后读同一个文件，数字一致。

### 后台队列里的限速与冷却

队列里的插件调用和内置来源走**同一个包装器**（`RunQueueSourceAsync`），来源键是 `plugin:<插件ID>`：

- **请求间隔**：先向 `scrape-source-backoff.json` 预约一个时间片，间隔取 `scrapeSourcePolicies["plugin:<id>"].IntervalSeconds`，未配置时用全局 `scrapeRequestIntervalSeconds`。同一插件两次调用的间隔因此不小于该值；不同插件互不影响。
- **失败冷却**：进程超时、崩溃、返回错误都会记为该来源的一次失败，冷却按 10/20/40 s 递增，连续失败达到 `ConsecutiveFailureLimit`（默认 4）后为 1 小时；插件返回 `rate_limited` 且带 `retryAfterSeconds` 时以它为准。冷却写入磁盘，**重启后仍然有效**。
- **跳过而不是等待**：队列遇到冷却中的插件不会调用它，而是继续尝试其它插件和内置来源；只有所有可用来源都不可用时任务才等待。
- **无结果不算故障**：插件正常返回空列表不写冷却，只表示这次没有候选。
- **重试上限**：只有插件来源且一直无结果时，任务按 10/20/40 s 退避，第 4 次记为失败（可手动重试），不会无限循环。
- **来源键与进程一致**：同一个列表既用于调度也用于执行，执行前还会校验 `plugin:<id>` 能解析回该插件，避免「为 A 预约了时间片却调用了 B」。

示例插件支持在自身目录放一个 `plugin-mode.txt`（内容 `crash` / `empty` / `slow`），强制该副本进入某种失败模式，便于在同一库里放一好一坏两个插件做隔离测试。

## 隐私默认值

- 只发送标题或番号；不发送完整路径、目录名、私有标签、备注、观看状态、API Key、Cookie。
- 子进程环境里会移除名字含 `API_KEY`/`TOKEN`/`SECRET`/`PASSWORD` 的变量和 `YINGGE_DATA_DIR`。
- 日志写入前把 Windows 路径、UNC 路径和 40 字符以上的长串替换为 `<path>` / `<token>`。
- 插件默认**没有**任何网络权限声明时，其返回的链接会被判为无效（`permissions.network` 为空 = 不接受任何链接）。

### 网络权限的真实边界

清单里的 `permissions.network` **只做两件事**：在启用弹窗里说明插件会访问哪些站点，以及校验插件返回的 `sourceUrl`/`posterUrl` 是否落在声明范围内。它**不能**限制子进程联网——插件以当前用户身份运行，可以自行连接任何地址。

因此启用弹窗的措辞是"接受它返回的、位于这些域名下的链接"，而不是"限制它访问这些域名"。若将来要做真正的限制，需要可验证的机制（例如 Windows 防火墙出站规则、AppContainer 或代理强制），并在文档中给出复现方法；当前版本没有实现。

## 构建并安装示例插件

```bash
cd samples/ExampleScrapePlugin
dotnet build -c Release
# 把构建输出复制到数据目录下的插件文件夹
xcopy /E /I bin\Release\net9.0 "%USERPROFILE%\.YinggeVideoLibrary\plugins\com.yingge.example.scrape"
```

或在设置页点「插件 → 打开插件管理」，用「打开插件目录」定位后手动复制。示例插件支持以下查询词触发故障路径：

| 查询包含 | 行为 |
| --- | --- |
| `slow` | 挂起 90 秒（触发宿主超时） |
| `crash` | 立即 `exit 7` |
| `huge` | 输出 400 KB 单行 |
| `ratelimit` | 返回 `rate_limited`，`retryAfterSeconds=900` |
| `pathleak` | 返回带 `C:/…` 的标题（宿主应丢弃） |
| `empty` | 返回空候选 |

## 自检

```bash
映格视频库.exe --self-test-plugins <隔离工作目录> <结果.json> [示例插件构建输出目录]
```

不传第三项时跳过进程类场景，并在 `notExercised` 里写明。自检拒绝把工作目录指向真实库或程序目录。

## 已知限制（下一版处理）

1. 独立进程**不是** Windows 安全沙箱：插件仍以当前用户身份运行，能读写用户可访问的文件。安装第三方插件必须信任来源。
2. 每次调用都冷启动进程（示例插件约 300–400 ms），批量搜刮时开销明显；后续考虑常驻会话与空闲回收。
3. 已接入「搜刮页来源下拉」的单视频手动搜刮；后台队列可通过「允许已启用的搜刮插件参与后台队列」开关参与（默认关，仅标题匹配）。插件失败只记错误、不写「无匹配」，有内置标题来源时会继续尝试内置来源。
4. 插件不能声明设置项，也没有签名/来源校验。
5. 未实现 AI 服务与工具命令能力。
