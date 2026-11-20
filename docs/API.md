# ONI AI Bridge — HTTP API v1（草案）

> 这份文档假设你**完全不知道**本项目是什么：只要《缺氧》在跑、mod 已装，你就能照着这里写客户端。
> 当前实现阶段与版本号见 [`CHANGELOG.md`](../CHANGELOG.md) 顶部条目；本文件里标 ⚠️ 的才是还没实现的。
> **验证状态**（哪条端点真机验过、哪条只有编译）见 [`VERIFIED.md`](VERIFIED.md)。两个 ⚠️ 意思不同：
> 本文件的 ⚠️ = **未实现**；VERIFIED.md 的 ⚠️ = **没有运行期证据**。

## 0. 30 秒上手

1. 装 mod，进游戏并载入一个殖民地。
2. mod 第一次启动会生成随机 token，写在这里：
   `%USERPROFILE%\Documents\Klei\OxygenNotIncluded\mods\config\OniAiBridge\settings.json`
3. 端口默认 8770（被占用会顺延 8770..8779），实际端口看 `/v1/meta` 或游戏日志 `[OniAiBridge] listening`。
4. 第一个请求：

```bash
curl -s -H "X-Oni-Token: $ONI_TOKEN" http://127.0.0.1:8770/v1/meta
```

```json
{
  "apiVersion": "1.1",
  "ok": true,
  "seq": 3,
  "serverTime": "2026-11-14T15:40:00+08:00",
  "data": {
    "mod": {
      "name": "ONI AI Bridge", "version": "0.2.0", "staticID": "OniAiBridge", "stage": "M1",
      "compiledAgainstGameBuildText": "U59-744825-S", "compiledAgainstGameBuild": 744825
    },
    "game": {
      "buildText": "U59-744825-S", "buildVersion": 744825,
      "buildTextSource": "BuildWatermark.GetBuildText()",
      "unityVersion": "6000.3.5f2",
      "unityBundleVersion": "1.0",
      "unityBundleVersionNote": "Unity bundleVersion is hardcoded to 1.0 in ONI and is NOT the game build; use buildVersion/buildText"
    },
    "server": { "port": 8770, "loopbackOnly": true, "rateLimitPerMin": 0 },
    "pumpReady": true,
    "colonyLoaded": true,
    "cheatsAllowed": false,
    "debugModeOn": false,
    "capabilities": { "state": true, "events": false, "map": false, "catalog": false, "commands": {} }
  },
  "error": null
}
```

**`data.mod.version` 是 mod 的版本，`data.game.*` 是游戏版本**，两者无关，别拿错
（`unityBundleVersion` 恒为 `1.0`，那是 Unity 的 bundleVersion，**不是**游戏 build）。

## 1. 约定

* 监听 **`127.0.0.1`**（只回环）。
* 鉴权：`X-Oni-Token: <token>` 请求头（本机调试也接受 `?token=`）。缺失/错误 → `401`。
* 响应统一信封：

```json
{ "apiVersion": "1.1", "ok": true|false, "seq": 1234, "serverTime": "...", "reqId": "...", "data": {...}|null, "error": null|{...} }
```

* `seq` 单调递增（每次响应都 +1），用于去重/排序。
* **`reqId`（0.96.1）**：请求可带 `X-Oni-Req-Id: <id>`（或 `?reqId=`），服务端清洗后原样回显在这个字段里；
  没带就是服务端生成的 `srv-…`。**同一个 `reqId` 的 POST/PUT 重发 ⇒ 服务端回放上次响应、不再执行**
  （响应头 `X-Oni-Replayed: 1`）。详见 §3.41。它和 `seq` 不是一回事：`seq` 是"第几次响应"，
  `reqId` 是"哪一次请求"。
* **每个数据块自带 `ts`**（采集时刻）；同一响应里各块共享一次主线程采集，所以 `ts` 通常相同。
* 单位陷阱（**必读**）：
  * `amounts.Calories` 是原始游戏值，**÷1000 = 界面 kcal**；另给 `amounts.CaloriesKcal`。
  * `amounts.Temperature` 是开尔文；另给 `amounts.TemperatureC`。
* **刚载入殖民地且游戏处于暂停时**，`amounts` 里模拟驱动的值（Calories / Temperature / Bladder…）会是 **0 或缺失**，
  因为模拟还没跑过一帧、这些值尚未初始化。**这不是 bug**：取消暂停后下一次 `/v1/state` 就正常。
  调用方可以用 `colony.isPaused` 区分这种情况，不要把"首次读到 0"当异常处理。
  * `colony.cycle` 已 +1 与界面顶栏一致；`colony.cycleRaw` 是游戏内部值（两者恒差 1）。

## 2. 端点表

| 方法 | 路径 | 说明 | 状态 |
|---|---|---|---|
| GET | `/v1/meta` | 版本、能力集、端口、双闸门状态（主菜单也能答） | ✅ M0/M2 |
| GET | `/v1/buildings` | **场上建筑清单**（prefabId/name/x/y/cell/worldId/operational/enabled），带迷雾过滤、分页 | ✅ M5.4 |
| GET | `/v1/catalog` | **只读建筑目录**：prefabId/中文名/宽高/层/分类/顶层分类（内存缓存） | ✅ M5.2 |
| GET | `/v1/state` | **主入口**：colony + dupes + elements | ✅ M1 |
| GET | `/v1/events` | **SSE 推送**：snapshot / diff / alert / heartbeat | ✅ M2 |
| GET | `/v1/commands` | 可用操作 + 参数 schema | ✅ M2 |
| POST | `/v1/command` | 执行操作（仅普通操作） | ✅ M2 |
| GET | `/v1/health` | 存活 + 泵状态 | ✅ M0 |
| GET | `/v1/debug/mods` | **诊断**：`modManager.mods` 每条的原生状态 + `ShouldDisplayMod` 结果 | ✅ M4.1b |
| GET | `/v1/dupes/{id}` | 单个复制人详情 | ⚠️ |
| GET | `/v1/map` | **地图降采样**：`bbox` + `stride` 必填，三通道（元素 id/温度/质量），单次硬上限 64×64 格；**默认关**（`allowMap`） | ✅ M6.89（0.93.5） |
| GET | `/v1/catalog/{kind}` | 静态表（buildings/elements/techs） | ⚠️ |
| GET | `/v1/journal` | **请求对账**（0.96.1）：`?reqId=` 查单条（含响应体）/ `?since=&limit=` 列账目 | ✅ 实现 / ⚠️ 未装机 |

## 3. `GET /v1/state`

```bash
curl -s -H "X-Oni-Token: $ONI_TOKEN" http://127.0.0.1:8770/v1/state
```

```json
{
  "apiVersion": "1.1", "ok": true, "seq": 12, "serverTime": "2026-11-14T15:52:10+08:00",
  "data": {
    "colony": {
      "ts": "2026-11-14T15:52:10+08:00",
      "cycleRaw": 4, "cycle": 5,
      "timeSinceStartOfCycle": 132.5, "timePlayed": 6875.2, "frame": 730953,
      "isNight": false, "isPaused": true, "speed": 0,
      "worldId": 0,
      "world": {
        "name": "expansion1::worlds/VanillaSandstoneDefault",
        "typeKey": "STRINGS.WORLDS.VANILLASANDSTONEDEFAULT.NAME",
        "isStartWorld": true, "size": [240, 380]
      }
    },
    "dupes": {
      "ts": "2026-11-14T15:52:10+08:00",
      "count": 4,
      "items": [
        {
          "ts": "2026-11-14T15:52:10+08:00",
          "name": "卡塔莉娜", "dbId": "CATALINA", "gender": "FEMALE", "cell": 12345,
          "chore": { "text": "挖掘", "id": "Dig" },
          "amounts": {
            "Stress": 0, "Calories": 3063737.5, "Temperature": 309.954,
            "Bladder": 49.5, "Stamina": 69.1, "Breath": 100, "HitPoints": 100,
            "CaloriesKcal": 3064, "TemperatureC": 36.804
          }
        }
      ]
    },
    "elements": {
      "ts": "2026-11-14T15:52:10+08:00",
      "count": 28, "unit": "kg", "minKg": 1,
      "items": { "SandStone": 35025.6, "Dirt": 32872.6, "Water": 10954.6, "Cuprite": 5459.2 }
    }
  },
  "error": null
}
```

字段说明：

* `colony.speed` 是**对外语义 1|2|3**（=1x/2x/3x）；游戏内部的 0 基原始值在 `colony.speedRaw`（0|1|2）。
  判断暂停请直接看 `colony.isPaused`。
* `dupes.items[].chore` 可能为 `null`（这个复制人当前没有活儿）；
  `chore.text` 是游戏自己的报告名（`Chore.GetReportName()`），`chore.id` 是 `ChoreType.Id`（可能为 `null`）。
* `elements.items` 只含 **kg ≥ `minKg`（默认 1）** 的元素，按 kg 降序，**最多 40 条**；
  被截断时带 `"truncated": true`，`count` 是截断前的真实条目数。
* 某一块读失败时，**该块内**会出现 `"error": "<异常类型>: <消息>"`，其它块照常返回（不会整包失败）。

### 状态码（`/v1/state` 特有）

| HTTP | code | 场景 |
|---|---|---|
| 503 | `NOT_READY` | 主线程泵还没跑起来（Harmony 补丁未生效 / 未载入殖民地） |
| 504 | `TIMEOUT` | 采集在主线程 3000 ms 内没完成（游戏卡顿/读档中） |
| 500 | `INTERNAL` | 采集返回空 |

## 3.5 `GET /v1/events`（SSE 推送，M2）

```bash
curl -N -H "X-Oni-Token: $ONI_TOKEN" http://127.0.0.1:8770/v1/events
# 断线续传：带上上次收到的最后一个 id
curl -N -H "X-Oni-Token: $ONI_TOKEN" -H "Last-Event-ID: 42" http://127.0.0.1:8770/v1/events
# 或 curl -N ".../v1/events?since=42"
```

服务端每 1000 ms 采样一次，变化才推。事件类型：

| event | data | 说明 |
|---|---|---|
| `snapshot` | 与 `/v1/state` 的 `data` 同构 | 连接（或续传补发）后立即给一份完整状态 |
| `diff` | `[{path,from,to}` 或 `{kind,id,...}]` 数组 | 字段级变化 |
| `alert` | `{"kind":"added|removed","id":"stress_high:ELLIE"}` | 阈值告警集合变化 |
| `heartbeat` | `{"seq":N}` | 15 s 无事件时保活 |

`diff` 里的 `path` 形式：`colony.cycle`、`colony.isPaused`、`colony.speed`、
`dupes.<dbId>.amounts.<id>`、`dupes.<dbId>.cell`、`elements.<tag>`；
生命周期用 `kind`：`dupe.spawned` / `dupe.died` / `dupe.chore`。

阈值（可在 `settings.json` 覆盖）：`alertStress`=60、`alertCaloriesKcal`=1500、`alertBladder`=90；
属性变化推送的最小幅度 `amountEpsilon`=0.1。服务端保留最近 256 条事件，最多 8 个并发订阅者。

## 3.6 命令（M2：只做普通操作）

```bash
curl -s -H "X-Oni-Token: $ONI_TOKEN" http://127.0.0.1:8770/v1/commands   # 先看有什么
curl -s -X POST -H "X-Oni-Token: $ONI_TOKEN" -H 'Content-Type: application/json' \
     -d '{"op":"pause"}' http://127.0.0.1:8770/v1/command
curl -s -X POST -H "X-Oni-Token: $ONI_TOKEN" -H 'Content-Type: application/json' \
     -d '{"op":"setSpeed","args":{"speed":3}}' http://127.0.0.1:8770/v1/command
curl -s -X POST -H "X-Oni-Token: $ONI_TOKEN" -H 'Content-Type: application/json' \
     -d '{"op":"setChorePriority","args":{"dupe":"CATALINA","priority":9,"priorityClass":"topPriority"}}' \
     http://127.0.0.1:8770/v1/command
```

| op | args | 说明 |
|---|---|---|
| `pause` | `{}` | 暂停（已暂停则 `changed:false`，幂等） |
| `resume` | `{}` | 取消暂停 |
| `setSpeed` | `{"speed":1\|2\|3}` | 1x/2x/3x（内部 0 基，接口已转换）；**不改变暂停状态**；执行后回读校验，没生效回 `409 NOT_APPLIED` |
| `setChorePriority` | `{"dupe":"dbId 或名字","priority":1..9,"priorityClass":"idle\|basic\|high\|personalNeeds\|topPriority\|compulsory"}` | 改该复制人**当前任务**的优先级；没有任务时 `404 NOT_FOUND_TARGET` |

作弊/内置 Debug 类操作（`debugInstantBuild` 等）**尚未开放**，`/v1/commands` 里不会出现。

## 3.7 `/v1/meta`（M2 起主菜单也能答）

* `buildText` / `unityVersion` / `unityBundleVersion` 来自 **OnLoad 时缓存**，不依赖主线程泵；
  主菜单下也能拿到。取不到时为 `null`（不编造）。
* `colonyLoaded` 未知时给 **`false`**（M1 曾给 `null`，客户端不好判断）；`debugModeOn` 同理。
* `server.sseSubscribers` / `server.lastEventSeq` 便于排查订阅与续传。
* `settingsUi` 里有 `available` / `kind` / `detail` / **`hotkey`**：`kind` 可能是
  `ReflectionManageHook`（官方「管理」按钮）/ `HarmonyModsButton`（自加按钮）/ `Unavailable`。
  **无论 `kind` 是什么，`hotkey`（默认 `Ctrl+Shift+O`）都能在游戏内与主菜单唤出设置页** ——
  **更正（M4.1d 实机）**：Local 条目其实在列表里（`shouldDisplayMod=true`），只是 `ModsScreen` 对
  `IsLocal` 条目渲染的是**「浏览」按钮（点开资源管理器）**，`on_managed` 不会被用上 ⇒
  **方案 A 只在创意工坊（Steam 平台）成立**；开发期热键 `Ctrl+Shift+O` 是唯一入口。
  设置页本身用**官方对话框 prefab**（`ScreenPrefabs.Instance.InfoDialogScreen`）显示，只读 + 一个「关闭」。
* **主菜单也能查**：`Global.Update` 也在推进主线程泵（M4.1c 起），所以主菜单下 `/v1/state` 会返回
  `colonyLoaded:false` 的正常结构，`/v1/debug/mods` 也能用（此前会误报 503 `NOT_READY`）。


```json
{ "apiVersion": "1.1", "ok": false, "seq": 13, "serverTime": "...", "data": null,
  "error": { "code": "UNAUTHORIZED", "message": "missing or wrong X-Oni-Token (see settings.json)", "details": {} } }
```

| HTTP | code | 场景 |
|---|---|---|
| 400 | `BAD_REQUEST` | 请求行/JSON 坏了 |
| 401 | `UNAUTHORIZED` | token 缺失或错误 |
| 403 | `ALLCHEATS_DISABLED` | 没开 `allowCheats`（「allcheats 未启用」）；M4 起用它取代 `CHEATS_DISABLED` |
| 403 | `SANDBOX_OR_DEBUG_REQUIRED` | `allowCheats` 开了，但存档不是沙盒档且 Debug 模式未开；取代 `DEBUG_MODE_OFF` |
| 403 | `CAPABILITY_UNAVAILABLE` | 本游戏版本里找不到该调试入口 |
| 404 | `NOT_FOUND` / `NOT_FOUND_TARGET` | 端点 / 目标不存在 |
| 405 | `METHOD_NOT_ALLOWED` | M1 只支持 GET |
| 429 | `RATE_LIMITED` | 超频（尚未实现限流） |
| 500 | `INTERNAL` | 未预期异常 |
| 503 | `NOT_READY` | 见上 |
| 504 | `TIMEOUT` | 见上 |

## 3.8 作弊与存档安全（M3，默认关闭）

> ⚠️ **这一节的功能不是给正常游玩用的。** 它们全部是"调度游戏内置 Debug 工具"，
> 后果由官方调试工具本身决定：**会破坏存档一致性、部分不可撤销、可能影响成就/统计**。
> 强烈建议：**只在专门的测试存档里用**，用前手动备份 `save_files\`。

### 开启方式（M4 起是**三段式**闸门）

必须同时满足：
1. `settings.json` 里 `"allowCheats": true`（或用游戏内 mod 设置页勾选）—— 否则 `403 ALLCHEATS_DISABLED`；
2. 并且处于下列**任一**状态 —— 否则 `403 SANDBOX_OR_DEBUG_REQUIRED`：
   * `SaveGame.sandboxEnabled`（这个存档本身是沙盒档）
   * `Game.SandboxModeActive`（沙盒工具处于激活态）
   * `DebugHandler.enabled`（游戏 Debug 模式；可用下面的 `debug` op 打开）
3. 开启游戏内 Debug 模式（若你走的是第 3 条路）：

```bash
curl -s -X POST -H "X-Oni-Token: $ONI_TOKEN" -H 'Content-Type: application/json' \
     -d '{"op":"debug","args":{"enable":true}}' http://127.0.0.1:8770/v1/command
```

`/v1/meta.gate` 会把四个状态逐个报出来，客户端可以自己算；`/v1/commands` 里每条作弊 op 都带
`available` + `unavailableCode` + `unavailableReason`（**命令永远列出来，只是标成不可用**）。

### op 一览

| op | args | 底层入口 | 后果 |
|---|---|---|---|
| `debug` | `{"enable":true\|false}` 或 `{}`（查询） | `DebugHandler.SetDebugEnabled` | 开/关调试模式本身 |
| `setInstantBuild` | `{"enable":bool}` | `DebugHandler.ToggleInstantBuildMode` | 跳过材料与建造校验 |
| `setInvincible` | `{"enable":bool}` | `DebugHandler.InvincibleMode` | 复制人不受伤/不窒息 |
| `revealFog` | `{"enable":bool}` | `DebugHandler.RevealFogOfWar` | **不可撤销**（探索状态已改） |
| `spawnElement` | `{"element":"Water","cell":12345,"mass":1000,"temperature":293.15}` | `DebugPaintElementScreen` + `DebugTool.DoReplaceSubstance` | 直接改写模拟格子质量/温度，**不可撤销** |
| `teleport` | `{"dupe":"CATALINA","cell":12345}` | `Navigator.Stop` + `Transform.position` | 绕过寻路；可能被导航拉回，**实验性** |

示例：

```bash
curl -s -X POST -H "X-Oni-Token: $ONI_TOKEN" -H 'Content-Type: application/json' \
     -d '{"op":"spawnElement","args":{"element":"Water","cell":12345,"mass":1000,"temperature":293.15}}' \
     http://127.0.0.1:8770/v1/command
# => {"op":"spawnElement","applied":true,"cell":12345,"element":"Water","massAfter":1000,"massRequested":1000}
```

### 校验与失败语义

* 每个 op 执行后**回读**实际状态。回读不一致分两种，**别混**（0.93.9 起）：
  * **未确认**（`spawnElement` 这种"同一帧写、同一帧读"）⇒ `200 ok` + `applied:false` + `pending:true` + `readback{…}`：
    ```json
    {"op":"spawnElement","applied":false,"pending":true,"cell":135578,"element":"Water",
     "massRequested":25,"temperatureRequested":295,
     "readback":{"element":"Oxygen","mass":1.483,"temperature":297.118},
     "note":"同一帧回读：格子仍报旧元素…隔 ~1 秒回读本格确认…不要因为 pending 立即重发。"}
    ```
    **实测（0.93.9）**：该响应里 `readback.element=Oxygen`，1.2 秒后同一格回读是 `Water mass=25` ⇒ 元素确实写进去了。
    409 对 AI 是"可重试"信号，误报会导致重复下发，所以这里不再报失败。
  * **真失败**（回读拿到 `null` / 格子已不可用）⇒ `409 NOT_APPLIED`，仍然报失败。
  * 已确认写入 ⇒ `applied:true`（同格再刷同元素实测返回 `applied:true, massAfter:25`）。
* 参数严格校验：元素不存在 / 格子越界 / 质量非正 / 温度非法 → `400` 或 `404`，不静默降级
  （实测：`cell=99999999` ⇒ 400；`element=NoSuchElementXYZ` ⇒ 404；`mass=-5` ⇒ 400）。
* `spawnElement` 需要游戏里**打开过一次调试面板的元素绘制页**（`DebugPaintElementScreen.Instance`），
  否则 `503 NOT_READY` 并说明原因。
  ⚠️ **这个前置条件是"每会话"的**：`Instance` 只在游戏运行期间存在，**关游戏就没了**——
  每次重新开游戏后，都要再手动打开一次那个面板，`spawnElement` 才能用。
  （`0.93.9` 实测这条已不成立：开 debug 后直接就能刷，没有开面板。见 CHANGELOG。）
* `teleport` 用 `Transform` 定位（游戏没有"接受坐标的传送 API"），执行后立即回读 `Grid.PosToCell`；
  导航系统之后仍可能拉回，**请隔 1 秒用 `/v1/state` 复查 `cell`**。

### `pipeLink`：连接 / 切断一格管线或电线（0.94.7 起，精确单方向）

```bash
curl -s -X POST -H "X-Oni-Token: $ONI_TOKEN" -H 'Content-Type: application/json' \
     -d '{"op":"pipeLink","x":117,"y":217,"dir":"right","connect":false}' \
     http://127.0.0.1:8770/v1/command
```

参数：`x` / `y`（整格坐标）、`dir`（`left|right|up|down`）、`connect`（`true`=连 / `false`=断）、
可选 `layer`（`gasConduit|liquidConduit|solidConduit|wire`，本格有多个可连层时必须给）、
可选 `dryRun:true`（**纯读**，不改状态，用来事后复读）。

响应（节选）：
```json
{"op":"pipeLink","cell":138129,"layer":"wire","prefabId":"Wire","dir":"Right",
 "connectRequested":false,"alreadyInRequestedState":false,
 "via":"DisconnectTool.DisconnectCellsAction(cell, go, IHaveUtilityNetworkMgr, Right) [reflection]",
 "before":{"getter":3,"array":3,"networkId":0},
 "expectedGetter":1,
 "after":{"getter":1,"array":1,"networkId":0},
 "applied":true,"inconsistent":false}
```

#### 实现路径（两条，2026-11-18 真机验过）
| 动作 | 调用 | 说明 |
|---|---|---|
| **连** | `IUtilityNetworkMgr.AddConnection(UtilityConnections, int32 cell, bool isVirtual)` | public 接口方法，只**加**不减 |
| **断** | **反射** `DisconnectTool.DisconnectCellsAction(int32 cell, GameObject go, IHaveUtilityNetworkMgr mgr, UtilityConnections connections)` | `private` / **`instance`**；实例取 **`public static` 字段 `DisconnectTool.Instance`** |

两者都只动**单方向**（`UtilityConnections.Left=1 / Right=2 / Up=4 / Down=8`）。
方向语义：Left/Right = x-1/x+1；**Up/Down = y+1/y-1（ONI 的 y 向上）**。

#### 主判据
**`IUtilityNetworkMgr.GetConnections(cell, false)` == `expectedGetter`**（`expectedGetter` = 连 `before|dir` / 断 `before&~dir`）。
`after.array` 是 `physicalGrid[cell].connections` 存的那份，**只作辅证**；两者不等 ⇒ `inconsistent:true`（两个值都带出来）。
`networkId`（`GetNetworkForCell().id`）随网络重建可能变号，仅供参考。

⚠️ **`after` 是"动作后立刻"的读数**：本 op 跑在主线程里，**不能在里面 Sleep**（0.94.6 实测睡 3 秒会把主线程堵死，
心跳停 33 秒、后续请求全 `RETRY_LATER`）。要确认"游戏重建后有没有被改掉"，**隔 1s/3s 再发一次 `{"dryRun":true}`** 读本格即可：

```bash
# 上面那条断线之后，隔 1 秒复读（同参数 + dryRun）
-d '{"op":"pipeLink","x":117,"y":217,"dir":"right","connect":false,"dryRun":true}'
```

#### 校验（游戏侧不拦，必须我们自己拦）
`AddConnection` **不做任何校验** —— 实测往"右边没有线"的格子连，它照样接受（掩码 1→3），造出正常玩法不可能出现的悬空连接。所以本 op 在动作前拦：

| 错误码 | 条件 |
|---|---|
| `404 NOT_FOUND_TARGET` | 本格在该层上没有建筑（含：显式给了 layer 但该层是空的 ⇒ 跨层混连也走这条） |
| `400 NEIGHBOUR_NOT_CONDUIT` | 邻格在**同层**上没有对象（气/液/固/电不能混连） |
| `400 PREFAB_MISMATCH` | 两格 prefabId 不同（例如细线接粗线）—— 复现游戏标红拒绝的行为 |
| `400 AMBIGUOUS_LAYER` | 本格有多个可连层且没给 `layer` |
| `503 TOOL_UNAVAILABLE` | `DisconnectTool.Instance == null`（工具未实例化） |

#### 两个坑（都踩过）
1. **`DisconnectCellsAction` 第 3 参是 `IHaveUtilityNetworkMgr`（组件本体），不是 `IUtilityNetworkMgr`（管理器）**。
   传管理器 ⇒ `ArgumentException: Object of type 'UtilityNetworkManager\`2[...]' cannot be converted to type 'IHaveUtilityNetworkMgr'`。
   正确写法：`go.GetComponent<IHaveUtilityNetworkMgr>()` 拿到组件再传。
2. **`AddConnection` 不做校验、且只加不减**。判断它有没有生效，必须在"已断开"的格子上试；
   在"已连通"的格子上调它看不出任何变化（0.94.1 就是因此误判"它没用"）。

#### 实测记录（2026-11-18，Cycle-6 小档，`Wire` @ (117,217)/(118,217)，游戏放行）
| 操作 | getter 前→后 | 数组 | networkId | applied |
|---|---|---|---|---|
| 断 `dir=right` | 3 → **1** | 1 | 0 → 0 | true |
| 连 `dir=right` | 1 → **3** | 3 | 0 | true |
| 邻格空 `(126,217) dir=right` | — | — | — | `400 NEIGHBOUR_NOT_CONDUIT` |
| 跨层 `layer=gasConduit` | — | — | — | `404 NOT_FOUND_TARGET` |

断完 1s/3s 复读仍是 1；连完 3s 复读仍是 3（**没有被重建冲掉**）。

#### 为什么没有 `isDisconnected` / `bidirectional`
* `IDisconnectable.IsDisconnected()` 实测**恒为 false**，且与真实状态矛盾（`Connect()` 返回 true 而掩码不动就是它）⇒ 不可作判据，已删。
* `bidirectional` / `neighbourSynced` 基于"双向冗余"，而实测**双向冗余不成立**：`AddConnection` 只改本格，邻居那格的反方向位不跟着置位
  （往空白格连，邻居 getter 始终 0）⇒ 字段已删。

### 卸载/回滚

* 删掉 `mods\Local\OniAiBridge\` 文件夹即可；mod 不修改游戏文件、不改存档格式。
* **只有作弊 op 会改变存档内容**，那是主动操作；改坏了用游戏自带备份或云存档恢复。
* 想彻底关掉作弊面：把 `allowCheats` 设回 `false` 并重载 mod（或关掉游戏内 Debug 模式）。

## 4. 错误响应

```json
{ "apiVersion": "1.1", "ok": false, "seq": 13, "serverTime": "...", "data": null,
  "error": { "code": "UNAUTHORIZED", "message": "missing or wrong X-Oni-Token (see settings.json)", "details": {} } }
```

| HTTP | code | 场景 |
|---|---|---|
| 400 | `BAD_REQUEST` | 请求行 / JSON 坏了；缺 `op`；`/v1/map` 缺 `bbox` 或 `stride`、或格式不合（错误 `message` 里自带格式说明） |
| 400 | `BBOX_TOO_LARGE` | `/v1/map` 的 `bbox` 超过 64×64 格 / 次（`stride` 不豁免这个上限） |
| 400 | `BBOX_OUT_OF_BOUNDS` | `/v1/map` 的 `bbox` **完全**落在 Grid 之外（部分越界会被夹取，见 `clipped`） |
| 400 | `INVALID_ARG` | 参数越界（`speed` 不在 1..3、`priority` 不在 1..9、未知 `priorityClass`） |
| 401 | `UNAUTHORIZED` | token 缺失或错误 |
| 403 | `ALLCHEATS_DISABLED` | 没开 `allowCheats`（「allcheats 未启用」）；M4 起用它取代 `CHEATS_DISABLED` |
| 403 | `SANDBOX_OR_DEBUG_REQUIRED` | `allowCheats` 开了，但存档不是沙盒档且 Debug 模式未开；取代 `DEBUG_MODE_OFF` |
| 403 | `MAP_DISABLED` | `/v1/map` 没开 `allowMap`（**默认就是关的**）。该码先判，此时不看 `bbox`/`stride` 是否给了 |
| 404 | `NOT_FOUND` | 端点 / 未知 `op` |
| 404 | `NOT_FOUND_TARGET` | 指定的复制人不存在，或该复制人当前没有任务 |
| 409 | `NOT_APPLIED` | 命令执行了但**回读发现没真的生效**（例如请求 3 倍速、游戏仍是 1 倍速）。此时**不要**当成功处理 |
| 405 | `METHOD_NOT_ALLOWED` | `/v1/command` 必须用 POST |
| 429 | `RATE_LIMITED` | 超频（尚未实现限流） |
| 500 | `INTERNAL` | 未预期异常 |
| 500 | `MAP_UNAVAILABLE` | `/v1/map` 采集异常（Grid 尺寸拿不到 / 采集抛异常） |
| 503 | `NOT_READY` | 主线程泵没跑起来 / 未载入殖民地 / SSE 订阅者已满 |
| 503 | `RETRY_LATER` | 主线程忙（典型是存档还在加载），请求**未执行**；1 秒后重试。多个重查询端点（含 `/v1/map`）在主线程排队超时时回这个码 |
| 504 | `TIMEOUT` | 主线程 3000 ms 内没完成 |

## 5. 版本与兼容

* `apiVersion` = `major.minor`；**加字段/加端点 = minor，改语义/删字段 = major**。
* mod 版本（`data.mod.version`）与 API 版本独立；`CHANGELOG.md` 里破坏性变更单独一节写迁移方式。
* 客户端应检查 `apiVersion` 的 major。

## 6. 客户端最小示例（Python，仅标准库）

```python
import json, urllib.request
TOKEN = open("token.txt").read().strip()
def call(path):
    req = urllib.request.Request("http://127.0.0.1:8770" + path, headers={"X-Oni-Token": TOKEN})
    return json.load(urllib.request.urlopen(req))["data"]

meta = call("/v1/meta")
print("mod", meta["mod"]["version"], "game build", meta["game"]["buildVersion"])
st = call("/v1/state")
c = st["colony"]
print("cycle", c["cycle"], "paused", c["isPaused"], "world", c["world"]["name"])
for u in st["dupes"]["items"]:
    print(u["name"], round(u["amounts"]["CaloriesKcal"]), "kcal", "stress", u["amounts"]["Stress"], "chore", (u["chore"] or {}).get("text"))
