# ONI AI Bridge —— 设计文档（v0.1 草案）

> 目标：把《缺氧》(Oxygen Not Included) 的**可读状态**与**可执行操作**通过一个本机 HTTP API 暴露出来，
> 让任意程序（DSH 或别人写的客户端）能"看见并操控"殖民地。公开发布到 Steam 创意工坊 + GitHub 开源。
>
> 状态标记：**✅ 已核实**（本文件末尾附核实命令与原始输出）· **⚠️ 待验证**（有合理依据但没实测）· **❌ 未做**

---

## 0. 一句话结论

技术路线已定并且**每个关键点都在本机查过**：`netstandard2.1` 类库 + 游戏自带的 `0Harmony 2.4.2.0`，
入口是 `KMod.UserMod2`，配置用 `mod.yaml` + `mod_info.yaml`（UTF-8 **无 BOM**）。
API 侧自建 HTTP 服务（`TcpListener`，零第三方依赖）+ **主线程泵**，请求永远不直接碰 Unity 对象。

---

## 1. 技术路线

### 1.1 本机环境（✅ 全部实测）

| 事实 | 值 | 证据 |
|---|---|---|
| 游戏根目录 | `<ONI_INSTALL>`（本机实测存在；文档/代码里一律用占位符，不落真实路径） | 目录存在 |
| Unity 版本 | **6000.3.5f2**（Unity 6） | `Player.log` 首部 `Initialize engine version: 6000.3.5f2 (3fa8bc678cb0)`；`UnityPlayer.dll` FileVersion `6000.3.5.4171964` |
| 自带 Harmony | **`0Harmony.dll 2.4.2.0`** 就在 `OxygenNotIncluded_Data\Managed\` | 目录列举 + 程序集版本读取 |
| 一个**现役**第三方 mod 的目标框架 | `.NETStandard,Version=v2.1` | FastSave 3.13.0.0（`mod_info.yaml` 声明 `minimumSupportedBuild: 719533`）的 `TargetFrameworkAttribute` |
| 该类库引用了哪些程序集 | `netstandard 2.1`、`Assembly-CSharp 0.0.0.0`、`Assembly-CSharp-firstpass`、`0Harmony 2.4.2.0`、`Newtonsoft.Json 7.0.0.0`、`UnityEngine.*` | 同上，AssemblyRefs 全表 |
| 游戏自带 `Newtonsoft.Json` | 7.0.0.0（`Managed\Newtonsoft.Json.dll`） | 目录列举 |
| mod 入口 API 所在程序集 | **`Assembly-CSharp.dll`**，命名空间 `KMod` | `KMod.UserMod2` / `KMod.Mod` / `KMod.Manager` / `KMod.Local` / `KMod.Steam` / `KMod.DLLLoader` / `KMod.KModHeader` / `KMod.KModUtil` 等均为该 dll 的 TypeDef |

### 1.2 结论：我们怎么做

```
netstandard2.1 类库  OniAiBridge.dll
  引用：Assembly-CSharp.dll、Assembly-CSharp-firstpass.dll、0Harmony.dll、
        UnityEngine.CoreModule.dll、Newtonsoft.Json.dll   ← 全部直接引用游戏 Managed 目录里的原件
  不 vendored、不复制 dll 进仓库（.gitignore 排除），构建脚本从 `--game-dir` 参数取路径
