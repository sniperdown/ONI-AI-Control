[h1]ONI AI Control[/h1]
[b]把《缺氧》的殖民地对任意程序开放：一个本机 HTTP 接口，读状态、判断能不能干、下达游戏内操作。[/b]

让脚本、AI 客户端或你自己的工具直接接管殖民地：看建筑与逐格元素、查"这活小人够不够得着"、下挖掘/建造/拆除/门/作息指令——全部走[b]游戏自己的代码路径[/b]，不是重写一套假逻辑。

[h2]它能做什么[/h2]
[list]
[*] [b]读[/b]：殖民地状态、复制人、建筑、逐格元素/质量/温度、环境汇总、区域、世界、间歇泉、告警、事件流、存档列表
[*] [b]判断[/b]：某格"有没有小人到得了"（用游戏自己的 Navigator.CanReach），以及"这个标记会不会真的被干"（workPos 判定）
[*] [b]动手[/b]：挖掘 / 建造 / 拆除 / 撤回 / 清扫 / 收获 / 研究 / 消毒 / 空管道 / 攻击 / 捕捉 / 拖地 / 门 / 作息表 / 铺设管线 / UI 点击 / 截图
[/list]

[h2]普通操作 = 玩家等价[/h2]
[list]
[*] 建造 = 放蓝图 + 复制人搬材料施工（[b]trybuild[/b]）
[*] 拆除 = Deconstructable.QueueDeconstruction
[*] 挖掘 = PlaceDig；门 = QueueStateChange；作息 = Schedule.Assign
[/list]
作弊功能（瞬间建造、直接摧毁、立即改门状态、生成复制人）[b]默认关闭[/b]，必须在配置文件里打开 allowCheats，且游戏处于沙盒/调试模式才放行。

[h2]怎么用（把 mod 交给 AI 的关键一步）[/h2]
这个 mod 本身不带 AI，它只提供接口。要让它「被 AI 操作」，你需要把下面两样东西告诉你的 agent（Claude Code / DSH / Cursor / 自己的脚本都行）：

[b]① 手册文件（就在 mod 目录里）[/b]
[code]Documents\Klei\OxygenNotIncluded\mods\Local\OniAiBridge\AI-GUIDE.md   ← 动手版，先读这个
Documents\Klei\OxygenNotIncluded\mods\Local\OniAiBridge\API.md        ← 逐端点细节[/code]

[b]② token（首次运行自动生成）[/b]
[code]Documents\Klei\OxygenNotIncluded\mods\config\OniAiBridge\settings.json   → 字段 "token"[/code]
所有请求都要带请求头：[code]X-Oni-Token: <token>[/code]，接口地址 [code]http://127.0.0.1:8770[/code]

[h2]直接粘给 AI 的开场白[/h2]
[code]你是《缺氧》(ONI) 殖民地的操作者。我装了 mod「ONI AI Control」，它在 http://127.0.0.1:8770
提供本机 HTTP API。

先做三件事：
1) 读 <mod目录>\AI-GUIDE.md（动手版手册）；需要细节再读同目录 API.md
2) 从 <Documents>\Klei\OxygenNotIncluded\mods\config\OniAiBridge\settings.json 读 "token"
3) 每个请求都带 header：X-Oni-Token: <token>

工作方式：
- 先用 GET /v1/state 看殖民地，再动手
- 下任何标记前先 POST /v1/reach 确认「小人到得了、这活真的有人干」
  （reach.workPos.verdict = will_do 才靠谱）
- 写操作默认关闭；要建造/挖掘/拆解，需要我在游戏里开沙盒或 debug，
  并把 settings.json 里的 allowCheats 改成 true，否则会返回 403
- 做完一件事用 GET /v1/marks 或 /v1/buildings 回读，别假设成功[/code]

[h2]最小验证（确认能通）[/h2]
[code]curl -H "X-Oni-Token: <token>" http://127.0.0.1:8770/v1/state[/code]
PowerShell：
[code]Invoke-RestMethod -Uri http://127.0.0.1:8770/v1/state -Headers @{"X-Oni-Token"="<token>"}[/code]

[h2]典型任务长什么样[/h2]
[list]
[*] [b]看[/b]：GET /v1/state（殖民地概览）、/v1/buildings（建筑）、/v1/map?bbox=…（逐格元素/温度）
[*] [b]判[/b]：POST /v1/reach {"cells":[[x,y],…]} → 每格 reachable + workPos
[*] [b]动[/b]：POST /v1/job {"type":"dig"|"build"|"deconstruct"|"cancel"|"destroy",…}
[*] [b]管[/b]：POST /v1/door（门）、GET/POST /v1/schedule（作息）、POST /v1/move（走位）
[/list]

[b]注意[/b]：没人需要去改 mod 的代码 or 直接调游戏内部——所有操作都走上面这套 HTTP 接口，
底层是游戏自己的代码路径（建造=蓝图+复制人施工，拆=QueueDeconstruction，挖=PlaceDig）。[/b]

[h2]安装[/h2]
[list]
[*] 把 mod 文件夹解压到 Documents\Klei\OxygenNotIncluded\mods\Local\ 下
[*] 游戏内主菜单 → Mods → 启用 ONI AI Control → 重启游戏
[*] 首次运行会在 mods\config\OniAiBridge\settings.json 生成 token，接口地址 http://127.0.0.1:8770
[/list]

[h2]安全[/h2]
[list]
[*] 只监听 127.0.0.1，不对外网开放
[*] 每个请求都要 token（X-Oni-Token 头）
[*] 默认只读：写操作要你自己在配置里解锁
[/list]

[h2]已知边界（诚实清单）[/h2]
[list]
[*] 没有图形界面（本项目只做接口层）
[*] 复制人优先级、任务队列、多小人分工未做
[*] 没有限流
[*] SnapshotTool / DisconnectTool 未接
[/list]

[h2]链接[/h2]
[list]
[*] GitHub（源码、API 文档、变更记录）：[url=https://github.com/sniperdown/ONI-AI-Control]github.com/sniperdown/ONI-AI-Control[/url]
[*] 文档重点：docs/API.md（逐端点）、docs/GAME-MECHANICS.md（游戏机制实证）、docs/RELEASE-1.0-STATUS.md（能做什么/缺什么）
[/list]

[i]本项目是第三方 mod，与 Klei Entertainment 无隶属关系；游戏本体与商标归 Klei 所有。[/i]