print("top elements", list(st["elements"]["items"].items())[:5])
```

## 7. 尚未实现（诚实清单）

* ⚠️ `/v1/dupes/{id}`、`/v1/catalog/{kind}`（`buildings/elements/techs` 的**子路径版本**；`/v1/catalog` 主端点已实现）。
  （`/v1/map` 自 0.93.5 起已实现，见 §3.40；`/v1/events`、`/v1/commands`、`POST /v1/command` 早已实现。）
* ⚠️ `world.displayName`（本地化名）暂缺，只给 `name`（prefab id）与 `typeKey`（STRINGS 键）。
* ⚠️ 无限流（`server.rateLimitPerMin` 报 0）。
* ⚠️ 所有数值的**正确性只在编译期验证过 API 存在性**；运行时是否与游戏界面一致，等 M1 实测后更新本节。
### 3.10 视野迷雾（M5.3）

`/v1/meta` 新增两个字段：
- `aiEnableFog`（bool，配置原值，默认 `true`）
- `vision`（派生：`"fogged"` = 只报看得见的地方；`"omniscient"` = 全图）

**语义**：`AIEnableFog=true`（默认）时，AI 只应拿到"人类看得见"的场景数据；
**判据是 `Grid.Visible[cell] > 0`**（有过一点点亮度就算看见 —— 与人的感受一致，而不是"是否进过视野圆"的布尔）。
**与 `allowCheats` 独立**：作弊开关管"能不能动手"，这个管"能看多少"。

**已确认的游戏机制**（免得使用者困惑）：
- 可见度由 `GridVisibility` 组件在实体移动时累积写入（`Grid.Reveal` 取 `Max`，**只增不减**），
  边缘按 `Lerp(innerRadius→radius)` 衰减 ⇒ `Grid.Visible` 值域 0..255，**不是 0/1**。
- 只有 `GameTags.Dead` 被排除，**没有"是否复制人"的区分** ⇒ **野生生物走过也会点亮格子**。
  这是符合直觉的（人也能看到动物活动的痕迹），不是 bug。
- `/v1/catalog`（静态建筑字典）**不受视野影响**。
### 3.11 `/v1/buildings`（M5.4）

`GET /v1/buildings?offset=0&limit=500`（`limit` 默认 500、上限 2000）。

- **迷雾**：`AIEnableFog=true` 时只返回 `Grid.Visible[cell] > 0` 的建筑（判定发生在采集循环内）；
  响应里 `fogged` 与 `visibility.filteredCount` / `visibility.rule` 如实报告模式与被滤掉的条数。
- **分页（0.96.0 起按"命中集"切片）**：`offset` **只在通过 fog / bbox / prefabId 之后的命中条目上计数**
  （旧实现先跳 `offset` 再判 `prefabId` ⇒ 带 `prefabId` 翻页既漏又重）。
  所以 `offset=0,500,1000…` 逐页翻不会有洞、不会重复：
  - `total` = **命中总数**（= 可翻页总数，`ceil(total/limit)` 页翻完）
  - `hasMore` / `nextOffset` = 下一页入口（`hasMore:false` 时 `nextOffset:-1`）
  - `offset ≥ total` ⇒ `count:0, hasMore:false`，**不报错**（翻到底不是错误）
  - `truncated` 保留，与 `hasMore` 同值（向后兼容）
  - 实测背景：大地图 `totalItems=72851`、`limit` 上限 2000 ⇒ 旧版一页最多 2000 条且没有总数，翻不动
- **单页体积**：只序列化本窗口内的 `limit` 条（**7 万条不会一次性攒进内存**，边扫边数、只留窗口）。
  每条默认**不含 `name`**：要名字就加 `?names=1`（`name` 走 `Catalog.PlainText`，而翻页时每页都要全量扫一遍算 `total`
  ⇒ 旧实现等于每页做 7 万次 LocText 查表）。`prefabId → name` 的对照也可以一次拿 `/v1/catalog`。
- **过滤计数（M5.4a 拆开）**：
  - `filteredByFog`：被迷雾挡掉的条数（`Grid.Visible[cell] == 0`）
  - `filteredInvalid`：算不出有效格子的条数（**与迷雾无关**，两种模式应相等）
  - `filteredByBox`：落在 `x0/y0/x1/y1` 框外的条数（0.96.0 起进硬等式）
  - `filteredByPrefab`：`prefabId` 不匹配的条数
  - `skipped`：命中但落在本页窗口**之前**的条数（`offset` 跳过的）
  - `notEmittedDueToLimit`：命中但落在本页窗口**之后**的条数（`hasMore:true` 时 > 0）
  - `filteredCount`：**`filteredByFog + filteredInvalid + filteredByPrefab` 三者之和**（向后兼容；
    别把它当"被迷雾滤掉"的专属计数；`filteredByBox` 不进这个和）
- **硬等式**（可用于自检）：
  ```
  scanned = count + skipped + notEmittedDueToLimit + filteredByFog + filteredInvalid + filteredByBox + filteredByPrefab
  total   = count + skipped + notEmittedDueToLimit
  offset=0 时：total = count + notEmittedDueToLimit
  同一 offset/limit：count_all = count_fog + filteredByFog
  ```
- **字段**：`prefabId / x / y / cell / worldId / underConstruction / operational / enabled / markedForDeconstruct`，
  **电池另有** `joules`（当前电量，J）/ `joulesCapacity`（容量，J），其它建筑为 `null`。
  ⚠️ **`operational` 不表示通电**（它只表示建筑完好、在运转，缺电时仍是 true）；
  判断供电要看 `joules` 是否在涨。
  `enabled` 为 `null` 表示该建筑没有启用/禁用概念（如砖块）。
  额外加 `?names=1` 时每条多一个 `name`（本地化中文名），响应顶层 `namesIncluded` 如实报告。
- 温度、材料、是否在用等**暂不提供**（字段越多 AI 读起来越贵）。
- **响应顶层**：`buildings[]` / `count` / `offset` / `limit` / `total` / `hasMore` / `nextOffset` / `scanned` /
  `skipped` / `totalItems` / `truncated` / `namesIncluded` / `elapsedMs` / `timing` / `fogged` / 各过滤计数 / `visibility`。
  ⚠️ `total`（可翻页总数）与 `totalItems`（表里原有对象数，含被滤掉的）**不是一回事**，别混用。
### 3.12 字段语义契约（M5.4 起）

- **`null` = 该概念不适用于这个对象**（例：砖块没有"启用/禁用"，`enabled: null`）。
- **"取不到 / 读失败" 不能用 `null`** —— 必须用单独的降级字段表达（`*Unavailable` / `*Error`），
  并在顶层 `ok` 保持 `true` 时把原因写清楚。
- 理由：调用方需要区分"这次没数据、值得重试"和"这个对象本来就没有这个概念"。
  两者都返回 `null`，对方就没法判断该不该重试。
#### `/v1/buildings` 响应示例（完整形态）

```json
{
  "buildings": [
    { "prefabId": "Tile", "x": 117, "y": 216, "cell": 141417, "worldId": 0,
      "underConstruction": false, "operational": false, "enabled": null, "markedForDeconstruct": false },
    { "prefabId": "ManualDoor", "x": 111, "y": 222, "cell": 141303, "worldId": 0,
      "underConstruction": false, "operational": true, "enabled": true, "markedForDeconstruct": false }
  ],
  "count": 2,
  "offset": 0,
  "limit": 500,
  "total": 2,
  "hasMore": false,
  "nextOffset": -1,
  "scanned": 2,
  "skipped": 0,
  "totalItems": 2,
  "truncated": false,
  "namesIncluded": false,
  "elapsedMs": 1,
  "timing": { "loopMs": 0, "visMs": 0, "nameMs": 0, "jsonMs": 0 },
  "fogged": true,
  "filteredByPrefab": 0,
  "filteredByBox": 0,
  "notEmittedDueToLimit": 0,
  "filteredByFog": 0,
  "filteredInvalid": 0,
  "visibility": {
    "aiEnableFog": true,
    "filteredByPrefab": 0,
    "filteredByBox": 0,
    "filteredByFog": 0,
    "filteredInvalid": 0,
    "filteredCount": 0,
    "rule": "Grid.Visible[cell] > 0"
  }
}
```

翻完整张图（0.96.0 起）的写法：

```text
GET /v1/buildings?limit=2000&offset=0        -> total=72851, count=2000, hasMore=true,  nextOffset=2000
GET /v1/buildings?limit=2000&offset=2000     -> total=72851, count=2000, hasMore=true,  nextOffset=4000
... 直到 hasMore:false（offset=72000 那页 count=851, nextOffset=-1）
```

⚠️ **`name` 默认不返回**（0.96.0）：需要逐条中文名就加 `&names=1`；只要"某个 prefab 叫什么"则查 `/v1/catalog` 一次即可。

⚠️ **`"enabled": null` 是正常值，不是缺字段**：
表示该建筑**没有"启用/禁用"这个概念**（砖块、门的一部分等没有 `BuildingEnabledButton` 组件）。
看表格工具里它显示为空白，那是 `null` 的渲染，不是"接口没给"。
判断方法：字段**存在**且值为 `null` = 概念不适用；字段**不存在** = 接口没提供。
---

### 3.13 `/v1/door`（**设计草案，未实现**）

> 状态：**草案**。M6.1 的第一个"手"操作，用来打通「按坐标找建筑 → 拿组件 → 调动作 → 分级回读」整条链。

```
POST /v1/door
{ "worldId": 0, "x": 111, "y": 222, "action": "open" | "close" | "lock" | "auto" }
```

#### 两种模式（对应已有的两层）
| 模式 | 触发条件 | 实现 | 生效时机 |
|---|---|---|---|
| **normal**（默认） | —— | `Door.QueueStateChange(target)` | **派 chore，等复制人走过去按** |
| **instant** | 请求带 `"instant": true` **且** `allowCheats==true` 且（沙盒 或 debug） | 反射写 `controlState` + 调 `ApplyControlState()` | 立即 —— **未实现（需要反射，待定）**；当前请求带 `instant:true` ⇒ `400 BAD_REQUEST`（M6.1 只做 normal） |

⚠️ **不用 `DebugHandler.InstantBuildMode` 那条捷径**：IL 里 `if (DebugHandler.InstantBuildMode) { ApplyRequestedControlState(); return; }`
是"瞬间建造模式"的副作用，语义不对，而且会改全局游戏状态。

#### 响应（三个状态量分开给）
```json
{
  "mode": "normal",
  "action": "open",
  "target": { "worldId": 0, "x": 111, "y": 222, "cell": 141303, "prefabId": "ManualDoor" },
  "before":  { "currentState": "Auto", "requestedState": "Auto",  "physicalOpen": false },
  "after":   { "currentState": "Auto", "requestedState": "Opened","physicalOpen": false },
  "accepted": true,
  "jobState": "pending",
  "note": "已派活（ChangeStateChore），等复制人执行；用 currentState 复核"
}
```
- `accepted`：`requestedState` 立即回读 == 目标
- `applied`：轮询 `currentState`（**超时 5 秒**）已变 ⇒ true
- **`pending`**：超时仍未变 ⇒ true（既非成功也非失败）
- `instant` 模式下 `applied` 应立刻为 true，`pending` 恒为 false

#### 错误码
| code | 场景 |
|---|---|
| `404 NOT_FOUND_TARGET` | 该格没有门（或不是 `Door` 组件） |
| `403 ALLCHEATS_DISABLED` / `403 SANDBOX_OR_DEBUG_REQUIRED` | 用了 `instant: true` 但闸门未开 |
| `409 NOT_APPLIED` | `instant` 模式写完回读仍不一致 |
| `400 BAD_REQUEST` | `action` 非法 |

#### 实现风险
| 部分 | 风险 |
|---|---|
| 按坐标找门（`/v1/buildings` 已有数据 + `PlacementCellsContainCell`） | 公共 API |
| 读 `CurrentState` / `RequestedState` / `IsOpen()` | 公共 API |
| normal 模式 `QueueStateChange` | 公共 API |
| instant 模式写 `controlState` + 调 `ApplyControlState()` | **反射**（两者都是 nonpub；`controlState` 无 public setter，已核实） |
---

### 3.14 `POST /v1/job`（M6.2，本版只有 `type=dig`）

统一的"下达工作指令"入口。设计依据见 `docs/ACTION-SURVEY.md`（游戏没有统一下命令入口，
但每种可标记对象都有 public 的 `Mark*` 方法；挖掘走静态捷径 `DigTool.PlaceDig`）。

```
POST /v1/job
{ "type": "dig", "worldId": 0, "x": 111, "y": 222, "radius": 0, "priority": 5 }
```

| 参数 | 必填 | 说明 |
|---|---|---|
| `type` | 是 | 本版只支持 `dig`；其它 ⇒ `400 NOT_IMPLEMENTED` |
| `x` / `y` | 是 | 世界内格子坐标 |
| `worldId` | 否 | 给了就校验，不符 ⇒ `400 BAD_TARGET` |
| `radius` | 否 | **只允许 0/1/2**（默认 0）；超范围 ⇒ `400 BAD_REQUEST` |
| `priority` | 否 | 1..9；给了就 `Prioritizable.SetMasterPriority` 并**回读比对** |

#### 响应
```json
{
  "type": "dig",
  "accepted": true,
  "jobState": "marked",
  "target": { "worldId": 0, "x": 111, "y": 222, "cell": 141303 },
  "radius": 0,
  "markedObject": { "name": "DigPlacer(Clone)", "instanceId": 123456 },
  "diggableFound": true,
  "alreadyMarkedBefore": false,
  "solidBefore": true,
  "solidAfter": true,
  "priorityRequested": 5,
  "priorityApplied": 5,
  "priorityMatch": true,
  "note": "标记已落地（Diggable 挂在 DigPlacer 上），等复制人去挖"
}
```

#### 语义
- **`Diggable` 不在自然砖上、也不在建筑上**，而在 `DigTool.PlaceDig` 新建的 **`DigPlacer` 标记物**上
  （IL 实证：`Assets.GetPrefab("DigPlacer")` → 设 `digTypeFlags` → 登记进 `Grid.Objects(cell, ObjectLayer.DigPlacer=7)`）。
  ⇒ 回读判据 = **`PlaceDig` 的返回值非空且带 `Diggable` 组件**。
- `jobState`：**只有** `marked`（标记已落地）/ `completed`（该格已无实心物质）。
  **`jobState` 不表达失败** —— 失败一律走 `ok:false` + `error.code`
  （例如 `500 MARKER_NOT_PLACED`：`PlaceDig` 返回了对象但没挂上 `Diggable`）。
- **部分成功**用独立字段：`partial: true` + `partialReason`
  （目前唯一情形：给了 `priority` 但回读不匹配，例如该对象上没有 `Prioritizable`）。
- **`priorityMatch` 是回读结果**，不是"调用过就算成功"；不一致会 `Log.Warn` 并在响应里报实际值。
- ⚠️ **`alreadyMarkedBefore: true` 只表示"该格已有标记"，不区分来源（AI 或玩家手标）。**
  游戏的标记组件里**没有任何"谁标的"字段**（全 DLL 搜 `MarkedBy`/`MarkSource` 0 次命中）⇒ 目前**无法区分**。
- **幂等（两道防线）**：
  1. **短路**：该格已有 `Diggable` ⇒ **不调 `PlaceDig`**，响应 `shortCircuited: true`、
     `alreadyMarkedBefore: true`（`priority` 若给了，会作用在**已有**标记上）；
  2. **计数**：调用前后各数一次该格对象数
     ```
     objectsBefore / objectsAfter / placedDelta = objectsAfter - objectsBefore
     ```
     短路时 `placedDelta` 恒为 0。**`placedDelta > 1` 说明 `PlaceDig` 一次调用在该格加了不止一个对象**（异常，需核对）。
     ⚠️ `objectsAtCell` 数的是 `Grid.ObjectLayers[cell]` 里**所有层**的对象，不只是 `DigPlacer` 层。
- `markedObject.instanceId` 可用于跨调用比对"是不是同一个标记物"（短路时应与首次相同）。

#### 判定顺序（调用方要能区分"参数错"和"没就绪"）
1. 纯参数校验（`type`/`x`/`y`/`radius`/`priority`，**不碰游戏对象**）⇒ `400`
2. `MainThread.Ready` ⇒ `503 NOT_READY`
3. `DigTool.Instance == null` ⇒ `503 NOT_READY`（dig tool not initialised）
4. 格子 / `worldId` / 实心判定 ⇒ `400 BAD_TARGET` / `400 NOT_DIGGABLE`
⇒ 主菜单下 curl 会**先**过参数校验（参数错就返回 400），参数没问题才返回 503。**看 `code`，别猜。**

#### 错误码
| code | 场景 |
|---|---|
| `400 BAD_REQUEST` | 缺 `type`/`x`/`y`、`radius` 超范围、`priority` 非 1..9 |
| `400 NOT_IMPLEMENTED` | `type != dig` |
| `400 BAD_TARGET` | 格子无效 / `worldId` 不符 |
| `400 NOT_DIGGABLE` | 该格不可挖：**没有实心物质**、**该格上有建筑**（应改用 `deconstruct`）、**已被挖掉**、或 `PlaceDig` 返回 null |
| `503 NOT_READY` | 主线程泵未就绪，或 `DigTool.Instance == null` |
| `500 INTERNAL` | **只留给真意外**（内部状态取不到 / 反射失配）。"不可挖"不在此列 |

#### `worldId` 的语义（重要，容易踩）
`worldId` **不是全局常量，而是"这个格子属于哪个世界"**：
游戏把所有小行星**并排**放在同一张网格里，每颗世界有自己的 `worldOffset` 与 `WorldSize`，
`Grid.WorldIdx[cell]`（`byte[]`）记录该格的归属世界。
⇒ 同一张地图上，`(117,216)` 可能属于 world 0，而 `(300,100)` 已越出该世界的矩形、属于 **world 1**。
- `/v1/buildings` 的每条记录给的是**该建筑所在格的世界**（与 `/v1/job` 同源：都用 `Grid.WorldIdx`）；
- 调用方**最省事的做法：不传 `worldId`**（服务端不做世界校验），或者**从 `/v1/buildings` 里逐条取**；
- 想确认某个坐标属于哪个世界：`GET /v1/debug/vision-sample?x=..&y=..` 会返回 `worldId` 与 `worldName`。
#### ⚠️ 单格入参与区域入参，**返回字段不一样**（两者都合法，但形状不同）

| 入参形式 | 走哪条路径 | 响应字段 |
|---|---|---|
| **单格** `{"type":"dig","x":..,"y":..,"radius":0..2}` | 原有单格路径（M6.2 已验收，逻辑未改） | `accepted` / `jobState` / `markedObject` / `diggableFound` / `alreadyMarkedBefore` / `shortCircuited` / `objectsAvailable` / `objectsBefore` / `objectsAfter` / `placedDelta` / `solidBefore` / `solidAfter` / `priorityRequested` / `priorityApplied` / `priorityMatch` / `partial` / `note` |
| **区域** `{"type":"dig","x0":..,"y0":..,"x1":..,"y1":..}` | M6.5 区域路径（**统计形状**） | `requested` / `applied{count,bounds,isContiguous}` / `alreadyMarked` / `rejected{count,byReason}` / `accepted` / `jobState` / `priorityRequested` / `priorityMismatch` / `partial` / `elapsedMs` / `sourceAgnostic` / `note`（`listCells:true` 时另有 `cellsListed`） |

**注意**：区域响应里**没有** `markedObject` / `diggableFound` / `objects*`（那是逐格概念）；
单格响应里**没有** `requested` / `applied` / `rejected`（那是批量概念）。
调用方请按入参形式选对应的解析分支 —— **两种形状不一样是有意为之，不是缺字段**。
#### 字段语义（M6.2 修订：`null` 不是哨兵）
| 字段 | 有值 | `null` 的含义 |
|---|---|---|
| `objectsBefore` / `objectsAfter` / `placedDelta` | 该格**所有层**上的对象数（`Grid.Objects[cell, layer]` 逐层数，层数取 `ObjectLayer.NumLayers`） | **没拿到**（格子无效 / 索引失败）；同时 `objectsAvailable: false` |
| `priorityRequested` | 调用方传的 1..9 | **没传这个参数**（不传 ≠ 传 0） |
| `priorityApplied` | `Prioritizable.GetMasterPriority().priority_value` 的回读值 | **拿不到**（该对象没有 `Prioritizable`，或没请求优先级） |
| `priorityMatch` | `priorityApplied == priorityRequested` | 同上：无法比较 |

⚠️ **不传 `priority` 时四个字段分别是** `null / null / null / null`，而不是 `0 / -1 / null`。
⚠️ `objectsAvailable: false` 时**不要**把 `objectsBefore` 当 `0` 用（那是"该格没有对象"，与"没拿到"完全不同）。
**唯一的例外（有意为之）**：`markedObject.instanceId` 拿不到时是 **`-1`**，不是 `null`。
理由：这是 **Unity 的实例 ID**，游戏本身就用一个整数表示"无效对象"；
它不会被误当数量/优先级使用，所以跟随游戏语义更自然。**别用这条去套其它字段。**

#### 幂等的真实证据（M6.2 实测）
同一格连发两次：
- **游戏侧**：`markedObject.instanceId` **两次相同** ⇒ `PlaceDig` 内部判重，**标记不会叠加**；
- **本服务侧**：第二次应返回 `alreadyMarkedBefore: true`、`shortCircuited: true`、`placedDelta: 0`。
  （0.33.2 及以前该判定失效 —— 当时用 `Grid.ObjectLayers[cell]` 查不到对象；0.33.3 起改用 `Grid.Objects[cell, layer]`。）
---

### 3.15 `/v1/job` 支持的类型（M6.3）

| type | 入口（已核实） | 前置守卫 | 回读判据 | jobState 取值 | 状态 |
|---|---|---|---|---|---|
| `dig` | `DigTool.PlaceDig(cell, radius)`（public static） | `Grid.IsSolidCell` 为真、该格无建筑（`ObjectLayer.Building`） | 返回值带 `Diggable` | `marked` → `completed` | ✅ 已验收（M6.2） |
| `deconstruct` | `Deconstructable.QueueDeconstruction(true)` | 该格建筑必须有 `Deconstructable`，否则 `400 NOT_DECONSTRUCTABLE` | `IsMarkedForDeconstruction()` | `marked` → `done`（建筑已不在场上） | ✅ 已实现（**未实机验**） |
| `harvest` | **`HarvestDesignatable.MarkForHarvest()`**（public 方法，会造 chore） | 无 `HarvestDesignatable` ⇒ `400 NOT_HARVESTABLE`；**有但未成熟** ⇒ **`400 NOT_RIPE`** | `MarkedForHarvest` + **`Harvestable.HasChore()`** | `marked` | ⚠️ 已重写（**未实机验**） |
| `sweep` | `Clearable.MarkForClear(false, false)`（参数取自 `ClearTool.OnDragTool`） | 该格须有 `Clearable`，否则 `400 NOT_CLEARABLE` | `Clearable.isMarkedForClear`（**nonpub 字段，反射读**） | `marked` | ⚠️ **已实现但未实机验证**（响应带 `"verified": false`） |
| `capture` | **`Capturable.MarkForCapture(bool)`**（逐格取组件；**不走** `CaptureTool.MarkForCapture(Vector2, Vector2, bool)`，理由见 §3.37.4） | 该格须有 `Capturable`，一个都没有 ⇒ `400 NO_TARGET`；默认**排除己方殖民者** | `Capturable.IsMarkedForCapture`（public，就是标记位本身） | `marked` | ✅ 已实现（M6.76，**矩形**语义；0.93.6 补回读字段；**未实机验**） |

#### 各 type 的字段差异
| 字段 | dig | deconstruct | harvest |
|---|---|---|---|
| `radius` | ✅ 0..2 | ❌（不适用，永远单格） | ❌ |
| `markedObject` | ✅（DigPlacer 的 name/instanceId） | ❌ | ❌ |
| `diggableFound` | ✅ | ❌ | ❌ |
| `solidBefore/solidAfter` | ✅ | ❌ | ❌ |
| `target.prefabId` | ✅ | ✅（建筑 prefabId） | ✅（`KPrefabID.PrefabTag`） |
| `alreadyMarkedBefore` / `shortCircuited` | ✅ | ✅ | ✅ |
| `objectsAvailable/objectsBefore/objectsAfter/placedDelta` | ✅ | ✅（**placedDelta 恒为 0**：此 type 不新建对象） | ✅（同上） |`n| `verified` | ❌ | ❌ | ❌ （仅 `sweep` 有：`false` 表示该 type 未实机验证） |
| `priorityRequested/Applied/Match` / `partial` | ✅ | ✅ | ✅ |

#### `sweep` 为什么没做（证据）
`Clearable.MarkForClear(bool arg1, bool arg2)` 的调用点实测：
```
Clearable.OnSpawn      → MarkForClear(true, true) 和 MarkForClear(true, false)
Clearable.OnClickClear → MarkForClear(false, false)      ← 用户点"清扫"走这里
Clearable.OnAbsorb     → MarkForClear(false, false)
ClearTool.OnDragTool   → MarkForClear(false, false)      ← 清扫工具拖拽走这里
```
⇒ **"用户主动清扫"反而传 `false`**，与 `OnSpawn` 传 `true` 矛盾 ⇒ **`arg1` 不是"是否标记"这么简单**，
`arg2` 疑为 `force`（IL 里它参与 `IsEntombed` / `GameTags.Stored` 那几个提前返回条件的组合）。
**在弄清 arg1/arg2 之前不做**，避免"清掉不该清的东西"。
---

### 3.16 `GET /v1/marks`（M6.4，单格标记查询）

```
GET /v1/marks?x=100&y=250            # worldId 可选
```
查**某一格上所有标记类组件**的状态（dig / deconstruct / clear / harvest 一次给全）。

```json
{
  "target": { "worldId": 0, "x": 100, "y": 250, "cell": 159100 },
  "dig":         { "marked": true,  "objectName": "DigPlacer", "instanceId": -446630 },
  "deconstruct": { "marked": false, "objectName": null },
  "clear":       { "marked": null,  "objectName": "Sand", "unavailable": "Clearable.isMarkedForClear 反射失败：…" },
  "harvest":     { "marked": false, "objectName": null },
  "objectsAtCell": 1,
  "scan": { "okLayers": 45, "errLayers": 0, "err": null },
  "sourceAgnostic": true,
  "note": "标记不记录来源：无法区分 AI 标的与玩家手标的"
}
```

#### 关键语义（**别混**）
| 表现 | 含义 |
|---|---|
| `marked: true` | 查到了，**有**标记 |
| `marked: false` | 查到了，**没有**标记 |
| **`marked: null` + `unavailable: "<原因>"`** | **查不到**（例如 `Clearable.isMarkedForClear` 是 nonpub，只能反射；反射失败就给 null） |

`unavailable` 的文案**带稳定前缀**，便于区分两种完全不同的原因：
- `field-not-found: …` ⇒ **字段名猜错了 / 游戏更新改了字段**（改个名就好）
- `reflection-error: <ExceptionType>: …` ⇒ 反射本身出问题（例如 `TargetException`、`MemberAccessException`）

⇒ **`null` 不是 `false`**：`false` 是"查了没有"，`null` 是"没查到"。
**绝不能用 `false` 假装"没有标记"** —— 那会让用户以为自己的清扫标记丢了。

#### 无法区分"谁标的"（已知局限）
游戏里 **`Diggable` / `Deconstructable` / `Clearable` / `Harvestable` 都没有"标记来源"字段**，
全 DLL 搜 `MarkedBy` / `markedBy` / `MarkSource` / `markOwner` 均 **0 次命中** ⇒
**本端点和 `/v1/job` 的 `alreadyMarkedBefore` 都无法区分"AI 标的"与"玩家手标的"**。
`instanceId` 只能说明"是哪个对象"，不能说明来源。

#### 复用
`dig` 复用 `/v1/job` 的 `FindDiggableAt`（D 层优先 + 逐层兜底 + 诊断）、`objectsAtCell` 复用 `CountObjectsAt`，
`scan` 字段与 `/v1/job` 日志里的 `objectsScan` 同义。

#### 未实现（下一步）
- `?x0=&y0=&x1=&y1=` 矩形区域查询（需分页 + `truncated`）。

> ✅ `capture` 类标记**已实现**（M6.76，`POST /v1/job type=capture`，矩形语义；0.93.6 补了回读字段），见 §3.37。
> 这里原来把它列在"未实现"是过时的 —— 当时它确实没接，后来随打标记批次一并做掉了。
---

### 3.17 `/v1/job` 区域模式（M6.5，目前仅 `dig`）

```
POST /v1/job
{ "type":"dig", "x0":100, "y0":100, "x1":120, "y1":120, "priority":5, "listCells":false }
```

#### 统计响应（**重点是统计，不是逐格**）
```json
{ "type":"dig",
  "requested":{ "x0":100,"y0":100,"x1":120,"y1":120,"cells":441,"worldId":0 },
  "applied":{ "count":231, "bounds":{"x0":100,"y0":100,"x1":120,"y1":110}, "isContiguous":false },
  "alreadyMarked":200,
  "rejected":{ "count":10, "byReason":{ "OUT_OF_WORLD":2,"HAS_BUILDING":0,"NOT_DIGGABLE":8,"READBACK_FAILED":0 } },
  "accepted":true, "jobState":"marked",
  "priorityRequested":5, "priorityMismatch":0, "partial":false,
  "elapsedMs":37, "sourceAgnostic":true }
```

#### 三条硬规则
1. **`applied.count` = 本次真正新标记的格数**（不含本来就有的）。
2. **`alreadyMarked` = 本来就有标记的格数，单独计数，不合并、不视为错误**
   —— 用户明确要求：重复标记不是失败。
3. **硬等式**（服务端日志自检，不平会 `Log.Warn`）：
   ```
   applied.count + alreadyMarked + rejected.count == requested.cells
   ```

#### `rejected.byReason` 的分类
| 原因 | 含义 |
|---|---|
| `OUT_OF_WORLD` | 格子无效，或（给了 `worldId` 时）该格属于别的世界 |
| `HAS_BUILDING` | 该格上有建筑 ⇒ 应该用 `deconstruct` 而不是 `dig` |
| `NOT_DIGGABLE` | 没有实心物质 / `PlaceDig` 返回 null |
| `READBACK_FAILED` | `PlaceDig` 给了对象但没有 `Diggable`，或该格扫描抛异常 |

#### 上限（**主线程操作，必须限**）
| 限制 | 阈值 | 超出 |
|---|---|---|
| 区域格数 | **5000** | `400 REGION_TOO_LARGE`（message 带实际格数） |
| `listCells: true` 时的格数 | **2000** | `400 TOO_MANY_CELLS` |

#### 逐格列表是**可选**的
默认**不返回**逐格列表（只给 stats + bounds）。要逐格状态传 `"listCells": true`，
响应里多一个 `cellsListed:[{x,y,state}]`（`state` = `marked` / `alreadyMarked` / 拒绝原因）。

#### `isContiguous` / `bounds`
- `applied.bounds` = 实际新标记格的**最小外接矩形**；
- **`isContiguous: false`** 表示实际区域**不连续**（bounds 会比真实覆盖范围大 —— 例子里 `y` 只到 110 就是这样看出来的）。

#### 兼容性（重要）
- **单格入参仍可用**：`{ "type":"dig", "x":.., "y":.., "radius":0..2 }` 走**原有单格路径**（逻辑未改动），
  响应仍是 `markedObject / diggableFound / alreadyMarkedBefore / shortCircuited / objectsBefore…` 那套；
- 区域模式（带 `x0`）走新路径，响应是上面这套统计；
- `GET /v1/marks` 保留（单格精查），但**不再是主路径**。
---

### 3.18 `deconstruct` 必须点名（`409 AMBIGUOUS_TARGET`）

> **证据等级标记（全文只用这三种字面量）**
> * `【实测 2026-11-14】` —— 在 **0.37.3 版本、当时的存档**下真机跑过。**换版本或换存档都可能失效。**
> * `【未实测】` —— 已实现、代码路径清楚，但**从没在游戏里跑过**。
> * `【推断】` —— 由反编译/元数据得出的结论，**没有运行时证据**。
>
> **本节除标注 `【实测 …】` 的段落外，一律是 `【未实测】` 或 `【推断】`。**

**版本演进（开源用户请对照自己的版本）**
```
M6.6 / 0.37.0  引入 409 AMBIGUOUS_TARGET + candidates（details 通道）
0.37.1         候选增加 alreadyMarked 字段
0.37.2~0.37.3  details 由 ThreadStatic 改为显式 out 参数；空 details 从 {} 改为 null
0.37.4         filterLayerName 遇 MISSING. 占位符 ⇒ null
0.38.0         harvest 改为 MarkForHarvest() + NOT_RIPE（不在本节范围）
```

**为什么必须点名**：`dig` / `sweep` 是**格**的属性，`deconstruct` / `harvest` / `door` 是**对象**的属性。
**一个格上可以有多个对象** ⇒ "给我个格子自己挑一个拆" = **猜**。