```

* **入口：`KMod.UserMod2`**（✅ 类型存在；⚠️ 其 `OnLoad(Harmony)` 签名按官方 wiki 与社区文档，
  本轮未反编译方法体核实，骨架阶段第一件事就是跑一次确认）。
  - 找不到 `UserMod2` 实现时游戏会自行 `Harmony.PatchAll()`；我们**显式实现**，以便控制
    "补丁失败要软失败（记日志、不抛）"和"启动 HTTP 服务"的时机。
* **Harmony 从游戏目录引用，不打包**（✅ 游戏自带 2.4.2.0）：避免与别的 mod 的 Harmony 版本打架。
* **不修改游戏目录里的任何文件**：mod 只作为独立文件夹被游戏加载（见 §6.1）。
* **零第三方运行时依赖**：HTTP/1.1 + SSE 自己用 `System.Net.Sockets.TcpListener` 写
  （`HttpListener` 在 netstandard2.1 里不属于可用面，也不该依赖）；JSON 用游戏自带的 Newtonsoft 7，
  或退化为手写序列化（见 §3.7 决策点）。

### 1.3 线程模型（本设计最关键的一条）

Unity/Mono 的对象**只能在主线程访问**。后台线程读 `Components.*` / 任何 `UnityEngine.Object`
会表现为"偶发崩溃、随机 NullReference、存档莫名损坏"——这类 bug 极难查。

```
[网络线程]  TcpListener.Accept → 解析 HTTP → 生成请求对象 → 投递到主线程队列 → 等 TaskCompletionSource
[主线程]    Harmony Postfix(Game.Update 或等价每帧钩子) → 排水队列 → 执行读/写 → 回填结果
```

* **HTTP 线程永远不触碰游戏对象**；只碰"已序列化好的不可变快照"和"命令 DTO"。
* 快照由主线程**定时采集**（默认 0.5 s 一次，可配），HTTP 请求读缓存 ⇒ 请求永不阻塞主线程。
* 命令有**超时**（默认 5 s）且**单线程串行执行**（避免同一帧内并发改状态）。
* 主线程找不到（未载入殖民地）⇒ 返回 `503 NOT_READY`，不排队。

---

## 1.4 引用游戏程序集的坑：`Action` / `DateTime` 会被遮蔽（✅ 编译期实测）

`Assembly-CSharp.dll` **自己定义了全局命名空间里的 `Action` 和 `DateTime`** 两个类型
（`Assembly-CSharp-firstpass.dll` 也有 `Action`）。后果：mod 代码里写 `using System;` 之后直接用
`Action`、`DateTime`，解析到的是**游戏的同名类型**，不是 BCL 的。

实测症状（第一次编译就撞上）：

```
error CS1660: 无法将 lambda 表达式转换为类型"Action"，原因是它不是委托类型
error CS0117: "DateTime"未包含"Now"的定义
error CS0149: 应输入方法名称
```

**对策**：mod 里所有 BCL 类型一律写全名 —— `System.Action`、`System.DateTime`、
`System.Exception`、`System.Collections.Concurrent.ConcurrentQueue<System.Action>` …
（`ManualResetEventSlim` 这类目前没发现冲突，但同一文件的风格保持一致）。

> 这条要写进 README 的"给贡献者"一节：**任何 `error CS1660/CS0117` 看起来"莫名奇妙"的报错，
> 先怀疑类型名被游戏程序集遮蔽**。

---

## 2. 信息层：字段清单与"去冗余"

### 2.1 原则

1. **只给决策需要的**：不整个对象图。任何列表默认只给"有问题的 / 变化了的 / 前 N 条"。
2. **一次拿够**：`GET /v1/state` 一个请求返回全部廉价字段（< 32 KB）。
3. **贵的东西按需**：地图数据、全建筑列表、全物品列表默认不下发，要显式拉且有降采样参数。
4. **变化可订阅**：SSE 只推 diff（字段级），带 `seq`，断线可用 `Last-Event-ID` 续。
5. **单位与语义写进文档**（卡路里原始值 ÷1000 = kcal 这类坑，见 §2.4）。

### 2.2 字段表（含取数 API 与核实状态）

| 组 | 字段 | 取数 API | 状态 |
|---|---|---|---|
| 时钟 | `cycleRaw` / `cycle`(=`+1`，界面同义) / `timeSinceStartOfCycle` / `timePlayed` / `frame` / `isNight` | `GameClock.Instance` 的 `cycle`、`timeSinceStartOfCycle`、`timePlayed`、`frame`、`isNight` | ✅ 字段名与语义已由内存读取实测确认（`cycle+1` 见 oni-mem README 第三轮） |
| 时钟 | `isPaused` / `speed` | `SpeedControlScreen.Instance`（✅ 类型存在；⚠️ 成员名待确认） | ⚠️ |
| 复制人 | `id` / `name` / `gender` / `nameStringKey` | `Components.LiveMinionIdentities` → `MinionIdentity.name`、`gender`、`nameStringKey`（✅ 类型存在；⚠️ `Components.LiveMinionIdentities` 成员名待确认） | ⚠️ |
| 复制人 | 属性：`Stress` `Calories` `Temperature` `Bladder` `Stamina` `Breath` `HitPoints` `Decor` `RadiationBalance` + 5 种病菌 | `MinionIdentity.GetComponent<Modifiers>().amounts` → `AmountInstance.modifier.Id` / `.value`（✅ 运行时可读链路实测；`Amount`/`AmountInstance` 均在 `Klei.AI`） | ✅ 链路 / ⚠️ 代码里成员名 |
| 复制人 | 当前任务一句话（"在挖掘/送水/休息"） | `ChoreDriver.GetCurrentChore()` → `Chore` 的 `choreType.Id` + 目标对象 | ⚠️ 待验证 |
| 复制人 | 位置（格坐标） | `Navigator.GetGridPosition()` / `Grid.PosToCell(GameObject)` | ⚠️ |
| 复制人 | 状态位：`idle` `hasErrand` `isSleeping` `isSuffocating` | 由上面组合推导（不引入新 API） | — |
| 世界 | `worldId` / `worldName`(prefab id) / `worldTypeKey` / `isStartWorld` / `size` | `ClusterManager.Instance` + `WorldContainer`（✅ 字段与取值由内存实测确认） | ✅ |
| 资源 | 元素存量 kg（只给真元素，过滤分类合计标签） | `WorldInventory.GetAmount(tag)` / `accessibleAmounts`（✅ 结构确认；⚠️ 公开方法名待确认） | ✅ 数据 / ⚠️ API |
| 资源 | 变化超过阈值才报 | 主线程快照 diff（§2.3） | — |
| 建筑 | **只给异常的**：断电/缺料/过热/被禁用/闲置 + 按类别计数 | `Components.BuildingCompletes` → `Operational` / `BuildingEnabledButton` / `Storage` | ⚠️ |
| 研究 | 当前研究项 + 进度 + 队列 | `Research.Instance` / `TechInstance`（✅ 类型存在；成员待确认） | ⚠️ |
| 通知 | 顶部告警前 N 条（默认 5） | `NotificationScreen` / `Notifier` | ⚠️ |
| 地图 | 逐格元素/温度/压力 | `Grid` + `ElementLoader` / `Sim` 数据 | ⚠️ 按需，默认关 |
| 文本 | 本地化名（元素名/建筑名/世界名） | `STRINGS.*`（编译期常量类，直接引用即可，无需反射） | ⚠️ 键名待确认 |

> **重要**：上表 ✅ 的是**数据语义与取值**（来自只读内存实测），⚠️ 的是**C# 成员名/签名**（来自类型存在性
> + 社区文档）。骨架阶段第一个 milestone 就是把这些 ⚠️ 逐个"编译期验证"——编不过就叫它错，
> 绝不带着猜测往下写。

### 2.3 去冗余的具体算法

1. **字段级 diff**：主线程每 0.5 s 采一次，与上一次比；`/v1/state` 里每个对象带 `changed:[...]`。
2. **资源阈值**：`|Δkg| < max(1 kg, 0.5%)` 不报；只推 `elements` 变化项。首次连接给全量。
3. **列表截断**：`dupes` 全给（≤ 数十人，成本低）；`buildings` 默认只给异常项 + `counts{类别:数量}`；
   `alerts` 默认 5 条；任何列表都带 `total` 与 `truncated:true`。
4. **地图不默认下发**：`/v1/map` 必须带 `bbox` 与 `stride`，硬上限（默认 64×64 格 / 次），
   超限返回 `400 BBOX_TOO_LARGE`。默认只给"元素 id + 温度 + 质量"三通道。
5. **不重复字符串**：世界名/建筑 prefab id/元素 id 只在 `catalog` 里给一次"id → 显示名"映射
   （`/v1/catalog/*`，可缓存），`state` 里只出现 id。

### 2.4 单位与已知坑（来自上一阶段的实测，必须写进 API 文档）

* `Calories` 原始值 ÷ **1000** = 界面 kcal（实测：`FoodInfo.CaloriesPerUnit` 对混合浆果派 = 4,200,000，
  而界面显示 4,200 kcal）。
* `cycle` 与顶栏数字差 1：顶栏走 `GameUtil.GetCurrentCycle()` = `GameClock.cycle + 1`。
* **暂停时**模拟驱动字段（`timeSinceStartOfCycle`、属性值）冻结，渲染驱动字段（`frame`、`timePlayed`）照涨。
  API 里显式给 `isPaused`，并把"连续两次 tSOC 不变"作为兜底判据。
* 温度是开尔文（310.15 K = 37 °C）。

---

## 3. API 草案（v1）

### 3.1 通用约定

* 绑定 **`127.0.0.1`**（默认端口 `8770`，被占用则顺延并在 `/v1/meta` 报告实际端口）。
* **鉴权**：启动时生成 32 字节随机 token，写 `mods\config\<staticID>\settings.json`（0600 尽量）；
  请求带 `X-Oni-Token: <token>`（或 `?token=`）。缺/错 → `401`。
  *不做"无鉴权"默认值*，即使只听 loopback——本机其它进程也能连。
* `Content-Type: application/json; charset=utf-8`；所有响应体统一包一层：

```json
{ "apiVersion": "1.0", "ok": true, "data": { ... }, "error": null, "serverTime": "2026-11-14T15:40:00+08:00", "seq": 1234 }
```

```json
{ "apiVersion": "1.0", "ok": false, "data": null,
  "error": { "code": "CHEATS_DISABLED", "message": "allowCheats is false", "details": {"op":"addResource"} } }
```

### 3.2 端点表（v1）

| 方法 | 路径 | 作用 | 备注 |
|---|---|---|---|
| GET | `/v1/meta` | 服务端元信息 | 版本、`apiVersion`、游戏 build、`capabilities`（哪些 op 当前可用/作弊是否开）、端口、限流 |
| GET | `/v1/state` | **主入口**：一次拿够的状态 | 查询参数 `parts=`（默认 `colony,dupes,elements,alerts`）、`since=`（只给 seq 之后的变更） |
| GET | `/v1/events` | **SSE 推送** | `text/event-stream`；事件名 `snapshot` / `diff` / `heartbeat`，带 `id:` = seq |
| GET | `/v1/dupes/{id}` | 单个复制人详情 | 含属性、任务、位置、状态位 |
| GET | `/v1/map` | 地图降采样 | 必须给 `bbox`；默认关，`allowMap` 开关 |
| GET | `/v1/catalog/{kind}` | 静态数据：`buildings` / `elements` / `techs` / `items` | 可长期缓存，带 `etag` |
| POST | `/v1/command` | 执行操作 | 见 §4；`{op, args, requestId}` |
| GET | `/v1/commands` | 当前可用的 op 清单 + 参数 schema + 是否需要作弊档 | 让客户端自发现，不硬编码 |
| GET | `/v1/health` | 存活 / 是否载入殖民地 | 供守护脚本用 |

### 3.3 `/v1/state` 响应骨架（字段一旦发布不改名）

```json
{
  "apiVersion": "1.0", "ok": true, "seq": 1234,
  "data": {
    "colony": {
      "cycleRaw": 4, "cycle": 5, "timeSinceStartOfCycle": 132.5, "timePlayed": 6875.2,
      "isPaused": true, "speed": 0, "isNight": false,
      "duplicants": 4, "worldId": 0,
      "world": { "name": "expansion1::worlds/VanillaSandstoneDefault", "typeKey": "STRINGS.WORLDS.VANILLASANDSTONEDEFAULT.NAME", "size": [240, 380], "isStartWorld": true }
    },
    "dupes": [
      {
        "id": "d1", "name": "卡塔莉娜", "gender": "FEMALE",
        "amounts": { "Calories": 3063737.5, "CaloriesKcal": 3064, "Stress": 0, "Temperature": 309.954,
                     "Bladder": 49.5, "Stamina": 69.1, "Breath": 100, "HitPoints": 100 },
        "chore": { "id": "Dig", "text": "挖掘" },
        "cell": 12345, "flags": ["hasErrand"],
        "changed": ["amounts.Calories"]
      }
    ],
    "elements": { "SandStone": 35025.6, "Dirt": 32872.6, "Water": 10954.6, "Cuprite": 5459.2 },
    "alerts": [ { "id": "no_oxygen", "severity": "high", "text": "氧气不足", "cell": 4321 } ],
    "truncated": { "elements": false, "alerts": true }
  }
}
```

* 派生字段（如 `CaloriesKcal`）用**后缀标明推导**，且文档写明公式；原始值永远保留。
* 名字本地化：默认给游戏当前语言的显示名，同时给 `id`；`?lang=en` 预留（⚠️ 依赖 STRINGS 表，待验证）。

### 3.4 `/v1/command` 请求/响应

```json
{ "op": "setPriority", "args": { "targetId": "b12", "priority": 7 }, "requestId": "abc-1" }
```

```json
{ "apiVersion": "1.0", "ok": true, "data": { "op": "setPriority", "applied": true, "seq": 1235, "requestId": "abc-1" }, "error": null }
```

### 3.5 错误码（固定枚举，只增不改语义）

| HTTP | `error.code` | 含义 |
|---|---|---|
| 400 | `BAD_REQUEST` | JSON 坏了 / 缺字段 |
| 400 | `INVALID_ARG` | 参数越界（格坐标非法、kg ≤ 0…） |
| 400 | `BBOX_TOO_LARGE` | 地图请求超上限 |
| 401 | `UNAUTHORIZED` | token 缺失/错误 |
| 403 | `CHEATS_DISABLED` | 该 op 属作弊档且 `allowCheats=false` |
| 403 | `DEBUG_MODE_OFF` | 该 op 需要游戏 Debug 模式，但 `DebugHandler.enabled == false` |
| 403 | `CAPABILITY_UNAVAILABLE` | 该 op 依赖的调试入口在本游戏版本里没找到（`/v1/commands` 会如实标注） |
| 404 | `NOT_FOUND` | 端点不存在 |
| 404 | `NOT_FOUND_TARGET` | 指定的建筑/复制人 id 不存在 |
| 409 | `CONFLICT` | 状态不允许（如殖民地在加载中） |
| 429 | `RATE_LIMITED` | 超出每分钟请求数（默认 600/min） |
| 500 | `INTERNAL` | 未预期异常（带 `details.traceId`，日志里有堆栈） |
| 503 | `NOT_READY` | 未载入殖民地 / 主线程泵未就绪 |
| 504 | `TIMEOUT` | 命令在主线程 5 s 内未完成 |

### 3.6 版本与兼容策略

* `apiVersion` **major.minor**：加字段/加端点/加 op = minor；删/改语义/改类型 = major。
* 响应里永远带 `apiVersion`；客户端可据此拒绝不兼容。
* 每个 op 的 schema 通过 `/v1/commands` 自描述；删除 op 先标 `deprecated:true` 至少 2 个小版本。
* `CHANGELOG.md` 里破坏性变更单独一节，写"迁移方式"。

### 3.7 待决策点（写进骨架阶段验证）

| # | 问题 | 备选 | 倾向 |
|---|---|---|---|
| D1 | JSON 库 | 游戏自带 `Newtonsoft.Json 7.0.0.0` / 手写 | 先用自带 Newtonsoft（零依赖、能解析入参）；若 7.0 在老 Mono 上有坑就退化为手写 |
| D2 | 主线程钩子 | Harmony patch `Game.Update` postfix / 自己的 `KMonoBehaviour` 挂在某个 prefab | patch postfix（简单可靠）；确认 `Game.Update` 是实例方法还是静态（⚠️） |
| D3 | 推送通道 | SSE / WebSocket | **SSE**（在本机 HTTP 上手写成本最低，够用；客户端更容易实现） |
| D4 | 端口被占用 | 顺延 / 直接报错 | 顺延 8770→8779，`/v1/meta` 报告实际端口，日志同时打印 |

---

## 4. 操作清单

分类：**A = 官方公开 API 能做**；**B = 需要 Harmony 改私有方法/字段**；**C = 有崩溃/坏档风险**。
所有 op 都要在 `/v1/commands` 里自描述，并在实现时逐个"能失败就失败"（不吞异常、不猜）。

### 4.1 普通操作（默认可用）

| op | 作用 | 类 | 备注 |
|---|---|---|---|
| `pause` / `resume` / `setSpeed(1\|2\|3)` | 暂停/继续/调速 | A | `SpeedControlScreen.Instance`（⚠️ 成员名） |
| `setPriority(targetId, priority 1..9)` | 改建筑/任务的优先级 | A | `Prioritizable`（✅ 类型存在） |
| `setBuildingEnabled(buildingId, bool)` | 开关建筑 | A/B | `BuildingEnabledButton`（✅ 存在；开关方法可能私有） |
| `setDoorState(doorId, open\|auto\|locked)` | 门状态 | A/B | ⚠️ |
| `queueResearch(techId)` / `cancelResearch` | 研究队列 | A | `Research.Instance`（✅ 类型） |
| `assignSkillPoint(dupeId, skillId)` | 加点 | A/B | ⚠️ |
| `setConsumableAllowed(itemId, bool)` / `setFoodRestriction` | 食物权限 | A | ⚠️ |
| `orderDig(cells[])` / `orderBuild(prefabId, cell, material?)` / `cancelOrder(cells[])` | 挖掘/建造/取消 | B（部分） | 直接构造 Chore 比模拟 UI 工具稳；⚠️ |
| `setSchedule(dupeId, blockIndex, scheduleBlock)` | 日程 | A/B | ⚠️ |
| `renameDuplicant(dupeId, name)` | 改名 | B | 私有 setter |
| `alertDismiss(alertId)` | 关掉某条通知 | A/B | ⚠️ |
| `save(name?)` / `load(saveName)` | 存/读档 | A | **高风险**：读档会重载整个殖民地，期间 API 返回 503；建议只暴露 `save`，`load` 放作弊档 |

### 4.2 作弊操作 = **调度器**，不是实现者（✅ 本轮按用户指示改）

**核心转向**：不自己写作弊逻辑，而是**调用游戏自带的 Debug 工具**。
理由（写进 README 也要说）：

* 游戏更新时，我们只依赖 `DebugHandler` / `DebugPaintElementScreen` 这些**官方调试入口**，
  不复制"瞬建=扣材料+加进度"这类私有业务逻辑 ⇒ **不会被游戏改逻辑打死**，维护成本从"重写一套"降到"改个调用"。
* 行为与玩家按 Backspace 调试菜单**完全一致** ⇒ 出问题可复现、可对照，不用怀疑"是不是 mod 写歪了"。
* 用户原话正是这个意思：*"如果 ai 搞错了，人工干预搞点'天意的大手'就行了"* —— 人机共用同一套工具。

**两道闸门，都满足才执行**：

1. 配置项 `allowCheats`（默认 **false**）；
2. 游戏内 Debug 模式已开：`DebugHandler.enabled == true`
   （公开 getter；由 `DebugHandler.SetDebugEnabled(bool)` 或玩家自己的调试快捷键打开）。
   未开时返回 `403 DEBUG_MODE_OFF`，并在 `message` 里告诉调用方怎么开。

### 4.2.1 内置 Debug 能力清单（✅ = 本机从 `Assembly-CSharp.dll` 元数据核实到具体成员名；⚠️ = 存在但入口是私有/需 IL 分析）

| # | 能力 | 游戏侧入口（已核实的成员名） | 可调用性 | API op |
|---|---|---|---|---|
| 1 | 开/关 Debug 模式 | `DebugHandler.SetDebugEnabled(bool)` 静态 public；`get_enabled` public | ✅ | `debugMode(enable)` |
| 2 | **瞬建** | 字段 `DebugHandler.InstantBuildMode`（静态 public）+ `ToggleInstantBuildMode()` | ✅ | `debugInstantBuild(bool)` |
| 3 | **无敌** | 字段 `DebugHandler.InvincibleMode`（静态 public） | ✅ | `debugInvincible(bool)` |
| 4 | **揭开地图迷雾** | 字段 `DebugHandler.RevealFogOfWar`（静态 public） | ✅ | `debugRevealFog(bool)` |
| 5 | 关掉通知 | `DebugHandler.ToggleDisableNotifications()` + 属性 `NotificationsDisabled` | ✅ | `debugNotifications(bool)` |
| 6 | 截图模式 / 隐藏 UI / 时间流逝 | 字段 `ScreenshotMode` `HideUI` `TimelapseMode` + `ToggleScreenshotMode()` `SetTimelapseMode(...)` | ✅ | `debugVisual(mode,bool)` |
| 7 | **刷元素 / 刷质量 / 刷温度 / 刷病菌** | `DebugPaintElementScreen.Instance`（静态 public）；公共字段 `element` `mass` `temperature` `diseaseCount` `diseaseIdx` `paintElement` `paintMass` `paintTemperature` `paintDisease` `affectCells` `affectBuildings`；公共方法 `OnChangeMass` `OnChangeTemperature` `OnDiseaseCountChange` | ✅ 状态可写 | `debugPaint(cells, elementId, mass, temperature, disease)` |
| 8 | 执行"画/填充/生成/存储" | 私有 `OnClickPaint` `OnClickFill` `OnClickSpawn` `OnClickStore` `OnSelectElement` `OnSelectDisease` | ⚠️ 反射调用 | 同上（内部走这几个） |
| 9 | **刷子拖拽（连续涂抹）** | `DebugTool.Instance`（静态 public）+ `Activate(...)`(2 重载) + `OnDragTool(...)`；静态 `DoReplaceSubstance`、`DeconstructCell`、`DoStoreSubstance` | ✅/⚠️ 签名待确认 | `debugBrush(fromCell,toCell...)` |
| 10 | 生成复制人 | `DebugHandler.SpawnMinion`（**实例、非公开**） | ⚠️ 反射 | `debugSpawnMinion(...)` |
| 11 | **跳转 / 传送** | `DebugGoToSubState.GoToCursor()`（public）；`DebugGoToMonitor`；`CreatureDebugGoToMonitor.ClearTargetCell()`（静态非公开） | ⚠️ 部分 | `debugGoTo(cell)` |
| 12 | 门禁/权限改写 | `DevToolAccessControl.Instance`（静态 public）；私有 `SetDoorAccessControl` `UpdateMinionAccess` | ⚠️ 反射 | ❌ 暂不开放 |
| 13 | 研究解锁 / POI 科技 / 解锁 ID | `DevToolResearchDebugger`、`DevToolPOITechUnlocks`、`DevToolUnlockedIds`（`unlockIdToAdd` `filterForUnlockIds`）—— 均**只有 `RenderTo`**，实际动作在局部 lambda 里 | ❌ 无可调方法 | ❌ 待 IL 分析（**不编**） |
| 14 | 调试菜单**完整**能力清单 | 全部条目定义在 `DebugHandler.UpdateUI`（静态非公开）内部 | ⚠️ 需 IL dump 才能逐条枚举 | — |

> ⚠️ 两个易错点，写代码前必须记住：
> * `DEBUG_TOOLS` / `DEVTOOLS` / `DEBUGGOTO` **不是工具类**，只是本地化字符串常量表
>   （`ENTER_TEXT` `DEBUG_ACTIVE` `TITLE` `WARNING` `NAME` `TOOLTIP` …）—— 别拿它们当入口。
> * 上表 ⚠️ 项的**方法签名没有核实**（本轮只读了成员名，没解签名/IL）。骨架阶段对每个 ⚠️ 项：
>   先编译期验证签名，编不过就**从 `/v1/commands` 里拿掉**，能力表里降级为"未支持"。

### 4.2.2 风险（必须写进文档与设置页）

* 这是**官方调试工具**，不是给正常游玩用的：`InstantBuildMode` 会**跳过材料与建造校验**，
  刷元素会直接改模拟格子 ⇒ **存档一致性/成就/统计都可能被破坏**，且**不可撤销**。
* 建议流程：另存一个测试档 → 打开 `allowCheats` 与游戏 Debug 模式 → 做事 → 用正常档继续玩。
* mod 侧不做"自动备份存档"这类侵入动作；README 教用户手动备份 `save_files\`。

---

## 5. 风险与回滚

### 5.1 风险最大的三件事

1. **主线程约束被破坏** → 偶发崩溃、存档损坏。缓解：§1.3 的泵模型 + 启动自检（后台线程若触碰
   Unity 对象，`UnityEngine.Object` 的线程检查会抛异常，把它当致命错误记日志并拒绝该 op）。
2. **内置 Debug 工具本身会破坏存档一致性**（瞬建跳过材料校验、刷元素直接改格子，不可撤销）。
   缓解：`allowCheats` +「游戏 Debug 模式已开」双闸门、默认关、文档与设置页写明"官方调试工具，非正常游玩"、
   建议只在测试档使用。**注意**：因为作弊层是"调度官方工具"，我们在实现上已经**没有**原来那种
   "自己写坏逻辑"的风险；剩下的风险全部来自官方工具本身的语义。
3. **游戏更新导致 API/Harmony 补丁失效**。缓解：所有补丁体 `try/catch` 包住并记日志；
   `/v1/meta.capabilities` 报告"实测可用"的能力集；mod_info 的 `minimumSupportedBuild` + 语义化版本
   + CHANGELOG 写清"针对哪个游戏 build 测试过"。

### 5.2 安装 / 卸载 / 回滚

* **安装**：把 `OniAiBridge/` 文件夹放进本地 mod 目录（`%USERPROFILE%\Documents\Klei\OxygenNotIncluded\mods\Local\`
  —— ⚠️ **本机当前没有 `Local` 目录**，这是唯一还没被实机样本证明的一环；首次安装后用
  `Player.log` 里是否出现该 mod 的加载行来验证）。
* **卸载**：删掉那个文件夹即可。mod 不改游戏目录、不改原存档格式。
  *只有作弊 op 会改变存档内容*，那属于用户主动操作。
* **它自己写什么**：只在 `mods\config\<staticID>\` 下写 `settings.json`（token、端口、开关）；
  可选 `bridge.log`（建议默认关，避免写盘）。
* **崩溃恢复**：删 mod 文件夹 → 游戏回到原状；若存档已被作弊改坏，用游戏自带备份
  （`save_files\` 下 `*.sav` 与 `RetiredColonies\`）或 cloud 存档恢复。
* **不用 mod 时的等价物**：本仓库外面那套只读内存工具（`shared/tmp/oni-mem`）不受影响，
  可作为"mod 挂了也能看状态"的兜底。

---

## 6. 创意工坊 + 开源工程规范

### 6.1 创意工坊（✅ 本机已安装的真实 mod 样本为准）

目录结构（一个 mod 一个文件夹，等号后是必填性）：

```
OniAiBridge\
  mod.yaml            (推荐)
  mod_info.yaml       (必填)
  OniAiBridge.dll     (DLL mod 必填)
  preview.png         (上传创意工坊需要)
  README.md / CHANGELOG.md / LICENSE   (可选，随包发布对用户友好)
```

`mod.yaml`（文件名与字段名已核实）：

```yaml
title: "ONI AI Bridge"
description: "Expose colony state and commands over a local HTTP API."
staticID: "OniAiBridge"        # 全局唯一；发布后不可改（Harmony 实例名也用它）
```

`mod_info.yaml`（字段已核实）：

```yaml
supportedContent: ALL          # ALL | VANILLA_ID | EXPANSION1_ID
minimumSupportedBuild: <TBD>   # ← 见下，本轮没拿到确切 build 号
version: 0.1.0                 # 创意工坊更新必须递增
APIVersion: 2                  # 带 dll 的 mod 必须为 2（Harmony 2）
```

* **UTF-8 无 BOM** —— 这不是建议：本机 `mods\Steam\2946564795\mod.yaml` 带了 BOM，
  `Player.log` 里就出现
  `Recoverable parse error ... Found a property '﻿le' on a type 'KMod.KModHeader'`（BOM 粘到 `title` 前面）。
  ✅ 实测证据。构建脚本必须强制 `new UTF8Encoding(false)`。
* **`minimumSupportedBuild` 必须填整数**（✅ 已核实）：解析 `mod_info.yaml` 的类型是
  **`KMod.PackagedModInfo`**，其 `minimumSupportedBuild` / `lastWorkingBuild` 字段都是 **int32**
  （`APIVersion` 也是 int32，`version` 是 string）。
  * 主菜单显示的是 **`U59-744825-S`**：`U59` = 更新号、`744825` = build、`-S` = 渠道。
  * 因此 `mod_info.yaml` 填 **`minimumSupportedBuild: 744825`**（只填 build 数字）。
    直接填整串 `U59-744825-S` 会被 YAML→int32 解析挡掉（同一个解析器已经在本机 BOM 的 mod.yaml 上
    报过 `Found a property ...` 这类可恢复错误）。
  * 同时也填了 `lastWorkingBuild: 744825`（该字段确实存在，语义="最后验证过的 build"）。
  * **装上去后仍要核对**：游戏自己怎么报 build，看 `Player.log` 与 `/v1/meta.data.game.applicationVersion`
    （后者是 `UnityEngine.Application.version` 的原样输出）。若它报的是 `U59-744825-S` 而这里填了整数，
    两者都不矛盾（一个是显示串、一个是整数），差异记在本节与附录。
* **preview.png 尺寸没有硬性要求**：本机 10 个已发布 mod 实测为
  `192x192, 360x360, 512x512 ×3, 900x900, 925x547, 1530x1530, 1920x1080`。
  **推荐 512×512**（多数派，且 mod 列表/工坊都显示得下）。
* 上传动作在游戏内完成（Mods 界面上传；⚠️ 确切按钮位置待首次实操确认），更新时 `version` 必须递增。

### 6.2 开源工程

```
oni-mod/
  README.md            陌生人从零：装 mod → 开 API → 第一个 curl → FAQ
  LICENSE              MIT
  CHANGELOG.md         语义化版本；破坏性变更单独一节 + 迁移方式
  DESIGN.md            本文件
  docs/API.md          **独立 API 文档**（不依赖 DSH 背景知识）
  docs/ARCHITECTURE.md 线程模型 / 主线程泵 / 数据流（给想改代码的人）
  src/OniAiBridge/     类库
  src/OniAiBridge/OniAiBridge.csproj   netstandard2.1；游戏 dll 路径来自 MSBuild 属性或环境变量
  build.ps1            构建 + 打包出可直接放进 mods\Local 的文件夹
  .github/workflows/build.yml   CI：只编译（不跑游戏）
  .gitignore
```

**`.gitignore` 必须有**：`bin/` `obj/` `*.user` `.vs/` `mods/` `dist/` `*.log` `*.sav` `preview.xcf`
以及任何本地路径文件。

**脱敏规则（提交前逐条查）**：

* 不出现 `F:\SteamLibrary\...`、`C:\Users\<真实用户名>\...`；路径一律来自 `--game-dir` 参数/环境变量
  `ONI_GAME_DIR`，文档里写占位符 `<ONI_INSTALL_DIR>`、`%USERPROFILE%`。
* 不出现 QQ 号、内网 IP、个人 token。仓库里出现的唯一 token 是**运行时随机生成**的那个（不落仓库）。
* 提交前跑一次自检脚本（`tools/check-sanitize.ps1`）：正则扫 `[A-Z]:\\Users\\`、`SteamLibrary`、
  真实用户名、`\d{7,11}`（QQ/手机号长度）等。

**License**：MIT（用户未反对即 MIT），`LICENSE` + `README` 末尾各写一次。

---

## 7. 里程碑（每步都有"验收判据"，不达标不往下走）

| # | 内容 | 验收判据 |
|---|---|---|
| M0 | **骨架**：mod 能加载 + 日志打印 + `/v1/meta` 返回 | `Player.log` 出现自己的加载行；`curl http://127.0.0.1:8770/v1/meta` 返回 `apiVersion` |
| M1 | 时钟 + 复制人属性（复用上一阶段已验证的字段语义） | `/v1/state` 的 `cycle` 与界面顶栏一致；4 个复制人属性与游戏面板一致 |
| M2 | 资源 + 告警 + 建筑异常项 | 元素存量与资源面板一致（误差 < 1%）；告警条数与界面一致 |
| M3 | 命令层上半：`pause/resume/setSpeed/setPriority` | 调用后界面立即变化；失败路径都有明确错误码 |
| M4 | SSE 推送 + diff + `since=` | 客户端断线重连不丢状态；空闲时只有心跳 |
| M5 | 作弊调度层（默认关）+ `/v1/commands` 自描述 | `allowCheats=false` 或 Debug 模式未开时返回 `403`；打开后每个 op 都调到**游戏自己的** Debug 入口（不是我们复写的逻辑）；⚠️ 项逐个降级或标注未支持 |
| M6 | 文档 + 打包 + 工坊上传 + GitHub 开源 | 陌生人照 README 一次跑通；仓库脱敏自检通过 |

---

## 8. 附录：本轮核实用的命令与证据

```powershell
# 1) Unity 版本
Get-Content "$env:USERPROFILE\AppData\LocalLow\Klei\Oxygen Not Included\Player.log" -TotalCount 6
#   → Initialize engine version: 6000.3.5f2 (3fa8bc678cb0)
(Get-Item '<ONI_INSTALL>\UnityPlayer.dll').VersionInfo.FileVersion   # → 6000.3.5.4171964

# 2) 游戏自带 Harmony / Newtonsoft
Get-ChildItem '<ONI_INSTALL>\OxygenNotIncluded_Data\Managed' -Filter *Harmony*
#   → 0Harmony.dll 2.4.2.0

# 3) 现役 mod 的目标框架与引用（tools/inspect-dll.ps1 读 metadata，不加载不执行）
pwsh -File tools\inspect-dll.ps1 -Path "<mods>\Steam\1867707267\FastSave.dll" -Mode summary
#   → TargetFramework: .NETStandard,Version=v2.1
#   → refs: netstandard 2.1.0.0 / Assembly-CSharp 0.0.0.0 / 0Harmony 2.4.2.0 / Newtonsoft.Json 7.0.0.0 ...

# 4) mod 入口 API 存在性（TypeDef 列举）
pwsh -File tools\inspect-dll.ps1 -Path '<ONI_INSTALL>\OxygenNotIncluded_Data\Managed\Assembly-CSharp.dll' -Mode typedefs -Filter 'KMod.'
#   → KMod.UserMod2 / KMod.Mod / KMod.Manager / KMod.Local / KMod.Steam / KMod.DLLLoader / KMod.KModHeader ...

# 5) mod.yaml / mod_info.yaml 真实样本
Get-Content "<mods>\Steam\1867707267\mod.yaml"      -Raw
Get-Content "<mods>\Steam\1867707267\mod_info.yaml" -Raw

# 6) BOM 会炸 mod.yaml（实测）
Select-String -Path "$env:USERPROFILE\AppData\LocalLow\Klei\Oxygen Not Included\Player.log" -Pattern 'KModHeader'

# 7) 已发布 mod 的 preview.png 尺寸分布
#    192x192, 360x360, 512x512×3, 900x900, 925x547, 1530x1530, 1920x1080

# 8) 内置 Debug 工具族（成员名，本轮核实来源）
pwsh -File tools\inspect-dll.ps1 -Path '<ONI_INSTALL>\...\Assembly-CSharp.dll' -Mode members  -Filter 'DebugHandler'
#   → F static public: InstantBuildMode / InvincibleMode / RevealFogOfWar / ScreenshotMode / HideUI
#                      TimelapseMode / DebugPathFinding / DebugCellInfo / SelectInEditor / DebugNextCall
#   → M static public: SetDebugEnabled / ToggleInstantBuildMode / ToggleDisableNotifications / GetMouseCell
#   → M inst nonpub  : SpawnMinion / OnKeyDown

pwsh -File tools\inspect-dll.ps1 -Path '<ONI_INSTALL>\...\Assembly-CSharp.dll' -Mode members  -Filter 'DebugPaintElementScreen'
#   → F inst public: element / mass / temperature / diseaseCount / diseaseIdx / paintElement / paintMass
#                    paintTemperature / paintDisease / affectCells / affectBuildings
#   → M inst public: OnChangeMass / OnChangeTemperature / OnDiseaseCountChange / SampleCell
#   → M inst nonpub: OnClickPaint / OnClickFill / OnClickSpawn / OnClickStore / OnSelectElement

pwsh -File tools\inspect-dll.ps1 -Path '<ONI_INSTALL>\...\Assembly-CSharp.dll' -Mode members  -Filter 'DebugTool'
#   → F static public Instance；M public Activate / OnDragTool / DoReplaceSubstance / DeconstructCell / DoStoreSubstance

pwsh -File tools\inspect-dll.ps1 -Path '<ONI_INSTALL>\...\Assembly-CSharp.dll' -Mode members  -Filter '*DevTool*'
#   → DevToolResearchDebugger / DevToolPOITechUnlocks 只有 RenderTo ⇒ 无可调入口（未编造 API）
```

`<mods>` = `%USERPROFILE%\Documents\Klei\OxygenNotIncluded\mods`
`<ONI_INSTALL>` = 游戏安装目录（本机为 Steam 库路径，文档里一律用占位符）

**本轮明确没解决的问题**（不许当成已完成）：

1. ~~游戏 build 号~~ ✅ 已解决：`744825`（主菜单显示 `U59-744825-S`）；`minimumSupportedBuild` 是 int32，
   只能填数字，见 §6.1。
2. `mods\Local\` 是否就是本地 mod 目录 —— ⚠️ 目录当前不存在；**首次安装后用 `Player.log` 验证**
   （看有没有扫到 `OniAiBridge`），安装动作本身需用户确认。
3. `UserMod2.OnLoad(Harmony)` 的确切签名 —— ✅ 编译期已验证（`public override void OnLoad(Harmony)` 可用）；
   但**运行时**是否被调用、`Game.Update` postfix 是否真在跑，仍未验证（要装上去才知道）。
4. ONI 是否有通用"mod 设置"界面（决定 `allowCheats` 怎么让用户改） —— ⚠️ 待查；M0 先只做配置文件。

## 9. 交付状态

### M0（已验证，2026-11-14 15:47 实机）
| 项 | 状态 |
|---|---|
| mod 加载 | ✅ `Player.log` 出现 `[OniAiBridge] OnLoad 开始 …`、`Harmony 补丁已应用`、`listening on http://127.0.0.1:8770/v1/meta` |
| `mods\Local` 是本地 mod 目录 | ✅ **证实**：装到 `…\Documents\Klei\OxygenNotIncluded\mods\Local\OniAiBridge\` 后游戏扫到了它（此前本机无该目录，是本次新建） |
| 鉴权 | ✅ 无 token → `401`；带 token → `200` |
| 泵 | ✅ `pumpReady:true`、`colonyLoaded:true` |
| 游戏版本 | ✅ `buildText:"U59-744825-S"` / `buildVersion:744825`，与游戏日志 `Build: U59-744825-S (release)` 一致 |
| `game.applicationVersion` 的坑 | ⚠️ 返回 `"1.0"` —— 那是 Unity 的 bundleVersion，ONI 里恒为 1.0，**不是游戏版本**；M1 已改掉（见 CHANGELOG 0.2.0 的破坏性变更） |

### M1（代码完成、编译通过，**运行时未验证**）
| 项 | 状态 |
|---|---|
| `GET /v1/state` | ✅ 代码就位：colony / dupes / elements 三块，每块带 `ts` |
| API 存在性 | ✅ 编译期逐个验证（成员清单见 CHANGELOG 0.2.0）；`GetAmount` 真实签名是 `(Tag, bool includeRelatedWorlds)` |
| 主线程铁律 | ✅ 采集整体走 `MainThread.Invoke`，超 3000 ms → `504 TIMEOUT`；HTTP 线程只拿字符串 |
| 元素去冗余 | ✅ kg ≥ 1、按量降序、最多 40 条、截断标 `truncated` |
| 产物 | ✅ `dist\OniAiBridge\OniAiBridge.dll` 26112 B，v0.2.0，`netstandard2.1`，refs 增加 `Assembly-CSharp-firstpass`（用了 `Tag`） |
| **运行时正确性** | ❌ 未验证 —— dll 不能热重载，等用户退出游戏后一次性安装再验 |

### M2（代码完成、编译通过，**运行时未验证**）
| 项 | 状态 |
|---|---|
| `/v1/events`（SSE） | ✅ 四类事件（snapshot/diff/alert/heartbeat），`id:`=seq，`Last-Event-ID`/`?since=` 续传，保留 256 条，最多 8 订阅者 |
| `/v1/commands` + `POST /v1/command` | ✅ 仅普通操作 4 个：`pause`/`resume`/`setSpeed`/`setChorePriority` |
| `/v1/meta` 设计问题 | ✅ 进程级信息 OnLoad 缓存（主菜单可答）；`colonyLoaded`/`debugModeOn` 未知给 `false` |
| `world.size` | ✅ `Vector2I` 字段确为 `x`/`y`（firstpass 实测）；`WorldSize` 返回 0×0 时输出 `null`+`sizeNote` |
| 产物 | ✅ `dist\OniAiBridge\OniAiBridge.dll` 42496 B，v0.3.0 |
| 作弊层 | ❌ M3（调度内置 Debug 工具），本轮**未碰** |
| 指派挖掘/建造 | ❌ 需走 `DigTool`/`BuildTool` UI 工具层，M2 不做，`/v1/commands` 里也不列出 |

### M2.1（代码完成、编译通过，**运行时未验证**）
| 项 | 状态 |
|---|---|
| `setSpeed` 语义 | ✅ 内部 0 基 → 对外 1/2/3。证据：IL（`speed = value & 3` + switch 选 slow/medium/fast，越界走 default）+ 界面实测（`speed:2` = 三倍速） |
| 回读校验 | ✅ `setSpeed`→`GetSpeed()+1`、`pause/resume`→`IsPaused`；不一致回 `409 NOT_APPLIED` |
| `colony.speed` | ✅ 改为对外 1/2/3（破坏性），原始值另给 `speedRaw` |
| 产物 | ✅ `dist\OniAiBridge\OniAiBridge.dll` 43008 B，v0.4.0，stage `M2.1` |

### M3（代码完成、编译通过，**运行时未验证**）
| 项 | 状态 |
|---|---|
| 作弊层定位 | ✅ **只调度**游戏内置 Debug 工具（不复制任何作弊业务逻辑）⇒ 游戏更新不会打死我们 |
| op | ✅ `debug` / `setInstantBuild` / `setInvincible` / `revealFog` / `spawnElement` / `teleport` |
| 双闸门 | ✅ `allowCheats` + `DebugHandler.enabled`，缺一回 `403 CHEATS_DISABLED` / `403 DEBUG_MODE_OFF` |
| 回读校验 | ✅ 每个 op 执行后回读（字段/格子元素/复制人位置），不一致回 `409 NOT_APPLIED` |
| 参数校验 | ✅ 元素不存在 404、格子越界/质量非正/温度非法 400、复制人不存在 404，不静默降级 |
| 产物 | ✅ `dist\OniAiBridge\OniAiBridge.dll` 51200 B，v0.5.0，stage `M3` |

M3 期间被编译器/元数据证实的三个细节（都写进 CHANGELOG 了）：
`DebugPaintElementScreen.element` 是 **`SimHashes` 枚举**（不是 Tag）；`DebugTool.DoReplaceSubstance(int cell)` 只吃格子号；
`Navigator` 没有 Teleport API，`DebugGoToSubState.GoToCursor` 也不接受坐标 ⇒ 传送只能 Transform 定位（实验性）。
另外：`DebugPaintElementScreen.Instance` 是**每会话**的（关游戏即失效），所以 `spawnElement` 每次开游戏后
都要先在游戏里手动打开一次调试面板的元素绘制页。

### M4.1（代码完成、编译通过，**运行时未验证**）
| 项 | 状态 |
|---|---|
| 设置页入口 | ✅ A（反射设 `manage_tooltip`+`on_managed` → 官方「管理」按钮）主，失败自动降级 B（Harmony postfix `ModsScreen.BuildDisplay` 加按钮）；都失败 ⇒ `settingsUi.available=false` + 原因 |
| 设置页本体 | ✅ `BridgeOptionsScreen : KModalScreen`，**只读展示** + 关闭按钮（编辑留 M4.2） |
| 闸门 | ✅ `allowCheats && (sandboxEnabled || SandboxModeActive || DebugHandler.enabled)`；两个独立错误码 |
| 命令可见性 | ✅ `/v1/commands` 永远列全 6 条作弊 op，带 `available`/`unavailableCode`/`unavailableReason` |
| 产物 | ✅ dll 59904 B，v0.6.0，stage `M4.1`，apiVersion 1.1 |
| 修正 | ⚠️ **M3 的 `/v1/commands` 其实没列作弊 op**（替换静默失败未被发现），本版已补；教训：替换后必须回读校验 |
### 下次安装后要验的（M3 作弊层）
1. **闸门**：`allowCheats=false` 时任何作弊 op 回 `403 CHEATS_DISABLED`；改成 true 后、游戏 Debug 模式未开时回 `403 DEBUG_MODE_OFF`。
2. **`debug`**：`{"op":"debug","args":{"enable":true}}` 后 `/v1/meta` 的 `debugModeOn` 应变 true，游戏内出现调试功能。
3. **`setInstantBuild`**：开启后进建造模式，材料不足也应能建（用户肉眼确认）；`/v1/commands` 里该 op 带 `requiresCheats:true`。
4. **`revealFog`**：地图迷雾消失（肉眼）；关闭后是否恢复（不可逆的部分不要期望恢复）。
5. **`spawnElement`**：先打开一次调试面板的元素绘制页，然后刷 Water 1000kg 到某格；返回的 `massAfter` 应接近 1000，
   该格在游戏内确实变成水；元素名不存在应回 404；格子越界应回 400。
6. **`teleport`**：把小挪到远处某格，返回 `applied:true` 且 `cell` 与请求一致；**隔 1 秒再查 `/v1/state` 的 `cell`**，
   若被导航拉回则记录为"实验性不可靠"（这是已知限制，不是新 bug）。
7. 全程只用**测试存档**；验证完把 `allowCheats` 改回 false。
### 下次安装后要验的（M1 + M2 + M2.1）
1. `/v1/meta`：`game.buildText` 是否 `U59-744825-S`、`buildVersion` 是否 744825、`capabilities.state` 是否 `true`。
2. `/v1/state`：`colony.cycle` 与界面顶栏一致；`isPaused` 与界面暂停按钮一致；`world.name` 与存档世界一致。
3. `/v1/state` 的 `dupes.items`：人数与游戏一致；`name`/`dbId` 对得上；`amounts.Stress`/`CaloriesKcal`/`TemperatureC`
   **与游戏内属性面板逐个对照**（这是本轮唯一没有内存实证支撑的部分，只能靠界面核对）。
4. `chore.text`：复制人正在干活时是否给出合理的一行文案（`Chore.GetReportName()` 的表现未验证）。
5. `elements`：抽查 3~5 个元素与资源面板数量级一致（`GetAmount(tag,false)` 口径 = 本世界可及量）。
6. **SSE**：`curl -N` 连 `/v1/events`，应立刻收到 `snapshot`；暂停/继续游戏应收到 `diff`（`colony.isPaused`）；
   让小人换任务应收到 `dupe.chore`；断开重连带 `Last-Event-ID` 应补发漏掉的事件；空闲 15 s 应看到 `heartbeat`。
7. **命令**：`pause`→界面暂停；`resume`→恢复；**`setSpeed{"speed":3}`→界面必须是 3 倍速**（上轮 bug 点），返回 `speed:3`/`speedRaw:2`；`/v1/state` 的 `colony.speed` 也应为 3；
   `setChorePriority`→该小人当前任务优先级变化；对不存在的 dupe 应回 `404 NOT_FOUND_TARGET`。
8. **meta 主菜单可答**：在主菜单请求 `/v1/meta`，`buildText`/`unityVersion` 应非 null、`colonyLoaded:false`、
   `pumpReady:false`（不再有 `null`）。

## 10. M4 游戏内设置页（参照 DGSM）—— 反编译研究

> 本轮**只做研究**，没有改代码。所有类名/方法签名都来自本机 dll 的元数据与 IL（`tools/inspect-dll.ps1`、
> oni-mem 的 `layout il`），行文中标 ⚠️ 的是"未核实、骨架阶段要编译期确认"的点。

### 10.1 关键发现：ONI **自带**官方的「管理 / 选项」按钮

`Assembly-CSharp.dll` 的 `ModsScreen.BuildDisplay()` IL 里逐条能读出来（这不是 DGSM 的代码，是游戏自己的）：

```
+517  72 "ManageButton"                    → 按钮 GameObject 名字
+52C  7B  <>c__DisplayClass16_0::mod
+531  6F  KMod.Mod::get_IsLocal
+536  2D 7 ; +538 7E MODS::MANAGE / +53F 7E MODS::MANAGE_LOCAL   → 官方本地化文案（本地 mod 用 _LOCAL 那套）
+544  28  LocString::op_Implicit ; +549 6F set_text
+552  7B mod
+557  6F  KMod.Mod::get_is_managed        → +55C 6F set_isInteractable
+563  6F  get_isInteractable ; +568 2C 48 （不可交互就跳过挂接）
+56C  6F  <按钮控件> ; +573 7B mod ; +578 6F KMod.Mod::get_manage_tooltip ; +57D LocString::op_Implicit ; +582 6F set_toolTip
+590  6F  KMod.Mod::get_on_managed        → +595 6F **add_onClick**   ← 官方把 on_managed 直接挂到 onClick
```

`KMod.Mod` 上的三个成员（签名原样）：

```csharp
public System.Action get_on_managed();            // 私有 setter
public LocString   get_manage_tooltip();          // 私有 setter
public bool        get_is_managed();
// get_is_managed 的 IL：ldarg.0; call get_manage_tooltip; ldnull; cgt.un  ⇒ 等价于 manage_tooltip != null
```

**结论**：官方按钮存在且是活的；按钮文案/可交互性/工具提示/点击全部由游戏处理。
**但 `on_managed` 和 `manage_tooltip` 的 setter 都是 non-public** ⇒ 第三方 mod 要挂上去**必须反射（或 Harmony）**。
这是"官方留了钩子但没开放 setter"的典型，如实记录。

另外 `KMod.UserMod2` 的 `mod` / `path` / `assembly` **都是 public getter**（`get_mod` 返回 `KMod.Mod`），
所以 `OnLoad` 里能直接拿到自己的 `Mod` 实例去设这两个属性。

### 10.2 DGSM 的做法（为什么它没走官方按钮）

`mods\Steam\1838445101\`（`mod.yaml` 里 `staticID: DGSM2`，`DGSM.dll` 422 KB，`TargetFramework .NETFramework,Version=v4.8`）。

它**没有**用 `on_managed`（因为 setter 私有），而是：

```csharp
// 类型：Ony.OxygenNotIncluded.ModsScreen_BuildDisplay
// 该类上的自定义特性 blob 解出来是：ModsScreen, Assembly-CSharp, Version=0.0.0.0, ... BuildDisplay
//   ⇒ [HarmonyPatch(typeof(ModsScreen), "BuildDisplay")]   ✅ 由属性 blob 证实，不是猜
static ModsScreen _modsScreen;
static void       Postfix(UnityEngine.Transform /*?*/, ModsScreen, System.Collections.IList);
static UIButton   AddSmallButton(UnityEngine.GameObject parent, string text, ColorStyleSetting style, LocalizeText loc);
```

即：**Harmony Postfix 打进官方 Mods 屏，自己往每个 mod 条目里塞一个小按钮**，点击打开
它自己实现的面板。面板不是复用 ONI 的控件，而是**自带一整套 UI 工具包**：

```
Ony.UnityUI.{UIButton, UIButtonBase, UIButtonIcon, UICheckBox, UICheckBoxLabel, UIInput, UIDialog,
              UIScroll, UIText, UILayoutGroup, UIPadding, ColorPicker, SpriteCache, UI, Tools, ...}  ≈ 40 个类型
