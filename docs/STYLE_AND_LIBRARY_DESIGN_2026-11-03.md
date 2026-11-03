# 可选外观与本地资料设计

## 视觉取舍

- 新拟物派：作为可选主题。浅灰同色底、圆角设置卡与双向柔和阴影；正文仍使用清楚的深色文字。参考 https://www.stylekit.top/zh/styles/neumorphism 与 https://www.stylekit.top/zh/guides/neumorphism 。阴影只用于设置卡等少量静态元素，不放进虚拟化海报卡片。
- 量化线场：作为可选纸白蓝主题，线条理念用于 v3 播放图标。图标由本项目绘制的竖向离散线段组成，不复制 StyleKit 示例品牌图形。参考 https://www.stylekit.top/zh/styles/quantized-line-field 。
- 纸感档案：暖纸色与低装饰分隔，便于人物、作品资料长时间阅读。参考 https://www.stylekit.top/zh/styles 与 https://www.stylekit.top/zh/recipes 。
- 柔和界面风：与新拟物派的使用场景相近，先通过现有新拟物主题覆盖；以后只有在交互与可读性有显著区别时再单列主题。
- 玻璃拟态：适合更新对话框等有限浮层，不作为全局主题；持续模糊海报列表会增加绘制成本并影响文字识别。参考 https://www.stylekit.top/zh/guides/glassmorphism 。

现有固定侧栏继续保留。仪表盘使用可换行的统计卡，审核页保留独立操作区域；不把品牌展示页的整屏滚动、视差或持续指针特效搬进视频库。参考 https://www.stylekit.top/zh/styles?type=layout&sort=favorites 和 https://www.stylekit.top/zh/templates/shadcn-analytics 。

字体借鉴 https://www.stylekit.top/zh/resources?tab=typography 的“标题与正文分工”方法：正文统一使用已安装的 Microsoft YaHei UI，纸感档案标题用宋体，线场主题的拉丁标题优先 Segoe UI，仪表盘数字用 Segoe UI。不下载网页字体；中文正文在所有主题保持同一套清晰字形。

## 动画档位

设置 → 外观：流畅优先（装饰动画 0 毫秒）、平衡（短过渡）、动画优先（较长过渡）。只作用于页面入场与设置卡展开。海报滚动、图形分类图布局、数据统计不因档位变化而重新计算。参考 https://www.stylekit.top/zh/animations 的入场、悬停、过渡分类。

## 本地资料

人物维基摘要保存在 `role-profiles.json`，含页面 ID、标题、链接、摘要、获取时间。原手写生平不被覆盖。同名人物各自成档，作品关联由用户确认并保存为手工关系；页面链接只是资料来源，不作为作品身份的证明。

AVScan 只做半自动交接：明确标记日本且无番号的视频才有入口，本地截帧交由用户选择上传，网站结果需人工输入番号并走原有搜刮审核。程序不静默上传图片或视频。

## 隔离验收

- Release 构建：0 警告、0 错误。
- `--self-test-features`：隔离数据目录，验证维基资料重启持久化、手工作品关系及 AVScan 入口条件。
- `--diagnose-ui-theme`：8 主题、3 档窗口、主要页面截图；分类图节点和连线完整。截图保存于隔离测试目录。