#### 三种点名方式
| 请求 | 行为 | 证据 |
|---|---|---|
| `{"x":111,"y":217}` 不点名 | 该格**唯一**可拆 ⇒ 直接拆；**≥2 个** ⇒ `409 AMBIGUOUS_TARGET` + `error.details.candidates[]`；0 个 ⇒ `400 NOT_DECONSTRUCTABLE` | **≥2 ⇒ 409**：`【实测 2026-11-14】`（3 个对象，两次响应完全一致）；**唯一 ⇒ 直接拆**：`【未实测】`；**0 个 ⇒ 400**：`【未实测】` |
| `"layer":"Wire"`（`ObjectLayer` 枚举名，主键） | 精确匹配；无命中再按 `filterLayer` 别名匹配；多命中 ⇒ 409 | `【未实测】` |
| `"prefabId":"Wire"` | 按 prefab 匹配；多命中 ⇒ 409；0 命中 ⇒ `404 NOT_FOUND_TARGET` | `【未实测】` |
| 两个都给 | **必须同时满足**；0 ⇒ `404`；多 ⇒ `409` | `【未实测】` |

#### `409` 的 `details`（`【实测 2026-11-14】`）
```json
{ "error": { "code": "AMBIGUOUS_TARGET",
  "message": "该格有 3 个可拆对象，必须点名（layer 或 prefabId 或两者），不替你挑",
  "details": { "candidates": [
    { "prefabId":"RationBox", "layer":"Building", "filterLayer":"Buildings",
      "filterLayerName":"建筑", "deconstructable":true, "alreadyMarked":false },
    { "prefabId":"Wire",      "layer":"Wire",     "filterLayer":"Wires",
      "filterLayerName":"电线", "deconstructable":true, "alreadyMarked":false },
    { "prefabId":"Wire",      "layer":"WireTile", "filterLayer":"Default",
      "filterLayerName":null, "deconstructable":true, "alreadyMarked":false }
  ] } } }
```
- `layer` = **`ObjectLayer` 枚举名**（主键，一对一）；
- `filterLayer` = `GetFilterLayerFromObjectLayer(layer)` 的**原样返回值**（**驼峰** `"Buildings"` / `"Wires"` / `"Default"`）；
  **实测只覆盖 3 个层**（`Building→Buildings`、`Wire→Wires`、`WireTile→Default`）`【实测 2026-11-14】`，
  **其余层未验证** `【未实测】`；
- `filterLayerName` = `STRINGS.UI.TOOLS.FILTERLAYERS.<filterLayer 转大写>.NAME`；
  **取不到就是 `null`**（"没翻译" ≠ "就叫这个名字"）。`【实测 2026-11-14】`（`Wires`→"电线"、`Buildings`→"建筑"）
  ⚠️ 游戏对**缺失的键**返回占位符：`filterLayer="Default"` 时得到
  `MISSING.STRINGS.UI.TOOLS.FILTERLAYERS.DEFAULT.NAME` `【实测 2026-11-14】`。
  **0.37.4 起**服务端把**以 `MISSING.` 开头或包含 `.MISSING.`** 的值一律当 **`null`**
  （该修复**本身未在真机确认过**，见清单 #9）`【未实测】`；
- `alreadyMarked`：**0.37.1 起**提供；实测三次都是 `false`（当时存档无遗留标记），
  **`true` 的情形未实测** `【未实测】`。

### 为什么 `layer` 必须是主键（`【实测 2026-11-14】`）
同一格上的三个候选里，**有两个 `prefabId` 都是 `Wire`**：
```json
{"prefabId":"Wire","layer":"Wire",     "filterLayer":"Wires", "filterLayerName":"电线"}
{"prefabId":"Wire","layer":"WireTile", "filterLayer":"Default","filterLayerName":null}
```
⇒ **同一个 prefab 可以挂在不同 `ObjectLayer` 上**（地上电线 vs 埋墙电线）。
⇒ `prefabId` **不能**当主键（会命中两个）；`layer`（`ObjectLayer` 枚举名，一对一）才是主键。

### 别名匹配是大小写不敏感的（`【推断】`：代码用 `OrdinalIgnoreCase`，未在真机用不同大小写试过）
`layer` / `prefabId` 匹配全部用 `StringComparison.OrdinalIgnoreCase`
（`"Wire"` / `"wire"` / `"WIRES"` / `"wires"` 应都能匹配；`filterLayer` 实际返回**驼峰** `"Wires"`，
而 `STRINGS` 的键是**全大写** `WIRES` —— 靠大小写不敏感对齐）。

#### 不做什么
- **不模拟 F2/视图切换**（`OverlayScreen` → `FilteredDragTool.currentFilters` 是**用户的 UI 状态**，
  写它会经 `SaveUserFilters` 污染用户偏好）`【推断】`（`SaveUserFilters` 存在是元数据事实，**污染后果未验证**）；
- **不用 `IsActiveLayer`**：那是给鼠标这种低精度输入用的筛子；API 能精确点名，不需要照抄。

#### ⚠️ 未验证清单（**可计数：10 条**；换档后每验一条划掉一条）
| # | 条目 | 等级 | 判据（怎么算验过） |
|---|---|---|---|
| 1 | `layer` 按 `ObjectLayer` 枚举名点名 | `【实测 2026-11-14】` OK | `layer:"FoundationTile"` => `accepted:true, jobState:"marked"` |
| 2 | `filterLayer` 别名点名 | `【实测 2026-11-14】` OK | `layer:"Tiles"` 命中（过滤层名可当别名） |
| 3 | `prefabId` 多命中 ⇒ 409 | `【实测 2026-11-14】` OK | 409，message 明确"命中 2 个对象，请改用 layer" |
| 4 | `layer` + `prefabId` 同时满足 | `【实测 2026-11-14】` OK | 同时满足时可执行；不满足走 404/409 |
| 5 | 唯一可拆 ⇒ **直接执行**（不 409） | `【未实测·疑不可达】` | **理论路径**：需要"一格只有 1 个可拆对象"。但实测 5 个"只含 1 个建筑"的格子全部 409（`Tile` 同时挂 `Building`+`FoundationTile`，电线同时挂 `Wire`+`WireTile`）⇒ **结构上很难发生** |

**为什么"唯一可拆"接近死代码（实测结构原因）**

从 72792 个建筑里挑出 13668 个"该格只有 1 个建筑"的格子，抽 5 个试（不点名）：

| 坐标 | 结果 |
|---|---|
| `(100,26)` / `(100,27)` / `(100,28)` | 409，**2 个**可拆 |
| `(10,342)` | 409，**3 个**可拆 |
| `(100,148)` | 409，**2 个**可拆 |

原因：**同一个对象会注册到多个 `ObjectLayer`** ——

```
(100,26)  Tile @ Building        filterLayer=Buildings  "建筑"
         Tile @ FoundationTile    filterLayer=Tiles      "格子"     ← 同一块砖，两层

(10,342) SolarPanel            @ Building      filterLayer=Buildings
         WireRefinedHighWattage @ Wire          filterLayer=Wires
         WireRefinedHighWattage @ WireTile      filterLayer=Default   ← 同一根线，两层
```

⇒ **只要格子里有地砖或电线，可拆数就 ≥2** ⇒ `409` 是正常结果；
**"唯一可拆 ⇒ 直接执行"这条分支保留在代码里，但实测中未遇到，可能不可达。**
**不为此构造合成场景**（成本高于收益）。

| 6 | `404 NOT_FOUND_TARGET` | `【实测 2026-11-14】` OK | `layer:"NoSuchLayerXYZ"` => 该格没有 layer=... 的可拆对象 |
| 7 | `400 NOT_DECONSTRUCTABLE`（0 个可拆） | `【实测 2026-11-14】` OK | 空格子 => 该格上没有任何对象 |
| 8 | `alreadyMarked:true` 的候选 | `【实测 2026-11-14】` OK | 第二次同格请求，候选里出现 `true` |
| 9 | `MISSING.` ⇒ `null`（0.37.4 修复） | `【实测 2026-11-14】` ✅ 通过 | `LiquidConduitRadiant @ LiquidConduitTile`（`filterLayer=Default`）返回 `filterLayerName: null`，不是 `MISSING.STRINGS…` |
| 10 | 别名匹配的大小写不敏感 | `【实测 2026-11-14】` OK | `layer:"tiles"`（小写）命中 |

**清单状态（2026-11-15 更新）**：`#1 #2 #3 #4 #6 #7 #8 #9 #10` **已实测通过**；`harvest` 的 `choreCreated:true` **也已实测通过**；
**只剩 1 条未验**：
- `#5` 唯一可拆 ⇒ 直接执行 —— **未验**（还没找到"只有 1 个可拆对象"的格子）

### 3.19 `/v1/marks` 的 `harvest` 字段契约（M6.9，**与 `/v1/job` 同源同字段**）

> 背景：0.39.0 之前 `marks` 读的是 `Harvestable.CanBeHarvested`（**成熟度**），却当成"已标记"返回，
> 与 `/v1/job` 的 `HarvestDesignatable.MarkedForHarvest`（**标记**）结论相反 —— 实测 `(162,190)` `VineMother` 一真一假。
> 现已统一：**两个端点都读 `HarvestDesignatable`**。

```json
"harvest": { "marked": true, "objectName": "VineMother", "layer": "Building",
             "readyToHarvest": true, "choreCreated": false,
             "hasHarvestable": false, "count": 1 }
```

| 字段 | 来源（**同一字段**） | 含义 |
|---|---|---|
| `marked` | `HarvestDesignatable.MarkedForHarvest` | 是否已被标记收获（**与 `/v1/job` 的 `markedForHarvest` 同源**） |
| `readyToHarvest` | `HarvestDesignatable.CanBeHarvested()` | **成熟度**（不是标记；别再把它当"已标记"） |
| `choreCreated` | `Harvestable.HasChore()` | 是否真的造出了 chore |
| `hasHarvestable` | 组件是否存在 | `false` ⇒ **该对象本就不会产生 chore**（见下） |
| `count` | 该格 `HarvestDesignatable` 个数 | 用来区分"单对象"与"整格聚合" |
| `objectName` / `layer` | 第一个命中的对象 | 显示用 |

#### ⚠️ `job` 报"单个对象"，`marks` 报"整格聚合" —— 这不是 bug
| 端点 | 口径 |
|---|---|
| `POST /v1/job` `type=harvest` | **本次请求选中的那一个对象**（`markedForHarvest`） |
| `GET /v1/marks` | **该格任意一个 `HarvestDesignatable` 被标记**（OR 聚合，`count` 给个数） |

⇒ 该格只有 1 个 `HarvestDesignatable` 时，两者必然一致；
**≥2 个且只标了其中一个时，可能"job=true 而 marks 也=true（聚合）"，也可能看起来不同 —— 看 `count` 判断聚合还是唯一。**

#### 经验：`choreCreated:false` 时先看 `hasHarvestable`
实测 `(162,190)` `VineMother`（DLC 藤蔓母体）：`marked=true`、**`hasHarvestable=false`**、`choreCreated=false`。
`/v1/job` 会直接给出解释：
> `choreUnavailableReason`: "该对象没有 Harvestable 组件（只有 HarvestDesignatable）⇒ MarkForHarvest 只置标记，不会产生 chore"

⇒ **这不是失败，是"该能力对此类对象合法地不适用"**。调用方看到 `hasHarvestable:false` 就不该期待 chore。

#### 【实测 2026-11-15】`choreCreated: true` 已成立（且有对照组）
```
POST /v1/job {"type":"harvest","x":75,"y":245}      // BasicFabricPlant（芦苇），已成熟
→ accepted:true  jobState:"marked"
  target: {prefabId:"BasicFabricPlant", layer:"Building", filterLayer:"Buildings", filterLayerName:"建筑"}
  markedForHarvest:true  readyToHarvest:true  choreCreated:true  hasHarvestable:true
  alreadyMarkedBefore:true  shortCircuited:true
  objectsBefore:6  objectsAfter:6  placedDelta:0
  note: "已标记收获且 chore 已创建（Harvestable.chore != null）—— 这是'真派活'的硬证据"
```
同刻 `GET /v1/marks?x=75&y=245`：
```json
{"marked":true,"objectName":"BasicFabricPlant","layer":"Building",
 "readyToHarvest":true,"choreCreated":true,"hasHarvestable":true,"count":1}
```

**对照组（证明 `choreCreated` 不是常量、是真读出来的）**
| 格子 | prefabId | `hasHarvestable` | `choreCreated` |
|---|---|---|---|
| `(162,190)` | `VineMother` | `false` | **`false`** |
| `(75,245)` | `BasicFabricPlant` | `true` | **`true`** |

⚠️ **证据等级（重要限定）**
这次请求里 `alreadyMarkedBefore:true` + `shortCircuited:true` ⇒ **`MarkForHarvest()` 在这次请求里并没有被执行**
（早就标过了，走的是短路）。
⇒ **"`choreCreated` 能反映真实情况"是实测的**（同一格上 false/true 都出现过）；
**但"造 chore 的是 `MarkForHarvest()` 这个调用"是推断链** —— 链上有实测支撑（之前那次调用后 `choreCreated` 变 true），
**不是单次直接观察**。
要把它变成单次观察，需要：**一株没标过的熟植物，第一次调用就看到 `alreadyMarkedBefore:false` + `choreCreated:true`**。

#### 【实测 2026-11-15】端到端可用：标记 → 派 chore → 小人收掉 → 标记自动清除

**功能结论**：`harvest` 端到端跑通。同一格 `(75,245)` `BasicFabricPlant`（芦苇）：

| 时刻 | `marked` | `readyToHarvest` | `choreCreated` | `hasHarvestable` |
|---|---|---|---|---|
| **11:03**（`POST /v1/job`） | **true** | **true** | **true** | true |
| **11:15**（只读 `GET /v1/marks`） | **false** | **false** | false | true |

⇒ **12 分钟内 `marked` 与 `readyToHarvest` 双双翻回 `false`：植物被复制人采掉了**，
并且**标记被游戏自动清除**（不需要我们手动取消）。

**证据分级（本条比 `choreCreated:true` 更强）**
| 证据 | 证明什么 | 证不到什么 |
|---|---|---|
| `choreCreated:true` | **chore 对象存在**（字段层） | chore 会不会真被小人领走执行 |
| **本次：标记消失 + 成熟度翻转** | **chore 被领了、被执行了、植物被采了**（行为层） | — |

⇒ **字段层能证明"我们创建了什么"，行为层才能证明"游戏真的做了什么"。**

**旁证**：同一时段 `/v1/state` 里有 **6 个复制人同时在执行"收获"**（LINDSAY / FRANKIE / REN / ULTI / STEELA / …）
⇒ 这套自动收割在该存档里一直正常运转。

⚠️ **仍然保留的限定**：`MarkForHarvest()` 那次是否被执行**仍未单次观察到**（当时 `shortCircuited:true`）。
但推断链上现在多了「**结果真的发生了**」这一环 —— 不再是"只有字段、没有后果"。

#### 【实测 2026-11-15】"干净样本"在这个存档里不存在（不是找得不够）
持久轮询 4 轮（每轮 264 格，90 s 间隔）：
```
轮1 cycle=6991  熟且已标=14  熟且未标=0
轮2 cycle=6991  熟且已标=13  熟且未标=0
轮3 cycle=6991  熟且已标=15  熟且未标=0
轮4 cycle=6991  熟且已标=15  熟且未标=0
```
**"熟且未标"恒为 0** —— 因为该存档的植物要么被人为标了"自动收获"，要么走**机器收割**（成熟即收，没有"熟着但没标"的窗口期）。

⇒ **记一条通用观察：不是所有测试目标都能靠"多扫几遍"找到。**
有些样本是**被存档的运行方式**消除了（这里是"自动收割把窗口期吃掉了"），
此时正确做法是**如实记"样本不具备条件"**，而不是改判定逻辑去凑一个通过。

#### 【实测 2026-11-14】单样本观察：重启后标记仍在
同一格 `(162,190)` 在**重启游戏后**再次 `harvest`，返回 `alreadyMarkedBefore: true`（`shortCircuited: true`）。
⇒ **观察到的只是"这次读到的是 true"**，它**不能证明"是我上一次写进去的"**，也不能单独支撑"标记持久化"这个结论。
要把它变成结论，至少需要**反向对照**：标记 → 存档 → 重启 → 读，**并且在另一个格子重复一遍**。
**本文档按"单样本观察"记录，不作为通用保证。**




---

### 3.20 `/v1/state` 的 `elements`（M6.17）

#### 三种 `elementsMode`
| `elementsMode` | 行为 | `diagnostics.scope` |
|---|---|---|
| **不带**（默认） | **Storage 聚合**（遍历建筑 `Storage`，按 `GetAmountAvailable(tag)` 累加） | `storages-sum(default 主路：遍历建筑 Storage 累加)` |
| `storage` | 强制 Storage（忽略 `WorldInventory` 的结果） | `storages-sum(forced by elementsMode=storage)` |
| `worldinventory` | **只**走 `WorldInventory`（观察用） | `all-worlds-sum` |
| `auto` | 先 `WorldInventory`，结果为 0 再回退 Storage | 两者都可能出现 |

> 为什么默认改成 Storage：`WorldInventory.GetAmount(tag,false)` 与 `GetAmount(tag,true)` 在**大存档与单星球新档上都是 0**（实测），
> 而 Storage 聚合在两个档上都能读到东西。

#### `diagnostics` 七个字段
| 字段 | 含义 |
|---|---|
| `tagsScanned` | 扫到几个 tag（默认路里等于 `tagsFound`，见下） |
| `tagsFound` | 其中**真正有数（>0）**的 tag 数 |
| `positiveTags` | `WorldInventory` 路里"GetAmount>0"的 tag 数 |
| `positiveTagsRelated` | `WorldInventory` 路里"GetAmount(tag,true)>0"的 tag 数（**验证第二参语义用**，默认路恒为 0） |
| `worldsScanned` | 扫了几个世界（只有 `WorldInventory` 路才有意义） |
| `storagesScanned` | 扫了几个 `Storage`（**备选路自证**：>0 说明真的跑过） |
| `scope` | 本次实际走的是哪条路（见上表） |

**`tagsScanned` 与 `tagsFound` 为什么要分开**：Storage 路只按"容器里存在的 tag"查询，所以两者相等是**正常的**；
将来若加"全元素表 × 容器"的扫法，两者才会分开。

#### `worldsScanned` 两个 0 含义完全不同（`【实测 2026-11-15】`）
| 档 | `worldsScanned` | 含义 |
|---|---|---|
| 大存档（15 个世界） | **15** | **查了 15 个世界，`WorldInventory` 仍然全 0** ⇒ 这条路读不到 |
| 单星球新档 | **0** | **`cm.WorldContainers` 为空（初始世界不在列表里）⇒ 根本没查** |

#### 已知限制（重要）
- **`Storage` 聚合不含地面散落物**（`Pickupables` 层），**只统计装在容器里的物品**；
- 因此 `elements` 的 `count` / 数值**是"库存"而不是"世界里存在的总量"**；
- `【待确认】` 新档实测 `tagsScanned:1`（只有 `FieldRation`）—— 大概率就是"打印舱初始物资有一部分不在 Storage 里"这个限制。

---

### 3.21 `/v1/job` `type=build`（M6.11，**只做校验**）

```json
POST {"type":"build","prefabId":"Tile","x":100,"y":30,"validateOnly":true}
→ {"ok":true,"data":{"type":"build","validateOnly":true,"valid":false,
     "reason":"必须建在未占用的空间中","applied":false,
     "orientation":"Neutral(default)",
     "target":{"…","prefabId":"Tile","name":"砖块"}}}
```

| 参数 | 行为 |
|---|---|
| `prefabId`（必填） | 不存在 ⇒ `400 UNKNOWN_PREFAB`（message 指路 `/v1/catalog`） |
| `validateOnly:true` | **只校验、不改存档**（响应 `applied:false`）；`reason` 是 `IsValidPlaceLocation` 的 `out string` **原文** |
| `orientation` | `neutral`（默认）/ `r90` / `r180` / `r270` / `fliph` / `flipv`；非法值 ⇒ `400 BAD_REQUEST` 并列出允许值 |
| `confirm:true` | 真建造——**本版仍返回 `400 BUILD_NOT_IMPLEMENTED`** |

**`confirm:true` 为什么还是"未实现"**：不是没做完，是**故意不做** ——
`BuildingDef.Build(...)` 需要 `materials` / `Storage` / 温度等一整套参数，
误造一座建筑会**改存档**，而误报"能不能建"不会。**在参数语义没核清之前不放开。**

**`name` 字段**已过 `Catalog.PlainText`，不会出现 `<link="TILE">砖块</link>` 这类游戏富文本。

---


### 3.40 0.95.0 四件事：拆解标记 / 作业目标 / 按坐标截图 / 阻塞对话框

#### ① `/v1/buildings` 新增 `markedForDeconstruct`（bool）
来源：该建筑上的 **`Deconstructable.IsMarkedForDeconstruction()`**（元数据实测存在；同名字段 `isMarkedForDeconstruction : bool` 也在，取方法更稳）。
没有 `Deconstructable` 组件或未标记 ⇒ `false`。

`【实测 2026-11-18】`（Cycle-6 小档）：`POST /v1/building/select {"x":117,"y":216}` → `GET /v1/ui` 拿到
`UserMenuScreen/Buttons/拆解Button` → `/v1/ui/click` 点它 ⇒ 立刻 `GET /v1/buildings?prefabId=Tile` 见到
`Tile @(117,216) markedForDeconstruct:true`，而相邻的 `(116,216)` 等仍是 `false`。
**这一步不需要派小人** —— 待拆建筑以前在接口里完全隐形，现在看得见了。

#### ② `/v1/dupes[].choreTarget`：作业目标（不是导航终点）
`chore` 仍是 `{text,id}`；新增**同级**字段 `choreTarget`：

```json
"chore":{"text":"拆解","id":"Deconstruct"},
"choreTarget":{"prefabId":"Tile","cell":137493,"x":117,"y":216,"worldId":0,"name":"TileComplete"}
```

来源：**`Chore.target`**（属性，类型 `IStateMachineTarget`，元数据实测）→ 转 `KMonoBehaviour` 取 `gameObject`；
prefabId 走 `Building.Def.PrefabID`，退回 `KPrefabID.PrefabTag.Name`；坐标走 `Grid.PosToCell`。
没有目标（如 `IdleChore`）⇒ `"choreTarget":null`，不编造。

⚠️ **不要拿 `path.targetX/targetY/targetPrefabId` 当作业目标** —— 那是**导航终点**。
`【实测 2026-11-18】` 阿玛里 `chore=[Deconstruct]` 的 `choreTarget` 正好是刚标记的 `Tile @(117,216)`；
同批还能看到 `ManualGenerator` / `Outhouse` / `MopPlacer` 各自的目标。

#### ③ `/v1/shot` 支持按世界坐标截：`centerX` / `centerY`
```bash
GET /v1/shot?w=800&h=450&centerX=117&centerY=217
```
内部：把选中的世界相机**临时移**到该格 → 走**离屏 RenderTexture** 渲染 → **立刻还原原位**（`finally` + 异常路径都还原）。
指定中心时**不再用 `ScreenCapture`**：它抓的是当前屏幕，相机移动要到下一帧才反映，同帧抓会拿到移动前的画面。

⚠️ 参数**没有**叫 `cx/cy`：`cx/cy` 在本端点早就是"裁剪原点（屏幕像素）"，语义冲突。

响应新增：
```json
"center":{"x":117,"y":217,"cell":138129,"cameraMoved":true,"cameraRestored":true},
"viewWorld":{"minX":94.244,"minY":204.2,"maxX":139.756,"maxY":229.8,"perPixelX":0.057,"perPixelY":0.057}
```
* `cameraRestored:true` 才是真还原；还原失败会直接报 `500 CAM_RESTORE_FAILED` 并放弃这张图（不把相机留在别处）。
* `viewWorld` 是本次画面覆盖的世界坐标范围，`perPixelX/Y` 用来在像素↔格子之间换算。
* `【实测 2026-11-18】` `centerX=117&centerY=217` ⇒ `viewWorld` 中心正好 `(117, 217)`，`renderPath:"RenderTexture"`，`cameraRestored:true`。

#### ④ `/v1/meta` 新增 `blockingDialog`：**只报告，不自动点掉**
```json
"blockingDialog":{"present":true,"count":1,"screens":["ResearchScreen"],
                   "note":"只报告不自动点掉（自动确认可能误触危险操作）…"}
```
**判据 = 白名单（0.95.1 起）**：只认下表这些**具体类名**（全部来自元数据：直接继承 `KModalScreen` 且带
confirm/cancel 或"强制确认"语义）。反序列化后逐条给出 `whitelist` 数组，便于调用方核对版本。

| 类名 | 用途 |
|---|---|
| `ConfirmDialogScreen` | 通用确认框（`confirmButton` / `cancelButton`） |
| `BarterConfirmationScreen` | 交易确认 |
| `GameOverScreen` | 全灭结算 |
| `VictoryScreen` | 胜利结算 |
| `DLCBetaMessageScreen` | DLC 测试期提示 |
| `OldVersionMessageScreen` | 旧版本提示 |
| `DemoOverScreen` | 试玩结束 |
| `PatchNotesScreen` | 更新后补丁说明（要按 OK 才继续） |

**刻意不含** `ResearchScreen` / `SkillsScreen` / `StarmapScreen` / `LoadScreen` 这类常驻面板 ——
它们同样是 `KModalScreen` 子类，但开着不代表"被挡住"。
0.95.0 曾用 `is KModalScreen` 判断，结果殖民地一加载就报 `ResearchScreen` ⇒ **误报**；
0.95.1 改成白名单后 `【实测 2026-11-18】` 同一场景（20 个 screen 激活、`ResearchScreen` 在其中）返回
`present:false, count:0`。

**不自动点掉**是有意的 —— 自动确认可能误触真正的危险操作；要清掉由调用方自己决定，例如
`POST /v1/ui/click {"text":"好的","screen":"ConfirmDialogScreen"}`（已实测可点掉）。

⚠️ 还剩的验证缺口：本轮**没能在线触发**一个真正的 `ConfirmDialogScreen`（退出到主菜单被本 mod 的安全名单挡住、
存档又会写出带 debug 痕迹的文件），所以 `present:true` 只在代码路径上成立，尚未拿到真机样本。
一旦有人手动弹出确认框（例如游戏内菜单里的退出确认），`/v1/meta` 应立刻变成 `present:true`。
### 3.22 `GET /v1/saves`（M6.17，只读）

```json
{"root":"C:\\Users\\…\\save_files\\",
 "roots":{"local":"C:\\Users\\…\\save_files\\","cloud":"C:\\Users\\…\\cloud_save_files\\","cloudExists":true},
 "scope":"local+cloud","count":8,"counts":{"local":1,"cloud":7},
 "saves":[{"file":"76561198174293646/第七次重启/第七次重启.sav","fileName":"第七次重启.sav",
           "source":"cloud","saveDir":"C:\\Users\\…\\cloud_save_files\\",
           "colony":null,"cycle":null,"dups":null,
           "colonyInfoUnavailable":"未解析存档头（.sav 二进制）；如需殖民地名/周期请后续版本加 LoadHeader",
           "isAutoSave":false,"clusterId":null,
           "savedAt":"2026-11-15 21:31:26","sizeBytes":1379194},
          {"file":"外世/完美.sav","fileName":"完美.sav","source":"local","saveDir":"…\\save_files\\",
           "colony":null,"cycle":null,"dups":null,"isAutoSave":false,
           "savedAt":"2026-11-14 21:59:07","sizeBytes":75093727}]}
```

#### 三处如实说明
1. **两个根都扫（0.93.8 起）**：本地 `save_files` + 云 `cloud_save_files`（布局 `cloud_save_files\<steamId>\<殖民地名>\*.sav`）。
   每条带 `source`（`local`/`cloud`）与 `saveDir`（它所属的根），`count` 是两者之和、`counts` 分列。
   `scope` 如实写：两处都扫到 ⇒ `local+cloud`；云目录不存在或枚举失败 ⇒ `local-only(<原因>)`（**不冒充全量**，另有 `cloudError`）。
   `【实测 2026-11-17】` 本地 1 个 + 云端 7 个 ⇒ `count=8`。
   ⚠️ **0.93.6 及以前只扫本地**（`scope:"local-only(cloud_save_files 未扫)"`）：那天主菜单「继续游戏」加载的
   恰恰是云端那个「第七次重启」，只看这个端点会以为"机器上只有一个档" —— 所以 0.93.8 补齐。
2. **`colony` / `cycle` / `dups` 恒为 `null`**：解析存档头需要 `SaveLoader.LoadHeader(string, ref Header)`，
   而 `Header` 的类型归属在元数据 dump 里不可信（见 DESIGN §10.6ai），**不猜字段名**；
   原因写在每条记录的 `colonyInfoUnavailable` 里；
3. **`file` vs `fileName`**：`file` 是**相对它所属根目录**的路径（云端那条就以 `76561198…/` 开头），
   配 `source` / `saveDir` 一起看；`fileName` 是纯文件名。排序按 `savedAt` 倒序（新的在前）。

---

### 3.23 `POST /v1/save/load` —— **已禁用（不是"待实现"）**

```json
501 {"code":"LOAD_PATH_UNSAFE",
     "message":"已禁用：在游戏内调用 SaveLoader.Load 会先 ClearScene，实测会清空当前殖民地且加载失败不回滚…"}
```

#### 为什么不能用（四条，都实测过）
1. `SaveLoader.Load(string)` 内部顺序是 **`ClearScene()` → `SaveManager.Load(IReader)`** —— **先毁后建**；
2. 实测在**已进入殖民地**时调用会撞**单例冲突**：
   `WorldResourceAmountTracker … already initialized（existing: SaveGame, new: SaveGame(Clone)）` ⇒ `Load` 返回 `false`；
3. **但场景已经被清了** —— 当前殖民地丢失、**没有回滚**，随后 `Grid` 失效导致每帧空引用（实测 `16890` 次）直至崩溃；
4. **主菜单下 `SaveLoader.Instance` 为 `null`**，根本调不动；`MainMenu.LoadGame()` 是 **nonpublic**（本插件不用反射）。

⇒ **同一时刻：游戏内会毁档、主菜单调不动** ⇒ 没有安全路径 ⇒ **硬禁用**。
正确入口待查（候选：`SaveLoader.LoadScene()` + `SetActiveSaveFilePath(path)`，两者都是 public）。

#### 校验顺序保留（所以 404/400 行为正常）
```
file 必填 → confirm:true → 白名单（必须是 /v1/saves 里出现过的） → 然后才 501
```
⇒ 缺 `file` ⇒ `400 BAD_REQUEST`；缺 `confirm` ⇒ `400 CONFIRM_REQUIRED`；
`file` 不在列表里 ⇒ `404 SAVE_NOT_FOUND`；**只有"本来要真加载"时才返回 501**。
**501 这条路径不会改动任何游戏状态。**

### 3.24 `POST /v1/job` `type=build` —— **作弊生成已可用；玩家等价建造未实现**（M6.39）

> 🔴 **先说结论（发布前必读）**
>
> **「玩家等价建造」（点分类 → 点建筑 → 世界坐标放置 → 小人搬材料施工）我们没有实现，也不能在近期实现。**
> 现在能用的只有 **`placeMode:"instant"`（作弊）** 和 **`placeMode:"defbuild"`（作弊，直造）**。
> 两者都：**不扣材料、不产生建造 chore、不需要小人施工**，且都必须过作弊闸门。

#### 为什么玩家等价建造做不到（实测 + IL 证据）

| 我们试过的入口 | 结果 |
|---|---|
| `BuildTool.OnLeftClickDown(Vector3)` | public，但 IL 显示它只做 `ClampPositionToWorld` + `AddDragPoint`，**不建造** |
| `BuildTool.OnLeftClickUp(Vector3)` | public，会回调 `DragTool.OnDragTool`；实测 `NOT_PLACED` |
| `BuildTool.OnDragTool(int,int)` | **CS0122 protected**（dump 曾误报 public） |
| `BuildTool.TryBuild(int)` | private，反射可调用；但见下 |
| `TryBuild` 前置 | IL：`visualizer==null → return`；`cell==lastDragCell && 朝向相同 → return`；`Grid.PosToCell(visualizer.position) != cell → 跳过建造` |
| `TryBuild` 后处理 | 走到 `PostProcessBuild` 后 **`PlanScreen.GetBuildingPriority()` 抛 NRE** —— `PlanScreen.Instance` 只在 `OnPrefabInit/OnSpawn` 赋值，**没有 UI 交互时它不存在**（底部工具栏属于 `BuildMenu`，不是 `PlanScreen`） |
| 副作用风险 | 「工具已 `Activate` + `def` 已设 + `visualizer` 已挪 + `PlanScreen==null`」这个中间态，**玩家下一次真实点击世界会把 NRE 抛到 Unity ⇒ 崩游戏**（实测崩过一次）。0.61.0 起用 `finally { BuildTool.Deactivate(); }` 收干净 |