Ony.OxygenNotIncluded.{ModScreenManager`2, IModScreenManager, CheckboxStateManager, LocalizeText, TranslationBase, ...}
ModScreenManager`2 的公开方法：
    void OpenConfig(?, ?, ?);
    Toggle/控件 CreateCheckbox(?, string, string);
    void CreateUpDown(?, ?, string, int32);
    Toggle CreateSlider(string, string, float, float);
    void CreateHeader(?);
    void CreateOrderedList(IEnumerable<?>, string, string, ?);
    void CreateKeyBindingList(?, string);
    void SaveSetting(string, bool);
```

**它的设置页是"从零搭的 UI"**，不是官方基类 —— 这就是为什么它要背 400 KB 的 dll。

### 10.3 持久化（DGSM 实测）

```
类型：Ony.OxygenNotIncluded.Configuration`2（+ ConfigurationBase`1、SerializeManager）
成员：
  static string GetModConfigFolder();   // 返回静态字段 _modConfigFolder
  static string GetModFolder();         // Path.Combine(RootFolder(), "mods")   ← IL 里的字符串就是 "mods"
  static void   LoadConfig();  static T LoadConfig(string);  static void Save();
  T    GetValue<T>(string);     void SetValue<T>(string, T);
  static bool IsDirectoryWritable(string);
ConfigurationBase`1 的生命周期钩子（可覆盖）：
  OnSettingsCreate / OnSettingsLoad / OnGameStarted / OnGameFullyLoaded / OnGameExited / OnSaved
```

**实盘文件**（我直接读了，不是推断）：

```
%USERPROFILE%\Documents\Klei\OxygenNotIncluded\mods\config\1838445101\config.json   （626 B，扁平 JSON）
{ "EnablePrintingPod": false, "NoNegativeTraits": false, ... "StartingDupsCount": 3,
  "MaxDuplicants": 100, "ConsoleEnabled": true, "ShowConfig": true }
```

⇒ 键 = 设置项名，值 = 原始类型（bool/int），与 ONI 的 `mods\config\<id>\` 约定一致 ——
**和我们现在 `mods\config\OniAiBridge\settings.json` 的落点完全一样**，只是它用 数字 id 当目录名
（Steam 工坊 mod），本地 mod 用 staticID。

### 10.4 改完怎么生效

DGSM 不需要重载 mod：`ModScreenManager.SaveSetting(string, bool)` 改完立刻写盘，
它自己的配置对象（静态 `_settings`）就是唯一真值来源，游戏逻辑每次用到时读这个对象；
另有 `OnGameStarted/OnGameFullyLoaded` 钩子做"进游戏后补一次初始化"。

**对我们更简单**：我们的命令层每次执行都读 `Bridge.SettingsRef`（同一个对象），
所以"游戏内改 `allowCheats` → 下一次 POST 立刻生效"，不需要重载 mod。

### 10.5 我们要写的骨架（推荐方案 + 备选）

**方案 A（推荐，代码最少）—— 反射挂官方按钮**

```csharp
public class ModEntry : UserMod2
{
    public override void OnLoad(Harmony harmony)
    {
        // 1) 现有逻辑（泵 + HTTP）
        // 2) 挂官方「管理」按钮：两个属性的 setter 都是 non-public，必须反射
        KMod.Mod m = this.mod;                       // get_mod 是 public
        SetPrivateProp(m, "manage_tooltip", new LocString("ONI AI Bridge", "打开设置"));  // ⚠️ LocString ctor 待编译期确认
        SetPrivateProp(m, "on_managed", new System.Action(OpenSettingsScreen));           // 注意 is_managed <=> manage_tooltip != null
    }

    static void SetPrivateProp(object target, string name, object value)
    {
        var p = target.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        var setter = p?.GetSetMethod(true);          // true = 非公开也拿
        if (setter == null) { Log.Warn("挂设置页失败：找不到 " + name + " 的 setter（游戏版本变了？）"); return; }
        try { setter.Invoke(target, new[] { value }); }
        catch (System.Exception e) { Log.Warn("挂设置页失败: " + e.Message); }   // 软失败：仍可用 json 配置
    }
}

public class BridgeOptionsScreen : KModalScreen        // KModalScreen 在 Assembly-CSharp（已验证存在）
{
    protected override void OnPrefabInit();   // 建 UI 层级：标题 + 若干行 + 关闭按钮
    protected override void OnSpawn();
    public override void OnCancel();          // 关闭 + 写回 settings.json
}
```

UI 构件（**都在 `Assembly-CSharp-firstpass.dll`**，已逐个核实存在）：
`KButton`（public `onClick` / `add_onClick` / `set_isInteractable` / `SignalClick(KKeyCode)` / `bgImage`）、
`KSlider`、`KImage`、`KNumberInputField`、`KInputTextField`、`KScreen`。
Assembly-CSharp 侧可用：`KModalScreen`、`LocText`、`MultiToggle`、`InfoDialogScreen`、`ConfirmDialogScreen`。
**注意：没有 `KCheckbox`** —— 勾选框要用 `MultiToggle` 或"按钮切换 text(true/false)"来做。

**方案 B（备选，DGSM 路线）**：`[HarmonyPatch(typeof(ModsScreen), "BuildDisplay")]` + Postfix 自己加按钮。
代码多 ~40 行，但不依赖 private setter，更抗游戏更新。若方案 A 反射失败就退回 B。

### 10.6 难度与风险

| 项 | 评估 |
|---|---|
| 方案 A 挂钩子 | 小，约 30–60 行（含反射与失败降级） |
| 屏幕本体（勾选框/数字/按钮 + 保存） | 中，约 200–350 行；难点是**运行时构建 UI 层级**与 `KScreen` 生命周期，不是业务逻辑 |
| 风险 1 | `on_managed`/`manage_tooltip` 是 private setter，游戏更新可能改成员名 ⇒ 必须 try/catch 软失败并回落到"只改 json" |
| 风险 2 | 运行时创建的 UI 要自己处理 UI 缩放/输入遮挡/ESC 关闭，容易"打开了关不掉" |
| 风险 3 | 我们的屏幕若用 `KModalScreen`，要确认它是否会随 `PauseScreen`/`KScreen` 管理器正常压栈（⚠️ 骨架阶段验证） |
| 结论 | 工作量**中等偏小**，但必须分两步：先做"只读展示 + 关闭"，跑通了再加可编辑控件 |

### 10.6ag 判据：**"看起来不合理"不等于"是 bug"**

**现场（2026-11-15）**：`sweep` 的候选里出现了 `Minion`（复制人），我第一反应是"显然不对，枚举范围错了"，
并据此加了 `if (!Clearable.isClearable) continue;` 把它筛掉。**结果复制人本来就能清扫** ——
用户实测：拖选三个小人，**真的清掉了**（`dupes` 从 3 变 0）。我差点把一个玩家能用的功能**静默关掉**。

**判据**：**在断言"这是 bug"之前，先问一句"这可能是设计如此吗"**；拿不准就直接问领域内的人（用户）。
**附带一条实现原则**：**"能不能做"这种判定，能用游戏自己的字段（`isClearable`）就别用我们的猜测** ——
但**是"报出来"还是"用来筛"，要分清**：报出来是给事实，筛掉是替调用方做决定。

**这次的最终做法**：`isClearable` **只作为候选字段返回，不做过滤** —— 既不误杀，也不隐瞒信息。

### 10.6ah 判据：**"失败也会造成破坏"的接口比"没做成"更糟**

**现场（2026-11-15）**：`POST /v1/save/load` 调用 `SaveLoader.Load(path)`，它返回 `false`，
而我们的响应写的是 **"加载没有发起"**。**但事实是：它已经执行了 `ClearScene()`** ——
当前殖民地被销毁，随后撞单例冲突（`SaveGame` vs `SaveGame(Clone)`），**新档没进来、旧档回不来**，
`Grid` 失效导致每帧空引用 16890 次直至崩溃。

**判据**：**任何 `ClearX(); CreateY();` 的组合，都要问一句"CreateY 失败了怎么办"。**
- **先毁后建**的操作必须能回滚，否则它就是一个"**失败也会造成破坏**"的接口；
- 这类接口**不能只报"没做成"**，必须报"**已经改了什么、现在是什么状态**"；
- 兜底：**在拿到"能确认目标可加载且失败可恢复"之前，直接禁用**（本插件选择了 501 `LOAD_PATH_UNSAFE`）。

**与 §10.6u / §10.6ab 的关系**：
- §10.6u：防"**没做说做了**"（虚报成功）；
- §10.6b：`409 NOT_APPLIED` 是"**没做就说没做**"（正例）；
- **§10.6ah：最坏的是"做了一半不说"** —— 因为它**已经改了状态**，调用方可能还以为什么都没发生。

### 10.6ai 判据（第三次实证）：**dump 只能用来"知道有这么个东西"，字段名必须靠编译器验证**

**现场（2026-11-15，同一小时内第三次）**：`SaveLoader.GetSaveFiles` 返回 `List<SaveFileEntry>`，
dump 告诉我 `SaveFileEntry` 有 `path / timeStamp / header / headerData`。编译器实际报：
```
CS0246 未能找到类型 "SaveFileEntry"          → 它是 SaveLoader 的**嵌套类型**
CS1061 "SaveLoader.SaveFileEntry" 未包含 "headerData"  → 那个字段属于**别的类型**，被平铺进来了
CS0426 类型 "SaveLoader" 中不存在类型名 "Header"       → Header 也不是它的嵌套类型
```
⇒ **dump 把多个类型（含嵌套类型）摊平成一张表**，看起来像"一个类型有很多字段"。
**处理**：放弃那条路，改用**纯文件系统枚举**（`Directory.GetFiles(root, "*.sav", AllDirectories)`）——
行为确定，代价只是少了存档头信息（已在 API 里如实标注 `colonyInfoUnavailable`）。

**四次实证汇总（§10.6aa 家族）**
| # | dump 说 | 编译器说 |
|---|---|---|
| 1 | `Harvestable.chore` 是 `public` | **CS0122 不可访问** |
| 2 | `IsValidPlaceLocation(...,ref string)` | **CS1620 要求 `out`** |
| 3 | `SaveFileEntry.headerData` 存在 | **CS1061 / CS0246 / CS0426 三条** |
| 4 | `Orientation` 有 9 个成员 | 实际是**两个同名枚举**，CS0117 |

**判据**：**dump 只能回答"有没有这个东西"；"能不能调、怎么调、字段叫什么"，唯一可靠的裁判是编译器。**

### 10.6a 踩坑：**"传模板进去、拿克隆出来"的 API，闭包指向模板**

`InfoDialogScreen.AddUI<T>(T control, out T output)` 的 IL 是 `KInstantiateUI(control.gameObject, contentContainer, true)`
—— 它**复制一份**，把克隆返回给你。于是：

* 我们在模板上设的 `MultiToggle.onClick = () => ...(toggle)...` **被克隆的成员照抄**，
  但闭包捕获的是**模板的 toggle 实例** ⇒ 点克隆体时改的是模板的状态，屏幕上看着"点了没反应/状态错乱"。
* 这类 bug 不抛异常、日志也看不出异常，只能靠推理发现。

**规矩**：碰任何 `Instantiate` / `KInstantiateUI` / `AddUI` / `Clone` 型 API，
先问一句「**闭包/委托/事件订阅会不会跟着复制并指向模板？**」；
需要指向克隆体的回调，**必须在拿到克隆之后重新订阅**，或者干脆像我们最终做的那样
**不克隆、直接把自建对象挂到父节点**（反射 `contentContainer`）。

同源坑（同为"框架已经帮你做了一半"）：
* `MultiToggle.OnPointerClick` **先 `NextState()` 再 `onClick.Invoke()`** ⇒ 回调里再 `ChangeState` 就翻两次。
* `InfoDialogScreen.AddLineItem(text, tooltip)` **第二参数是 tooltip 不是值**（值塞进去就只剩标签）。

### 10.6c 规律总结：**ONI 的 UI 组件大多"在 Awake 里假设字段已就绪"**

四个同类坑连起来看是一条规律：
| # | 坑 | 机制 |
|---|---|---|
| 1 | `KButton.UpdateColor` NRE | 缺 `ColorStyleSetting` |
| 2 | `LocText` 缺 `TextStyleSetting` 时不显示/NRE | Awake/ApplySettings 读该字段 |
| 3 | `AddLineItem(text, tooltip)` | 第二参数是 tooltip，不是值（名字骗人） |
| 4 | `LocText.Awake()` NRE | `new StringKey(this.text)`，而 `AddComponent` 会立即触发 Awake ⇒ 来不及赋值 |

**结论（判据）**：自造前先看该组件的 `Awake`/`OnEnable` 会读哪些字段：
* 只读 Unity 自带组件（`Image`/`RectTransform`）或能被"后赋字段"满足的（`MultiToggle.states` 在 `ChangeState` 前赋即可）
  ⇒ **可以自造**（`PCheckboxLite` 已验证到"创建成功"这一步）。
* Awake 里就读了本对象字段的（如 `LocText` 读 `this.text`）
  ⇒ **自造要先让物体 inactive**（`new GameObject` 默认 active 时先 `SetActive(false)` → `AddComponent` →
  赋值 → `SetActive(true)`），否则只能**克隆 prefab** 或**只用官方 `Add*` 方法**。
  本次按最小改动选了后者（`AddLineItem` + 自建无文字勾选框）。
  **但要说清 inactive 手法为什么真的能成**（别让人误以为"自造 LocText 绝对不可能"）：
  `new GameObject()` 默认 active ⇒ `AddComponent` 会**立即**走 `Awake`；先 `SetActive(false)` 则 `Awake` 不跑，
  这时字段随便赋（`LocText.text` 是 public，能赋），最后 `SetActive(true)` 时统一补跑 `Awake`/`OnEnable`/`Start`。
  ⚠️ 前提：**`Awake` 读的字段必须全都能在 `SetActive(true)` 之前赋上** —— 对 `LocText` 成立（`text` 是 public），
  所以"自造带文字的 LocText"用 inactive 路线**可行**；本次只是判断 `AddLineItem` 更稳，不是做不到。

### 10.6q 判据：**"下命令"和"已生效"是两种状态，API 必须能分开表达**

ONI 里大量"操作"其实是**派活给小人**（`QueueStateChange` → `Chore`），不是立刻改世界状态。
M6.1 门操作的 IL 实证：
```
Door.QueueStateChange(target):
   requestedState = target;                          // 立刻变
   if (目标 == controlState) { 取消 chore; return; }  // 视为取消
   if (DebugHandler.InstantBuildMode) { ApplyRequestedControlState(); return; }   // 作弊捷径（语义不对，别用）
   changeStateChore = new Chore(ChoreTypes.Toggle, …);   // ★ 正常路径：等复制人走过去按
Door.ApplyRequestedControlState()（小人执行时才跑）:
   controlState = requestedState;  ApplyControlState();
```
**判据（以后所有"操作类"端点都照这条）**：
1. 发起后**立即回读"请求态"**（`requestedState`）⇒ 等于目标 = **已受理**（`accepted: true`）；
2. **延迟轮询"实际态"**（`currentState`）⇒ 变了 = **已生效**（`applied: true`）；
3. **超时必须返回 `pending`** —— 既不是成功也不是失败（"已派活，等小人"）。
4. **绝不说"已开门"**，只说"已派活"或"已生效"。
踩过的同类坑（5 次）：`SaveAndVerify` 只校验它假设的字段、`count`/日志对不上、`filteredCount` 混义 —— 全是"把'看起来完成'当'完成'"。

### 10.6af 判据：**返回"空/0/没有"的接口，必须同时返回【它查询的范围】**

**没有范围的 0 和"真的没有"是同一个字符串，但含义相反。**

**实证（2026-11-15，P2）**：`/v1/state` 的 `elements` 长期是 `count: 0`，看起来像"这个殖民地没资源"。
根因：
```csharp
WorldContainer wc = cm.GetWorld(cm.activeWorldId);   // ← 只看当前视线所在的那一个世界
WorldInventory inv = wc != null ? wc.worldInventory : null;
```
⇒ **不是"真的没有资源"，是"我只问了其中一颗星球"**。
改成**跨世界累加** + 加诊断后：
```json
"diagnostics": { "tagsScanned": 42, "positiveTags": 17, "worldsScanned": 3, "scope": "all-worlds-sum" }
```
⇒ `count:0` 现在能立刻分三种：`tagsScanned=0`（元素表没加载）/ `positiveTags=0`（真没有）/ `worldsScanned` 不对（范围问题）。

**判据**：
1. 任何可能返回"空/0/没有"的接口，**都要附上本次查询的范围**（扫了几个世界、几个 tag、几个对象、时间窗）；
2. 光给 `count` 是**不可判定**的 —— 调用方无法区分"世界为空"和"我查错了对象"；
3. **这不是"再试一次"，是"给这次查询附上它自己的边界"。**

**与 §10.6ad 的关系**：§10.6ad 是"结果来自上一次调用"（时间维度的假象）；
本条是"结果只覆盖了一部分范围"（空间维度的假象）。两者都会产出**看起来正常但不可判定**的回答。
### 10.6ad 判据：**短路返回的字段，证明不了这次调用做了什么**

**幂等/短路逻辑会让返回值携带"上一次留下的状态"。此时字段全部为真，但这次一次都没执行。**

**判据**：任何"**这次调用产生了 X**"的结论，必须先确认【**这次调用真的执行了**】。
执行与否，要看不带缓存的证据 —— `alreadyMarkedBefore` / `shortCircuited` / `changedCount` / `skipped`
这类**显式的"我跳过了"**字段。

**实证（2026-11-15）**：`harvest (75,245) BasicFabricPlant` 返回 `choreCreated:true`，
但同一次响应里 `alreadyMarkedBefore:true` + `shortCircuited:true` ⇒ **`MarkForHarvest()` 这次压根没跑**。
`choreCreated:true` 是**上一次（扫描期）调用的残留**。
若把它当成"`MarkForHarvest()` 造了 chore"的直接证据，就是**把上一次的结果读成这一次的证据**。

**它为什么比 §10.6u 更容易中招**
| 条 | 机制 | 何时暴露 |
|---|---|---|
| §10.6u | 游戏**说谎**（返 ok 但没做） | 后面往往露馅（状态不对） |
| **§10.6ad** | 游戏**没说谎**，每个字段都真 | **不露馅** —— 返回值全绿、数字全对、测试全过，只是"这次没做" |

⇒ 这条是**"静默成功"**：**最容易被写进文档当"已验证"**。

**配套的可执行补丁**
> **测试一个"会改变状态"的 API 时，第一次调用必须用一个【干净样本】**
> （`alreadyMarkedBefore:false` / `从未操作过`）。
> **第二次及后续调用只能验"幂等/短路"，不能验"生效"。**

**与另外几条的边界**
- §10.6u：先信游戏返回值（游戏可能说谎）；
- §10.6y：搜索方式让"存在"看不见；
- §10.6z：报告加工让"原值"传不出去；
- §10.6aa：工具简化让"事实"变视图；
- **§10.6ad：生命周期 —— 被观测的东西在观测之前就已经存在了。**

**落地**：写"已验证"三个字之前，先问一遍 —— **"那次调用真跑了吗？"**
### 10.6ac 判据：**能力"合法地不适用"时，要解释为什么，而不是沉默或报错**

实测（0.39.0，`(162,190)` `VineMother`）：`marked=true` 但 `choreCreated=false` ——
因为该对象只有 `HarvestDesignatable`、**没有 `Harvestable` 组件**，标记本身就不产生 chore。

接口没有沉默（不假装成功），也没有报错（不制造失败感），而是**给了一句解释**：
> `choreUnavailableReason`: "该对象没有 Harvestable 组件（只有 HarvestDesignatable）⇒ MarkForHarvest 只置标记，不会产生 chore"

**为什么这比错误码好**：错误码只能**分类**（"失败/不适用"），这句话能**终止追问** ——
调用方不会再查第二遍"是不是我们写坏了"，也不会把这格记成"功能缺陷"。

**判据**：
1. 当某个能力对某类对象**合法地不适用**时，返回值里要给出**具体原因**（哪个组件缺失 / 哪个前提不满足）；
2. **不要沉默**（`false` 而不解释 ⇒ 调用方只能猜是 bug 还是不适用）；
3. **也不要报错**（4xx/5xx 会让人去"修"，而这里没东西可修）；
4. 同族规矩：**给事实，不给判定**（§10.6q/§10.6r），以及 **`null` ≠ `false`**（§3.12）。

**与 §10.6ab 的关系**：`NOT_APPLIED` 是"我下命令了、游戏没照做"（要报 4xx）；
本条是"这个命令对这类对象本来就没有那个效果"（不是失败，要解释）。
### 10.6ab 正面例子：**命令被如实拒绝，比命令"看起来成功"值钱**

实测（2026-11-14，大存档 + 「模组差异」对话框阻挡）：
```
POST /v1/command {"op":"resume"}
→ 409 NOT_APPLIED  "requested isPaused=false but game reports true"
```
游戏处于暂停、而且被一个第三方对话框挡住 ⇒ `resume` 确实没生效。
**接口没有虚报成功，而是把"我下了命令、游戏没照做"如实报出来。**

**为什么这是正面例子**：如果它回 `ok:true`，调用方（人或 AI）会以为游戏已经在跑，
接下来所有"为什么植物还是 `NOT_RIPE`""为什么复制人不动"的排查都会建立在**错误前提**上。
**一次虚报成功，会污染后面所有的推理。**

**由此落成两条规矩**：
1. **凡是"改变状态"的命令，返回前必须回读**（`requested` vs `game reports`），不一致 ⇒ **4xx + `NOT_APPLIED`**，
   不能因为"我调用成功了"就报成功（这与 §10.6u「先信游戏返回值」是同一条的两面）；
2. **`NOT_APPLIED` 的 message 要同时给"我要求什么"和"游戏实际是什么"** —— 只给一句"失败了"，
   排查者还得再猜一轮；两个值一起给，"卡在哪"立刻清楚。

**反面对照（同日发生）**：`/v1/job` 早期把 `PlaceDig` 返回 null 报成 `500 INTERNAL "不该发生"`，
把"正常拒绝"包装成"我们的 bug"。**虚报的两种形态：把失败说成成功（本节），把拒绝说成崩溃（§10.6t）。**
### 10.6aa 判据：**工具的输出是"视图"，不是"事实"**

同一个东西可以有多个观察者：**编译器 / 运行时反射 / 元数据 dump / 反编译文本 / 文档 / 我的记忆**。
它们不一致时，**按"离真实最近的"排序**，不是按"我先看到的"排序：

```
编译器、运行时  >  反射元数据 dump  >  反编译文本  >  官方文档  >  记忆
```

**判据**：报"我确认它是 X"之前，先问一句 —— **我是从哪一层看到 X 的？**
**如果那一层做了简化（两态、截断、跳过、合并），就只能说"某层显示 X"，不能说"它是 X"。**

**实证（2026-11-14，同一天内发生）**
- 我的元数据 dump 输出：`F inst public chore : Chore` ⇒ 我据此在报告里写"**元数据里确实有** public"；
- 编译器实际报：`CS0122: "Harvestable.chore" 不可访问，因为它具有一定的保护级别`；
- 原因：**dump 工具只输出两态（`public` / `nonpub`）**，`protected` / `protected internal` 被归进了 `public`。
- ⇒ 结论：**编译器是权威，dump 是视图。** `chore` 从外部程序集**访问不到**，改用 public 的 `HasChore()`
  （其 IL 为 `return chore != null;`，已核）。

**三个实证（2026-11-15 同一天）**
| 断言 | dump 说 | 编译器说 |
|---|---|---|
| `Harvestable.chore` | `public` | **CS0122 不可访问** |
| `ResearchCenter.CreateChore()` | `public` | **CS0122 不可访问** |
| `BuildingDef.IsValidPlaceLocation(...,ref string)` | `ref` | **CS1620 要求 `out`** |
| `ClusterManager.worlds` | 有这个字段（`m_worldContainers`） | **CS1061 不存在**，实际是 `cm.WorldContainers` 属性 |

⇒ **dump 只输出两态（`public`/`nonpub`），而且修饰符（`ref`/`out`）也不可信。**
⇒ **dump 是"平铺文本"，它抹掉了命名空间与声明位置：同名类型会混成一坨。**
   实证：`Orientation` 有两个（一个 `Neutral/R90/R180/R270/FlipH/FlipV`，另一个 `Vertical/Horizontal/SingleCell`），
   dump 把它们打在一起，看起来像"一个类型有九个成员"；我据此写了 `Orientation.Horizontal`，编译报 `CS0117`。
   **两类伤害方向不同**：两态简化 ⇒ **权限信息丢失**；平铺文本 ⇒ **归属信息丢失**。
⇒ **dump 只能回答"有没有这个东西"，不能回答"能不能调、怎么调"。**
⇒ **能不能调，唯一可靠的裁判是编译器。**

**这个例子同时证明了两件事**：
1. **该信谁** —— 不一致时按层排序，编译器/运行时优先；
2. **为什么会信错** —— 不是因为我偷懒，而是**工具替我做了简化，而我不知道**。

**与 §10.6z 的关系**
- §10.6z：**我在报告里加工了**原值（`ToUpper` 后当成函数返回值）；
- §10.6aa：**工具替我加工了，我不知道**（两态 dump 把 `protected` 说成 `public`）。
- **后者更隐蔽** —— 因为 §10.6z 至少是我自己动的手，能自省到；§10.6aa 里错在工具，
  我看到的已经是"干净的结果"，没有可疑之处。

**与 §10.6y 的关系**：§10.6y 是"搜索方式让存在看不见"，本条是"观察层让不存在看起来存在"。三者同族：
**都是"我看到的 ≠ 事实"，而代码不报错。**
### 10.6z 判据：**报告"函数返回什么"必须原样贴，任何加工都要单列**

**实证（本次口误）**：`FilterLayerZh` 里我先 `filterLayer.ToUpperInvariant()` 再去查 `STRINGS`；
然后在报告里写「`GetFilterLayerFromObjectLayer` 返回 `"WIRES"`」——
**其实它返回的是驼峰 `"Wires"`，`WIRES` 是我转成大写后的键**。
代码没错（查表本来就要大写），**错在报告把"我加工后的值"当成了"它返回的值"**。

**为什么这类错比代码 bug 更危险**：
- 代码 bug 可以读代码找出来；
- **报告里被"加工"过的事实，读代码找不到** —— 因为代码里那一步的作用是查表，不是"报出去的值"。
- 更糟：读报告的人会据此建立错误的世界模型（"这函数返回全大写"），以后所有基于它的推理都会偏。

**判据**：
1. 报"某个函数/接口返回什么"时，**原样贴**（含大小写、空白、转义）；
2. 任何加工（`ToUpper` / `Trim` / 格式化 / 单位换算 / 取子串 / 我自己的拼接）**必须单独标注**：
   「它返回 `Wires`，我为了查 STRINGS 把它转成 `WIRES`」；
3. 拿不准时**贴两遍**：原文 + 我加工后的，中间写清怎么变的。

**与 §10.6y 同族** —— 都是"我看到的 ≠ 我报的"：
- §10.6y：**搜索方式**让"存在"看不见；
- §10.6z：**报告加工**让"原值"传不出去。
### 10.6y 判据：**"0 命中"必须换一种写法再搜一次才能采信**

**实证（本次）**：要确认"`details:{}` 是否还有残留"，先用 `-SimpleMatch 'details":{}'` 搜 ⇒ **0 命中**。
但源码里那一处写的是**转义形式** `\"details\":{}}`（C# 字符串里的转义引号）⇒ 第一次搜**根本没匹配到**。
换转义形式重搜 ⇒ **命中 1 处**，改掉后再用两种写法复核 ⇒ 都是 0。

**判据**：
1. 报"没有 / 全是 0 / 已清理干净"之前，**换一种表示法再搜一次**
   （`{}` / `\"{}\"` / `\u007b\u007d` / 带命名空间前缀 / 大小写变体）；
2. 记结论时**写清用的哪种写法**（"用 `-SimpleMatch 'details":{}'` 搜得 0" 这种表述比 "0 处" 有用得多）；
3. **"确认某物不存在"的错误代价 > "确认某物存在"的错误代价** ——
   因为"不存在"这个结论会让人**停止寻找**，而"存在"至少还会被复核一次。

**与 §10.6x 同族**：都是"看起来没问题，其实是没查到"。
- §10.6x：值没出现 ⇒ 先怀疑**通道**；
- §10.6y：值没出现 ⇒ 先怀疑**搜索方式**。
### 10.6x 判据：**隐式通道静默失败，显式返回值失败时报错**（并排三例）

**规矩**：同一请求/同一次操作内的中间结果，必须走**返回值 / out 参数 / 局部变量**；
**不许用线程级静态（`[ThreadStatic]`）或全局变量传递。**

**理由**：隐式通道断开时**不报错**，只会**静默给出空结果** —— 比抛出异常更难查。
判据：**如果两个位置通过"看不见的通道"通信，任何一次重构都可能悄悄弄坏它。**

**本次实证（第三次同形问题）**：`409 AMBIGUOUS_TARGET` 的 `details` 恒为 `{}`：
- 写入在 `MainThread.Invoke(...)` 的 lambda 里（**主游戏线程**），
- 读取在 `Bridge.HandleJob`（**HTTP 连接线程**），
- `[ThreadStatic]` 语义是"每线程一份" ⇒ **两边各有一份**，HTTP 那边永远是 null。
- 而我当时还在回复里"论证它安全"（"每请求独占线程"）—— **只验了"线程不共享"，没验"读写是否同线程"，同一个断言只验了一半。**

**排查判据（放在例子前面，因为它比例子更有用）**：
> **凡"某个值没出现 / 为空 / 是默认值"，先怀疑通道，不要先怀疑业务逻辑。**
> 三个同形事故全是这个形状：不崩溃、不报错，只是安静地给出了错的结果。

### 三个同形案例并排（都是"没报错，只是安静地给了错结果"）
| # | 事故 | 症状 | 根因 | 正确判据 |
|---|---|---|---|---|
| 1 | `Grid.ObjectLayers[cell]` 拿不到对象 | `alreadyMarkedBefore` 恒 false（短路形同虚设） | 用了错误的**访问路径**，而它不报错、只返回空 | 与游戏同一访问方式（`Grid.Objects[cell, layer]`）；**先信游戏返回值**（§10.6u） |
| 2 | `-1` 当"没拿到"的哨兵 | `objectsBefore:-1`、`priorityRequested:0`（把"没传"当成"传了 0"） | 用魔法值承载"缺失"语义 | `null` = 概念不适用/没拿到 + 独立可用性字段（§3.12） |
| 3 | `[ThreadStatic]` 传 `details` | `details` 恒 `{}`，调用方拿不到候选 | 隐式通道跨了线程 | 显式 `out` 参数（本节） |

**共同形状**：**代码没报错，只是安静地给了错的结果。** 三者都不是"崩溃"，都是"看起来正常"。
⇒ **凡是"某个值没出现/为空/是默认值"的现象，先怀疑通道，而不是先怀疑业务逻辑。**
### 10.6w 判据：**"挑一个 1×1 的对象做首测"是通用规矩，不是特例**

M6.3 验 `deconstruct` 时选 `Wire`（电线，1×1）而不是 `Bed`(2×2) / `PlanterBox`(3×1) / `ResearchCenter`(4×3)，
理由不是"电线不重要"，而是**变量要少**：

- 多格对象的链路是「**锚点格 → 实际占用格**」的映射：`/v1/buildings` 给的是锚点 `x,y`，
  而动作判定走 `PlacementCellsContainCell(cell)`，**一次要同时验「锚点命中」和「整栋处理」两件事**；
- 失败时**分不清是哪一层错**（坐标映射？还是命令本身？）；
- **1×1 对象删掉了"映射"这一层** ⇒ 失败只可能来自"命令本身"，一次只有一个嫌疑。

**推广**：任何"对某个对象下命令"的首测，**先找一个 1×1（或等价的单格）对象**：
1×1 的电线/管子/砖块 > 多格机器 > 跨房间结构。

**与 §10.6u / §10.6v 同族**：**先削掉变量，再验**。
- §10.6u：先信游戏返回值，再信自己的查询（减少"自证"这个变量）；
- §10.6v：用等式代替逐项检查（减少"人对不上账"这个变量）；
- §10.6w：首测选 1×1（减少"坐标映射"这个变量）。

**副产品（本次顺带想清的）**：区域模式下 `isContiguous:false` 才是有信息量的；
如果首测就挑一个"形状规整、无建筑、全实心"的矩形，`isContiguous` 恒为 true，**等于没验**。
### 10.6v 判据：**批量操作的正确性靠"等式"，不靠"逐项检查"**

M6.5 区域挖的实现把格子分成三类：`applied`（本次新标记）、`alreadyMarked`（本来就有）、`rejected`（按原因分类）。
**正确性判据是一条等式**：
```
applied + alreadyMarked + rejected == requested.cells
```
一旦不成立，说明有格子被**漏掉或重复计数**——而**逐项看是看不出来的**（你不可能对着 441 格逐个数）。

**落地要求**：
1. **等式要打在服务端日志里做自检**（`| 等式 OK/FAIL(sum≠cells)`），不能只算给调用方看。
   调用方可能不检查，日志是我们自己的防线。
2. 三类必须**互斥且穷尽**：任何一格只能落进其中一类；分类时**先穷举拒绝原因**，剩下的才算 applied。
3. `rejected` 必须**按原因分组**（`byReason`），只给总数等于放弃诊断能力。
4. 与"双锚"（§10.6u）互补：**等式是批量的锚，instanceId 是单对象的锚。**

**实测**：75 格 ⇒ `58 applied + 0 alreadyMarked + 17 rejected = 75` ✅，其中 5 格 `HAS_BUILDING`、12 格 `NOT_DIGGABLE`。
### 10.6u 判据：**"证明它生效"要先信任游戏返回值，再信任自己的查询函数**

M6.2 实测（决定性证据）：
- 同一格连发两次 `/v1/job type=dig`，`PlaceDig` 返回的 `instanceId` **完全相同**（`-446618`）⇒ **游戏内部判重，标记没有叠加**；
- 但我们自己的 `FindDiggableAt` 却说"这格没有标记"（`alreadyMarkedBefore:false`）⇒ **短路形同虚设**；
- 而两者查的是同一格 —— 差别在**访问方式**：我们用了 `Grid.ObjectLayers[cell]`（拿不到），
  游戏内部用的是 `Grid.Objects[cell, layer]`（拿得到）。

**判据**：
1. **先看"游戏自己返回的东西"**（`PlaceDig` 的返回值、`instanceId`、组件的 public 属性），
   再拿自己的查询函数去**交叉验证**；两者不一致时，**先怀疑自己的查询**。
2. 自己的查询函数**错了不会报错，只会安静地给出错误答案** —— 这类错比崩溃更难发现。
3. 所以"证明生效"必须至少有**一个不依赖自己代码的锚**：同一个 `instanceId`、状态真的变了、
   或游戏侧计数变化。

**落地**：本轮把两处查询统一改成 `Grid.Objects[cell, layer]`（与游戏同一访问方式），
层数用 `ObjectLayer.NumLayers`，不硬编码层号。
**实证演进（M6.2，三轮）**：
1. **0.33.1**：锚 1（`instanceId` 两次一致 = 游戏判重）✅ / 锚 2（`alreadyMarkedBefore`）❌ ⇒ **不能算过**；
2. **0.33.2**：改了错误码，但查询仍用 `Grid.ObjectLayers` ⇒ 锚 2 依旧 ❌；
3. **0.33.3**：改用 `Grid.Objects[cell, layer]` ⇒ **锚 1 + 锚 2 同时 ✅**，才算真过。

⇒ **规矩：两边都算对才算过；一边对不能算。** 尤其"我实现的接口说它生效了"这一侧，
必须能独立于"游戏自身返回值"成立，否则就是自证。

**另一个副产品**：验证方法也要能用两向证明 ——
先"全新放"（证明会建）再"同坐标再放"（证明不重复建），
一次覆盖两个方向，比"比对上次记录"更抗环境变化（存档没了也不会让验证失效）。
### 10.6t 判据：**"用户的输入不合法"和"我们代码出错了"必须是不同的错误码**

M6.2 实测踩到：对着**已建成的砖块**发 `type=dig`，`DigTool.PlaceDig` 返回 null，
我们当时报的是 **`500 INTERNAL` + "不该发生"** —— 错了两处：
1. 它**会经常发生**（建筑 / 已被挖掉 / 地形不支持，都是正常拒绝）；
2. 把 4xx 报成 5xx，调用方会去**重试一个永远不可能成功的请求**。

反方向同样有害：把 5xx 报成 4xx，调用方会以为是自己的错而**放弃**。

**判据**：
| 归类 | 语义 | HTTP | 调用方该做什么 |
|---|---|---|---|
| **调用方的错** | 参数/目标不合法（不可挖、worldId 不符、格子无效） | **4xx** | 改请求（换坐标 / 换 type / 先 deconstruct） |
| **我们的错** | 真意外（取不到内部状态、反射失配、不该为 null 的东西为 null） | **5xx** | 重试无意义，报 bug |

**落地要求**：新增一个 4xx 分支时，message 必须**写清"下一步该做什么"**
（例："该格上有建筑，请用 deconstruct 而不是 dig"），而不是只说"失败"。
### 10.6s 判据：**跨 type 的响应字段必须语义一致；不一致的用枚举值区分，不要复用同一个名字**

`/v1/job` 是一个多 type 端点（`dig` / 以后还有 `deconstruct` / `sweep` / `build` …），
如果不同 type 用**同一个字段名表达不同含义**，调用方必然误判。

实例（差点犯的错）：
- 门：`applied` = "小人走到了、真生效了"
- 挖：`applied` = "标记落地了"（小人还没动）

⇒ **同一个词，两个意思。** 改成两层：
| 字段 | 语义 | 跨 type 是否一致 |
|---|---|---|
| `accepted` | **已受理**（门=已派活；挖=标记已落地） | 一致（都是"系统的活干完了，剩下等小人"） |
| `jobState` | **该 type 特有的语义枚举** | 名字一致、取值按 type 定义：挖 `marked`/`completed`；门 `pending`/`done` |

**规矩 1（失败不进语义枚举）**：**`jobState` 只表达"受理之后的状态"，永远不出现"失败"**。
失败一律走 `ok:false` + `error.code`；"受理了但某一步没成"用独立字段 `partial: true` + `partialReason`。
理由：一旦 `jobState` 里能出现 `failed`，所有调用方都会去那里找失败，`error` 字段就废了。

**规矩 2**：新增 type 时，**先看能不能用现有 `jobState` 取值**；不能就新增取值（并在文档里列全），
**绝不新增一个与既有字段同名但语义不同的字段**。
### 10.6r 判据：**同一对象上的"多个状态量"不许合并成一个布尔**

以门为例，IL 实证三个量**语义不同、变化时机不同**：
| 量 | 读法 | 含义 | 何时变 |
|---|---|---|---|
| `currentState` | `Door.CurrentState`（public 属性，纯字段读） | 控制档位 `Auto/Opened/Locked` | **小人执行后**（或作弊直接写） |
| `requestedState` | `Door.RequestedState`（同上） | 排队中的目标档位 | **发起后立刻** |
| `physicalOpen` | `Door.IsOpen()`（读状态机 `open/closedelay/closeblocked`） | 门此刻物理开着没有 | 物理开合瞬间 |

并且 **`Door.Open()` / `Door.Close()` 只改物理开合，不改 `controlState`**（IL：`controller.sm.isOpen.Set(...)` + `OnSimDoorOpened/Closed`）。
⇒ 把三者压成 `open: true` 会让 AI 以为"门开了"，实际它只是"被要求开"。
**推广**：做其它建筑动作时会再遇到同类区分（工作台：**配方队列** vs **正在工作** vs **是否有工人**；
建筑：**启用/禁用** vs **运行/停机** vs **供电/断电**）。**每种都要单独成字段**，并在文档里写清"何时变"。
### 10.6p 设计判断：**不要给调用方"我判断好了"的布尔值，给它做判断所需的事实**

背景（M5.4 收尾）：`/v1/buildings` 的 `count` 在殖民地刚加载时会明显偏小（见 §10.6o）。
提出过加一个 `settled: true/false` 字段，最终**否决**，理由：

- `settled` 必须依赖一个阈值（"多少秒 / 连续几次相等算稳定"），而**这个阈值因机器、因存档大小而异** ——
  **写死一个常量就是在编**（与"拿不到就说拿不到"同一条精神）。
- 更糟的是它**替调用方做了决定**：调用方拿到 `true` 就不再质疑；拿到 `false` 就干等，
  而它其实可能有更好的判据（例如它自己知道刚点了"载入存档"）。

**改为给事实，不给判断**：
```
scannedAt     : 这次采样发生的时刻（游戏内时间或 UTC）
worldLoadTime : 进图/载档的时刻
scanned       : 这次扫到多少个
```
调用方用「采样时刻 − 进图时刻」自己决定信不信，阈值由它定。

**推广（以后所有"状态类"字段都照这条）**：
> 能给事实就不要给结论。凡是"是否已就绪 / 是否稳定 / 是否可用"这类**依赖阈值**的布尔，
> 都优先换成"原始时刻 + 原始计数"，让调用方自己算。
> 例外：确实存在**游戏自己给出的权威判定**（如 `Operational.IsOperational`、`BuildingEnabledButton.IsEnabled`）——
> 那不是我们在拍板，直接透传即可。
### 10.6o 踩坑：**刚进图时"世界对象集合"是不全的，而且不报错**

M5.4 验收时实测：同一会话前后两次 `/v1/buildings`，
```
第一次（殖民地刚加载完）: count=192   filteredByFog=0
第二次（世界跑了一会儿）: count=192   filteredByFog=2635
```
**`Components.BuildingCompletes` 在殖民地刚加载时只登记了一部分**（世界还没初始化完），
加载完才逐步补全。**它不抛异常、不报错，就是数量少** —— 于是"门禁没触发"看起来像"过滤无效"。

**加强版判据（M5.4 收尾补充）："数量偏小"有两种可能，必须先分清**
| 现象 | 含义 | 结论 |
|---|---|---|
| `scanned` 大、`count` 小 | **过滤生效** | 正常 |
| `scanned` 本身就小 | **采集基数不全**（数据还没加载完） | 数据不完整，别看 `count` |

**区分方法：看 `scanned`，不是看 `count`。**
实测：第一次 `scanned=192 / count=192`（基数不全）；第二次 `scanned=2827 / count=192`（过滤生效）。
两次 `count` 一模一样，**看 `count` 完全分不出来** —— 只有 `scanned` 能分。

**两个方向都会错，都踩过**：
- 把"过滤没触发"（`filteredByFog=0`）误当成"过滤无效"；
- 把"基数不全"（`scanned` 小）误当成"世界就这么大"。
**推广**：所有依赖"世界对象集合"的接口（建筑/物品/生物/格子内容），
在刚进图的头几秒都会返回不完整数据 —— 这类接口应当**报出"采样时刻"或"是否已稳定"**，
否则调用方无法区分"世界就这么大"和"还没加载完"。
### 10.6n 判据：**「删除项残留检查」不能按"字符串出现次数"判，要按"被删的行为是否还会发生"判**

M4.4 加自检时踩的实例：把 `Debug 模式` 放进"残留必须为 0"清单 ⇒ 报残留 3 次，**全是误报**：
- `Cheats.cs`：`/v1/commands` 里 debug op 的说明「查/开/关游戏 Debug 模式」——合法；
- `Gate.cs`：`Gate.UnavailableReason()` 的文案「…且游戏 Debug 模式未开启」——合法；
- `WebUi.cs`：已搁置的网页页面提示——也是合法存在。

⇒ **判据**：不要问"产物里还有没有这个词"，要问"**被删的那个行为还会不会发生**"。
落到实现上：
* **A 类（产物字符串）**只用于**只属于被删界面**的词（`沙盒(存档)`、`作弊闸门`、`勾选立刻写盘`）；
* **C 类（源码模式）**用于**在别处有合法用途**的词 —— 例如判"设置页还会不会创建 `L("Debug 模式")` 这一行"，
  而不是"dll 里有没有 `Debug 模式`"。**一旦某个词有第二用途，必须挪到 C 类。**
* **B 类（允许保留）**：回退用的代码路径（`AdjustStep 已下线` / `CycleField 已下线`）标 `KEPT` 输出 ——
  **把"这是有意为之"的决定写进输出**，比"当残留报错"和"直接忽略"都好，后来人不会误判。

实现在 `tools/check-removed.ps1`（`build.ps1` 第 4 步，失败即中止打包）。

### 10.6m 取舍：**UI 上"信息写在哪"是可以选择的**

M4.3b 的真实取舍：`[− 5] [+ 5]` 这种"按钮带步长"会随"当前字段"变化而过期
（切到 `amountEpsilon` 还显示 `− 5` 就是 bug）。为了让它正确，得换成
`AddOption(bool, ref KButton, ref LocText)` 重载 + "切换字段时重建按钮文字" ——
**等于把刚跑通的东西再拆一遍**。

**结论**：按钮保持 `[−] [+]` 不带数字，步长/范围**挪到状态行**
（`当前字段：amountEpsilon（步长 0.05，范围 0–10）`）。

**规矩**：**按钮上不带数字不是缺陷，是把易变的信息挪到专门显示它的地方。**
遇到"控件要随上下文变化"的取舍，先问：这条信息是否**有更合适的固定位置**（状态行/标题/提示）——
有的话就别让控件承担它，控件越简单越不容易过期。

### 10.6l 纪律：**"回读校验/断言"必须先列清它到底比对了哪些字段 —— 缺一个就是假校验**

同一个形状已经出现三次（全部是"看起来成功"）：
| # | 假成功 | 真相 |
|---|---|---|
| 1 | 反射设 `on_managed` 没抛异常 | 该属性对 `Local` 平台不参与渲染，设了等于没设 |
| 2 | M3 `/v1/commands` "6 条都在" | 文本替换静默失败，实际只有 4 条 |
| 3 | `SaveAndVerify` 返回 true | `LoadFromDisk()` **只解析 `port/allowCheats/token`**，数值项压根没被比对 |

**规矩（可执行）**：
1. 写 `SaveAndVerify` / 回读 / 断言时，**先列出被比对的字段清单**，并让它与"可写字段清单"一一对应；
   新增字段时，**同一个改动**里必须同步加进这两处，否则校验对它就是盲的。
2. 判据不是"返回 true"，而是"**每一个写过的字段都被读过并比较过**"。
3. 定期用一句话自检：**"如果这个字段根本没写进去，这段校验能发现吗？"** 答案是否，就等于没校验。

### 10.6k 事实澄清：`mods.json` 的 `enabled` **不是**"会不会加载"的开关

实测（2026-11-14，用户机器）：
```
已知会加载的 Magpie Bridge : enabled=false  status=1  enabledForDlc=["EXPANSION1_ID"]  crash_count=0
本 mod (OniAiBridge)        : enabled=false  status=1  enabledForDlc=["EXPANSION1_ID"]  crash_count=0
全库统计                    : enabled=false ×30（全部）; enabledForDlc=["EXPANSION1_ID"] ×28, [] ×2
```
⇒ **绝大多数 mod（包括所有能正常加载的 Steam mod）在 `mods.json` 里都是 `enabled: false`**。
真正的门槛是 **`status`（=1 表示已安装）** 和 **`enabledForDlc`（必须含当前 DLC，空数组 `[]` 就不会加载）**。

**两个操作层面的坑**：
1. **游戏运行中改 `mods.json` 无效**：ONI 在内存里维护这份数据，**退出时整份重写**，
   我们写进去的值会被它内存里的旧值覆盖（实测 18:21 改成 true，游戏退出后又变回 false）。
   ⇒ `tools/enable-mod.ps1` 现在检测到 `OxygenNotIncluded` 进程就直接 `REFUSED` 退出。
2. **看到 `enabled: false` 不要急着"修"** —— 先看 `status` 与 `enabledForDlc`；
   真不加载时，先查 `enabledForDlc` 是否被写成了 `[]`（历史上就是这么坏的），而不是 `enabled`。

### 10.6j 方法论：**研究"第三方库为什么没按预期工作"前，先看同生态里已经用它的项目**

M4.2 那串弯路（`NextState` 语义推了五六版）的根因不是 IL 读不懂，而是**方向错了**：
一直假设"官方 `OnPointerClick` 会推进状态"，于是反复研究它为什么没推进。
最后去翻 PLib 的 `PCheckBox`（几十个 mod 在用的实现）才发现：**官方本来就不翻，所有人都是自己翻**
（PLib 的 `SetCheckState` = `if (current != state) ChangeState(state)`，从不调 `NextState`）。

**规矩**：
* 遇到"第三方 API 行为不符合预期"，**先找同生态里能跑的用法**（本机 `mods\Steam\` 下的 dll 就是最好的语料），
  照抄它的调用序列；它们能跑就说明路是通的，我们缺的只是某个细节。
* 只有在"没有同类实现"或"照抄仍不通"时，才去 IL 里逆推设计意图 —— 那是最慢的一条路。
* 判断标准也很朴素：**先用探针确认"官方逻辑到底有没有被调用"**（Prefix/Postfix 两侧打点），
  再谈"为什么没生效"。这一步能省掉整轮猜测。

### 10.6i 机制：**Harmony/Unity 事件回调里的异常会被静默吞掉**

症状（M4.2n 实证）：`MultiToggle.OnPointerClick` 的 Prefix / Postfix 都打了、
我们自己的日志停在某一行、而**官方后续逻辑（`NextState()`）完全没执行**，
日志里**没有任何堆栈**。原因：我们的 `onClick` 回调抛异常，
Unity 的事件系统捕获后吞掉，`OnPointerClick` 从这里直接返回。

**规矩**：
* 任何挂在**第三方组件的点击/事件/回调**上的自定义代码，**整体 `try/catch`**，
  并在 catch 里至少 `Log.Warn("…异常: " + e)`（`Log.Warn` 不会触发 ONI 的 mod 出错判定）。
* 探针要**打两侧**：`Prefix`（进入）与 `Postfix`（返回），中间缺哪一段就说明异常在那一段。
* 判断"官方逻辑有没有跑"比猜它的语义快得多 —— 先确认**有没有被调用**，再研究**为什么没生效**。
### 10.6h 踩坑（**最贵的一条**）：`Debug.LogError` 会让游戏**自动禁用你的 mod**

Player.log 实证：`ONI AI Bridge's assembly declared the method OniAiBridge.Log:Error in the stack trace,
adding to referenced mods list` ⇒ 随后弹「游戏出现错误」屏，并把 `mods.json` 里本 mod 的
`enabled` 改成 `false`、`crash_count` +1。**看起来像"崩溃"，其实是日志级别用错。**

**规矩**：
* 断言失败、写盘失败、找不到对象、任何**非致命**问题 ⇒ 一律 `Debug.LogWarning` / `Log.Warn`；
* `Debug.LogError` 只留给"这个 mod 真的不该继续跑"；
* 本 mod 的 `Log.Error` 已改为内部走 `LogWarning`（保留 API 兼容），新代码不要用它。

### 10.6g 实证：`MultiToggle` 的点击顺序是 **onClick 先、NextState 后**

IL（Assembly-CSharp，均为本机反编译）：
```
NextState()            = ChangeState((state + 1) % states.Length)          // 24 字节
ChangeState(int,bool)  = if (force) stateDirty = false; ChangeState(state)
ChangeState(int)       = if (!stateDirty && arg == state) return;           // 守卫
                         stateDirty = false; state = arg;
                         套 toggle_image.sprite/color → rect_margins → additional_display_settings
                         → RefreshHoverColor()
OnPointerClick         = AllowClick → (onDoubleClick/onClick.Invoke()) → 最后 NextState()   // 98 字节
```
⇒ **在 `onClick` 回调里读到的 `CurrentState` 一定是"点击前"的值**，状态推进在回调返回之后才发生。
**正确用法**：回调里只算目标值（`(cur+1)%len`）并写盘，翻转交给官方 `NextState()`；
下一帧**断言** `CurrentState == target`，不符就报错（可用官方 `ChangeState(target)` 纠正一次），
**不要**在回调里自己 `ChangeState`（会和官方那次叠加，导致状态与写盘值不一致）。

### 10.6f 踩坑：**`worldPos` 不等于屏幕像素坐标**

实机：日志 `checkbox worldPos=(2232.52, 958.95)`，而它实际落在屏幕 `(2232, 1207)` —— **垂直差约 250px**。
原因：canvas `scaleFactor = 1.33`，且 dialog 根下还有一层 `Layout` 偏移。

⇒ **写 UI 自动化/点击定位时不要用 `Transform.position`**：
* 要屏幕坐标就用 `RectTransformUtility.WorldToScreenPoint(canvas.worldCamera, worldPos)`；
* 或者直接截图按像素找（我们用 `desktop_control`/`screen_view` 的做法）；
* 反过来，要"把控件放到某处"就用世界坐标定位再让 Unity 自己换算（M4.2i 起就是这么做的）。

### 10.6e 判据：**挂控件前先看父级有没有 `LayoutGroup`**

有 `LayoutGroup` 的父级会**接管所有直接子节点**的尺寸与位置：我们设的 `sizeDelta`/`anchoredPosition`
会被重算（实测 `content` 把我们 24×24 的勾选框重置成 0×0，而挂在只有 `LayoutElement` 的
`official-row` 里尺寸能保住）。

**探针要打这一条**（M4.2i 之前一直漏了）：
```
[probe] content components: RectTransform, CanvasRenderer, VerticalLayoutGroup, ... LayoutGroupCount=1  ← 会接管子级尺寸/位置
[probe] dialog-root components: ... LayoutGroupCount=0
```
**选择父级的顺序**：dialog 根（无 LayoutGroup）&gt; 普通容器 &gt; 有 LayoutGroup 的容器（必要时配合 `LayoutElement.preferredWidth/Height`）。

### 10.6d 踩坑：**`ForceRebuildLayoutImmediate` 不是万能补丁**

M4.2f 的探针数字：`Content` 的 `rect = 0×0`、`anchorMin = anchorMax = (0,0)`、`sizeDelta = (0,0)`。

**它只能让"已经有 size 的父级"把子级排好**；父级自己没有 size 来源时（本例：content 靠
`InfoDialog` 的 `OnShow`/`OnActivate` 或上层 `Layout` 组件喂尺寸），`ForceRebuild` 再怎么调，
排出来的仍然是 0 尺寸，控件自然看不见。

⇒ **稳妥做法是"延到 dialog 显示完再挂控件"**（`MainThread.Enqueue` 到下一帧、或在 `OnShow` 之后再挂），
`ForceRebuild` 只作加速/兜底，不能当根因解法。

判据（怎么知道布局算完了）：`content.rect.width > 0 && content.rect.height > 0`，且子行的 `rect` 也非 0。

### 10.6b 更正：**"必须官方 prefab"这条判断是错的**（M4.2c 反转）

**旧判断（§10.5/§10.6，已作废）**：以为自建控件一定崩，所以必须克隆官方 prefab。

**实际根因（有 IL 证据）**：
| 现象 | 真正原因 |
|---|---|
| `KButton.UpdateColor` NullReferenceException | `KButton` 的 `ColorStyleSetting` 字段为空 —— M4.1b 裸造时没填 |
| `LocText` NRE（`ApplySettings`） | `LocText.textStyleSetting` 为空（PLib 用它的 `Fonts.UILightStyle` 填） |
| 控件不显示 / 点不动 | 对象没设 `layer = LayerMask.NameToLayer("UI")`、没设 anchors/localScale |

**反证**：PLib 的 `PCheckBox` **本身就是自建**（`PUIElements.CreateUI` = `new GameObject` + `SetParent`
+ `localScale=one` + `SetAnchors` + 设 UI 层），它只用几行把这些字段填对，就有 20+ 个 mod 在正常工作。

**结论**：自建控件**可以**，前提是①②③齐全（UI 层/anchors、`ColorStyleSetting`、`TextStyleSetting`）；
`PCheckboxLite` 就是按这条写的最小实现（见 CHANGELOG 0.11.0）。

### 10.7 沙盒模式判断入口（已验证）

| 入口 | 类型/签名 | 语义 |
|---|---|---|
| `SaveGame.Instance` | `public static SaveGame Instance` | `SaveGame` 单例 |
| `SaveGame.sandboxEnabled` | `public bool` 字段 | **存档级**："这个档是沙盒档" |
| `Game.sandboxModeActive` | `private bool` 字段 | 运行时字段 |
| `Game.SandboxModeActive` | `public bool` 属性（get/set） | **运行时**沙盒工具激活态；`set_SandboxModeActive` 的 IL 会 `Trigger(0x8BE14953)` 并刷新 `PlanScreen/BuildMenu/OverlayMenu/ManagementMenu` |
| `SandboxSettings.InstantBuild` | `public bool` 字段 | 沙盒面板里的"瞬建"勾选 |

建议闸门表达式：

```csharp
bool sandbox = (SaveGame.Instance != null && SaveGame.Instance.sandboxEnabled)
            || (Game.Instance != null && Game.Instance.SandboxModeActive);
bool allowed = settings.AllowCheats && (sandbox || DebugHandler.enabled);
```

### 10.8 `/v1/commands` 不再藏命令（M4 需求）

* 永远列出全部 op，每条带 `requiresCheats`、`available`、`unavailableReason`（机器可读码）
  以及 `unavailableMessage`（人类文案）。
* 两个闸门各自对应一个错误码，文案要直白：
  * `403 ALLCHEATS_DISABLED` → 「allcheats 未启用：把 `settings.json` 的 `allowCheats` 改成 true（或用游戏内 mod 设置页勾选）」
  * `403 SANDBOX_OR_DEBUG_REQUIRED` → 「需要游戏处于沙盒模式或 debug 模式」（因为这是给 AI 用的口子，比人类自己按键作弊卡得更严）

### 10.9 其它 mod 的设置页做法（本机实测盘点）

扫描范围：`mods\Steam\` 下**全部 29 个含 dll 的 mod**。判据是 dll 原始字节里的元数据字符串
（`ManageButton` / `MODS::MANAGE(_LOCAL)` / `BuildDisplay` / `ModsScreen` / `manage_tooltip` / `on_managed`
/ `is_managed` / `KModalScreen` / `HarmonyPatch` / `GetProperty`）。命中 ≠ 用，所以对两条代表性 mod 又做了 IL 复核。

| mod（工坊 id） | 有配置页 | 路线 | 证据 |
|---|---|---|---|
| DGSM - Duplicants Generation Settings (1838445101) | 有 | **B** | `[HarmonyPatch(typeof(ModsScreen),"BuildDisplay")]`（特性 blob）+ `Postfix` IL 里 `call AddSmallButton`；自有 `Ony.UnityUI.*` UI 库 |
| SmartDig (1844888487) | 有 | **B** | 与 DGSM 同作者，同一套 `Ony.*` 辅助库（命中数完全一致） |
| Fast Save (1867707267) | 有 | **B（经 PLib）** | 类型清单里有 `PeterHan.PLib.*`（含 `PAction/PToggleData/PKeyBinding` 等），`BuildDisplay`/`ModsScreen` 字样来自 **打包进来的 PLib**，不是手写 |
| Queue For Sinks (1881925531)、AI Improvements (2128885110)、Pip Plant Overlay (2493100777) 等 PeterHan 系 | 有 | **B（经 PLib）** | 同上，`PeterHan.PLib` 被打进各自 dll |
| Suppress Notifications (1832319118)、No Research Alerts (1872885565)、Better Automation Overlay (1878896484)、Fast Save 之外的 Aze 系 | 有 | **B** | `BuildDisplay`+`ModsScreen`+`GetProperty`+`AddComponent` 命中（与 PLib 同构的骨架） |
| Mod Updater (2018291283) | 有 | **B** | `BuildDisplay` 命中 3 次、`ModsScreen` 4 次（同一 patcher 里多处引用） |
| Rooms Expanded (2348079481)、Blueprints fixed (2435244304)、True Tiles (2815406414)、Decor Pack I (2660131829)、Magpie Bridge (2861126557)、Darkness Not Excluded (2964969010)、Natural Construction (3749231019) | 有 | **B** | 均有 `BuildDisplay`/`ModsScreen` 命中 |
| No Leaky Walls / Gigawatt Wire / No Zone Tint / Material Selection / Wrangle & Carry / LadderReplace / 取水大爷量杯 / Drywall… | 无 | 只用 Harmony 做别的事 | 只有 `HarmonyPatch`（无 `ModsScreen`/`BuildDisplay`） |
| Build Over Plants (1835394161)、传送端口 (3519386331) | 无 | 无 | 无任何相关字样 |

**结论（一句话）**：**方案 B 是事实标准**——所有有配置页的已装 mod 都走 `ModsScreen.BuildDisplay` 那条路
（多数是把 PeterHan 的 PLib 直接打进 dll，PLib 里有现成的 mod 选项按钮实现）；
**方案 A（反射设 `on_managed`）在本机 29 个 mod 里没有一个是主入口**——`manage_tooltip`/`on_managed`
只出现在 Ony 系的两个 mod 里，而那两个 mod 的入口实际是 B。

**对 M4 的影响（按你的拍板执行，不擅自改方案）**：A 不是"明确不可用"，只是**没有前人验证**；
所以 A 主 + B 兜底的策略正好——A 失败会被 try/catch 捕获并自动落到 B，而 B 这条路在本机有 20 个成功样本。
另外 `mod.yaml` / `mod_info.yaml` 里**没有任何与配置页相关的键**（只有 `title/description/staticID` 和
`supportedContent/minimumSupportedBuild/version/APIVersion`），所以"点选项按钮"不可能靠 yaml 声明，
必须靠代码挂钩子——这条也反证了上面的 B 路线。

### 10.10 M4.1 的入口为什么看不见（IL + 日志实测复盘）

**现象**：方案 A 反射成功（日志 `设置页入口：官方「管理」按钮（方案 A 成功）`、`/v1/meta.settingsUi.kind=ReflectionManageHook`），
但主菜单 Mods 屏里滚到底也找不到 `ONI AI Bridge` 这一条。

#### (1) `ModsScreen.BuildDisplay()` 遍历的集合是什么（IL 原文）

```
+82  7B  Global::modManager
+87  7B  KMod.Manager::mods        ← 集合来源 = manager.mods（全量），不是按平台过滤的子集
+8D  6F  get_Item
+9F  28  ModsScreen::ShouldDisplayMod
     （后面 brfalse 跳过不显示的条目）
+667 7B  Global::modManager        ← 同一个集合被**第二次**遍历（第二个分区/循环）
+66C 7B  KMod.Manager::mods
```

`ModsScreen.ShouldDisplayMod(Mod mod)` 的 IL（29 字节，全部符号）：

```
ldarg.1; ldfld KMod.Mod::status; brfalse …      ← status 为 0 直接不显示
        ldfld KMod.Mod::status; ldc.i4.2; bgt/…  ← 再比一个枚举常量
        callvirt KMod.Mod::HasOnlyTranslationContent; ldc.i4.0; ceq; ret
```

关键结论：**过滤条件只有 `status` 和 `HasOnlyTranslationContent()`，没有 `distribution_platform` 过滤。**
`KMod.Mod::get_IsLocal` 在 `BuildDisplay` 里**只用来选按钮文案**（`+531 IsLocal` → `+538 MODS::MANAGE` / `+53F MODS::MANAGE_LOCAL`），
不参与"显不显示"。

`HasOnlyTranslationContent()` 的 IL：`get_available_content(); <常量>; ceq`；`HasContent()` 是 `get_available_content(); ldc.i4.0; cgt`
⇒ 判定依据就是 `available_content` 这个位标志。

#### (2) 我们的 mod 在 manager 里是什么状态（Player.log 实证）

```
[08:56:05.510] ONI AI Bridge has a mod.yaml with a staticID `OniAiBridge`, using that from now on.
[08:56:05.511] ONI AI Bridge: Successfully loaded from path 'root' with content 'DLL'.   ← 和别的 mod 同样的成功行
[08:56:07.084] Loading mod content DLL [ONI AI Bridge:OniAiBridge] (provides DLL)
[08:56:08.631] Subscribe to mod ONI AI Bridge                                            ← 已被订阅（进入 manager）
```

⇒ 我们**不是"没装上"**：`available_content` 含 DLL、已 Subscribe。按上面的过滤条件**理论上应当显示**。
所以"看不见"的原因只剩两种可能：① 它落在 `BuildDisplay` 的**第二个循环/分区**里，而那个分区在 UI 上更靠下或被折叠；
② 某处 UI 逻辑（不在 `ShouldDisplayMod` 里）把本地平台条目隐藏了。**这两种都必须靠运行时数据判定，不能靠猜。**

#### (3) 有没有独立的"本地 mod"界面（元数据普查）

* 名字里带 `ModsScreen` 的类型：**只有 `ModsScreen` 一个**（没有 `LocalModsScreen` 之类）。
* 名字里带 `LocalMod` 的类型：**不存在**。
* `KMod.Local` 只是"分发平台"实现（`folder` / `GetDirectory()` / `Subscribe()`），不是界面。

⇒ **本地 mod 没有独立管理界面**，只能出现在这个 `ModsScreen` 列表里。

#### (4) `mods.json` 的 `enabled:false` 到底管不管用

* 实测：`mods.json` 里 **30 条全部 `enabled:false`**（24+ 个 Steam mod 也都是 false），而它们的 DLL 照样加载。
  ⇒ 这个字段**不是"是否加载/是否显示"的开关**。
* 真正的开关是 `enabledForDlc` 数组：我们的条目是 `[]`（空数组），Steam mod 基本都是 `["EXPANSION1_ID"]`。
  `KMod.Manager.Load` 的 IL 里出现的是 `Mod::enabledForDlc`、`Mod::enabled`、`SetEnabledForDlc`、
  `IsEnabledForActiveDlc`、`SetEnabledForActiveDlc`（多次）——即**按 DLC 维度**判断启用。
* 但日志证明：`enabledForDlc: []` 的我们**仍然被加载了**（Gigawatt Wire 也是空数组，它被跳过是因为**缺 mod_info.yaml**，
  日志原文 `Is missing a mod_info.yaml file and will not be loaded`）。
  ⇒ 本地 mod 空 DLC 列表 ≠ 禁用；至少"加载"不受影响。

#### (5) 候选方案（成本 / 风险，按我推荐的顺序）

| 方案 | 做法 | 成本 | 风险 | 评价 |
|---|---|---|---|---|
| **先做诊断** | 在 `/v1/meta` 加 `mods[]`：对 `Global.Instance.modManager.mods` 每一条报 `title/staticID/platform/status/available_content/IsLocal/HasOnlyTranslationContent()/on_managed!=null` | **小（~30 行）** | 无（纯只读） | **强烈建议先做**：一次实测就能定案"我们的条目到底在不在待渲染集合里、被哪个条件挡了"，不用继续猜 |
| **B2** 游戏内/主菜单挂按钮 | 往 `PauseScreen`/`MainMenu` 之类已有 UI 里加一个 `KButton`，或自己 `KScreen` 类 | 中（与已写好的 `BridgeOptionsScreen` 复用，只需找一个稳定宿主） | 中：宿主屏的生命周期/是否随时存在要验 | **推荐**：最直观，用户一定看得到 |
| **C2** 快捷键 | 在 `Game.Update` postfix（我们已有）里轮询 `Input.GetKeyDown`，或走 `InputManager` 注册 | **最小（~15 行）** | 低；但用户要知道按哪个键 | **推荐作为兜底**：零 UI 依赖，装上就能用 |
| **A2** 往 mod 列表里插自己的条目 | Harmony postfix `BuildDisplay`，自己构造一整行（图标/标题/开关/按钮） | **大**：要复刻 `DisplayedMod` 那一行的结构与交互 | 高：每次屏幕重建都要重插；且**根因未明**时等于绕过问题 | 暂不推荐（最后考虑） |
| **D2** 放弃 UI，只留 json | — | 0 | — | 作为 A2/B2/C2 全部失败时的退路 |

**M4.1d 结论：方案 A 只在 Steam 平台成立（实机证实）**。诊断输出显示本地 mod 一切正常
（`platform=Local, status=Installed, availableContent=DLL, shouldDisplayMod=true, onManaged=true, manageTooltip=true`，
`shouldDisplayTrue=30/30`），但**点列表里我们的条目弹出的是资源管理器**（foreground=`…\mods\Local\OniAiBridge (explorer)`）
⇒ `ModsScreen` 对 `IsLocal` 条目渲染的是「浏览」按钮，`on_managed` 根本没被用上。
**发布到创意工坊后 platform=1，才会出现官方「选项/管理」按钮，那时 A 应当生效**；开发期只能靠热键。

**M4.1c 的实际做法（v0.8.0）**：设置页改用**官方对话框 prefab**
（`ScreenPrefabs.Instance.InfoDialogScreen` + `KScreenManager.StartScreen(prefab, GameScreenManager.ssOverlayCanvas)`），
只读展示 + 「关闭」；`UiBuilder`（裸造 KButton/LocText）已删除，那正是 M4.1b 崩溃的根因
（`KButton.UpdateColor`/`OnSpawn` NRE，控件依赖 prefab 侧初始化）。方案 B 暂缓。

**已实现（M4.1b，v0.7.0）**：`GET /v1/debug/mods` 诊断端点 + `Ctrl+Shift+O` 热键（`Global.Update` 与 `Game.Update` 双挂，
主菜单与游戏内都能开）。`B2 宿主按钮` 与 `A2 插条目` 仍未做，等诊断结果决定是否还需要。

**建议的执行顺序（原计划）**：`诊断字段`（读一次就知道真相）→ `C2 快捷键`（最小成本，立刻可用）→ `B2 宿主按钮`（体验最好）。
A2 只有在诊断显示"我们的条目确实被排除、且无法用一条反射修好"时才考虑。
