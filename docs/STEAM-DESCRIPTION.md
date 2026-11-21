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