#### 可用的两条（都是作弊）

```http
POST /v1/job
{"type":"build","prefabId":"Tile","x":108,"y":220,"confirm":true,
 "placeMode":"instant","materials":["SandStone"]}
```

| placeMode | 机制 | 需闸门 | `jobState` | 材料 |
|---|---|---|---|---|
| `instant`（**推荐**） | 临时把 `DebugHandler.InstantBuildMode` 设为 true，再反射调 `BuildTool.TryBuild(cell)`，**在 true 期间调用**，`finally` 还原 | ✅ | `cheat-instant` | 不扣 |
| `defbuild` | `BuildingDef.Build(cell, Neutral, null, materials, …)` 直接造对象 | ✅ | `cheat-spawned` | 不扣 |
| `trybuild` / `dragtool` | 反射调用，**已知不通**（`NOT_PLACED` / NRE），仅保留作排查 | — | — | — |

#### 材料这一关（很多人会踩）
* 需求质量取 `BuildingDef.Mass[0]`；
* 材料种类由 `BuildingDef.MaterialCategory` 决定，**`DefaultElements()` 只是"该类别里的默认一项"**，不一定是库存里有的那个；
* **`materials` 参数可以让调用方指定**（如 `["SandStone"]`、`["Cuprite"]`），生效后响应里 `materialsDiag` 会写 `materialsSource=request.materials; used=[Cuprite]`；
* **材料不足现在报 `409 INSUFFICIENT_MATERIAL`**（不再是容易误导的 `NOT_PLACED`）：
  ```json
  {"code":"INSUFFICIENT_MATERIAL",
   "details":{"need":[{"category":"Metal","kg":200}],
              "have":[{"tags":"Cuprite=114.904","kg":114.904}],
              "hint":"需要 200kg Metal，当前仅 114.904kg",
              "gateTrace":"…","toolRestored":true}}
  ```
* **实例**：`MineralDeoxidizer`（氧气扩散器）属 **Metal（金属矿）** 类，默认 `ZincOre`；
  **藻类（Algae）不是它的建造材料，而是它运行时的消耗品** —— 别拿 `/v1/state` 里 Algae 的数量当"能不能建"。

#### 自证字段（每次 build 都回）
`placeMode` / `activated` / `methodInvoked` / `builtItemAtCell` / `builtItemInTrueWindow` /
`toolRestored` / `restoreErr` / `instantBuildDiag`（`before` / `setToTrueReadBack` / `restoredTo`）/
`gateTrace`（`visualizerNull` / `visPosMatches` / `dedupHit` / `defNull` / `instantBuildAtInvoke` / `instantBuildAfterInvoke`）/
`materialsDiag`（`materialsSource` / `used` / `defaultElements` / `materialCategory`）

#### 回归基线（验收用）
```http
POST /v1/job {"type":"build","x":108,"y":220,"prefabId":"Tile","confirm":true,
              "placeMode":"instant","materials":["SandStone"]}
→ placed:true, jobState:"cheat-instant", builtItemInTrueWindow:true, toolRestored:true

GET /v1/buildings?x0=108&y0=220&x1=108&y1=220
→ count:1（Tile @ 108,220）
```
**跑完再用真实鼠标点一下世界，不应崩溃**（这是 0.61.0 修的副作用）。

### 3.25 研究（M6.43，**实测全绿**）

> 结论先行：**研究可以用 API 完整做完** —— 读状态、设研究方向（玩家等价）、直接完成（作弊，需闸门）。
> **UI 点击这条路对研究不可行**，理由见本节末尾。

#### 3.25.1 `GET /v1/research`（只读）

```http
GET /v1/research
```

```json
{"source":"Research.Instance（Research.techs(反射只读)）",
 "techCount":100,"activeTechId":"FineDining","targetTechId":"FineDining",
 "researchPointInventory":{"byType":{}},
 "completed":[{"techId":"FarmingTech","name":"基础耕种"},
              {"techId":"Combustion","name":"内燃机"},
              {"techId":"PowerRegulation","name":"电力调节"},
              {"techId":"Jobs","name":"职业管理"},
              {"techId":"ImprovedOxygen","name":"空气系统"}],
 "completedCount":10,
 "available":[{"techId":"FineDining","name":"食物制备","progressPct":0,"isActive":true},
              {"techId":"Agriculture","name":"农业","progressPct":0,"isActive":false}],
 "availableCount":90,
 "note":"只读：不改变任何研究状态。`isActive:true` 表示游戏当前正在研究它"}
```

* `name` 是**中文本地化文本**（`Catalog.PlainText(Tech.Name)`），不是 key；
* `activeTechId` / `targetTechId` 为 `null` 表示"当前没有研究方向"（新档常见）；
* **唯一非公开成员**是 `Research.techs`（`List<TechInstance>`），用反射**只读**读取 —— 其余全是 public：
  `Research.Instance`、`GetTechInstance(string)`、`GetActiveResearch()`、`GetTargetResearch()`、
  `IsBeingResearched(Tech)`、`TechInstance.IsComplete()`、`GetTotalPercentageComplete()`。

#### 3.25.2 设研究方向（**玩家等价**，不挂闸门）

```http
POST /v1/job
{"type":"research","techId":"FineDining"}
```

```json
{"type":"research","techId":"FineDining","name":"食物制备",
 "jobState":"target-set",
 "activeTechIdAfter":"FineDining","applied":true,
 "method":"Research.SetActiveResearch(Tech, true)（public）",
 "note":"玩家等价：只设定研究目标，之后小人会去研究站工作；不修改完成状态"}
```

* **`applied` 是回读校验**：设完再读 `GetActiveResearch()` 比对，不是"我调用了就算成功"；
* **视觉证据（实测）**：设置后打开研究界面，科技树上目标科技**橙色高亮** ⇒ 游戏真的把它当成了当前研究方向；
* `techId` 有值时**完全不看 `x` / `y`**（早期版本因为分派位置写错曾被坐标校验挡成 `400 需要 x / y`，0.64.1 已修）；
* 错误：`400 UNKNOWN_TECH`（没这个 techId）、`400 ALREADY_COMPLETE`（已完成，无需再选）。

**「设方向」≠「完成」**：前者只改"当前研究目标"，`completed` 不变、`isComplete` 仍为 false，
需要小人去研究站工作积累点数；后者见 3.25.3。

#### 3.25.3 直接完成（**作弊**，需闸门）

```http
POST /v1/job
{"type":"research","techId":"FoodRepurposing","instant":true}
```

```json
{"type":"research","techId":"FoodRepurposing","name":"食物再利用",
 "jobState":"cheat-research","spawned":false,"materialsConsumed":false,
 "wasCompleteBefore":false,"isCompleteAfter":true,
 "method":"TechInstance.Purchased()（public）",
 "note":"作弊路径：直接标记该科技完成，不消耗研究点数、不需要小人研究"}
```

* **需要作弊闸门**：`allowCheats` 且（sandbox 或 debug），否则 `403` + `Gate.UnavailableCode()`；
* **`materialsConsumed:false`** —— 这条路径**不消耗研究点数**；
* 回读：`completedCount` 10 → 11，该科技出现在 `completed` 里（实测）；
* 这样做的语义是"天意的大手"，**不是玩家等价操作**。

#### 3.25.4 为什么研究不能走 UI 点击（实测结论）

| 事实 | 证据 |
|---|---|
| 研究界面开着时 `ResearchScreen` **只有 5 个 `KButton`** | `GET /v1/build-menu?screen=ResearchScreen` ⇒ `CloseButton` / `ClearButton` / `ZoomOut` / `ZoomIn` / 搜索框清空 |
| 科技节点**不是 `KButton`** | 上述清单里没有任何科技节点 |
| `Clickable` / `TechWidget` **这两个类型在程序集里不存在** | `layout.exe types` 扫 `Assembly-CSharp` 与 `Assembly-CSharp-firstpass`：含 `Click` 的只有 `OnClick`、`ClickCallback`、`ClickType`、`IPointerClickHandler` …；含 `Tech` 的只有 `POIxxxTechUnlockConfig`、`ResearchTreeTitle` … |
| `ManagementMenu` 里的「研究」页签也点不到 | 该 screen 未激活时 `POST /v1/ui/click {"text":"研究","screen":"ManagementMenu"}` ⇒ `404`（匹配到文字 0 个） |

⇒ **研究一律走 API（3.25.1–3.25.3），不要依赖 UI 点击。**
（`GET /v1/ui/widgets` 提供了"按 `IPointerClickHandler` 接口枚举可点控件"的能力，
但那是给**别的界面**用的；研究不需要它。即便枚举出 `ResearchTreeTitle` 一类节点，
点击入口是否存在仍未验证，别在这条路上花时间。）

#### 3.25.5 `/v1/ui/click` 对 `KToggle` 的定位（**重要限制**）

研究界面的科技节点是 **`KToggle`**（`kind:"KToggle"`，`text` 形如 `<link="FINEDINING">食物制备</link>`），
`/v1/ui/click {"techId":"…"}` 可以点它（`clickedVia:"KToggle.Click"`），但：

> ⚠️ **`KToggle.Click()` 只做视觉切换，不保证业务效果。**
> 实测：点 `AGRICULTURE` 后 `/v1/research` 的 `activeTechId` **没有变化**；
> 而点**已经选中**的科技（`isOn == true`）会把研究方向**清空**（切换语义）。
> 因此 `/v1/ui/click` 对已开启的 `KToggle` **直接拒绝**：`409 ALREADY_ON`（`clicked:false`）。

**⇒ 研究请一律用 `/v1/job {"type":"research","techId":"…"}`（§3.25.2），不要用 UI 点击。**
`/v1/ui/click` 的 `KToggle` 支持仅用于"界面能被程序操作"这一能力的展示，不是研究入口。

#### 3.24.1 `build` 的能力边界：**多格建筑已支持**（M6.63 更正，此前的"只保证 1x1"作废）

| 规模 | 结果 | 证据 |
|---|---|---|
| **1x1**（如 `Tile`） | ✅ 稳定成功 | `placed:true`，`foundCell == requestedCell`，回读一致 |
| **1x2 / 2x2**（如 `MineralDeoxidizer` / `AlgaeHabitat`） | ✅ **已支持**（0.86.0 起） | 见下方实测证据 |

**多格建筑的真正死因（M6.62 定位，M6.63 修复）**：**建造预览（`visualizer`）不在目标格**。

* `TryBuild(cell)` 的**单格路径直接用传入的 `cell`** ⇒ 预览在哪都无所谓（这一点长期误导了我们：1x1 一直成功）；
* **多格建筑要靠预览的 `transform.position` + `PlacementOffsets` 推算占地** ⇒ 预览不在目标格 ⇒ 占地算不合法 ⇒ **静默不建**
  （表现是 `methodInvoked=True` 但 `builtItemAtCell=False`、无异常）。

**修法**（`placeMode != "defbuild"` 时）：
1. 反射读 `InterfaceTool.visualizer`，记下原位置与所在格 `visualizerFrom`；
2. `visualizerFrom != cell` ⇒ `vis.transform.position = Grid.CellToPos(cell)`，记 `visualizerMoved/To`；
3. **`finally` 里恢复原位**（不把玩家的预览丢在别处）。

**实测证据（氧气扩散器，0.86.0）**
```http
POST {"type":"build","x":109,"y":217,"prefabId":"MineralDeoxidizer","confirm":true,
      "placeMode":"trybuild","materials":["Cuprite"]}
→ ok:true placed:true jobState:"under-construction" footprint=1x2
  visualizerMoved=True  visualizerFrom=136864  visualizerTo=138121  visPosMatches=True
  builtItemAtCell=True  foundCell=138121
GET /v1/buildings?x0=109&y0=217&x1=109&y1=217
→ count=1  MineralDeoxidizer  underConstruction=True
```
⇒ **这是"画蓝图 → 小人来建"的玩家等价路径**，不是作弊生成。
材料未拦（箱里 `Cuprite` 114.9kg < 需求 200kg）是**按设计**：材料不足不阻塞蓝图（§3.24.3）。

**锚点规则**：`TryBuild` 期望的 `cell` 按"**鼠标所指格**"处理是**正确的** —— 1x2 一次成功，
**不需要**按 `PlacementOffsets` 换算左下角（此前本节写"锚点未核实"，此结论已结）。

**`validateOnly` 与占地（M6.47 更正，此前本节结论有误）**：

* 【实测】**游戏自己的 `IsValidPlaceLocation` 已经校验整个占地**：
  先 `instant` 建 `Tile @ (108,221)`，再 `validateOnly AlgaeHabitat @ (108,220)`
  ⇒ `valid:false`，`reason:"必须建在未占用的空间中"`（**游戏给的 ref string**，不是我们补的）。
* 因此 API **不在 `IsValidPlaceLocation` 之后补占用检查**（补了也永远跑不到 —— 游戏先拦了，是死代码，已删除）。
  ⚠️ 但**占用判定本身仍保留，位置提前到 `IsValidPlaceLocation` 之前**，用途不同：
  它给的是 **`409 CELL_OCCUPIED`**（"这里有东西"），比游戏的 `BUILD_INVALID`（"这里不能建"）更精确（§3.24.2）。
* 至于「`(113,210)` 报 `valid:true` 而 `(113,211)` 有 `ExteriorWall`」：
  解释是 **`ExteriorWall` 位于墙体层、不阻塞建筑放置**，那个位置在游戏眼里本来就合法。
  **此前"游戏漏检锚点以外格子"的说法是错的，已撤回。**
* API 只要该 prefab 是多格建筑（`WidthInCells*HeightInCells > 1`）就**总是返回** `"footprint":{"w":…,"h":…}`。

#### 3.25.6 `KToggle` 的显式取消：`allowToggleOff`

```http
POST /v1/ui/click {"techId":"FINEDINING"}                        → 409 ALREADY_ON（防手滑）
POST /v1/ui/click {"techId":"FINEDINING","allowToggleOff":true}   → jobState:"toggled-off"
```
「再次点击取消」本身是**正常设计**（研究要人、要材料、要电，某些情况下取消研究是合理的），
所以护栏不做成"永远禁止"：**默认拒绝（防误触），显式传 `allowToggleOff:true` 才执行取消**。

#### 3.26 `GET /v1/buildings` 的计数语义（M6.48 更正）

| 字段 | 含义 |
|---|---|
| `totalItems` | 表里建筑总数 = **已完工（`Components.BuildingCompletes.Count`）+ 施工中（`FindObjectsOfType<BuildingUnderConstruction>()`）**（M6.50 起施工点默认计入）。**不是可翻页总数** |
| **`total`** | **0.96.0**：**命中总数** —— 通过 `fog / bbox / prefabId` 之后的条数 = **可翻页总数**；`total = count + skipped + notEmittedDueToLimit` |
| **`hasMore` / `nextOffset`** | **0.96.0**：还有下一页吗 / 下一页的 `offset`（没有则 `-1`）；`truncated` 与之同值（向后兼容） |
| `scanned` | **本次全量扫描过的条数**（不再早退；原实现只报"本页处理了几条"，会误读成全档总数） |
| `count` / `emitted` | 真正写进 `buildings[]` 的条数 |
| `filteredByFog` / `filteredInvalid` / `filteredByPrefab` / `filteredByBox` | 各类过滤掉的数量 |
| `skipped` | 命中但因 `offset` 落在本页窗口**之前**的数量（**0.96.0 起 `prefabId` 先判、再算 `offset`**，所以 `skipped = min(offset, total)`） |
| **`notEmittedDueToLimit`** | 命中但因落在本页窗口**之后**没输出的数量（`hasMore` 为真时会出现） |

**硬等式**（每轮日志都会自检）：
```
scanned == emitted + filteredByFog + filteredInvalid + filteredByPrefab + filteredByBox + skipped + notEmittedDueToLimit
total   == emitted + skipped + notEmittedDueToLimit
```
`limit=1` 时：`scanned` = 全表条数（如 202），`count` = 1，`notEmittedDueToLimit` = 200 左右 ——
**两者不再混为一谈。**

**M6.50：施工点（蓝图）默认列出，并带 `underConstruction` 字段。**
（0.96.0 起默认不带 `name`，要名字加 `?names=1`；下面的形状就是默认形态。）

```json
{"prefabId":"AlgaeHabitat","x":109,"y":217,
 "cell":…,"worldId":0,"underConstruction":true,"operational":false,"enabled":null,"markedForDeconstruct":false}
```

为什么改：`Components.BuildingCompletes` **只有已完工的建筑** —— 蓝图画在哪、有没有真的加上，
用这个端点一律看不见，于是"建没建成"的判断是瞎的（实测：蓝图放下后 `/v1/buildings` 返回 0 条，
但 `GET /v1/marks?x=…&y=…` 的 `objectsAtCell` 是 1）。

实现注意：**游戏里没有 `Components.Buildings` 这个容器**（编译期确认，CS0117），
所以是"已完工列表 + `FindObjectsOfType<BuildingUnderConstruction>()`"两路合并。
扫描日志会打 `totalItems=N（完工=A 施工中=B）`，可直接核对。

判据建议：**`underConstruction:true` ⇒ 还没建完**（在等材料 / 等小人施工 / 等挖通），
不能当成"已建好"用；要判断"建好了"必须 `underConstruction:false`。

#### 3.27 `GET /v1/inventory?tag=Cuprite`（只读，M6.49）—— 把"三种口径"分开报

```json
{"tag":"Cuprite",
 "inStorage":114.904,
 "onGround":0,
 "inWorld":null, "inWorldUnavailable":true,
 "storagesScanned":16, "groundStacks":0, "worldsScanned":0,
 "source":"inStorage=Storage.GetAmountAvailable；onGround=Pickupables(无 storage) 的 PrimaryElement.Mass；inWorld=WorldInventory.GetAmount(tag,false)",
 "note":"建造只认 inStorage（与 INSUFFICIENT_MATERIAL 的 have 同口径）；游戏资源面板显示的多半是 inWorld（含未挖矿）或 inWorld+onGround ⇒ 面板比 inStorage 大是正常的"}
```

| 字段 | 含义 | 用途 |
|---|---|---|
| `inStorage` | 储物箱里**可用于建造**的量 | **建造看这个**；与 `409 INSUFFICIENT_MATERIAL` 的 `have` 同口径 |
| `onGround` | 地上散落（`Pickupable` 且不在任何 `Storage` 里） | 判断"要不要先派人搬" |
| `inWorld` | 世界总量（多星球相加，`WorldInventory.GetAmount(tag,false)`） | 面板数字大概率对应这个 |
| `inWorldUnavailable` | 读不到世界总量（`worldsScanned=0` 或全为 0）| **如实标注，不拿 0 冒充** |

**为什么需要它**：实测出现"玩家说库存 5 吨铜，API 只读到 114.9 kg" ——
不是 bug，是**口径不同**：`/v1/materials` 与 `/v1/state.elements` **只统计储物箱**。
有了这个端点，"面板有 5 吨却建不了"就不用再猜。

#### 3.24.2 `409 CELL_OCCUPIED`：不在已有建筑/蓝图上重复建造（M6.51）

```http
POST /v1/job {"type":"build","x":109,"y":217,"prefabId":"AlgaeHabitat","confirm":true,
              "placeMode":"trybuild","materials":["Dirt"]}
→ 409 CELL_OCCUPIED
  {"cell":…,"prefabId":"AlgaeHabitat","underConstruction":true}
  message: 目标格已被占用：cell=… ，prefab=AlgaeHabitat（施工中/蓝图） ⇒ 不在已有建筑/蓝图上重复建造
```

**为什么加**：往**已有蓝图**的格子重复发 `placeMode:"trybuild"` 时，`BuildTool.TryBuild` 内部会抛
`NullReferenceException`（大概率仍是 `PlanScreen.GetBuildingPriority()` 那条）。异常被我们 `catch` 住、
**游戏不会崩**，但对调用方只报 `REFLECT_INVOKE_FAILED` —— **看不出真正原因**。
现在**在放置之前**先扫该 prefab **占地范围内的每一格**（所有 `ObjectLayer`），
有 `Building` 或 `BuildingUnderConstruction` 就直接 `409`，并回显是哪个 prefab、是不是蓝图。

* `underConstruction:true` ⇒ 那格上是**蓝图/施工中**；
* `underConstruction:false` ⇒ 那格上已有**完工建筑**；
* 判据：**看到 `CELL_OCCUPIED` 就换个格子（或先 `deconstruct`）**，不要重复发。
#### 3.28 `GET /v1/tools`（只读，M6.55）—— 引擎里的工具清单

```json
{"count":39,"emitted":39,"activeToolId":"SelectTool",
 "source":"PlayerController.Instance.tools（public 字段，游戏自己的工具集合）",
 "tools":[{"id":"DigTool","active":false,"enabled":null},
          {"id":"MopTool","active":false,"enabled":null},
          {"id":"DisinfectTool","active":false,"enabled":null}, …],
 "note":"id 是游戏类名，稳定唯一；中文对照见 docs/API.md §3.28 的静态表。列的是引擎里的工具（含沙盒/调试工具），不等于玩家界面上看得到的按钮"}
```

**设计决定（小僵拍板）**：`id`（类名）就是主键，**不追求运行时解析中文名**。
中英对照放在下面这张**静态表**里，人工核对一次即可 —— 运行时去挖 `LocText` / `HoverTextConfiguration`
反而会引入不确定（实测工具对象上没有 `LocText`，早期实现全退化成类名，等于假数据）。

* `active` 读 nonpublic 字段 `PlayerController.activeTool`（反射**只读**）；
* `enabled` 只在工具自身有可读 `enabled` 属性时给 `true/false`，**否则 `null`**（`null` = 读不到，不是禁用）；
* 本端点**不切换工具、不改任何状态**。

> ⚠️ **`/v1/tools` 列的是"引擎里的工具"（39 个，含沙盒/调试工具），
> 不等于玩家界面上看得到的按钮（界面约 15 个）。** 别误读成"右下角有 39 个按钮"。

#### 3.28.1 工具 id ↔ 中文对照（静态表，人工核对）

**界面可见的 15 个（玩家悬停实测）**

| id | 中文 | 说明 |
|---|---|---|
| `BuildTool` | 新建 | 建造入口（对应 `POST /v1/job type=build`） |
| `PlaceTool` | 放置 | 放置蓝图（从蓝图集里取） |
| `DigTool` | 挖掘 | `type=dig` |
| `CancelTool` | 取消 | **模式**，不产生 job |
| `DeconstructTool` | 拆解 | `type=deconstruct` |
| `PrioritizeTool` | 优先度 | **模式**，不产生 job |
| `DisinfectTool` | 消毒 | `type=disinfect`（M6.76，单格；见 §3.37） |
| `AttackTool` | 攻击 | `type=attack`（M6.76，**矩形**；见 §3.37） |
| `CaptureTool` | 捕捉 | `type=capture`（M6.76，**矩形**）。注意：本 mod **不调**这个工具，走 `Capturable.MarkForCapture`（见 §3.37.4） |
| `EmptyPipeTool` | 清空管道 | `type=empty_pipe`（M6.76，单格；见 §3.37） |
| `SnapshotTool` | 拍摄快照 | 新建蓝图并立即放到别处；**未实现**（与蓝图工具的关系待确认） |
| `ClearTool` | **清扫** | 把碎片移到存储箱（**不是"清除"**）；`type=sweep` |
| `MopTool` | 擦拭 | `type=mop`（M6.76，单格液体）。本 mod **不调**这个工具，直接实例化 `MopPlacer`（见 §3.37.4） |
| `HarvestTool` | 收获 | `type=harvest` |
| `DisconnectTool` | 拆断 | **未实现** |

**沙盒工具（11 个，沙盒模式才可见）**：`SandboxBrushTool` / `SandboxFloodTool` / `SandboxSprinkleTool` /
`SandboxHeatTool` / `SandboxStressTool` / `SandboxClearFloorTool` / `SandboxCrittersTool` 等 ——
**只在沙盒模式下可用**；本 mod 把它们**照常列出**（它们在 `PlayerController.tools` 里），不代表普通存档能用。

**其它（不在界面主条里）**：
`SelectTool`（默认选中，`activeToolId` 常见值）/ `CopySettingsTool`、`CopyEntitySettingsTool`（复制设置，两者差别待确认）/
`UtilityBuildTool`、`WireBuildTool`（建造子类：管线/电线）/ `StampTool`、`PrebuildTool`、`CreateBlueprintTool`、
`UseBlueprintTool`（蓝图相关，具体对应待确认）/ `MoveToLocationTool` / `ClusterMapSelectTool` / `DebugTool`（debug 用）。

> ⚠️ **上表"待确认"的项我没有臆造中文**：`StampTool` / `PrebuildTool` / `CreateBlueprintTool` /
> `UseBlueprintTool` / `CopySettingsTool` vs `CopyEntitySettingsTool` / `UtilityBuildTool` vs `WireBuildTool` vs `BuildTool`
> 的**界面含义尚未用悬停核对**。要补，需要一次悬停截图对照（或对这些工具做一次动作观察）。

#### 3.24.3 材料不足**不阻塞蓝图**（M6.56，小僵拍板）

> 原话：**"肯定算的，而且就算材料不够也要算，不然没法提前规划了。"**

玩家在游戏里**没材料也能画蓝图**（等有材料/小人搬来再建），接口必须一致：

* **`placeMode:"trybuild"`（画蓝图）⇒ 材料不足不再是错误**：
  `INSUFFICIENT_MATERIAL` 在这个路径下**彻底不再出现**；
* **`placeMode:"instant"`（作弊、不扣料）⇒ 本来就不拦**；
* **`placeMode:"defbuild"`（直接生成实物、立即扣料语义）⇒ 仍保留材料校验**（`409 INSUFFICIENT_MATERIAL`）。

**成功响应新增 `materials` 块（只报数，不参与放行）**：

```json
{"placed":true,"jobState":"under-construction",
 "materials":{"need":[{"category":"Metal","kg":200}],
              "inStorage":114.904,"onGround":4922.171,"total":5037.075,
              "enough":true,"used":["Cuprite"],
              "hint":"箱里 114.904kg，地上 4922.171kg，合计 5037.075kg（enough 仅供参考，不参与放行）"}}
```

**三种口径**（与 `GET /v1/inventory?tag=…` 完全同口径）：

| 字段 | 含义 |
|---|---|
| `inStorage` | 储物箱可用（`Storage.GetAmountAvailable`） |
| `onGround` | 地上散落（`Pickupables` 中 `storage == null` 的 `PrimaryElement.Mass`）—— **小人能捡起来用** |
| `total` | `inStorage + onGround` |
| `enough` | `total >= need`，**仅信息**，不参与判断 |

⚠️ **不要用 `inWorld` 判断能不能建**：它常为 `null` + `inWorldUnavailable:true`（`WorldInventory` 在部分存档读不到量）。

**实测背景**：某档 `inStorage=114.904` 但 `onGround=4922.171` ——
旧实现只看箱子，于是把"地上 4.9 吨铜"当成没材料、把能画的蓝图挡掉了。**这是本条的由来。**

#### 3.24.4 "假失败"修正：`PostProcessBuild` 抛异常但蓝图其实已放下（M6.57）

**症状（0.80.0 实测）**
```
POST {"type":"build","x":104,"y":223,"prefabId":"Tile","confirm":true,
      "placeMode":"trybuild","materials":["SandStone"]}
→ 409 REFLECT_INVOKE_FAILED  NullReferenceException
     at PlanScreen.GetBuildingPriority()
     at BuildTool.PostProcessBuild(Boolean instantBuild, Vector3 pos, GameObject builtItem)
     at BuildTool.TryBuild(Int32 cell)
但回读：GET /v1/buildings?x0=104&y0=223&x1=104&y1=223 → count=1  Tile  underConstruction=True
```
**⇒ 蓝图真的放下了，只是"后处理"（设优先级）炸了。**
旧实现把它当失败返回，调用方会重试 ⇒ **每次都多放一个蓝图**（实测两次调用放下两个）。

**现在的处理顺序**：
1. 反射调用抛异常 ⇒ **不立刻判失败**，先把异常记到 `invokeExc`；
2. **照常回读**该 prefab 的占地（prefab 过滤 + ±1）；
3. **回读到对象 ⇒ 按成功返回**，并把异常降级成：
   ```json
   {"placed":true,"jobState":"under-construction", …,
    "warning":"TryBuild 调用抛异常：NullReferenceException: …",
    "warningKind":"POST_PROCESS_FAILED"}
   ```
4. **回读不到 ⇒ 才报 `409 REFLECT_INVOKE_FAILED`**（真的没放上）。

**`warning` 的成因（M6.58 更正，此前写法已撤回）**：

* 【已撤回】此前本节写"刚进档 `PlanScreen` 未初始化，等几秒 / 先开建造菜单即可"——
  **实测：手动打开建造菜单之后再建，`warningKind:"POST_PROCESS_FAILED"` 一样出现**，所以那个解释不成立。
* 【当前假设】**`BuildTool.selectedElements`（`IList<Tag>`，nonpub）为空**：
  `buildToolFields` 里它就是空列表，而 `PlanScreen.GetBuildingPriority()` 很可能按"已选材料"推算优先度 ⇒ 空列表 ⇒ NRE。
  0.81.0 起在 `Activate` 之后**若为空则塞入本次材料**，并在 `gateTrace` 里报
  `selectedElementsBefore` / `after` —— **看这两个数就知道假设成立与否**：
  * `after=1` 且 `warning` 消失 ⇒ 假设成立，这条路彻底干净；
  * `after=1` 但仍 `warning` ⇒ 假设不成立，需要再看 `PostProcessBuild` 读了别的什么（栈偏移 `+0x4d`）。
* 无论哪种，**"建造成功 + warning" 都是成功**：判据看 `placed:true` 与回读，不要看有没有 warning。

**`CELL_OCCUPIED` 的判定顺序（M6.58）**：
```
占地占用检查（CELL_OCCUPIED） → IsValidPlaceLocation → 材料（只报数，不拦） → TryBuild
```
以前把占用检查放在 `IsValidPlaceLocation` 之后，游戏会先报 `BUILD_INVALID「必须建在未占用的空间中」`，
调用方拿不到更精确的 `CELL_OCCUPIED`。**现在同格重复建造应稳定得到 `409 CELL_OCCUPIED`。**

#### 3.24.5 `PlanScreen.GetBuildingPriority()` 的 NRE 兜底（M6.61，Harmony Finalizer）

**实测事实链（每一步都有 gateTrace 证据）**
```
trybuild 成功放下蓝图，但 PostProcessBuild 里 PlanScreen.GetBuildingPriority() 抛 NRE（栈偏移 [0x00006]）
planScreenInstanceNull=false      planScreenInstanceId=-189146
planScreenPriorityProbe=THREW:NullReferenceException@PlanScreen.GetBuildingPriority [0x00006]
toolMenuInstanceNull=false        priorityScreenNull=false      priScreenId=-191824
priorityScreenLastSelected=PrioritySetting   priorityScreenGetLastSelected=OK
```
⇒ **三个候选对象（`PlanScreen` / `ToolMenu` / `PriorityScreen`）全都非 null 且可用**，
NRE 来自 `PlanScreen` 实例自身某个为 null 的成员；`PlanScreen` 成员表里**没有** priority 相关字段 ⇒ **停止猜测**。

**处理：Harmony `Finalizer`（不是 Prefix —— 只有 Finalizer 能既拿到异常、又改返回值）**
* `__exception == null` ⇒ **原样返回，不碰 `__result`**（玩家正常建造路径零影响）；
* 抛异常 ⇒ 兜底 `new PrioritySetting(PriorityClass.basic, 5)`（**全反射构造**：`PriorityClass` 编译期不可按名引用），
  吞掉异常 + 每次 `Log.Warn`；
* 开关：`settings.json` 的 **`FixPlanScreenPriorityNRE`**（默认 `true`）。设 `false` ⇒ **只记录不吞**（异常照原样上抛）。

**验收判据**
1. 新空格 `trybuild` ⇒ `ok:true`、`placed:true`，**`warningKind` 消失**；
2. 同格重复 ⇒ 仍 `409 CELL_OCCUPIED`；
3. `Player.log` 出现 `[build] PlanScreen.GetBuildingPriority 抛 NullReferenceException …`（证明补丁真的挂上并触发过）；
4. 玩家手动建造不受影响（正常路径 `__exception == null`，Finalizer 直接返回）。

#### 3.24.6 1x2 的真正死因：**预览（visualizer）不在目标格**（M6.62）

**实测证据（0.85.0，`purgetrace` 原文节选）**
```
请求 x=109 y=217 → cell=138121
visualizerCell=136864  (= 215*636+124 → (124,215))
visPosMatches=False
methodInvoked=True      builtItemAtCell=False      foundCell=-1
```
**⇒ 建造预览停在 `(124,215)`，目标格是 `(109,217)`。**

**为什么 1x1 一直没事、只有多格失败**：
* `TryBuild(cell)` 的**单格路径直接用传入的 `cell`** ⇒ 预览在哪都无所谓（这一点长期误导了我们）；
* **多格建筑要靠预览的 `transform.position` + `PlacementOffsets` 推算占地** ⇒ 预览不在目标格 ⇒ 占地算不合法 ⇒ **静默不建**
  （`methodInvoked=True` 但 `builtItemAtCell=False`）。

**0.86.0 的做法**（`placeMode != "defbuild"` 时）：
1. 反射读 `InterfaceTool.visualizer`，算它所在格 `visFrom`；
2. `visFrom != cell` ⇒ `vis.transform.position = Grid.CellToPos(cell)`，记 `visualizerMoved/From/To`；
3. **`finally` 里恢复原位置**（不把玩家的预览丢在别处，和 `toolRestored` 一个思路）。

**验收判据**
```http
POST {"type":"build","x":109,"y":217,"prefabId":"MineralDeoxidizer","confirm":true,
      "placeMode":"trybuild","materials":["Cuprite"]}
→ ok:true placed:true jobState:"under-construction"
GET /v1/buildings?prefabId=MineralDeoxidizer → 能查到 (109,217)/(109,218)，underConstruction=true
```
`gateTrace` 里应看到 `visualizerMoved=True; visualizerFrom=…; visualizerTo=…`。

> ✅ **已结（M6.63）**：`TryBuild` 期望的 `cell` 就是**鼠标所指格** —— 把预览挪到 `cell` 后 1x2 一次成功
> （`visualizerMoved=true / from=136864 / to=138121 / builtItemAtCell=true`，回读 `(109,217)` 得到
> `MineralDeoxidizer underConstruction=true`）。**不需要**按 `def.WidthInCells/HeightInCells` 换算左下角锚点。

##### 3.29 `GET /v1/geysers`（只读，M6.64）—— 间歇泉 / 火山清单

> **为什么单开端点**：间歇泉**不在** `Components.BuildingCompletes` 里 ——
> 实测全档 234 条建筑，`Group-Object prefabId` 里**没有任何 `Geyser*` / `Volcano*`**，
> 所以 `/v1/buildings` 永远读不到它们。而且间歇泉**长在中子质上、被物块掩埋**，挖开才激活
> ⇒ **必须"埋在物块里也能读到坐标"**，不能用"已激活/已挖开"当枚举条件。

```http
GET /v1/geysers              # 默认当前世界（worldId 不传 = 不过滤）
GET /v1/geysers?worldId=0
```

```json
{"count":3,"total":3,"filter":{"worldId":0},"filteredByWorld":0,"stateProbeOk":0,
 "geysers":[{"worldId":0,"x":112,"y":188,"cell":119764,
             "prefabId":"GeyserGeneric_slush_water",
             "elementId":"DirtyWater","geyserTypeId":"slush_water",
             "buried":true,"state":null,
             "stateUnavailable":"Geyser 成员表里没有公开的 phase/state 字段；反射 smi.phase/state 也没读到",
             "remainingIdleTime":null,"remainingEruptTime":null,"remainingEruptPreTime":null,
             "lifeTime":null,"shouldGoDormant":null}],
 "source":"UnityEngine.Object.FindObjectsOfType<Geyser>(true)（含 inactive；间歇泉不在 BuildingCompletes 里）",
 "note":"只读。buried 的判据 = 泉所在格是固体（Grid.IsSolidCell）；state 取不到为 null 并带原因，不编；不提供 nextEruptionCycle（拿不到周期数）"}
```

**字段与取值路径（实测/未实测如实标注）**

| 字段 | 取值路径 | 状态 |
|---|---|---|
| `worldId` / `x` / `y` / `cell` | `Grid.PosToCell(transform.position)` + `Grid.CellToXY` + `Grid.WorldIdx` | ✅ 编译验证 |
| `prefabId` | `KPrefabID.PrefabTag.Name`（firstpass，`public Tag PrefabTag`）；退回 `gameObject.name` | ✅ 成员存在 |
| `elementId` | `Geyser.configuration.geyserType.element`（`SimHashes`）→ `.ToString()` | ✅ 成员存在 |
| `geyserTypeId` | 同上 `.id` | ✅ |
| `buried` | `Grid.IsSolidCell(cell)` —— **泉所在格是固体即算被埋** | ✅ 判据简单可验证 |
| `remainingIdleTime` / `remainingEruptTime` / `remainingEruptPreTime` / `lifeTime` | `Geyser` 的 public 方法，逐个 try，失败为 null | ✅ 方法存在 |
| `shouldGoDormant` | `Geyser.ShouldGoDormant()`（public） | ✅ |
| `state` | **无公开字段**；反射尝试 `smi.phase` / `smi.state` | ⚠️ 取不到给 `null` + `stateUnavailable`（**不编原值**） |
| `stateDerived` | 从 `remainingEruptPreTime` / `remainingEruptTime` / `remainingIdleTime`（+`shouldGoDormant`）**推导**：`preErupt` / `erupting` / `dormant` / `idle` | ✅ 推导值（**明确不是游戏原始阶段值**，原始值看 `state`） |
| `FindObjectsOfType<Geyser>(true)` 实测条数 | —— | ✅ **实测通过（2026-11-15，Cycle 6990「外世」档）**：**72 个泉**，跨 6 个世界（worldId 1/3/4/7…）；`elementId` 覆盖 `Steam`/`MoltenNiobium`/`MoltenTungsten`/`Magma`/`CarbonDioxide`；`buried` 有 true 有 false（判据有区分度） |
| `nextEruptionCycle` | —— | ❌ **不提供**（拿不到周期数，只有秒数） |

**注意**：`total` 是 `FindObjectsOfType<Geyser>` 的原始条数，`count` 是过滤后**实际输出**的条数；
`filteredByWorld` 是被 `worldId` 过滤掉的数量 —— 三者对不上时先看这三个数。

**M6.65 实测修正：`FindObjectsOfType(true)` 会扫到「预制体模板」**

实测（0.88.1，Cycle 6990「外世」档）：`count=total=72`，其中 **31 条是内存里的 prefab 原型** ——
`x=0,y=0,cell=0,worldId=0,elementId=null,geyserTypeId=null`，`prefabId` 分布为**每种泉各一个**
（`Geyser` / `ChlorineGeyser` / `NiobiumGeyser` / `MethaneGeyser` / `GeyserGeneric_steam` …）。
**72 − 31 = 41 个真实的泉。**

**两道过滤（都在用，且都可观测）**
1. **主判据（世界归属）**：`geyser.GetMyWorldId()` 无效（`<0` 或 `255`）⇒ 视为模板并丢弃；
2. **兜底（坐标全 0）**：`x==0 && y==0 && cell==0 && worldId==0` ⇒ 视为模板并丢弃
   （⚠️ 代价：**真在 (0,0) 的泉也会被丢**；这条只是兜底，主判据是 1）。

两者丢弃的条数合并计入 **`filteredTemplates`**（响应里新增，**必须对数**）：
```
count + filteredTemplates + filteredByWorld == total
```

**新增 `?includeInactive=0`**：默认 `true`（保住"被埋/未激活"的泉）。
传 `0` 时改用 `FindObjectsOfType<Geyser>(false)` —— **用来实测"去掉 true 会不会丢真泉"**，响应里 `scanMode` 会写明本次用的哪一种。

**`stateDerived` 增加 `"unknown"`**：三个计时器（`remainingEruptPreTime` / `remainingEruptTime` / `remainingIdleTime`）
**都有值但全为 0** ⇒ `unknown`（"尚未开始计时/未初始化"），**与 `null`（取不到）区分**。
`note` 里说明了这一点。

**M6.66 实测结论：默认改成 `includeInactive=false`**

两次实测（同一场景，`worldId=0`）：

| 扫描方式 | 耗时 | `total` | `filteredTemplates` | `count` | 结果 |
|---|---|---|---|---|---|
| `includeInactive=true`（0.88.2 默认） | **192 ms** | 33 | **31** | **2** | `GeyserGeneric_molten_cobalt @(71,220)` / `GeyserGeneric_filthy_water @(139,210)` |
| `includeInactive=false`（**0.88.3 默认**） | **62 ms** | 2 | 0 | **2** | **同样两口泉，坐标/元素一致** |

结论（都用条数说话，不靠推测）：
1. **模板过滤正确**：`31` 与预期模板数吻合，`count + filteredTemplates + filteredByWorld == total` 成立（2+31+0=33）；
2. ✅ **实测：被物块掩埋的泉仍然是 active 对象** —— `includeInactive=false` 一样拿到那两口 `buried=true` 的泉；
3. ⇒ **默认改为 `false`**（同结果、62ms、省掉 31 条模板的过滤开销）；
   `?includeInactive=1`（或 `true`）仍可显式扫未激活对象（**逃生口**）。

⚠️ **两次的 `total` 语义不同，别看错**：
* `includeInactive=true` 的 `total=33` = 2 真泉 + 31 模板；
* `includeInactive=false` 的 `total=2` = 只有真泉；
* 大档（「外世」Cycle 6990，0.88.1 实测）是 `total=72 / filteredTemplates=31 / count=41` —— 那是 `true` 那一档的数字。

**`filteredTemplates` 保留**：默认 `false` 时它通常为 0，但它是"丢了多少模板"的唯一可观测点，**不删**。

**其它实测事实（写清楚，别记错）**
* 小僵记得的污水泉确实在 **`(139,210)`**，`buried=true`、`stateDerived=idle`；
* `stateDerived` 在真实数据上出过 `idle` / `preErupt`（推导链路是活的，不是恒 `null`）；
* 大档 `stateDerived` 分布：`dormant 13 / erupting 16 / idle 10 / preErupt 1`（0.88.1 实测，含模板）；
* 整个过程游戏 `Responding=True`、无异常 —— **此前那次卡死与本端点无关**（是 Harmony Finalizer 签名问题，0.87.0 已修）。

### 3.30 `GET /v1/worlds`（只读，M6.67）—— 世界表 + 区块行列数

```json
{"chunkSize":32,"activeWorldId":0,"count":8,
 "worlds":[{"worldId":0,
            "name":"expansion1::worlds/VanillaSandstoneDefault",
            "displayName":null,
            "offsetX":0,"offsetY":0,"width":240,"height":380,
            "chunksX":8,"chunksY":12,
            "dupeCount":0,"isActive":true}],
 "source":"ClusterManager.WorldContainers + WorldContainer.{id,worldName,overrideName,WorldOffset,Width,Height}（全部 public，成员已核）",
 "note":"只读。offsetX/offsetY 是该世界在整张 Grid 里的左下角；坐标一律全局网格坐标（与 dupes[].x/y 同一套）。chunkSize=32，列数=ceil(width/32)、行数=ceil(height/32)。`dupeCount` 目前恒为 0：复制人统计在 /v1/state，本端点未接入。"}
```

**`WorldContainer` 的真实成员名（经元数据核对，不是猜）**

| 需求 | 实际成员 | 可见性 |
|---|---|---|
| 世界 id | `id : int32` | public 字段 |
| 内部名 | `worldName : string` | public 字段 |
| 显示名 | `overrideName : string` | public 字段（常为 null ⇒ `displayName:null`） |
| 偏移 | `WorldOffset : Vector2I`（属性；`Vector2I` 在 **firstpass**，字段 `x`/`y`） | public |
| 尺寸 | `Width` / `Height : int32`（属性）；兜底 `WorldSize : Vector2I` | public |
| 世界列表 | `ClusterManager.Instance.WorldContainers : List<WorldContainer>` | public |
| 当前世界 | `ClusterManager.Instance.activeWorldId : int32` | public |

> ⚠️ 注意：`worldOffset` **字段**是 nonpub，但**同名属性 `WorldOffset` 是 public** —— 用属性，不用反射。

### 3.31 区块（chunk）规则 —— **写死，全项目唯一出处**

```
块大小       32 × 32（Chunks.ChunkSize，可配但当前写死 32）
原点         该世界的左下角 = WorldContainer.WorldOffset（运行时读，不写死）
Y 轴         向下为正（与引擎 Grid 一致）
列数         cols = ceil(width / 32)
行数         rows = ceil(height / 32)
末块         不足 32 的照常算一块，并报告真实 w/h（不是假装的 32）
块内 bbox    x0 = offsetX + cx*32
             y0 = offsetY + cy*32
             x1 = x0 + min(32, width  - cx*32) - 1
             y1 = y0 + min(32, height - cy*32) - 1
越界         cx/cy 超出 cols/rows ⇒ 400 BAD_CHUNK（带"该世界只有 N 列 × M 行"），**不静默裁剪**
```

**示例（world 0 = 240×380）**：`cols=8`、`rows=12`、共 **96 块**；末列宽 **16**，末行高 **28**。
即 `cx=7` 的块 `x0=224,x1=239`（宽 16）；`cy=11` 的块 `y0=352,y1=379`（高 28）。

### 3.32 `GET /v1/area`（只读，M6.67）—— 区域**数字档**

两种寻址（**二选一**）：
```http
GET /v1/area?worldId=0&cx=3&cy=5                 # 块寻址（规则见 §3.31）
GET /v1/area?worldId=0&x0=10&y0=20&x1=40&y1=50   # 框寻址（全局网格坐标，反向也接受）
```

```json
{"worldId":0,
 "bbox":{"x0":96,"y0":160,"x1":127,"y1":191,"w":32,"h":32},
 "chunk":{"cx":3,"cy":5,"w":32,"h":32},
 "cells":{"total":1024,"scanned":1024,"mineable":610,"unmineable":0,
          "vacuum":12,"gas":180,"liquid":0,"solid":832},
 "elements":[{"id":"SandStone","count":500,"massKg":123456.7},
             {"id":"Dirt","count":120,"massKg":23456.7},
             {"id":"other","count":792,"massKg":34567.8}],
 "elementKinds":13,
 "buildings":[{"prefabId":"Tile","count":40},{"prefabId":"WireRefined","count":7}],
 "buildingKinds":2,
 "source":"Grid.Element/Mass/IsSolidCell + Grid.Objects 全层（建筑按 instanceId 去重）",
 "note":"**逐格**统计（固/液/气/真空都算），与 /v1/env 口径不同（后者只算气体格且受迷雾门禁）⇒ 两边数字本来就不该相等。`mineable` = 实心且非中子质/非 Unobtanium；`unmineable` = 实心但挖不动。元素最多列 10 个，其余合并 `other`；建筑最多列 20 种。"}
```

* 上限：`maxCells` 默认 **4096**、最大 65536；超了 `400 AREA_TOO_LARGE`（带实际格数）；
* `bbox` 是**这次实际覆盖**的范围（块寻址时 = 该块的全局 bbox）；
* 建筑按 **`instanceId` 去重**（多格建筑只算一次，不会因为占 4 格就数 4 栋）。

### 3.33 `POST /v1/job {"type":"dig", …}` —— 按块 / 按框挖（M6.67）

```http
POST /v1/job {"type":"dig","worldId":0,"cx":3,"cy":5,"confirm":true}
POST /v1/job {"type":"dig","worldId":0,"x0":10,"y0":20,"x1":40,"y1":50,"confirm":true}
```

```json
{"type":"dig",
 "requested":{"x0":96,"y0":160,"x1":127,"y1":191,"cells":1024,"worldId":0},
 "applied":{"count":610,"bounds":{"x0":..,"y0":..,"x1":..,"y1":..},"isContiguous":false},
 "alreadyMarked":0,
 "marked":610,"skipped":414,"cellsScanned":1024,
 "chunk":{"cx":3,"cy":5,"w":32,"h":32,"chunkSize":32},
 "rejected":{"count":414,"byReason":{"OUT_OF_WORLD":0,"HAS_BUILDING":2,"NOT_DIGGABLE":412,"READBACK_FAILED":0}},
 "accepted":true,"jobState":"marked", …}
```

**护栏（两条都必须）**
1. **`confirm:true` 强制**：区域/块挖不给 ⇒ `400 CONFIRM_REQUIRED`（message 里带本次格数）。
   ⚠️ **这是行为变更**：以前的 `dig` + `x0..y1` 不需要 confirm，现在需要（防手滑；单格 `dig` 不受影响）。
2. **限流 5000 格**：超过 ⇒ `400 REGION_TOO_LARGE`（带实际格数），让调用方自己切块 —— 一个 32×32 块只有 1024 格，正常不会触顶。

**字段对照（新旧并存，旧字段不删）**

| 新字段（本批约定） | 旧字段 | 含义 |
|---|---|---|
| `marked` | `applied.count` | 本次新标记的格数 |
| `skipped` | `rejected.count` | 被拒的格数（按 `byReason` 分类） |
| `cellsScanned` | `requested.cells` | 扫描格数 |
| `chunk` | — | 块寻址时回显 `cx/cy/w/h/chunkSize`（框寻址为 `null`） |

**硬等式**（每轮日志自检）：`applied + alreadyMarked + rejected == cells`。

### 3.34 「某格能不能挖」的**唯一判据**（M6.68，`CellRules.IsDiggable`）

> 起因：同一块（w0 的 `cx=0,cy=0`）`/v1/area` 报 `mineable=144`，而 `/v1/job dig` 实际 `marked=419` —— **两个端点口径矛盾**。
> 根因：`Area` 自己写了一套更严的判据（把 `Unobtanium` 也算"不可挖"），而 `dig` 走的是游戏判定。

**判据（单点定义在 `src/OniAiBridge/CellRules.cs`，`Area` 与 `dig` 都调它）**

| 条 | 条件 | 不满足时的 `verdict` |
|---|---|---|
| 1 | `Grid.IsValidCell(cell)` | `INVALID_CELL` |
| 2 | 实心：`Grid.IsSolidCell(cell)`，兜底 `Grid.Solid[cell]` | `NOT_SOLID` |
| 3 | 该格**没有建筑**（`Grid.Objects[cell, ObjectLayer.Building]` 为空 —— 建筑该拆不该挖） | `HAS_BUILDING` |
| 4 | 该格**还没有挖标记**（`ObjectLayer.DigPlacer` 为空） | `ALREADY_MARKED` |

**⚠️ 中子质（`Neutronium`）不在判据里拦**：实测 `dig` 的区域路径只按「实心 + 无建筑」预判，
**中子质能否挖由游戏 `DigTool.PlaceDig` 最终裁定**（返回 `null` 即不可挖）。所以：
* `Area.mineable` 是**上界**（满足上面 4 条的格数）；
* `dig.marked` 是**实际**（`PlaceDig` 真的成功）；
* 两者允许有差，差额 = 游戏在预判之外又拒绝的格，dig 会记进 `rejected.byReason.NOT_DIGGABLE`。
* `Area.cells.neutronium` 是**信息字段**（实心里的中子质格数），**不参与判定**。

`Area.cells` 现在有：`total / scanned / mineable / unmineable / alreadyDigMarked / neutronium / vacuum / gas / liquid / solid`，
其中 `unmineable` 目前**只表示"实心但有建筑挡着"**（判据 3）。

### 3.30.1 世界表补充：`isStub`（占位世界）

实测该档 `count=15`，其中 **7 个是 `32×32`、`name=''` 的占位世界**（星际目标/飞船留下的空壳，不是真小行星）。

* 新增字段 **`isStub`**，判据写死：**`name` 为空 且 `width<=32` 且 `height<=32`**；
* `dupeCount` 明确标 **未实现**：字段恒 `0`，并额外给 **`dupeCountUnavailable:true`**（下一批次接入真实统计）。

### 3.33.1 `dig` 响应补充

* `applied.bounds` **不再出现空串**：有标记时给 `{x0,y0,x1,y1}`，`applied=0` 时给 `bounds:null`；
* `marked` / `skipped` / `cellsScanned` / `chunk` 与旧字段 `applied` / `rejected` / `requested` **并存**（旧字段不删）。

### 3.34.1 「已标记」的判据也单点化（M6.69）—— 与 `/v1/marks` 同源

**实测现象（0.89.1）**：同一块 `dig` 报 `marked=419`，紧接着 `/v1/area` 只认 `alreadyDigMarked=180`，
`mineable` 从 419 掉到 239（等式仍成立：239+6+180=425）。

**根因（已改）**：`Area` 判"已标记"用的是 `Grid.Objects[cell, ObjectLayer.DigPlacer] != null` —— **太窄**。
标记物的落地方式与"层索引"并非一一对应（`PlaceDig` 可能在同格叠放/换层/延后注册）。

**修法**：`CellRules.IsMarkedForDig(cell)` 改成**全层搜 `Diggable` 组件**（实现直接复用 `JobQuery.FindDiggableAt`），
**与 `/v1/marks` 的 `dig.marked` 完全同一个实现**。`Area` 与 `dig` 的 `alreadyMarked` 都调它。

> ✅ **已实机复核（0.89.2，Cycle 6990「外世」档，w0 cx=0 cy=0）**：
> `dig` ⇒ `marked=419 / markedStillPresent=419 / **markedVanished=0**`；紧接着 `area` ⇒ `alreadyDigMarked=418`。
> ⇒ 判定：**419 是真的**，"之前 180"是 `Area` 判据窄（本版已修）；**不是游戏自我清理**（可能 1 排除）、**不是计数虚高**（可能 3 排除）。
>
> ⚠️ **两个数允许差 ≤ 建筑占用格数，不是 bug**：`dig.marked` 记的是"提交并成功打上标记"的格，
> `area.alreadyDigMarked` 只统计**实心且无建筑**的格 —— 若某格恰好有建筑又被标过，`area` 会把它归到 `unmineable` 而不计入 `alreadyDigMarked`。
> 实测该档差 **1 格**（419 vs 418，块内 6 格有建筑）。**量级 1/1024，不为此改代码**；
> 判据：**差 ≤ `cells.unmineable`（即建筑占用格数）就算一致**，超过才需要追。

### 3.33.2 `dig` 新增**请求末尾复核**（M6.69）

`dig` 现在在同一个请求内、扫描结束后**立刻重数一遍** bbox 内的挖标记，并报：

| 字段 | 含义 |
|---|---|
| `markedStillPresent` | 请求结束时 bbox 内**实际还在**的挖标记数（`-1` = 复核失败） |
| `markedVanished` | `marked - markedStillPresent`，即"报了但已不在"的数量（`null` = 复核失败） |

⇒ **不用再隔几秒外部抽查**：如果 `markedVanished` 很大，说明 `PlaceDig` 返回非 null 但标记随后被撤
（可能是游戏对不可挖格的自我清理）；如果它接近 0，说明 419 个标记是真的，之前 area 的 180 就是判据窄。

**代价**：每次 `dig` 多扫一遍 bbox（32×32 再走 1024 格，`FindDiggableAt` 逐层）——实测一次 dig 183ms，
这一遍预计再增加几十毫秒；若你嫌慢，可以加参数关掉（现在没有开关）。

### 3.32.1 `cells.neutronium` 修正（M6.69）

0.89.1 里 `cells.neutronium` **恒为 0** —— 因为它只比了 `SimHashes.Neutronium`，
而游戏里那片"不可破坏"的边界元素实际叫 **`Unobtanium`**（中文"中子质"）。
0.89.2 起 **两个名字都认**（`Unobtanium` 或 `Neutronium`）。
> 字段仍然**只作信息**、不参与可挖判定（能不能挖最终由 `DigTool.PlaceDig` 裁定）。
> 装机后该档 `cx=0,cy=0` 的 `neutronium` 应≈**281**（与元素统计里的 `Unobtanium×281` 对上）。

### 3.35 `GET /v1/dupes`（只读，M6.70）—— 复制人清单 + 寻路 + 疑似卡住

```http
GET /v1/dupes            # 全部
GET /v1/dupes?idle=1     # 只要空闲的（chore.id == "Idle"，大小写不敏感）
```

每人字段：`name / dbId / kind / gender / cell / x / y / worldId / chore{text,id} / amounts / amountMeta / path / stuckProbe`。

#### 3.35.1 ⚠️ **两种身体**（实测 0.89.2「外世」Cycle 6990，24 人）

| | `kind:"organic"`（18 人） | `kind:"bionic"`（6 人） |
|---|---|---|
| 有 | `Calories, CaloriesKcal, Bladder, Stamina, …` | `BionicGunk, BionicInternalBattery, BionicOil, BionicOxygenTank, …` |
| 没有 | — | **没有** `Calories / CaloriesKcal / Bladder / Stamina` |

* **`kind` 的判据（写死）**：**有没有 `BionicOil` 这个 amount** —— 有 ⇒ `bionic`，无 ⇒ `organic`。
  （实测该档 6 个仿生人 / 18 个普通小人，与游戏内一致；`埃德威铎/奥铽/斯镝拉/索铌亚/吉滋镆/器普` 六个都命中。）
* **`amounts` 只输出该小人实际存在的键**：不存在的键**不出现**，**绝不补 0**。
  （0.89.x 的 `/v1/state` 把缺失的 `Calories` 填 0，会把仿生人报成"快饿死" —— 这是本版要避免的误报。）
* **`amountMeta[id] = {min,max,units}`**：`units` 是 `Amount.units` 的枚举名，`min/max` 来自
  `AmountInstance.GetMin()/GetMax()`（public 方法）。**绝对值和百分比靠它互算**。

#### 3.35.2 `path` —— 只给首尾两格

```json
"path":{"hasPath":true,"pathLen":23,"firstCell":133699,"targetCell":131072,
        "targetX":200,"targetY":206,"targetWorldId":0,"targetPrefabId":"LadderFast"}
```
* 数据源：`MinionIdentity.GetComponent<Navigator>().path`；
* ⚠️ **真实类型是 `PathFinder.Path`（struct，不是 class）** —— 不能与 `null` 比较；空路径表现为 `nodes == null / Count == 0`；
* `path.nodes : List<Node>`，`Node.cell` 是格号；**只取 `nodes[0]` 与 `nodes[Count-1]`**，不返回整条路径；
* `targetPrefabId` = 目标格上的建筑 prefabId（没有就 `null`）。

#### 3.35.3 `stuckProbe` —— **只是候选，不是结论**

| 字段 | 含义 |
|---|---|
| `possible` | 三条**同时**满足才 true |
| `stillSeconds` | `cell` 与 `pathLen` 都不变的持续秒数 |
| `choreIsMovement` | `chore.id` 是否命中移动类白名单 |
| `windowSeconds` | 阈值（当前 **5.0 秒**） |
| `reason` | 一句人话（哪种条件没满足） |

**判据（写死，单点定义在 `AlertRules`）**：
1. `chore.choreType.Id` **以 `MoveTo` 开头**（白名单前缀，当前只有这一个）；
2. `path.hasPath == true`；
3. 连续 **5 秒**（`AlertRules.StuckWindowSeconds`）`pathLen` 没有减少**且** `cell` 没有变。

> ⚠️ **`possible:true` 不是结论**：等开门、排队、就地工作、被传送、被其它人挡住都可能命中。
> 它只是"值得看一眼"的候选。**第一次查询只建立基线**，同一进程内第二次起才可能为 true。

### 3.36 `GET /v1/alerts`（只读，M6.70）—— 按严重度排序

```json
{"count":7,"dupesScanned":24,"buildingsScanned":72851,"bionicDupes":6,
 "critical":1,"warning":5,"info":1,
 "alerts":[{"severity":"critical","kind":"dupe_breath","subjectKind":"dupe",
            "subject":{"dupeName":"奥铽","prefabId":null,"x":139,"y":210,"worldId":0},
            "value":6.2,"threshold":10,"unit":"%","hint":"马上安排呼吸/供氧，或把人撤出缺氧区"}],
 "thresholds":{...},"note":"…"}
```

#### 3.36.1 阈值表（**两套**，全部写死在这里，代码在 `AlertRules`）

| kind | 判据 | warning | critical | unit |
|---|---|---|---|---|
| `dupe_breath` | `Breath` 低 | `< 30` | `< 10` | `%` |
| `dupe_calories` | `Calories` 低（**仅 organic**） | `< 1500` | `< 500` | `kcal` |
| `dupe_stress` | `Stress` 高 | `> 80` | `> 95` | `%` |
| `dupe_bladder` | `Bladder` 高（**仅 organic**） | `> 95` | — | `%` |
| `dupe_bionic_reserve` | **仅 bionic**：`BionicInternalBattery / BionicOil / BionicOxygenTank` 的 **`value/max`** | `< 20%` | `< 10%` | `Amount.units` |
| `dupe_disease` | 任一疾病 amount `> 0`（两套通用） | `> 0` | — | `%` |
| `building_broken` | `Operational.IsFunctional == false` | ✓ | — | — |
| `building_stopped` | `IsFunctional==true && IsOperational==false` | — | —（`info`） | — |

**仿生人为什么用比例**：`BionicInternalBattery` 实测 **566,566 ~ 624,104**，单位未确认（像焦耳或内部刻度，不是百分比）
⇒ 判据一律 `value / GetMax()`（`AmountInstance.GetMax()` 运行时读），**与单位无关**。
响应里同时给 `value`、`threshold`（= 比例 × max）、`unit`（`Amount.units`）；
**若 `max` 读不到（0/NaN）⇒ 该条标 `thresholdUnavailable:true`，只给 `value`，不给阈值**（不拿拍脑袋的数字报警）。

#### 3.36.2 本版**不提供**的告警（如实说明，不编）

* **`building_no_material`**：缺料要"建造 chore + 配方比对"，没有可靠单一判据 ⇒ **整类省略**；
* **`dupe_stuck_candidate`**：目前只在 `/v1/dupes` 的 `stuckProbe` 里（可靠性低，不适合进告警流）；
  需要它进 `/v1/alerts` 时说一声，我按 `info` 加。

#### 3.36.3 每条告警的形状

```
{ severity, kind, subjectKind("dupe"|"building"),
  subject{ dupeName, prefabId, x, y, worldId },
  value, threshold, unit, hint,
  thresholdUnavailable?   // 只在拿不到阈值时出现
}
```
排序：`critical` → `warning` → `info`；顶部给三类计数 + `dupesScanned` / `buildingsScanned` / `bionicDupes`。
**`value` 与 `threshold` 一定同时给**（除非 `thresholdUnavailable`），让调用方能看出差多少。

### 3.36.4 建筑类告警**聚合**（M6.71，0.90.1）

实测（0.90.0，Cycle 6990）：`building_stopped` 在**刚进档时 2447 条**、跑 15 秒后 **1152 条**，响应体 559 KB / 280 KB ——
其中绝大多数是**被动件**（`SolidConduitBridge ×480`、`LiquidConduitBridge ×321`、`SolidConduitInbox ×224`、
`SolidTransferArm ×197`、`GasConduitBridge ×169`、`CeilingLight ×94`）。一栋一条会把 22 条真 `broken` 淹掉。

**改法**
* **建筑类按 `(kind, prefabId, worldId)` 聚合**：一条给 `prefabId` / `worldId` / `count` / `sample`（最多 **3** 个坐标）；
* **复制人类仍一 dupe 一条**（人少，不聚合）；
* 顶部 **`counts`** 同时给两个数：
  ```
  "counts":{"alerts":<聚合后条数>,"occurrences":<聚合前原始条数>,
            "dupeAlerts":…,"buildingGroups":…,"buildingOccurrences":…}
  ```
  `critical/warning/info` 三个计数 = **聚合后**各严重度的条数。

**聚合条目的形状**
```json
{"severity":"info","kind":"building_stopped","subjectKind":"building",
 "subject":{"dupeName":null,"prefabId":"SolidConduitBridge","worldId":0},
 "prefabId":"SolidConduitBridge","worldId":0,"count":480,
 "sample":[{"x":172,"y":302},{"x":168,"y":300},{"x":160,"y":298}],
 "value":null,"threshold":null,"unit":null,
 "hint":"检查供电/输入/条件；停工不一定是故障"}
```

### 3.36.5 `?detail=1` 逃生口 + **200 KB 硬上限**

* **默认不返回逐条明细**；`GET /v1/alerts?detail=1` 才返回**未聚合**的逐条明细
  （此时**不再**输出聚合条目，避免同一现象出现两次；`counts` 仍然给，用于对账）。
* **硬上限 200 KB**（`MaxBytes = 200*1024`，按 `string.Length` 计）：聚合后仍超限 ⇒
  **只返回计数、不返回明细数组**，并明确标注：
  ```json
  "alerts": null, "truncated": true,
  "truncatedReason": "聚合后响应体仍超过 200KB 上限（实际 N 字节）⇒ 本次只返回计数，不返回明细数组；用 ?detail=1 取明细（同样受上限约束）"
  ```
  **绝不静默截断**。
* 尾部固定字段（`thresholds` / `source` / `note`）走同一个 `AppendTail`，全量与截断两条路径一致，不会各写一套。

### 3.36.6 ⚠️ 0.90.1 事故：`/v1/alerts` 返回半截 JSON（M6.72，0.90.2 修复）

**症状**：`GET /v1/alerts` 与 `?detail=1` 都是 **HTTP=000 SIZE=0**（连接被直接关闭），而 `/v1/meta`、`/v1/dupes` 正常。
`Player.log`：
```
[OniAiBridge] [alerts] 生成的 JSON 非法（已拒绝下发）:
  Unexpected end of content while loading JObject. Path 'info', line 1, position 306. | 长度=306
  前 240 字符: {"dupesScanned":24,...,"counts":{"alerts":304,"occurrences":2458,"dupeAlerts":0,"buildingGroups":3…
```

**根因（一行残留代码）**：0.90.1 重写输出段时，**旧代码的 `data = sb.ToString();` 没删干净**，
它排在 `data = text;` **之后**执行 ⇒ 把已经拼好的完整 JSON（`sb` + `alerts` 数组 + 尾段）**覆盖成只剩前缀 `sb`**。
`sb` 正好是 `{"dupesScanned":…,"counts":{…}` 这一截，长度 ~306 字节，且**末尾没有闭合 `}`** ⇒
`JsonCheck.Validate` 正确拒绝下发（**JsonCheck 的表现完全正确，未改动**）。

**修复（0.90.2，只改 `Alerts.cs`）**
1. **删掉残留的 `data = sb.ToString();` 与紧随其后的重复 `Log.Info`**（这就是截断点）；
2. **catch 打全异常**：`[alerts] 采集异常（完整栈前 500 字）: …` —— 以后不用靠猜；
3. **下发前廉价护栏**：`data[0] != '{' || data[^1] != '}'` ⇒ 直接 `500 INTERNAL` 并说明长度
   （这类"半截 JSON"最典型的特征就是首尾不配对；它不替代 `JsonCheck`，只是提前一层）。

> 教训：**"重写一段输出"时必须确认旧的赋值语句被删掉** —— 编译不会报错（`data` 被赋两次，后者胜），
> 只有运行时才暴露，而且表现是"合法 JSON 变成半截"。

### 3.36.7 建筑**三态**判定（M6.73，0.91.0）—— 0.90.2 的 `building_broken` 是误报

**实测证据（0.90.2，Cycle 6990）**：14 组 `building_broken` 里**没有一个是真坏**：
```json
{"prefabId":"FarmStation","x":157,"y":215,"operational":false,"enabled":false}      ← 玩家手动关的
{"prefabId":"SpiceGrinder","x":125,"y":225,"operational":false,"enabled":true}     ← 空闲（没料/没工人）
```
涉及：`FarmStation / RanchStation / SpiceGrinder / MilkFeeder / SweepBotStation / RailGunPayloadOpener /
WarpConduitReceiver / SolidConduitInbox / HabitatModuleMedium / RocketInteriorGasOutputPort / SuitMarker`。

**根因**：0.90.2 的 `building_broken` 判据本质是「**没在运行**」（`!Operational.IsFunctional`），
和 `building_stopped` 同一套东西，只是严重度标高了。

**三态判据（唯一出处：`Alerts.cs` 里的建筑分支）**

| kind | 判据 | severity | 说明 |
|---|---|---|---|
| `building_broken` | **`BuildingHP.HitPoints <= 0`** | `warning` | 血量归零 = 已损坏待修；聚合条目附带 `hp` / `maxHp` 便于核实 |
| `building_disabled` | **`BuildingEnabledButton.IsEnabled == false`** | `info` | 玩家用建筑开关关掉了 ⇒ **配置，不是故障** |
| `building_stopped` | 其余（`!operational && enabled && hp>0`） | `info` | 缺料/缺工/没通电，不升级成 warning |

* **`Operational` 上并没有 `IsBroken` 属性**（成员表核对：只有 `IsFunctional` / `IsOperational` / `IsActive`），
  所以"坏"用血量判定 —— **不再用"没在运行"冒充损坏**；
* `BuildingHP` 组件不存在的建筑（如纯装饰/标记类）**永远不会被判 broken**；
* 建筑类依旧按 `(kind, prefabId, worldId)` 聚合，`sample` ≤3 个坐标。

### 3.30.2 `/v1/worlds` 的可读名与 `isActive`（M6.73，0.91.0）

**`displayName` 的来历**（按优先级）：
1. `WorldContainer.overrideName`（玩家改过的名字）；
2. **`Strings.Get(WorldContainer.worldType).String`** —— `worldType` 本身就是 `STRINGS.WORLDS.<X>.NAME` 这种完整 key
   （`/v1/state` 的 `colony.world.typeKey` 就是它的副本）；`Strings.Get` 返回 `StringEntry`，取 `.String`；
   若 key 不带 `.NAME` 后缀会补一次；
3. 都拿不到 ⇒ **`null`**（**绝不拿内部名 `expansion1::worlds/…` 冒充**）。

新增 **`typeKey`** 原样输出，方便调用方自行查表。

**`isActive` 的判据**（0.90.2 全 false 的原因见下）：
实测 0.90.2 现场：`ClusterManager.activeWorldId = 7`，而玩家在 **w0** ⇒ 用它判定 **不可信**。
0.91.0 改成 **以"摄像机所在格的世界"为准**（`Camera.main` → `Grid.PosToCell` → `Grid.WorldIdx`，即 `cameraWorldId`），
取不到才退回 `activeWorld.id` / `activeWorldId`。顶部同时给三个值便于对账：
`activeWorldId` / `activeWorldIdFromContainer` / `cameraWorldId`。

### 3.30.3 ⚠️ `isActive` 更正（M6.74，0.91.1）：**`activeWorldId` 是对的，错的是"拿它跟什么比"**

**先撤回 0.91.0 的前提**：我曾写"`activeWorldId = 7` 而玩家在 w0 ⇒ 该值不可信" —— **这是错的**。
小僵当时在看「**筛土星**」= `MiniRegolithMoonlet` = **w7**（`/v1/state` 的 `colony.worldId = 7`、
`world.name = expansion1::worlds/MiniRegolithMoonlet`、`typeKey = STRINGS.WORLDS.REGOLITHMOONLET.NAME` 三处一致），
⇒ **`ClusterManager.Instance.activeWorldId` 完全正确**。摄像机方案（`Grid.WorldIdx` 可能给 255）**已撤销，不参与判定**。

**真正的 bug：比较对象选错了**
`WorldContainer.id` 与 `Grid.WorldIdx` 给出的 worldId **不是同一套编号**（0.90.x 直接拿 `wc.id` 去比 `activeWorldId` ⇒ 全 false）。

**0.91.1 的判据**
```
gridWorldId = Grid.WorldIdx[ Grid.XYToCell(offsetX + width/2, offsetY + height/2) ]   // 该世界中心格的真实编号
isActive    = (gridWorldId == activeWorldId)        // gridWorldId < 0 时退回 (WorldContainer.id == activeWorldId)
```
响应新增 **`gridWorldId`**（每个世界一条），顶部保留 `activeWorldId`，`cameraWorldId` 仅作附加信息。

**装机后判据**：w7（`MiniRegolithMoonlet`）应 `isActive:true`，其余 false；若 w7 的 `gridWorldId != 7`，
把该条发我 —— 说明中心格落在虚空/边界，我改用"世界内第一个有效格"取样。

### 3.37 `POST /v1/job` 的**打标记**批次（M6.76，0.92.0）

五个新 `type`（`dig` / `build` 不变）：

| type | 入口 | 单格 / 矩形 |
|---|---|---|
| `disinfect` | `Disinfectable.MarkForDisinfect(bool)` | 单格（逐格取建筑） |
| `empty_pipe` | `EmptyConduitWorkable.MarkForEmptying()`（有 `IEmptyConduitWorkable` 接口时走接口） | 单格 |
| `attack` | `AttackTool.MarkForAttack(Vector2, Vector2, bool)` | **矩形**（也接受单格，等价 1×1） |
| `capture` | `Capturable.MarkForCapture(bool)` | **矩形**（也接受单格） |
| `mop` | `MopTool.OnDragTool(cell, 0)`（反射调用，见下） | 单格（逐格液体） |

#### 3.37.1 请求形状
```json
POST /v1/job
{"type":"disinfect","x":157,"y":215,"worldId":0,"priority":5,"dryRun":true}
{"type":"attack","x0":100,"y0":200,"x1":120,"y1":220,"worldId":0,"dryRun":true}
{"type":"disinfect","x":157,"y":215,"confirm":true}
```
* **`dryRun:true`**：只解析、只报"会标记到谁"，**一个字节都不改**；**不需要** `confirm`；
* **`confirm:true`** 才真做（与 `build` 一致）；两个都不给 ⇒ `400 CONFIRM_REQUIRED`；
* `priority`（可选 1..9）：对每个目标的 `Prioritizable.SetMasterPriority` 生效；缺省不动。

#### 3.37.2 **dryRun 与真做共用同一段解析**（硬要求）
`JobMarks.Resolve()`（`src/OniAiBridge/JobMarks.cs:271`）是**唯一**的目标解析入口：
先解析成 `targets[]`，`dryRun` 就在此返回；否则遍历 `targets` 逐个调 `Mark*`。
⇒ `dryRun` 看到的就是真会发生的（列在 `list[]`，含 `prefabId` / `cell` / `x` / `y` / `worldId` / `alreadyMarked` / `extra`）。

#### 3.37.3 各 type 的解析判据（行号 = `JobMarks.cs`）
| type | 解析位置 | 判据 |
|---|---|---|
| `disinfect` | L326 | 逐格扫全层 `Grid.Objects[cell, layer]`，取 `Disinfectable`；`extra.diseaseCount` = `PrimaryElement.DiseaseCount`；`alreadyMarked` = 反射读 `isMarkedForDisinfect`（nonpub） |
| `empty_pipe` | L344 | 逐格扫全层，取 `EmptyConduitWorkable`；**幂等回读不可用**（无公开标记位）⇒ 该目标带 `extra.alreadyMarkedUnavailable=1`，`alreadyMarked` 一律 false |
| `capture` | L360 | 逐格扫全层，取 `Capturable`；`alreadyMarked` = `Capturable.IsMarkedForCapture`（public） |
| `attack` | L377 | 逐格扫全层，取 `AttackableBase`（`FactionAlignment.attackable` 是 nonpub，故用组件存在性判）；`extra.hp` = `Health.hitPoints` |
| `mop` | L288 | 逐格判 `Grid.Element[cell].IsLiquid && Grid.Mass[cell] > 0` + `Grid.Solid[cell 下方]` + `mass <= MopTool.maxMopAmt`；**0.93.8 起** `alreadyMarked` = `HasMopChore(cell,worldId)` **或** `Grid.Objects[cell, ObjectLayer.MopPlacer] != null`（两者任一即已标记，见 3.37.13）；`extra.liquidMassKg` |

#### 3.37.4 执行细节（真做路径）
* **attack**：不逐目标调 —— 用解析出的目标包围盒调 **`AttackTool.MarkForAttack(v0, v1, false)`**（游戏自己的范围实现，`JobMarks.cs:408`）；
* **capture**：**逐目标**调 `Capturable.MarkForCapture(true)`（见下"为什么不用 `CaptureTool`"）；
  `marked` = **尝试数**；另有 `captureMarkedCount` = **游戏侧回读**到的已标记数（`Capturable.IsMarkedForCapture`，
  它本身就是 `return markedForCapture;` ⇒ **同帧可靠**，不用像 attack 那样等下一帧）。两者不等 ⇒ 游戏没真标记
  （`Capturable.MarkForCapture` 的 IL 第一个判断就是 `IsCapturable()`，为假时**静默什么都不做**）。
  回读抛异常 ⇒ `captureMarkedCount: -1` + `captureMarkedUnavailable`（**不拿 0 冒充**）；
* **mop**：`MopTool.OnDragTool(cell, 0)` 用**反射**调用（`JobMarks.cs:432`）——
  元数据标 `public`，但编译器报 **CS0122 不可访问**，只能反射；
  **本版不调用 `Activate/Deactivate`**（没激活就没有状态要复原）；若实测发现未激活时不生效，再补"激活 + 复原"；
* **priority**：`PriorityClass` 编译期不可按名引用 ⇒ 反射构造 `PrioritySetting`（`TryMakePriority`，与 `PlanScreenFix` 同手法）；
* **每个 Mark 单独 try/catch**：失败不中断整批，`failed[]` 里给 `cell` / `prefabId` / `error`；
* **不跨世界误伤**：给了 `worldId` 就只解析 `Grid.WorldIdx[cell] == worldId` 的格；`x,y` **一律经 `Grid.XYToCell` 换算**，不直接当 `cell`。

#### 3.37.4b `capture` 为什么不用 `CaptureTool.MarkForCapture`（0.93.6 定案）

`CaptureTool.MarkForCapture(Vector2 min, Vector2 max, bool mark)` **确实存在**（public static，签名已用反射核实），
也确实是"两个世界坐标的矩形"接口。但**本 mod 不用它**，理由来自 IL（`Assembly-CSharp.dll`，已逐条核过）：

```
CaptureTool.MarkForCapture 的实现：
  foreach (Capturable in Cmps<Capturable>.Items)          ← 遍历全局组件表
      pos = transform.position; Grid.PosToXY(pos)          ← 换算成格子
      if (不在矩形内) continue
      Capturable.MarkForCapture(mark, PrioritySetting, bool)   ← 调的**就是**本 mod 直接调的那个方法
          PrioritySetting = ToolMenu.PriorityScreen.GetLastSelectedPriority()   ← 取【界面】上的优先级
      再 PopFXManager.SpawnFX("fx_capture_splash", ...)     ← 弹一个飘字
```

* 它**没有**额外的游戏逻辑：过滤矩形之后调的仍是 `Capturable.MarkForCapture`；
* 它多出两样**副作用**：① 优先级来自**界面上次选中的值**（HTTP 调用方无法控制，也不会知道是什么），
  而本 mod 的 `priority` 是请求参数里显式给的；② 弹 UI 飘字（服务端调用不需要）。
* `Capturable.MarkForCapture(bool)` 单参重载的 IL 是：构造 `new PrioritySetting(...)` → 调三参重载 ⇒ 同一条游戏路径。

⇒ **矩形语义由 `JobMarks.Resolve()` 的逐格解析提供，执行仍落在组件方法上**，格式与 `attack` 完全一致（`x0/y0/x1/y1`）。

> ⚠️ **已知差异**（不影响主要用途）：`CaptureTool` 扫的是**全局组件表**，因此装在容器/箱子里
> （不挂在格子对象层）的 `Capturable` 它也会碰到；本 mod 走**逐格**解析，这类不可见的不会被标记 ——
> 这与其它 type 一致的"按格/矩形"模型，且"捕捉范围"按格子理解才是调用方要的语义。

#### 3.37.5 错误码
| code | 何时 |
|---|---|
| `400 CONFIRM_REQUIRED` | 既没 `dryRun` 也没 `confirm` |
| `400 NO_TARGET` | 解析完 `targets` 为空（这格/这范围没有能标记的东西） |
| `400 RECT_NOT_SUPPORTED` | 给 `disinfect/empty_pipe/mop` 传了**非 1×1** 的矩形（这三类只做单格，范围由调用方循环） |
| `400 BAD_REQUEST` | 坐标/priority 参数不合法 |
| `503 RETRY_LATER` | 主线程忙 |

> **没有 `409 AMBIGUOUS_TARGET`**：与 `deconstruct` 不同，这五类的目标是**多对象并列**（一颗格子上可能既有建筑又有动物），
> 逐个标记是正确行为，不存在"必须二选一"的歧义 —— 所以不设该错误码，`list[]` 里全部列出。

#### 3.37.6 真机验证判据（0.92.0 **未装机**，以下是给装机后用的）
| type | dryRun 应该看到 | 真做后应该看到 |
|---|---|---|
| `disinfect` | `list[].extra` 含 `diseaseCount=<n>`，`targets ≥ 1` | `/v1/marks?x&y` 的 `disinfect` 段出现标记；再 dryRun 同格 ⇒ `alreadyMarked:true` |
| `empty_pipe` | `list[].kind="EmptyConduitWorkable"`，`prefabId` 是管道类 | 该建筑出现"清空管道"chore（`/v1/dupes` 里能看到有人去做）；`/v1/marks` **无**对应段（本版无回读） |
| `attack` | `list[]` 有 `prefabId`（如 `Hatch`）+ `extra.hp=<n>`，**先看这个再确认** | 小动物 hp 下降 / 出现攻击 chore（**会真的伤害**） |
| `capture` | `list[]` 有 `kind="Capturable"` | `captureMarkedCount` = `marked`（且再 dryRun ⇒ `alreadyMarked:true`）；小动物被装入陷阱/箱。**若 `captureMarkedCount < marked`** ⇒ 游戏侧 `IsCapturable()` 为假，那些目标没真标记 |
| `mop` | `list[]` 每格有 `extra.liquidMassKg=<kg>` | `Grid.Objects[cell, ObjectLayer.MopPlacer]` 上出现对象（`/v1/marks` 的 clear 段可能也反映）；工具状态未变 |

### 3.37.7 ⚠️ 0.92.0 的三个实测问题与 0.92.1 的修法

**真机实测（0.92.0，Cycle 6990）**
| 现象 | 数据 |
|---|---|
| `disinfect` / `capture` / `empty_pipe` | ✅ 正常（`capture` 是本批唯一走通"做→回读"的：再 dryRun 同格 ⇒ `alreadyMarked:true`） |
| `mop` / `attack` **计数自相矛盾** | `{"targets":1,"marked":1,"failed":1,"failedDetails":[{"error":"…不应逐目标调用"}]}` |
| `mop` **实际没生效** | 真做后再 dryRun 同格：`alreadyMarked=0`、`liquidMassKg` 不变 |
| `attack` 无法判断是否标上 | 真打后动物 `hp` 不变（正常：攻击是挂任务，不是即时伤害），但当时**没有任何回读字段** |

**1. 计数矛盾（mop / attack）**
根因：这两类是"**一次工具调用完成全部**"，但 0.92.0 仍然进了逐目标循环，每个目标都抛
`InvalidOperationException("…不应逐目标调用")` ⇒ `marked` 与 `failed` 同时 +1。
**0.92.1**：`attack` / `mop` **整段跳过逐目标循环**，只调一次工具；`marked` = 工具调用后**回读确认**的数量
（`mop` 看 `MopPlacer` 层、`attack` 看 Attack chore），`failed` 只在工具调用真抛异常时 +1。

**2. `mop` 未生效**
根因：0.92.0 没调 `Activate`，`MopTool.OnDragTool` 在未激活状态下不落地。
**0.92.1**：`PlayerController.Instance.ActivateTool(MopTool.Instance)` → `OnDragTool(cell,0)` → **`finally` 反射调
`PlayerController.DeactivateTool(tool)` 复原**（该方法 nonpub）。
并且**每个格都回读** `Grid.Objects[cell, ObjectLayer.MopPlacer] != null`，只有真落地才计入 `marked`；
响应 `list[].mopPlacerPresent` 直接给这个布尔，`extra` 里也带 `mopPlacerPresent=0/1`。

**3. `attack` 的回读判据（新增）**
`list[].attackMarked`：该对象身上**是否已挂着 `Attack` chore**。
依据：chore 挂在对象自身的 **`ChoreProvider`** 组件上，`ChoreProvider.choreWorldMap : Dictionary<int, List<Chore>>`
是 **public 字段**；`Chore.choreType.Id` / `Chore.gameObject` 都是 public。
⇒ 判据 = 该对象的 `ChoreProvider` 在 `worldId` 桶里存在 `choreType.Id` 含 `"Attack"` 的 chore。
`ApplyAttack` 也在调用后统计"有几个目标出现了 Attack chore"，**回读为 0 时把原因写进 `failedDetails`**（而不是假装成功）。

### 3.37.8 0.92.1 → 0.92.2 的三处修正（M6.78）

#### ① `mop` 改走**预制体路径**（不再依赖工具状态）
**0.92.1 实测**：`ActivateTool` → `OnDragTool` → `DeactivateTool` **仍不落地**（`marked=0`，再 dryRun 同格 `mopPlacerPresent:false`）。
**0.92.2 做法**（与 `MopTool.OnDragTool` 的 IL 同一条路径）：
```
取预制体：反射读 MopTool.Instance.Placer（nonpub 实例字段）
          兜底 Assets.GetPrefab(new Tag(MopPlacerConfig.ID))
逐格：go = Util.KInstantiate(prefab, Grid.CellToPos(cell))
      Grid.Objects[cell, ObjectLayer.MopPlacer] = go
      go.SetActive(true)
      priority>0 ⇒ Prioritizable.SetMasterPriority
      回读 MopPlacerPresent(cell) —— **只有真落地才计入 marked**
```
**不再调用任何工具方法**（`Activate` / `OnDragTool` / `DeactivateTool` 全部移除），因此也无需复原工具状态。

#### ② `attack` 的包围盒**用世界坐标**（这是 21 只动物被误标的根因）
**0.92.1 的 bug**：`v0/v1` 直接用了**格子序号**（`x0/y0`、`x1/y1`），而 `AttackTool.MarkForAttack` 吃的是**世界坐标**
⇒ 一次 `1×1` 的调用（如 `(75,180)`）被解释成一个横跨上百格的世界坐标矩形 ⇒ 实测 **21 只动物**（`MoleDelicacy`/`Chameleon`/`Drecko`/`Squirrel`/`Bee`/`Staterpillar`/`HatchHard`）被标上。
**0.92.2 做法**：
```
c0 = Grid.XYToCell(rect.x0, rect.y0) ; c1 = Grid.XYToCell(rect.x1, rect.y1)
p0 = Grid.CellToPos(c0)              ; p1 = Grid.CellToPos(c1)      // ← 世界坐标
AttackTool.MarkForAttack(new Vector2(p0.x,p0.y), new Vector2(p1.x,p1.y), false)
```
响应新增 **`attackBBox`**：`{worldXY:{x0,y0,x1,y1}, cells:{x0,y0,x1,y1}, cell0, cell1}` —— 直接与请求的 `rect` 对照，
`1×1` 的请求应该看到 `worldXY` 的跨度约 1 格（1.0），而不是几百。

#### ③ `attack` 的两个字段与"静默无效果"
* 顶层 **`alreadyMarked`** 现在**按 `attackMarked` 统计**（同一口径），并额外给 **`attackMarkedCount`**：
  ```
  attackMarkedCount = 调用后回读到的、带 Attack chore 的目标数
  marked            = attackMarkedCount（调用了但没标上的不算成功）
  alreadyMarked     = 解析阶段本来就带 chore 的目标数
  ```
* **"调用了但什么都没发生" 必须进 `failedDetails`**：对每个 `未标记 → 仍未标记` 的目标追加一条
  `{"cell":…,"prefabId":…,"error":"MarkForAttack 已调用，但该对象仍无 Attack chore（可能不可攻击/不是敌对阵营）"}`，
  并把 `failed` 设成这类目标的数量（不再 `failed=0` 假装成功）。

#### 已知未验证（0.92.2 未装机）
* `mop` 预制体路径是否真的落地（`MopPlacerPresent` 回读会立刻告诉我们）；
* `attack` 修好后 `attackBBox` 是否 1×1、是否不再误标一片；
* 「`Glom` 真做无效果」的根因（不可攻击 / 非敌对阵营 / 目标位置不匹配）—— 现在会以 `failedDetails` 出现。

### 3.37.9 0.92.2 → 0.92.3：`mop` 回读判据换成 **Mop chore**（M6.79）

**0.92.2 实测**：
```json
真做 (86,180)：{"marked":1,"failed":0,"jobState":"marked"}      ← 时刻上 MopPlacerPresent = true
紧接 dryRun 同格：{"alreadyMarked":0,"mopPlacerPresent":false}   ← 一秒后 placer 没了
```
⇒ **`MopPlacer` 是工具拖拽期间的临时标记物，不是最终状态**（玩家松手即被消费，换成真正的"拖地工作"）。
拿它当回读判据，只能看到一瞬 —— **实现没错，判据错了**。

**0.92.3 改法（照 `attack` 那套抄）**
| 项 | 0.92.2 | 0.92.3 |
|---|---|---|
| `alreadyMarked` | `MopPlacerPresent(cell)` | **`HasMopChore(cell, worldId)`** |
| `marked` | `MopPlacerPresent` 为真 | **`HasMopChore` 为真**（与 `attack` 同口径） |
| 回读字段 | `mopPlacerPresent` | **`mopChoreFound`**（新增，dryRun 与真做都给） |
| `mopPlacerPresent` | 参与计数 | **只作临时观察**（`extra` 里注明 `_observationalOnly`，不参与 `marked`/`alreadyMarked`） |

**`HasMopChore` 判据**（`JobMarks.cs`）：
逐层扫 `Grid.Objects[cell, layer]` → 取对象上的 **`ChoreProvider`** →
在 `choreWorldMap[worldId]` 里找 **choreType 就是 Mop** 的 chore（判据见 3.37.13）。
与 `HasAttackChore` 同一手法（`choreWorldMap` 是 public 字段）。

> ⚠️ 上面这一整节是 **0.92.3 的历史**。其中"`alreadyMarked` 只看 chore"和"按 `choreType.Id` 含 `"Mop"` 匹配"
> 都已被 **0.93.8** 取代 —— 见下面 3.37.13。
> （另：0.92.3 那条"没出现 Mop chore ⇒ 记 `failed`"早在 M6.83 就改掉了：真做只按 `MopPlacerPresent` 判成败，
> `mopChoreFound` 只作观察。`【实测 2026-11-17】` 真标一格 = `marked=1 failed=0 mopChoreFound=false`。）

### 3.37.13 0.93.8：`mop` 的 `alreadyMarked` 补回 `MopPlacer` + 判据换成真实 choreType

#### ① 症状（0.93.6，实测 cell=152161）
真标记一格（`marked=1`，`mopPlacerPresent` 已 `true`），紧接着再 `dryRun` 同格：
`alreadyMarked:0`、`wouldMark:1`、`extra` 里却写着 `mopPlacerPresent=1`。⇒ 调用方按 `alreadyMarked` 做幂等会**重复下发**。

根因是**两处**，缺一不可修：

| # | 位置 | 问题 |
|---|---|---|
| a | `Resolve()` 的 mop 分支 | `AlreadyMarked = hasChore` —— 只认 chore，漏了 `MopPlacer` 标记物。而拖地这件事是两阶段的：**标记物先落层，小人接活后才出现 chore** |
| b | `Execute()` 的顶层聚合 | `already` 只在"非工具类型"的逐目标循环里 `already++`；`attack`/`mop` 是"一次工具调用完成全部"**不进那个循环** ⇒ 工具型两条路径的顶层 `alreadyMarked` **恒为 0**（`wouldMark` 因此恒等于 `targets`） |

#### ② 0.93.8 的判据
```json
"alreadyMarked": 1, "wouldMark": 0,
"list":[{"alreadyMarked":true,"alreadyReason":"该格已有 MopPlacer 标记",
         "extra":"…; mopChoreFound=0; mopPlacerPresent=1"}]
```
* 逐目标 `alreadyMarked` = **`HasMopChore(cell,worldId)` 或 `MopPlacerPresent(cell)`**；
  `alreadyReason` 三种取值：`该格已有 Mop chore` / `该格已有 MopPlacer 标记` / `该格已有 Mop chore + MopPlacer 标记`；
* 顶层 `alreadyMarked` = 逐目标汇总（工具型两条路径也汇总）；`wouldMark = targets - alreadyMarked`；
* 真做时 `ApplyMop` 对 `AlreadyMarked` 的目标**直接跳过** ⇒ 重复 `confirm` 返回
  `marked:0, jobState:"already-marked"`，**不会重复下发**；
* `extra` 里的 `mopPlacerPresent=0/1` 不再带 `_observationalOnly` 后缀 —— 它现在**是判据之一**。

> 关于"`MopPlacer` 松手即被消费"那条旧注释：说的是**拖拽工具**路径的临时预览物。
> 本插件走的是直接 `Util.KInstantiate` 预制体再写 `Grid.Objects[cell, MopPlacer]`，实测标记物会**留到被拖掉**
> （`【实测 2026-11-17】` 22:07 标上 → 22:09 复测仍在 → 重装 0.93.8 后复测仍成立）。

#### ③ 判据不再用"字符串含 Mop"的推断
```json
"mopGating":{"maxMopAmt":150,"liquidCells":1,"skippedTooMuch":0,"skippedNotOnFloor":0,"choreTypeId":"Mop"}
```
* 判据取**游戏自己的表**：`Db.Get().ChoreTypes.Mop`（引用比较优先，`Id` 相等兜底），
  不再对 `choreType.Id` 做 `IndexOf("Mop")` —— 原注释自己写着那是"从 `阿玛里=Mop` 推断的"；
* `mopGating.choreTypeId` 回显这个单例的 `Id`（取不到就是 `null`，**不猜**）；
  `【实测 2026-11-17 · U59-744825-SCRPA】` = **`"Mop"`**（值上与原推断一致，但现在是引用而非猜测）；
* 同一判据用于 `list[].mopChoreFound` 与 `list[].mopMinionChores`。

#### ④ 在线验收（0.93.8，同一格连发四次）
| 请求 | `targets` | `alreadyMarked` | `wouldMark` | `marked` | `jobState` |
|---|---|---|---|---|---|
| A 标记前 `dryRun` | 1 | 0 | 1 | — | `dry-run` |
| B `confirm:true` | 1 | 0 | — | 1 | `marked` |
| C **标记后 `dryRun`** | 1 | **1** | **0** | — | `dry-run` |
| D 再 `confirm:true` | 1 | 1 | — | **0** | **`already-marked`** |

#### 3.37.10 `dryRun` 也返回 `attackBBox` / `attackMarkedCount`（M6.79）
0.92.2 的 dryRun：`attackBBox: null`、`attackMarkedCount: -1` —— 两边不一致，调用方没法"先看盒子再决定"。
0.92.3：**`dryRun` 与真做走同一个 `BuildAttackBBox(rect)`**（`JobMarks.cs` L116 / L480）；
`attackMarkedCount` 在 dryRun 时给**解析时的真实回读值**（调用后会被覆盖为调用后的值），**不再出现 -1**。

### 3.37.12 0.92.4 三件事（M6.81）—— `attack` 同帧回读 / 己方殖民者安全 / `mop` 判据

#### ① `attack`：**不要用同一帧的回读判失败**（这是三次"假失败"的根因）
实测（lead）：0.92.3 上对 `Raptor@(79,190)`、`Oilfloater@(84,205)`、`LightBugCrystal@(87,213)` 各真打 1×1，
**三次都返回 `marked=0, failed=1`**；但 **8 秒后再 dryRun 整片，带 chore 的从 21 只涨到 23 只**（`Squirrel` 3→8）。
⇒ `AttackTool.MarkForAttack` **确实挂了 chore**，只是 chore 要到**下一帧/下一次 chore 更新**才进 `choreWorldMap`。

**0.92.4 规则**
* 工具调用**没抛异常** ⇒ `marked = targets.Count`（不再由同帧回读决定）；
* 新增 **`choreObservedImmediately`**：同帧回读到的 chore 数（**可能为 0，不代表失败**）；
* **`failed` 只留给工具调用抛异常**；同帧回读为 0 **绝不写进 `failedDetails`**；
* `note` 写明「chore 需要下一帧才可见，请隔 1–2 秒再 dryRun 复核」。

> 为什么必须这样：假失败会让调用方以为没做，**重复下发** —— 那才是真的危险。

#### ② 安全：`attack`/`capture` **默认排除己方殖民者**
实测（lead）：块 `(40..103,160..223)` 的 dryRun 57 个目标里混进了
`Minion @(94,165)`、`Minion @(90,199)`、`Minion @(95,210)`…… ⇒ **一次范围攻击会把自家殖民者挂上攻击标记**。

**0.92.4**
* 解析时默认排除己方：`GetComponent<MinionIdentity>() != null` 或 prefabId 含 `Minion`（`Minion` / `BionicMinion`）；
* 被排除的数量报在 **`ownColonyExcluded`**；
* 要打自己人必须显式 **`"includeOwnColony": true`**，此时响应带 **`hazard:"包含己方殖民者（includeOwnColony=true）"`**；
* `capture` 同样排除（理论上不该出现殖民者，但核过更安全）。

#### ③ `mop`：判据改为「非固体 + 液体 + **下方是地面** + **质量 ≤ 150kg**」
（详细核实结果见 §3.37.11）要点：
* **`Moppable` 不是前置条件** —— 它是 `MopPlacer` 预制体自带的（`MopPlacerConfig.CreatePrefab` 的 IL 里加 `Moppable` + `Workable`），
  只在**标记之后**出现；所以"找 `Moppable` 再调它的 Mark"这条路不存在。
* 游戏真正的可拖判据在 `MopTool.OnDragTool` 的 IL：非固体 → `Element.IsLiquid` → **`Grid.Solid[Grid.CellBelow(cell)]`** → **`Mass <= MopTool.maxMopAmt`**（= **150.0f**，cctor 常量 `0x43160000`）。
* 响应新增 `mopGating:{maxMopAmt,liquidCells,skippedTooMuch,skippedNotOnFloor}`。
* **结论（写进文档，避免再踩）**：拖地的正确验证环境是**小水渍**（几 kg、在地面上）；
  `(86,180) 486kg` 天然不可拖，`(85,181) 100.015kg` / `(86,182) 96.493kg` 才是合法目标。
* ⚠️ **诚实标注**：`mop` 目前仍走"实例化 `MopPlacer`"这条路（与 `MopTool.OnDragTool` 的 IL 同链路），
  **尚未实测确认能真正排上工作**。若装机后仍失败，则应把 `mop` 标成 **`NOT_IMPLEMENTED`** 并说明
  "需要复刻 `MopTool` 的完整拖拽流程（`OnDragTool` + `OnDragComplete` + 工具激活/复原），代价大于收益" ——
  比"返回成功但什么都没发生"诚实得多。

### 3.38 ✅ `mop`（拖地）—— **placer 路径有效**（M6.83，0.92.6 更正；0.92.5 的 501 是误判，已撤回）

**实测（端到端，Cycle 5「第七次重启」，2026-11-16）**
1. 小僵禁用厕所跑了 5 分钟，小人尿地板 → 在**小人脚下**扫到真正的小水渍：
   `(111,217) liquidMassKg=0.037 onFloor=1`（对比「外世」那个 486kg 的池子）；
2. 真拖 `{"type":"mop","x":111,"y":217,"worldId":0,"confirm":true}` → 当帧 `marked=0`（回读判据错，见下）；
3. **3 秒后 `mopPlacerPresent` 变成 1 并留住**（以前"一秒就消失"是因为被测的是 486kg 的池子 —— 本来就不合格）；
4. **+20 秒**：该格 `targets=0`（水渍被拖掉），小人任务里出现 **`阿玛里=Mop`**，其余三人 `StorageFetch`。

⇒ **`Util.KInstantiate(MopPlacer)` 这条路是有效的**：placer 落地 → 变成工作 → 小人接 `Mop` chore → 拖干净。

#### 回读错在哪（0.92.0–0.92.4 的假失败全因它）
`HasMopChore(cell, worldId)` 查的是**该格物体**上的 `ChoreProvider`；
但 ONL 的拖地 chore 挂在**接活的小人**身上 ⇒ 那个判据**永远 false**。
（`Moppable` 也不是前置条件 —— 它是 `MopPlacer` 预制体自带的，见 §3.37.11。）

#### 0.92.6 的判据（与实现同一口径）
| 字段 | 含义 |
|---|---|
| **`marked`** | **`MopPlacerPresent(cell) == true` 的格数**（placer 是否留住 = 是否真的排上了工作） |
| `failedDetails` | placer 未能留住的格 |
| **`list[].mopPlacerPresent`** | 该格 `ObjectLayer.MopPlacer` 是否有对象（**marked 的判据**） |
| **`list[].mopMinionChores`** | 当前有 `Mop` chore 的**小人数量**（真正的执行信号；20 秒后应 >0） |
| `list[].mopChoreFound` | ⚠️ **仅"格上物体"的同帧快照，不代表成败**（保留是为了排查，不要用它判断） |

#### mop 的正确测试条件（这次最有价值的一条）
* **小水渍**（`< 150kg`，`MopTool.maxMopAmt`）+ **下方一格是固体** + 在地板上；
* **造法**：禁用厕所 → 跑几个周期 → 小人尿地板 → 得到 `0.0x kg` 的水渍；
* **反例**：`486kg` 的池子**天然不可拖**（超 `maxMopAmt=150`），拿它测永远失败
  —— 0.92.0–0.92.4 的"三次失败"里有两次是在测这个反例。

### 3.39 UI 从"只能列出"到"能读能点" + 游戏内截图（M6.84，0.93.0）

#### 3.39.1 `GET /v1/ui` 的按钮名读全了
旧版只找按钮**自身或直接子级**的 `LocText`，一半是 `text:null`。现在按优先级取（**取到就停**）：

| 序 | 来源 | 字段 `textSource` |
|---|---|---|
| 1 | `GetComponentInChildren<LocText>(true)`（**递归、含未激活子物体**） | `LocText` |
| 2 | `GetComponentInChildren<UnityEngine.UI.Text>(true)`（老式 UGUI） | `UIText` |
| 3 | **`GameObject.name` 兜底**（ONI 对象名通常有意义，如 `LoadButton`/`CloseButton`） | `GameObjectName` |
| 4 | 都没有 ⇒ `text:null` + `textUnavailable` | — |

每个按钮**新增字段**：
* `name`：`GameObject.name`；
* `path`：**相对该 KScreen 的父级链**（`A/B/C`）—— `click` 用同一个 path 定位，跨屏共享按钮不会串屏；
* `index`：该 screen 内按钮的 0-based 序号；
* `rect`：`[x0,y0,x1,y1]` 屏幕坐标（`RectTransform.GetWorldCorners` → `WorldToScreenPoint`，取 min/max）。

#### 3.39.2 `POST /v1/ui/click` 升级（path > name > index）
```json
{"screen":"AllDiagnosticsScreen","path":"Root/CloseButton","confirm":true}
{"screen":"...","name":"CloseButton","dryRun":true}
{"screen":"...","index":3,"confirm":true}
```
* **`dryRun:true`**：只解析、只报"会点哪个"，**不点**；
* **`confirm:true`** 才真点；
* 定位优先级 **`path` > `name` > `index`**（`name` 同时匹配 `GameObject.name` 与 path）；
* 只考虑可见按钮（`activeInHierarchy`）；**不可见 ⇒ `400 NOT_VISIBLE`**；
* 不可交互 ⇒ **`400 NOT_INTERACTABLE`**；
* 多个命中 ⇒ **`409 AMBIGUOUS_TARGET`** + 候选（`path`/`name`/`index`/可见性/可交互性）；
* 没有匹配 ⇒ **`404 NO_TARGET`**；
* 真点：`UnityEngine.UI.Button.onClick.Invoke()`；拿不到 UGUI Button 时退 `KButton.SignalClick(KKeyCode.Mouse0)`；
* **回读**（0.93.3 起**所有定位路径**都填）：响应 `readback.screenBefore` / `screenAfter` /
  `interactableAfter`，另带 `screenAfterSource`（`target` / `activeScreenAfterClick` / `targetBeforeClick`）、
  `activeScreensAfter[]` 与 `clickedDuringReadback`。点完界面被关掉（目标已销毁）时 `screenAfter` 退一步取
  "点击后仍激活的界面"；两者都取不到才写 `screenAfterUnavailable` 说明原因 —— **不留无解释的空值**。
  改界面后仍建议再 `GET /v1/ui` 复核。
* **`target`**（0.93.3 起四条定位路径都有）：这次真正点到的元素 —— `path` 与 `/v1/ui` 的
  `clickables[].path` **同源**（同一个 `UiQuery.PathOf`，相对该 `KScreen` 的父级链、同名兄弟带 `#n`），
  另有 `kind` / `role` / `text` / `name` / `screen` / `elementIndex`（= `clickables[].index`）/
  `buttonIndex`（= `buttons[].index`）/ `clickVia` / `interactable` / `activeInHierarchy`。

> ⚠️ 分流实现：`/v1/ui/click` 的老实现仍处理 `techId`/`pathContains`；**带 `dryRun`/`name`/`index` 的请求走新实现**
> （`UiShot.Click`，见 `Bridge.cs` 里那个 if）。两套并存，老调用不受影响。

#### 3.39.3 `GET /v1/shot` —— 游戏内截图（不含桌面）
```http
GET /v1/shot?w=1600&quality=80
GET /v1/shot?w=1600&cx=0&cy=0&cw=800&ch=600     # 只截左下角一块
```
* 链路（**都在主线程**）：`Camera.main` → `new RenderTexture(w,h,24)` → `cam.targetTexture = rt` → `cam.Render()`
  → `RenderTexture.active = rt` → `Texture2D.ReadPixels` → `Apply()` → **`UnityEngine.ImageConversion.EncodeToPNG(tex)`**
  → 写文件到 mod 目录下的 `shots/`；`finally` 里恢复 `cam.targetTexture` 与 `RenderTexture.active`；
* **返回**：`{path, width, height, bytes, format:"png", crop, screen, camera, note}` —— **不返回 base64**；
* **像素上限 4194304**（4M）：超了 `400 TOO_MANY_PIXELS`（message 里给上限与实际）；
* **`Camera.main == null`**（主菜单/加载场景）⇒ **`409 NO_CAMERA`**，**不静默出黑图**；
* 不做 `WaitForEndOfFrame`（主线程里没法 await）—— 用**同步离屏渲染**替代；**不改变任何游戏状态**。

#### 3.39.4 新增依赖
`UnityEngine.ImageConversionModule.dll`（`EncodeToPNG` 扩展方法所在）——已在 `OniAiBridge.csproj` 加引用。
`UiShot` 还用了 `KScreen`（现有 `/v1/ui` 用的就是它，不是 `UIScreen`）。

### 3.39.5 0.93.0 → 0.93.1 的三个修正（M6.85）

#### ① `/v1/ui/click` 分派：**两套实现都在，但都是"新实现"，0.93.0 的失败其实是 screen 名匹配错了**
0.93.0 报 `400 NO_TARGET「该界面下没有按钮」` —— 那句话是**新实现**（`UiShot.Click`）的文案，
根因是 **`KScreen` 的屏幕名是 `screenName` 属性，不是 `GameObject.name`**，而 0.93.0 只比了 `.name`（实战里是 `MainMenu_SpacedOut/...` 这种 prefab 名）。
0.93.1：`ScreenMatches()` 依次比 **`screenName` → `displayName` → `GameObject.name` → `screenName` 子串**。
⇒ 带 `dryRun` / `index` 的请求现在能正确进入新实现（`dryRun` 一定能看到候选）。

#### ② `path` 唯一性：同名兄弟加 `#n`
主菜单 10 个菜单项是**同一预制体克隆**，`name` 与 `path` 完全相同（实战 9 条一模一样 ⇒ 永远 409）。
0.93.1 的 `PathOf` 给**同名兄弟**追加序号：
```
MainMenu_SpacedOut/MainMenuMenubar/MainMenuButtons/ButtonTemplate#4
```
判据：`/v1/ui` 里那 10 个按钮的 `path` **两两不同**；`click` 用任一个 path 只命中 1 个。
（`click` 与 `/v1/ui` 用的是**同一个 `UiQuery.PathOf`**，所以不会出现"列出来的 path 点不到"。）

#### ③ `/v1/shot`：相机选错（`overlayNoDepth`）+ 全黑
0.93.0 用 `Camera.main`，实战选中 `overlayNoDepth`（叠加层）⇒ 纯黑 10692 字节。
0.93.1 三处改动：
1. **相机选择** `PickWorldCamera()`：枚举 `Camera.allCameras`，**排除**名字含 `overlay`/`nodepth`/`ui`/`canvas` 的；
   评分 = `depth*100` + (`targetTexture==null` 加 50) + (非正交加 5)，取最高；
2. **渲染方式**：优先 **`ScreenCapture.CaptureScreenshotAsTexture()`**（同步、只含游戏视图、不含桌面）；
   失败才退回 `RenderTexture` + **选好的世界相机**；然后统一缩放到 `w/h`；
3. **黑图拦截**：`AverageLuma()` 采样最多 4096 点算平均亮度，**< 1/255 ⇒ `500 SHOT_BLACK`**，
   message 带 `camera=<name>` 与 `renderPath`；**绝不把黑图当成功返回**。
   响应新增 `renderPath`（`ScreenCapture` / `RenderTexture`）与 **`avgLuma`**（0–255）。

**新增路由** `GET /v1/shot/cameras`（只读）：列出所有相机的
`name / depth / cullingMask / targetTexture / enabled / orthographic / rect / pixelRect`，
并给出 **`pickedForShot`**（`/v1/shot` 会用哪台）—— 一次就能判断该用哪台，不用猜。

**新增依赖**：`UnityEngine.ScreenCaptureModule.dll`（csproj 加引用）。

### 3.39.6 行不是按钮：`/v1/ui` 的 `clickables` + 按类型分派的点击（M6.86，0.93.2）

#### ① 根因：`/v1/ui` 只列 `KButton`，而"行"挂的是 `MultiToggle`
（游戏源码逐类型核对，不是猜；反编译 `Assembly-CSharp.dll`）

| 界面 | 行的组件 | 出处 |
|---|---|---|
| `ColonyDiagnosticScreen`（殖民地诊断的那一列行） | `MultiToggle` | `DiagnosticRow`：`button = gameObject.GetComponent<MultiToggle>()`，回调用 `Delegate.Combine` 挂到 `MultiToggle.onClick`（内部做 `SelectTool.Instance.SelectAndFocus` / `ActiveWorldStarWipe`） |
| `AllDiagnosticsScreen`（全部诊断行） | `MultiToggle` | `SpawnRow()`：`HierarchyReferences` 的 `MainToggle` / `SubrowToggle` / `PinToggle` |
| `ReportScreen`（日报行） | `MultiToggle` | `ReportScreenEntryRow` 的 `public MultiToggle toggle` |
| `TableScreen`（表格的勾选 / 优先级格） | `MultiToggle` | `widget_go.GetComponent<MultiToggle>()` |

`MultiToggle` 实现了 `IPointerClickHandler`，但它的 `OnPointerClick` IL 只有 **`AllowClick` 门槛 + `onClick.Invoke()`**，
**不会** `NextState()`（`NextState` 只被别处显式调用）⇒ "点一次行"等于触发该行回调，不会把行状态翻掉。

#### ② `/v1/ui` 新增 `clickables`（`buttons` 不变）
每个界面对象里多了：

| 字段 | 含义 |
|---|---|
| `clickables[]` | 该界面**全部**可点击元素（判定 = `MonoBehaviour` 实现 `IPointerClickHandler` / `ISubmitHandler`，**不看类名**） |
| `clickables[].kind` | 组件类名：`KButton` / `KToggle` / `MultiToggle` / `Button` / `Toggle` / 自定义 |
| `clickables[].role` | 归一化种类：`button` / `toggle` / **`row`**（`MultiToggle` 行）/ `uguiButton` / `uguiToggle` / `pointerClick` |
| `clickables[].clickVia` | **这一行会被/已被用哪个入口点击**（与 `/v1/ui/click` 的实际分派同源） |
| `clickables[].clickable` | 是否有点击入口（现在恒为 `true`；留给将来未知类型） |
| `clickables[].index` | 在 `clickables` 数组内的 0-based 序号 |
| `clickables[].buttonIndex` | 若该元素是 `KButton`：它在 `buttons` 数组里的 `index`；否则缺省 |
| `clickables[].state` / `stateCount` / `hasOnClick` | `MultiToggle`：当前状态 / 状态数 / `onClick` 是否挂了回调 |
| `clickables[].isOn` | `KToggle`：当前是否开启 |
| `clickables[].path` / `name` / `text` / `textSource` / `rect` / `interactable` / `activeInHierarchy` | 与 `buttons` 同口径（`path` 与 `buttons` 用**同一个** `UiQuery.PathOf`，同名兄弟带 `#n`） |
| `clickableTotal` / `clickablesEmitted` / `clickablesTruncated` / `clickablesLimit` / `clickablesMode` | 该界面去重后的可点击元素总数 / 实际输出条数 / 是否截断 / **生效上限**（0.93.3 起默认 2000）/ 输出模式（`compact` 或 `full`；**0.93.4 起默认 `compact`**）。四件套恒自洽：`clickablesEmitted = min(clickableTotal, clickablesLimit)`、`clickablesTruncated = clickableTotal > clickablesLimit`；`?clickablesLimit=N` 覆盖上限（上限 20000，非法/越界回落默认并另给 `clickablesLimitRequested`）；`?clickables=full` 时每条输出完整字段，`?clickables=compact`（默认）时每条只写 `kind`/`role`/`text`/`path`。界面内这一份 `clickablesMode` 自 0.93.3 起就有；**本次请求的模式以 `data` 顶层的 `clickablesMode` 为准**（0.93.4 起新增，取值与界面内那份一致） |

`buttons` 仍是**只有 `KButton`** 的数组，字段与 0.93.1 完全一致（老调用方不受影响）；
`clickables` 才是"行也在里面"的全集。

#### ③ `/v1/ui/click` 按类型分派（不再写死 `KButton`）
| 组件 | 点击入口 |
|---|---|
| `KButton` | `SignalClick(KKeyCode.Mouse0)`（自带 `interactable` 门槛） |
| `KToggle` | `Click()`（保留原有"已开启不许点"护栏：`409 ALREADY_ON`，除非 `allowToggleOff:true`） |
| `MultiToggle`（行） | `OnPointerClick(PointerEventData{button:Left, clickCount:1})`（虚方法，保留子类覆写与 `AllowClick` 门槛） |
| UGUI `Button` | `onClick.Invoke()` |
| UGUI `Toggle` | `OnPointerClick(PointerEventData)` |
| 其它 `IPointerClickHandler` | `ExecuteEvents.Execute<IPointerClickHandler>(go, …)` 兜底 |

定位入口与以前一致，只是候选集合扩到"全部可点击元素"：

* `text`：匹配 `clickables` 里的文字（三级回退：递归 `LocText` → 递归 UGUI `Text` → `GameObject.name`）；
  同名多处仍是 `409 AMBIGUOUS_TARGET`，**新增可选 `kind`**（组件类名或 `role`，如 `"MultiToggle"` / `"row"`）用来消歧；
* `labelClick`：找到文字恰等的 `LocText` 后，**向上找最近的可点击组件**（以前只找 `KButton`，所以行类文字一律 `400 NO_CLICKABLE_ANCESTOR`）；
* `path` / `pathContains` / `techId` / `name` / `index` / `dryRun`：候选集合扩到全部元素；
  **`index` 仍是 `KButton` 序号**（与 `buttons[].index` 一致），行请用 `path`（来自 `clickables[].path`）；
  `409` 候选里另有 `elementIndex`（该元素在该界面 `clickables` 里的序号）与 `kind` / `role` 便于对照；
* 响应新增 `kind` 与 `clickedVia`（实际调用的入口），用于核对"列出来的入口"和"真点的入口"是否一致。

### 3.39.7 `GET /v1/shot/cameras`：`pickedForShot` 是**列表**（M6.86，0.93.2）

#### ① 根因：只从 `Camera.allCameras` 取、用名字黑名单筛、并且只输出**单个名字**
0.93.1 的实现里 `pickedForShot` 的类型是**字符串**：`Snap.Str(PickWorldCamera())` —— 没有可用相机时就是 `null`，
所以看起来"是空的"。两个来源问题叠在一起：

1. **取错集合**：只看 `Camera.allCameras`（Unity 文档口径：**只含 `enabled` 的相机**）；
   世界主相机在加载 / 主菜单 / 界面切换时可能处于 `enabled=false` 或未激活，那一瞬间候选就是 0；
2. **过滤条件太严**：按名字黑名单（含 `overlay`/`nodepth`/`ui`/`canvas` 才排除）来猜角色，
   没有用游戏自己的相机注册表 —— `CameraController.Instance` 明确有
   `baseCamera` / `overlayCamera` / `overlayNoDepthCamera` / `uiCamera` / `lightBufferCamera` /
   `simOverlayCamera` / `infraredCamera` / `timelapseFreezeCamera` 与 `cameras` 列表。

#### ② 现在的相机来源（三路并集）
1. `CameraController.Instance` 的具名相机字段 + `cameras` 列表（游戏注册表，角色最准）；
2. `Camera.allCameras`（只含 enabled）；
3. `Object.FindObjectsOfType<Camera>(true)`（**含 disabled / 未激活**，补上第 2 路漏掉的）。

角色 `role` 取值：`base` / `overlay` / `overlayNoDepth` / `ui` / `lightBuffer` / `simOverlay` /
`infrared` / `timelapse` / `registry` / `other`；拿不到注册表时按名字兜底猜。

#### ③ 响应字段
| 字段 | 含义 |
|---|---|
| `pickedForShot[]` | **当前可用于截图的相机**，按 `score` 降序；**这是列表**，每项即下面这条 `cameras[]` 的完整对象（`chosen` 标记为 `true` 的那台排第一） |
| `pickedCount` | 可用相机数量（`pickedForShot` 非空 ⇔ 该值 > 0） |
| `pickedFallback` | 角色判定把全部相机都排除时，退一步改为"`enabled` 且已激活的相机"；此时为 `true`（正常情况下 `false`） |
| `chosenForShot` | `/v1/shot` 实际会渲染的那台（= `pickedForShot[0]`；无可用相机时 `null`） |
| `cameras[]` | **全部**相机（含不可用的），每项带 `usableForShot` 与 `unusableReason` |
| `count` | `cameras[]` 条数 |
| `registryAvailable` | `CameraController.Instance` 是否可用（`false` ⇒ 角色只能按名字猜） |

每台相机的字段：

| 字段 | 含义 |
|---|---|
| `name` / `role` / `score` / `chosen` / `fromRegistry` | 名字 / 角色 / 排序分 / 是否被选中 / 是否来自游戏注册表 |
| `usableForShot` / `unusableReason` | 能否用于截图 / 不能的原因（UI 画布、光照缓冲、无深度叠加层、延时冻结、红外、停用、未激活） |
| `enabled` / `activeInHierarchy` | 相机 `enabled` / 所在 GameObject 是否激活 |
| `depth` / `cullingMask` / `clearFlags` | 渲染顺序 / 剔除掩码 / 清屏方式 |
| `targetTexture` | 渲染目标（`null` = 直接上屏） |
| `orthographic` / `orthographicSize` / `fieldOfView` | 投影方式与视野大小 |
| `rect` / `pixelRect` | 归一化视口 `[x,y,w,h]` / 像素视口 `[w,h]` |
| **`view`** | **视野范围**：视口 `(0,0)` 与 `(1,1)` 换算到世界坐标后的 `[minX,minY,maxX,maxY]`（正交相机即当前画面覆盖的世界范围；可再经 `/v1/area` 换算成格） |

#### ④ `/v1/shot` 的连带变化
* 相机排序换成上面这套 `score`（`base`=1000 基准分，`overlay`=450，`simOverlay`=350，`other`=300，
  `overlayNoDepth`=60，`infrared`=40，`lightBuffer`=10，`timelapse`=−100，`ui`=−200；
  `targetTexture==null` 加 20、正交加 5；停用 −500、未激活 −1000）；
* 响应新增 **`cameraRole`**（与 `cameras[].role` 同源，便于对照是"哪类相机出的图"）。
### 3.39.8 0.93.3（M6.87）：`target.path` / `readback` 补齐 + `clickables` 上限 2000

#### ① `/v1/ui/click` 的 `target` 与 `readback` 是空的
**根因不是取值失败，是字段根本不存在**：`/v1/ui/click` 按请求内容分流到两套实现（见 `Bridge.cs` 那个 if）——
带 `dryRun` / `name` / `index` 的请求走 `UiShot.Click`，它有 `target` 与 `readback`；
其余（`text` / `path` / `pathContains` / `techId` / `labelClick`）走 `UiClick`，那三条响应里**从来没有这两个字段**。
真机用 `path` 点 `AllDiagnosticsScreen` 的行时看到 `clicked:true` 但 `target.path` 与 `readback` 为空，就是这个分流造成的。

0.93.3 起四条定位路径统一：
* `target` 由同一个 `UiRows.AppendTargetJson` 写：`path` 与 `/v1/ui` 的 `clickables[].path` **同源**
  （同一个 `UiQuery.PathOf(comp.transform, s.transform)`，相对该 `KScreen` 的父级链、同名兄弟带 `#n`）；
  `elementIndex` 即 `clickables[].index`，`buttonIndex` 即 `buttons[].index`；
  `labelClick` 的目标由 `UiRows.Describe` 现算（没经过采集 ⇒ `elementIndex:-1` + `elementIndexUnavailable` 说明原因）；
  父链拼不出来时退一步给"整条场景父链"这个真实值并标 `pathSource:"fullSceneFallback"`，不留无解释的 null；
* `readback` 由同一个 `UiRows.AppendReadbackJson` 写，必答 `screenBefore` / `screenAfter` / `interactableAfter`，
  另带 `screenAfterSource` / `activeScreensAfter[]` / `clickedDuringReadback`。
  点完界面被关掉（主菜单点「新游戏」、诊断界面点「关闭」）⇒ 目标已销毁，`screenAfter` 退一步取"点击后仍激活的界面"
  并标 `activeScreenAfterClick`；两者都取不到 ⇒ `screenAfterUnavailable` 写明原因；
  回读连主线程都进不去 ⇒ 仍然写一份 `readback`，用 `screenAfterUnavailable` / `interactableAfterUnavailable` 说明"未执行"及原因。

#### ② `clickables` 被 500 截断
**根因**：`UiQuery.ClickablesLimit = 500`，而采集上限 `CollectLimit` 是 2000 —— 元素采到了，只输出四分之一。
实测 `ToolMenu` 的 `clickables` 停在 500 条（该界面一共 724 个按钮）。

0.93.3 起：
* 默认上限 `ClickablesLimit = 2000`（与 `CollectLimit` 同量级）；
* `GET /v1/ui?clickablesLimit=N` 覆盖，上限 `ClickablesLimitMax = 20000`；
  非数字 / ≤0 / 超范围**一律回落默认值、不报错**（查询参数写错一个数字不该让整次读取失败），
  生效值与请求值不同时另给 `clickablesLimitRequested`；
* 采集上限跟着抬到 `max(CollectLimit, 生效上限)`，否则"想输出更多"也采不到更多；
* 四件套恒自洽：`clickablesEmitted = min(clickableTotal, clickablesLimit)`、
  `clickablesTruncated = clickableTotal > clickablesLimit`（默认 2000 下 724 条不再截断）；
* 体积兜底：`?clickables=compact` 时每条只写 `kind` / `role` / `text` / `path`（约降六成），
  `clickablesMode` 标明当前模式；**默认仍是全字段、不丢元素**（默认行为不变，精简是显式选择）。
  > 0.93.4 起本节结论已变更：默认改为 `compact`，全量需显式 `?clickables=full`，见 §3.39.9。

#### ③ 真机核对项（0.93.3 未装机）
`GET /v1/ui`（默认参数）上 `ToolMenu`：`clickableTotal = 724`、`clickablesEmitted = 724`、
`clickablesTruncated = false`、`clickablesLimit = 2000` —— 724 < 2000，不截断。
其中 724 来自真机实测的按钮数；`clickableTotal` 与 `buttonCount` 是否同一个数要进游戏读一次确认
（若 `ToolMenu` 还含 `KToggle` 类页签，`clickableTotal` 会略大于 `buttonCount`）。

### 3.39.9 0.93.4：`clickables` 默认改为 `compact`，模式字段补到 `data` 顶层

#### ① 默认值变更
0.93.3 真机实测：`GET /v1/ui` 默认（全量）返回 **1,714,519 字节**；同一请求加 `?clickables=compact`
返回 **898,280 字节**（降 48%）。默认全量的体积主要来自"每条都给 `index` / `rect` / `clickVia` 等
十余个字段"，而定位一个元素只需要四项。
0.93.4 起：

| 请求 | 每条 `clickables` 的字段 |
|---|---|
| `GET /v1/ui`（不传 `clickables`） | `kind` / `role` / `text` / `path`（**默认 compact**） |
| `?clickables=compact` | 同上（显式写与不写等价） |
| `?clickables=full`（等价写法 `?clickables=all`） | 0.93.3 及以前默认的完整字段集（见下） |
| `?clickables=xyz`（任何其它取值） | 回落默认 **compact**，**不报错** |

取值比较大小写不敏感。字段名不新增、不删除：compact 只保留四个既有字段名。

#### ② `clickablesMode` 的确切位置（0.93.3 的"读不到"是层级问题，不是没实现）
0.93.3 确实写了这个字段，但写在**每个界面对象内部**（与 `clickablesLimit` 相邻，形如
`data.screens[i].clickablesMode`），响应顶层与 `data` 顶层都没有，因此按 `data.clickablesMode`
读取会得到 `undefined`。
0.93.4 在 **`data` 顶层、紧邻 `screens` 数组之后**补一份同名 `clickablesMode`：

```json
{
  "apiVersion": "…", "ok": true,
  "data": {
    "screens": [ { "name": "ToolMenu", "clickables": [ … ], "clickablesMode": "compact", … } ],
    "clickablesMode": "compact",
    "count": 1,
    "buttonCount": 1566
  }
}
```

两处同源、取值恒一致：`data.clickablesMode` 是**本次请求**的模式（全局唯一一份）；
`screens[i].clickablesMode` 是 0.93.3 的既有位置，为兼容保留，**不要删除**。
程序读取时以 `data.clickablesMode` 为准。

#### ③ 与 `clickablesLimit` 正交
模式只决定"每条写几个字段"，不参与截断；`clickablesLimit` 只决定"输出多少条"，与模式无关。
四件套在两种模式下都严格成立：
`clickablesEmitted = min(clickableTotal, clickablesLimit)`、
`clickablesTruncated = clickableTotal > clickablesLimit`。
真机 0.93.3 已核对：`ToolMenu` 为 `clickableTotal=1566 / emitted=1566 / truncated=false`，
`ResearchScreen=702`、`ResearchScreenSideBar=597`；`?clickablesLimit=50` 时 `emitted=50、truncated=true`；
`?clickablesLimit=abc` 回落 2000 且不报错。

#### ④ 两种模式的字段清单
compact（默认，恒为四个字段）：`kind` / `role` / `text` / `path`。

full（`?clickables=full`）：恒有 `kind` / `role` / `text` / `name` / `path` / `index` / `clickable` /
`clickVia` / `interactable` / `activeInHierarchy`；条件出现 `textSource`（文字取自哪里）、
`textUnavailable`（文字读不到且 `text` 为 null）、`buttonIndex`（该元素是 `KButton` 时）、
`rect`（屏幕矩形算得出时）、`state` / `stateCount` / `hasOnClick`（`MultiToggle` 行）、
`isOn`（`KToggle`）。

### 3.40 ✅ `GET /v1/map`（M6.89，0.93.5）—— 地图降采样（默认关）

> **设计约束**（`DESIGN.md` §2.3 第 4 条）："地图不默认下发：`/v1/map` 必须带 `bbox` 与 `stride`，硬上限
> （默认 64×64 格 / 次），超限返回 `400 BBOX_TOO_LARGE`。默认只给'元素 id + 温度 + 质量'三通道。"
> 端点表（§3.2）："必须给 `bbox`；默认关，`allowMap` 开关。"

#### 3.40.1 开关：`allowMap`（默认 `false`）

**默认不开放**。三种打开方式：

| 方式 | 怎么做 |
|---|---|
| `settings.json` | `%USERPROFILE%\Documents\Klei\OxygenNotIncluded\mods\config\OniAiBridge\settings.json` 里 `"allowMap": true`，然后**重载 mod**（配置只在加载时读一次） |
| 游戏内设置页 | `Ctrl+Shift+O` → 勾上「允许ai读取地图格子数据（/v1/map）」→ 点 `[保存]`（改的是内存草稿，**点保存才写盘**） |
| 直接改配置后重启游戏 | 同上第一条 |

关着的时候请求 ⇒ **`403 MAP_DISABLED`**（不是空数组、不是 `ok:true`）：

```json
{"apiVersion":"1.1","ok":false,"seq":42,"data":null,
 "error":{"code":"MAP_DISABLED",
  "message":"allowMap is false —— /v1/map 默认关闭（DESIGN.md §2.3 第 4 条：地图不默认下发）。打开方式：settings.json 里 \"allowMap\": true，或游戏内设置页（Ctrl+Shift+O）勾上地图开关，然后重载 mod。",
  "details":null}}
```

`GET /v1/meta` 会如实反映这个开关（见 §3.40.8）。

#### 3.40.2 请求

```bash
curl -s -H "X-Oni-Token: $ONI_TOKEN" "http://127.0.0.1:8770/v1/map?bbox=10,20,12,21&stride=1"
```

| 参数 | 必填 | 说明 |
|---|---|---|
| `bbox` | **是** | `x0,y0,x1,y1` —— 4 个整数、**逗号**分隔、**闭区间**（含两端）。端点顺序不敏感：`x1<x0` / `y1<y0` 会被归一化，响应回显归一化后的框。`%2C` 也接受。 |
| `stride` | **是** | 抽样步长，整数 `>= 1`。`stride=1` 逐格；`stride=2` 隔一格取一个（含起点）。**不设上限**（`bbox` 本身就 ≤ 64×64 格，再大也只会得到 1×1）。 |
| `token` | — | 同其它端点：`?token=` 或 `X-Oni-Token` 头。 |

**`bbox` 与 `stride` 缺任一 ⇒ `400 BAD_REQUEST`** —— 本端点**没有默认值**，不会臆测你想读哪块地图。
错误 `message` 里**自带格式说明**（这是刻意的：调用方光看错误就该知道怎么改）：

```json
{"apiVersion":"1.1","ok":false,"seq":43,"data":null,
 "error":{"code":"BAD_REQUEST",
  "message":"缺参数 bbox。要求 bbox=x0,y0,x1,y1：4 个整数、逗号分隔、**闭区间**（含两端）；例：bbox=10,20,13,25 = x 10..13、y 20..25 的 4×6 格区域。**bbox 与 stride 必须同时给**，缺任一即 400 BAD_REQUEST（本端点没有默认值，不会臆测你想读哪块地图）。",
  "details":null}}
```

格式不对（段数不是 4、有非整数、空串）走的也是 `400 BAD_REQUEST`，`message` 里会回显你实际传了什么：

```json
{"code":"BAD_REQUEST","message":"bbox 解析失败（收到 \"10,20,12\"）。要求 bbox=x0,y0,x1,y1：4 个整数、逗号分隔、**闭区间**（含两端）；例：bbox=10,20,13,25 表示 x 在 10..13、y 在 20..25 的 4×6 格区域（含两端）。"}
```

#### 3.40.3 硬上限：64×64 格 / 次

`bbox` 覆盖的格数 = `(x1-x0+1) × (y1-y0+1)`，**单边 ≤ 64、总数 ≤ 4096**；超了 ⇒ `400 BBOX_TOO_LARGE`：

```json
{"code":"BBOX_TOO_LARGE",
 "message":"bbox 太大：本请求 65×1 格（65 格），单次硬上限 64×64 格（4096 格）。请缩小 bbox 后分块请求；**stride 不改变这个上限**（它只降低采样密度，不缩小 bbox 覆盖的格数）。"}
```

> ⚠️ 上限按 **`bbox` 的格数**算，**不是**按采样点数算。`bbox=0,0,64,0&stride=64` 仍然是 `BBOX_TOO_LARGE`
> —— `stride` 是"看得粗一点"，不是"允许看更大一块"。

#### 3.40.4 错误码与判定顺序

| 顺序 | 情况 | HTTP | code |
|---|---|---|---|
| 1 | 不是 GET | 405 | `METHOD_NOT_ALLOWED` |
| 2 | `allowMap=false` | 403 | `MAP_DISABLED`（**先判这个，连参数格式都不看**） |
| 3 | 缺 `bbox` / 缺 `stride` / 格式坏 / `stride<1` | 400 | `BAD_REQUEST` |
| 4 | `bbox` 超过 64×64 | 400 | `BBOX_TOO_LARGE` |
| 5 | 主线程泵没跑起来 | 503 | `NOT_READY` |
| 6 | `bbox` **完全**落在 Grid 之外 | 400 | `BBOX_OUT_OF_BOUNDS`（要读 Grid 尺寸，所以发生在主线程里） |
| 7 | 主线程排队超时（存档加载中） | 503 | `RETRY_LATER`（请求**未执行**，1 秒后重试） |
| 8 | 采集抛异常 / Grid 尺寸拿不到 | 500 | `MAP_UNAVAILABLE` |

第 6 条的 `message` 会给出 Grid 实际尺寸，例如：

```json
{"code":"BBOX_OUT_OF_BOUNDS",
 "message":"bbox 完全在网格之外：请求 x=1000..1001、y=1000..1001；Grid 是 240×380，合法范围 x=0..239、y=0..379。"}
```

**部分越界不报错**：夹到网格内，并在响应里给 `clipped: true` + `bboxRequested`（原始请求值），
`bbox` 则是**实际生效范围** —— 两者别混。

#### 3.40.5 响应

```json
{
  "apiVersion": "1.1", "ok": true, "seq": 77, "serverTime": "2026-11-17T19:49:24+08:00",
  "data": {
    "bbox": { "x0": 10, "y0": 20, "x1": 12, "y1": 21, "w": 3, "h": 2 },
    "clipped": false,
    "stride": 1,
    "width": 3, "height": 2,
    "cells": 6, "emitted": 6,
    "grid": { "widthInCells": 240, "heightInCells": 380 },
    "units": { "temperature": "K", "mass": "kg" },
    "indexRule": "行主序：idx = row*width + col；row 0 = bbox.y0，col 0 = bbox.x0；第 (row,col) 个采样格的坐标 = (bbox.x0 + col*stride, bbox.y0 + row*stride)",
    "channels": {
      "element":     ["SandStone", "SandStone", "Dirt", "SandStone", "Dirt", "Vacuum"],
      "temperature": [293.15, 293.15, 310.149, 291.7, 289.05, 0],
      "mass":        [1000, 1000, 850.5, 1000, 820, 0]
    },
    "fogged": false,
    "visibility": {
      "aiEnableFog": false,
      "sampled": 6, "emitted": 6,
      "filteredByFog": 0, "filteredInvalid": 0, "filteredCount": 0,
      "rule": "none (omniscient)"
    },
    "source": "Grid.Element[id] / Grid.Temperature / Grid.Mass（+ Vision.Allow = Grid.Visible>0 门禁）",
    "note": "三通道默认集（元素 id / 温度 / 质量）。数组长度恒为 width×height，**行主序**，数组里的 null = 该采样格没有数据（被迷雾滤掉 / 格子无效 / 该通道整列不可用），**不是真空**：真空格给的是 element=\"Vacuum\"、mass=0。温度单位 K（310.15 K = 37 °C），质量单位 kg（真空 0）。单次硬上限 64×64 格（4096 格）按 **bbox 的格数**判定，stride 不影响上限；bbox 部分越界时夹到网格内并给 clipped:true + bboxRequested。",
    "elapsedMs": 2
  },
  "error": null
}
```

| 字段 | 说明 |
|---|---|
| `bbox` | **实际生效**的格子范围（部分越界时已被夹取）。`w`/`h` 是它的宽高（格） |
| `bboxRequested` | 仅在 `clipped:true` 时出现：你原本请求的框 |
| `clipped` | 是否发生过夹取 |
| `stride` | 回显本请求生效的步长 |
| `width` / `height` | **采样栅格**的列数 / 行数 = `ceil(bbox.w / stride)` / `ceil(bbox.h / stride)` |
| `cells` | `width × height`，**三个通道数组的长度恒等于它** |
| `emitted` | 真正给出数据的采样格数（= `cells - filteredByFog - filteredInvalid`） |
| `grid` | 全局格网尺寸（`Grid.WidthInCells` / `Grid.HeightInCells`），用来判断你这块框在世界里的位置 |
| `units` | `temperature` 恒 `"K"`、`mass` 恒 `"kg"`（**温度是开尔文：310.15 K = 37 °C**） |
| `channels.element` | 元素 id（`Element.id.ToString()`，即 `SimHashes` 名：`SandStone` / `Oxygen` / `Vacuum`…），**不是**中文名 |
| `channels.temperature` | 该格温度（K） |
| `channels.mass` | 该格质量（kg）；真空是 `0` |
| `fogged` | 本响应是否受迷雾门禁（= `AIEnableFog` 配置原值，与 `/v1/buildings` 同名同义） |
| `visibility` | 口径同 `/v1/buildings`：`aiEnableFog` / `sampled` / `emitted` / `filteredByFog` / `filteredInvalid` / `filteredCount` / `rule` |
| `source` / `note` | 取数出处与口径（**机器可读的字段才是接口**，这两条是给人看的） |
| `elapsedMs` | 采集耗时 |

#### 3.40.6 怎么还原坐标

```
idx  = row * width + col           // 行主序，row 0 = bbox.y0（下边界），col 0 = bbox.x0（左边界）
x    = bbox.x0 + col * stride
y    = bbox.y0 + row * stride
```

例：`bbox=10,20,12,21&stride=1` ⇒ `width=3, height=2`，`channels.temperature[4]` 对应
`row=1, col=1` ⇒ `(x,y) = (11, 21)`。

`stride>1` 时只采样 `x0 + col*stride` / `y0 + row*stride` 这些点，中间格子**根本不会被读到**
（不是"读了再丢"）—— 这也是 `stride` 唯一的用处：降采样省时间与体积。

#### 3.40.7 与迷雾的关系（`aiEnableFog=true` 时）

- 门禁与 `/v1/buildings`、`/v1/env` **同一个出处**（`Vision.Allow(cell)` ⇒ `Grid.Visible[cell] > 0`），
  且在**采集循环里逐格判断** —— 不是"先全查再 filter"。
- 被滤掉的格子**不输出任何数值**：通道数组里该位置是 **`null`**，数组长度不变（`= cells`）。
  这样调用方不必自己推导稀疏索引就能逐格对齐坐标；代价是滤得多时有连片 `null`（体积上限由 64×64 兜住）。
- 响应如实报告：`fogged: true`、`visibility.filteredByFog`（被迷雾滤掉的格数）、
  `visibility.filteredCount`（= `filteredByFog + filteredInvalid`，**与 `/v1/buildings` 同口径：它是"被滤掉的总数"，不是单指迷雾**）。
- `AIEnableFog=false`（全图模式）时 `rule` 是 `"none (omniscient)"`、`filteredByFog` 恒为 0。

迷雾开启时的响应片段（`bbox=0,0,3,3&stride=2` ⇒ 2×2 采样点，其中 1 格不可见）：

```json
{
  "bbox": { "x0": 0, "y0": 0, "x1": 3, "y1": 3, "w": 4, "h": 4 },
  "stride": 2, "width": 2, "height": 2, "cells": 4, "emitted": 3,
  "channels": {
    "element":     ["SandStone", null, "Dirt", "SandStone"],
    "temperature": [293.15,     null, 301.5,  293.15],
    "mass":        [1000,       null, 900,    1000]
  },
  "fogged": true,
  "visibility": { "aiEnableFog": true, "sampled": 4, "emitted": 3,
                  "filteredByFog": 1, "filteredInvalid": 0, "filteredCount": 1,
                  "rule": "Grid.Visible[cell] > 0" }
}
```

#### 3.40.8 通道读不到时的降级字段（**不是** `null`，也不是 0）

某个通道在**所有**采样点上都读不出来（例如 `Grid.Temperature` 抛异常）时：

- `channels.<通道>` 变成 **`null`**（"本次没有这个通道的数据"），
- 顶层给 `temperatureUnavailable` / `massUnavailable` / `elementUnavailable`，值是异常名 + 消息。

```json
{
  "channels": { "element": ["Oxygen"], "temperature": null, "mass": [120.5] },
  "temperatureUnavailable": "NullReferenceException: Object reference not set to an instance of an object"
}
```

其余通道**照常输出**（一个通道坏不该让整条请求失败）。三个通道都正常时，这三个 `*Unavailable`
字段**不出现**（有原因才出现 / 没原因就不出现，同 `/v1/catalog` 的 `categoriesUnavailable` 规矩）。

> 单格读失败（不是整列坏）时该格是 `null`，但仍计入 `emitted` —— 这是"这格没读到"，
> 与"这格被迷雾挡住"在语义上不同，**两者都会让该位置是 `null`**；要区分请结合
> `visibility.filteredByFog` 与 `*Unavailable` 判断，别只看 `null`。

#### 3.40.9 `GET /v1/meta` 上的相关字段（0.93.5 起）

| 字段 | 说明 |
|---|---|
| `data.capabilities.map` | `true` = 端点在 + `allowMap` 已开；`false` = `allowMap` 关着（**端点本身是存在的**） |
| `data.allowMap` | 配置原值（`false` = 默认） |
| `data.mapLimits` | `{"maxSide":64,"maxCells":4096,"channels":["element","temperature","mass"]}`，让客户端自发现上限与通道集 |
| `data.capabilities.catalog` | 顺带更正：自 0.93.5 起如实报 `true`（`/v1/catalog` 从 M5.2 就是可用端点，原来这里硬编码 `false` 是错的） |

#### 3.40.10 与 `/v1/area`、`/v1/env` 的口径区别（**别混**）

| 端点 | 给什么 | 迷雾 |
|---|---|---|
| `/v1/area` | 一块区域的**统计数字**（固/液/气/真空格数、元素聚合质量、建筑聚合） | **不过滤**（口径见其 `note`：逐格统计全量） |
| `/v1/env` | 一块区域的**气体环境**（只算气体格、气压、温度统计） | 过滤（`AIEnableFog` 时只算可见格） |
| `/v1/map` | **逐格原始值**（元素 id / 温度 / 质量三通道，可定位到格子） | 过滤（只输出可见格，其余 `null`） |

`/v1/map` 不统计、不聚合、不排序 —— 它只把格子摆成矩阵。要"这块地方有哪些资源"用 `/v1/area`，
要"这格里是什么"才用 `/v1/map`。

#### 3.40.11 未验证到哪一步（诚实清单）

* **编译通过**（`0 个错误`），`Map.cs` 的 bbox/stride 校验、64×64 上限、归一化、clipped、
  stride 抽样点、迷雾过滤与计数、通道降级、真空/数值格式，均已在**替身 `Grid` 的离线脚手架**上逐条断言通过
  （替身只换掉游戏侧依赖，`Map.cs` 原样编译）。
* ⚠️ **真机未装机、未进游戏**：本 mod 至今从未在游戏里跑过一次。因此下面这些都**没有**运行时证据：
  `Grid.Temperature` 在真实存档里的量纲与取值范围、`Grid.Element[cell]` 在真空格的返回值、
  大存档里 4096 格的采集耗时、`Grid.Visible` 的迷雾值与 `/v1/buildings` 是否完全一致。
  第一次装机后应核对：`bbox` 取一个已知格（例如某个复制人脚下）看 `element`/`temperature` 是否与游戏内
  温度叠加层一致；以及 `AIEnableFog=true` 时 `emitted < cells` 是否真的对应屏幕上看不见的地方。

---

### 3.41 `GET /v1/journal` + `reqId`：请求对账与幂等回放（0.96.1）

> 起因：外部 agent（WorkBuddy 那轮验收）的 shell 只活一轮、`curl` 没超时没重试，
> 一次超时或 `503` 之后**"执行了但回执丢了"和"根本没执行"长得一模一样**，而写操作又没有幂等键。
> 设计说明与"现状摸底"见 [`WORKBUDDY-CHANNEL.md`](WORKBUDDY-CHANNEL.md)。
> 验证状态见 [`VERIFIED.md`](VERIFIED.md)。

#### 3.41.1 `reqId` 怎么给、怎么回

| 位置 | 写法 |
|---|---|
| 请求头（推荐） | `X-Oni-Req-Id: wb-20261119-abc12345` |
| query | `?reqId=wb-20261119-abc12345` |

* 服务端**清洗**：只留 `[A-Za-z0-9._:-]`，最长 64；清洗后为空则当作"没给"。
* 回显：响应体顶层 `reqId`（`/v1/health`、`/v1/meta`、全部 `/v1/*`），响应头 `X-Oni-Req-Id` 也带一份。
* 没给 ⇒ 服务端生成 `srv-<ticks>-<n>`。这种 id 永不重复，**所以永远不会命中回放**（= 0.96.0 的行为）。

#### 3.41.2 回放（幂等）规则

```
同一个 reqId 的 POST / PUT 第二次到达  ⇒  直接返回上次那条响应 + 响应头 X-Oni-Replayed: 1
                                      ⇒  **不再执行一次**
```

* **只有 POST / PUT 回放**（写操作）。GET 不去重（读没有副作用，重复读反而可能是想要的）。
* **`503 RETRY_LATER` 不记账、不回放** —— 它的语义是"动作尚未执行，请重试"，回放会把重试堵死。
* 记忆时点是**先记账、再写 socket**：写 socket 失败正是"动作做了、回执没送到"，那条账就是重试要回放的东西。
* 响应体超过 64 KB 的（本次没有写接口会这么大）只记状态码；这种情况下回放会给
  `REPLAY_BODY_UNAVAILABLE`（`ok:false`），并明确写"这次**没有再次执行**"。

#### 3.41.3 查账

```
GET /v1/journal?reqId=wb-20261119-abc12345      # 单条（含那次响应体）
GET /v1/journal?since=41&limit=50               # 账目列表（不含响应体）
GET /v1/journal                                 # 等价 since=0&limit=20
```

单条（命中）：

```json
{"apiVersion":"1.1","ok":true,"seq":77,"reqId":"wb-20261119-abc12345","data":{
  "reqId":"wb-20261119-abc12345","found":true,
  "entry":{"seq":41,"reqId":"wb-20261119-abc12345","method":"POST","path":"/v1/job",
           "status":200,"ok":true,"at":"2026-11-19T18:20:11+08:00","elapsedMs":12,
           "replays":1,"bodyTruncated":false,
           "body":"{\"apiVersion\":\"1.1\",\"ok\":true,…}"}}}
```

单条（没查到）：

```json
{"reqId":"wb-…","found":false,"note":"本进程内存里没有这条 reqId 的账。可能是：①请求没到达服务端；
 ②服务端回了 503（按约定不记账）；③账已被挤出（容量 128 条）；④mod 或存档重载过。
 **`found:false` 不等于「一定没执行」**，只等于「没有记录」。"}
```

列表：

```json
{"since":41,"capacity":128,"stored":17,"count":17,"entries":[
  {"seq":42,"reqId":"…","method":"GET","path":"/v1/state","status":200,"ok":true,
   "at":"…","elapsedMs":3,"replays":0,"bodyTruncated":false}]}
```

#### 3.41.4 边界（别当保证）

* 账本**只在内存**、环形 **128** 条；**mod 重载 / 存档重载即清空**。
* 不做离线投递、不做队列、不做持久化；`found:false` 只说明"没有记录"。
* 客户端封装：`tools/oni_client.ps1`（reqId + 超时 + 有界重试 + 自动对账 + 退出码 0/1/2/3/4/5），
  退出码语义见 [`WORKBUDDY-CHANNEL.md`](WORKBUDDY-CHANNEL.md) §2.4。

#### 3.41.5 未验证到哪一步（诚实清单）

* **编译 0 error**；`ReqJournal` 的记账/回放/环形淘汰/查账逻辑有**独立单测 13/13**（把 `ReqJournal.cs`
  原文链进一个桩掉 `Snap` 的控制台工程跑，不碰游戏）。
* `tools/oni_client.ps1` 的失败路径实测过：连不上端口 ⇒ 3 次尝试后 `exit 5`（`reconciled:"unknown"`）；
  坏 `Path` / POST 空 body ⇒ `exit 1`。
* ⚠️ **`/v1/journal` 端点与回放本身未装机**：0.96.1 没有真机样本。装机后按
  `WORKBUDDY-CHANNEL.md` §4 的四步验（reqId 回显 → 同 id 重发看到 `X-Oni-Replayed: 1` 且**没有第二次执行**
  → `/v1/journal` 能查到 → 关掉游戏后客户端 `exit 5` 而不是假装成功）。

---

> ### ⚠️ 通用：状态变化**无法归因**（读任何端点前先知道）
>
> **AI 读到的所有状态里都不含"谁改的"这一维** —— 只有"现在是什么样"。
> 任何变化都可能是 **① 玩家手操 ② 其他 mod ③ 自己上一轮的残留 ④ 游戏自然行为**，而端点无法区分。
>
> 记一次真实事故：本 mod 的验证记录里曾把「10 个挖标记被挖穿」写成"殖民地原有标记被挖穿 / 天然验证"，
> 实际上是**主人自己动手挖的**。现场是人造的，归因错了。
>
> **规则**：在把某个变化归因为「系统自然行为」之前，**必须先排除人工操作**。
> 可做的只有：记录变化前后两次读数 + 时间 + 自己做过什么，**别替"谁做的"下结论**。
> 这不是本 mod 的 bug —— 游戏侧本来就没有"操作者"字段，这是信息不存在。
### 3.42 `GET /v1/elements` —— 元素/材料性质（0.97.0）

**是什么**：任意元素的**物理性质字典**。**只给数据，不给推荐**（没有 `recommended` 之类字段）。
数据**运行时**从 `ElementLoader.elements`（`List<Element>`）读，**不在代码里硬编码元素表**，
也不写死 wiki 数字 —— 返回的永远是当前这局游戏的真值。

| 用法 | 说明 |
|---|---|
| `GET /v1/elements?tag=Cuprite` | 单条全字段（≈1.2 KB） |
| `GET /v1/elements?tags=Cuprite,SandStone` | 批量（对比材料用）；`missing[]` 报告查不到的 |
| `GET /v1/elements` | 轻量列表：`tag / name / state / materialCategory / 比热 / 导热`（≈28 KB，212 条） |

**单条字段**（实测 Cuprite）：

```json
{"tag":"Cuprite","name":"铜矿","state":"Solid","dlcId":"","disabled":false,
 "materialCategory":"Metal","oreTags":["Ore","BuildableAny","StartingMetalOre","Solid","Metal"],
 "specificHeatCapacity":0.386,"specificHeatCapacityUnit":"DTU/(g*C)",
 "thermalConductivity":4.5,"thermalConductivityUnit":"DTU/(m*s*C)",
 "molarMass":143.09,"molarMassUnit":"g/mol",
 "lowTempK":null,"lowTempC":null,"lowTempTransitionTarget":null,
 "highTempK":1357,"highTempC":1083.85,"highTempTransitionTarget":"MoltenCopper",
 "maxMass":1840,"maxMassUnit":"kg/cell","strength":0.7,"hardness":25,
 "defaultMass":800,"defaultMassUnit":"kg/cell","defaultTemperatureK":300,"defaultTemperatureC":26.85,
 "lightAbsorptionFactor":1,"radiationAbsorptionFactor":0.56,"radiationPer1000Mass":0,"toxicity":0,
 "attributeModifiers":[{"attributeId":"Decor","value":0.1,"isMultiplier":true,"display":"Decor x1.1"}]}
```

要点：
* **温度双给**：`lowTempK`/`lowTempC`、`highTempK`/`highTempC`、`defaultTemperatureK`/`defaultTemperatureC`。
  内部单位是 K；`0` 或负值表示该方向没有相变 ⇒ 给 `null`。
* `name` 已去掉 Klei 富文本（`<link="CUPRITE">铜矿</link>` → `铜矿`）。
* `attributeModifiers` 是**事实**（这个材料给什么加成），不是推荐：`Decor x1.1` = 装饰 ×1.1。
* `dlcId` 为空串 = 本体内容；DLC 专属元素**不过滤**，靠这个字段判断"你这一档有没有"。
* **相变温度不随气压变化**（压力只影响受压破坏），详见 [`ELEMENTS-DESIGN.md`](ELEMENTS-DESIGN.md) §2。

---

### 3.43 `GET /v1/build-materials?prefabId=Electrolyzer` —— 这个建筑能用哪些材料（0.97.0）

**为什么要有**：`build` 不传 `materials` 时走 `def.DefaultElements()`（该建筑的**默认一项**），
**那个默认项不保证库存里有**。盲测实录：Tile 的默认项是 `Isoresin`（本档 0 kg），
API 返回 `placed=true`，但那栋蓝图**永远建不完**。根因不是 API 坏了，是**调用方不知道还有别的材料可选**。

**候选集怎么来的**：直接调**游戏 UI 同一个方法** `MaterialSelector.GetValidMaterials(类别Tag, false)`，
按 `def.MaterialCategory` 逐个类别取并集 ⇒ 返回的候选与玩家在建造菜单里看到的**完全一致**。
（不要自己按 `Element.materialCategory` 筛：Electrolyzer 自筛只有 13 条，UI 给 17 条，
 多出 `Steel`/`Niobium`/`Iridium`/`TempConductorSolid` 这类 `BuildableAny` 材料；Tile 自筛会漏掉 `Isoresin`。）

**穷举口径**：类别内**全部列出** —— 本档 0 kg 的、未解锁的都列。**只列够的会让调用方连"能换材料"都不知道。**

```json
{"prefabId":"Electrolyzer","name":"电解器","needKg":200,"materialCategories":["Metal"],
 "counts":{"total":17,"enough":1,"short":16,"unlocked":1},
 "candidateSource":"MaterialSelector.GetValidMaterials（与游戏 UI 同一套）",
 "materials":[
   {"tag":"Cuprite","available":{"inStorage":114.904,"onGround":5583.559,"total":5698.463},
    "need":200,"enough":true,"unlocked":true,"props":{ …与 /v1/elements 单条同构… }},
   {"tag":"ZincOre","available":{"inStorage":0,"onGround":0,"total":0},
    "need":200,"enough":false,"unlocked":false,"props":{ … }}
 ]}
```

* **排序**：`enough=true` 在前，组内按 tag 名。排序**不影响"全部列出"**。
* `available.inStorage` = 箱子里的量；`onGround` = 地上散落（小人能捡起来用）；
  `total = 两者之和`，与 `INSUFFICIENT_MATERIAL` 的 `have` 同口径。
* **`discovered`**（0.97.2 由 `unlocked` 改名）= **该资源是否曾进入本档的 `WorldInventory` 账本。**
  `false` = 本档还没进过账本，**不是「没研究解锁」**；研究状态请查 `/v1/research`。
  每个响应都带 `discoveredMeaning` 字段重复这句自解释。

* `inWorld` 是**同一本账的「数量」读数**，与 `discovered`（「存在性」读数）是**同一事件的两条观测**，
  **不是互为因果**。真实结构（这是整件事的核心）：

  ```
  掉落
   └─ WorldInventory 注册 fetchable      ← 中心事件
        ├─ GetAmount(tag) 因此 +N        ← 数量读数（inWorld）
        └─ OnAddedFetchable → Discover   ← 存在性读数（discovered）
  ```

* `inWorldUnavailable=true` **不是读取失败**，是**我们自己**的判定（`Inventory.cs` L122）：
  **账本说 0 但地上有货 ⇒ 不信任这个 0，显示成 `null` 而不是 `0`**。
  这条不是游戏行为，也不是 mod 之外的任何东西。

* **实证（2026-11-20 反向验证；读数与证据等级分开标）**：
  * 【实测读数】挖前 `IgneousRock`：`GetAmount` 返回 **0**（本档显示 `inWorld:null`）、
    `onGround=9406.786`、`discovered=false`；挖穿一格掉 920kg 后，**同一轮**读到：
    `onGround=10326.786`、`inWorld=`**920**、`discovered=`**true**、`inStorage` 仍 **0**。
  * 【IL 可读】`WorldInventory.OnAddedFetchable` 方法体内直接调
    `DiscoveredResources.Discover` + `GetCategoryForEntity`。
  * 【**推断，未抓到实锤**】「**掉落 → `OnAddedFetchable`**」这最后一跳：全程序集**没有直接调用点**
    （token 扫描 0 命中），疑似**事件/委托注册**，扫不到 ⇒ 别当实锤引用。
  * 结论边界：「**入箱不是必要条件**」有实测支撑（`inStorage=0` 时已 `true`）；
    「掉落即注册」是**推断**。

* ⚠️ `onGround` ≠ `inWorld`：同一个 tag 可以「地上 9406 kg、账本 0」——
  **"地图里有"不等于 `discovered:true`**，判据只有"进过账本"。
* 性能：Electrolyzer 17 条 ≈ 16 KB / 6–17 ms。

#### 3.43.1 建造两步走（**新流程，盲测就是漏了第一步**）

```bash
# 1) 先查：这个建筑能用什么材料、哪个够
curl -sS -H "X-Oni-Token: $TOKEN" "http://127.0.0.1:8770/v1/build-materials?prefabId=Tile"
#    在 materials[] 里挑一个 enough=true 的 tag（如 SandStone）

# 2) 再建：把选中的材料传进去
curl -sS -H "X-Oni-Token: $TOKEN" -X POST -H 'Content-Type: application/json' \
     -d '{"type":"build","prefabId":"Tile","x":119,"y":215,"confirm":true,"materials":["SandStone"]}' \
     "http://127.0.0.1:8770/v1/job"
```

⚠️ **不传 `materials` ⇒ 用 `DefaultElements()` 的默认项，可能本档 0 kg**（Tile→Isoresin、Electrolyzer→ZincOre），
而 API 仍返回 `placed=true`（材料不足不阻塞蓝图是 M6.56 的设计）。**判断建得成不成，看 `enough`，不看 `placed`。**

#### 3.43.2 `build` 响应里的 `candidates`（0.97.1）

**信息要放在必经之路上**：调用方没查 `/v1/build-materials` 也可能直接 `build`，所以下面两种情况下
**`build` 的响应里自动带一份轻量候选清单**：

1. **没传 `materials`**（走 `DefaultElements()` 默认项 —— 最容易翻车的场景）
2. **传了但 `enough=false`**（选的材料本档不够）

传了足够的材料时**不附**（响应保持轻量，Tile 约 1.9 KB vs 带清单约 3.7 KB）。

```json
{"placed":true,"materialsUsed":"Isoresin",
 "materials":{"need":[{"category":"BuildableRaw","kg":200}],"enough":false,...},
 "candidates":[{"tag":"Obsidian","available":{"inStorage":400,"onGround":0,"total":400},"enough":true,"unlocked":false},
               {"tag":"SandStone","available":{"inStorage":9156.41,"onGround":25869.171,"total":35025.582},"enough":true,"unlocked":true},
               {"tag":"Isoresin","available":{"inStorage":0,"onGround":0,"total":0},"enough":false,"unlocked":false},
               …],
 "candidatesMeta":{"total":16,"enough":4,
   "reason":"没传 materials，用的是 DefaultElements() 默认项；以下是这个建筑的全部合法材料",
   "note":"备选清单，不是推荐；排序为够的在前。unlocked 来自 DiscoveredResources.IsDiscovered，实测存疑（有库存也可能为 false），别当硬判据"}}
```

* 轻量：只有 `tag` / `available` / `enough` / `unlocked`，**不带 `props`** —— 要性质再查 `/v1/elements`
* 顺序与 `/v1/build-materials` 一致（够的在前）
* **不是推荐结论**，没有 `recommended`；未解锁的、0kg 的照样列
* 只在这个响应里加字段，`build` 其余行为（`placed`、不阻塞蓝图）**没动**
