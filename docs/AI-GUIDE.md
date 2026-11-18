# OniAiBridge — AI 操作手册

> 读者是**一个没有任何上下文的 AI**。你不需要先读别的文档，也不需要有人给你 token。
> 版本：0.95.1 / M7.3（API `apiVersion: "1.1"`）。人看的接口细节在 [`API.md`](API.md)，本文件是**动手版**。

---

## 0. 给 AI 的话（先读这 20 行）

你在玩《缺氧》(Oxygen Not Included)。这个 mod 把**正在运行的那局殖民地**暴露成本机 HTTP API，
你可以读状态、也能像玩家一样操作（挖、建、打标记、点界面、截图）。

三个必知事实：

| 项 | 值 |
|---|---|
| 地址 | `http://127.0.0.1:8770`（只监听本机回环） |
| 认证 | 每个请求都要带请求头 `X-Oni-Token: <token>` |
| token 在哪 | `%USERPROFILE%\Documents\Klei\OxygenNotIncluded\mods\config\OniAiBridge\settings.json` 里的 **小写键 `"token"`** |

读 token（PowerShell，无需人工）：

```powershell
$settings = "$env:USERPROFILE\Documents\Klei\OxygenNotIncluded\mods\config\OniAiBridge\settings.json"
$token = (Get-Content $settings -Raw | ConvertFrom-Json).token      # 32 位十六进制
```

一条**能直接复制就跑**的自检（bash / Git Bash；把 `$ONI_TOKEN` 换成上面读到的值）：

```bash
TOKEN=$(powershell -NoProfile -Command "(Get-Content \"$env:USERPROFILE\Documents\Klei\OxygenNotIncluded\mods\config\OniAiBridge\settings.json\" -Raw | ConvertFrom-Json).token")
curl -s -H "X-Oni-Token: $TOKEN" http://127.0.0.1:8770/v1/meta
```

看到 `"ok":true` 且 `data.mod.version` 有值 ⇒ 通了。然后直接跳到 §3 工作流。

**三个最容易踩的坑，现在就记住**：

1. **token 是全量校验**：即使从 `127.0.0.1` 发请求，不带 `X-Oni-Token` 也是 `401`。
2. **键名是小写 `"token"`**，不是 `"Token"`；按大写找会拿到空串然后一路 401。
3. **写操作先 `dryRun`**：所有会改状态的接口都有 `dryRun`/`validateOnly` 分支，先看再动。

---

## 1. 连通性自检与失败对照

```bash
curl -s -H "X-Oni-Token: $TOKEN" http://127.0.0.1:8770/v1/health
```

| 你看到的 | 含义 | 怎么办 |
|---|---|---|
| 连不上 / connection refused | 游戏没开，或 mod 没加载 | 需要人启动游戏并进档；mod 只有在游戏运行时才监听 |
| `401 UNAUTHORIZED` | token 错/没带/键名找错 | 重读 `settings.json` 的小写 `token` |
| `503 NOT_READY` + `pumpReady:false` | 游戏主线程泵还没起来（加载中） | 等几秒重试 |
| `{"ok":true,...}` | 通了 | 继续 |

响应外壳（所有端点一致）：

```json
{"apiVersion":"1.1","ok":true,"seq":42,"serverTime":"2026-11-19T01:00:00+08:00","data":{...},"error":null}
```

失败时 `ok:false`、`data:null`、`error:{"code":"...","message":"...","details":...}`。

---

## 2. 端点总表（34 条，全部实装）

> `GET` 端点都是只读；`POST` 会改状态（除 `dryRun` 分支）。所有请求都要 `X-Oni-Token`。

### 2.1 元信息 / 健康

| 方法 | 路径 | 用途 | 关键点 |
|---|---|---|---|
| GET | `/v1/meta` | **先打这个**：版本、游戏 build、`colonyLoaded`、闸门状态、能力清单 | 含 `blockingDialog`（见 §4.6）、`capabilities`、`mapLimits` |
| GET | `/v1/health` | 最小存活检查 | 比 meta 轻 |
| GET | `/v1/settings` | 读设置 | **只回 `tokenLength`，不回 token 内容**；token 不能通过接口改 |
| PUT | `/v1/settings` | 改设置 | 会回读校验，写盘失败会报错 |
| GET | `/v1/commands` | 列出所有 `/v1/command` 的 op 及**当前可用性** | 作弊类 op 会带 `available`/`unavailableCode` |
| GET | `/v1/events` | SSE 事件流 | 长连接；`Last-Event-ID` 续传 |

### 2.2 世界与殖民地状态

| 方法 | 路径 | 用途 | 关键点 |
|---|---|---|---|
| GET | `/v1/state` | 周期、暂停、倍速、复制人概要、世界 | 最省 token 的"现在怎么样" |
| GET | `/v1/worlds` | 世界表（8~15 个）、尺寸、`isActive`、可读名 | `displayName` 可为 null |
| GET | `/v1/area?worldId=&cx=&cy=` | 一个 32×32 区块的**数字档**（可挖/不可挖/中子素…） | 判据与 `dig` 同源 |
| GET | `/v1/map?bbox=x0,y0,x1,y1&stride=N` | 地图降采样：元素/温度/质量三通道 | **默认关**（`allowMap:false`）；单次上限 64×64=4096 格；缺 bbox/stride 直接 400 |
| GET | `/v1/env` | 区域环境（元素质量汇总） | `?maxCells=` 可调扫描上限 |
| GET | `/v1/geysers` | 间歇泉清单 | 已过滤喷发器模板 |
| GET | `/v1/saves` | 存档列表 | **本地 + 云存档都扫**；每条带 `source`(local/cloud) |
| POST | `/v1/save/load` | 加载存档 | **永久禁用，恒 501**（直调 `SaveLoader` 会毁档） |

### 2.3 实体

| 方法 | 路径 | 用途 | 关键点 |
|---|---|---|---|
| GET | `/v1/buildings` | 建筑清单 | 参数 `offset`/`limit`/`prefabId`/`x0,y0,x1,y1`；每条含 `markedForDeconstruct`、`underConstruction`、`operational` |
| GET | `/v1/dupes` | 复制人清单 | 含 `chore{text,id}` + **`choreTarget`**（作业目标，不是导航终点）、`path`、`stuckProbe` |
| GET | `/v1/alerts` | 告警（按严重度） | `?detail=1` 给明细；**200 KB 硬上限**，超了给 `alerts:null,truncated:true` |
| GET | `/v1/inventory?tag=<元素>` | 某元素的库存/地面/全图量 | **必须给 `tag`**（如 `?tag=SandStone`），否则 400 |
| POST | `/v1/building/select` | 选中某格建筑（弹出它的菜单） | body `{"x":..,"y":..}`；之后用 `/v1/ui/click` 点菜单项 |
| POST | `/v1/door` | 开/关门 | 需 `x`/`y` 与动作 |

### 2.4 建造与研究

| 方法 | 路径 | 用途 | 关键点 |
|---|---|---|---|
| GET | `/v1/catalog` | 建筑目录（prefabId、尺寸、材料） | 找 `prefabId` 用这个 |
| GET | `/v1/build-menu` | 当前建造菜单可见项 | 界面态 |
| GET | `/v1/materials` | 材料清单 | 建造选材 |
| GET | `/v1/research` | 科技树状态 | |
| POST | `/v1/job {"type":"build",...}` | 建造 | 玩家等价路径；`confirm:true` 才动手 |
| POST | `/v1/job {"type":"research",...}` | 设研究方向 / 直接完成（后者作弊） | |

### 2.5 作业（挖掘 / 拆解 / 收割 / 清扫 / 打标记）

| 方法 | 路径 | 用途 | 关键点 |
|---|---|---|---|
| POST | `/v1/job` | 下发作业 | `type` 支持 `dig` `deconstruct` `harvest` `sweep` `build` `research` + 五类标记 `disinfect` `empty_pipe` `attack` `capture` `mop` |
| GET | `/v1/marks` | 单格标记查询 | 与 `/v1/job` 同源同字段 |
| GET | `/v1/tools` | 可用工具/参数 | 调试用 |

`dig` 用 `{"type":"dig","x0":..,"y0":..,"x1":..,"y1":..}`；单格给 `x`/`y`。
**打标记一律先 `"dryRun":true`**，看 `list[]` 里会标到谁，再 `"confirm":true`。
`attack` 会真的伤害小动物。

### 2.6 管线 / 电线

| 方法 | 路径 | 用途 | 关键点 |
|---|---|---|---|
| POST | `/v1/command {"op":"pipeLink",...}` | 连/断一格管线或电线（精确单方向） | 见 §3.6 |

### 2.7 界面（UI）

| 方法 | 路径 | 用途 | 关键点 |
|---|---|---|---|
| GET | `/v1/ui` | 列出**当前激活**的界面、按钮、可点击元素 | `clickables` 默认 `compact`（每条 4 字段）；`?clickables=full` 要全字段；每屏默认最多 2000 条 |
| GET | `/v1/ui/widgets` | 更细的控件树 | 调试用 |
| POST | `/v1/ui/click` | 点一个控件 | 定位三选一：`text`（按钮文字）/ `path` / `name`+`exitsting`；会改界面或状态 ⇒ 需要 `confirm:true`，dryRun 用 `"dryRun":true` |

### 2.8 截图

| 方法 | 路径 | 用途 | 关键点 |
|---|---|---|---|
| GET | `/v1/shot` | 截一张游戏画面 PNG | `?w=&h=` 尺寸；`?centerX=&centerY=` 指定世界格坐标（相机临时移过去再还原）；`?cx=&cy=&cw=&ch=` 是**裁剪**（屏幕像素，和 centerX/Y 不是一回事） |
| GET | `/v1/shot/cameras` | 可选相机清单 | `pickedForShot` / `chosenForShot` |

### 2.9 命令（通用操作入口）

| 方法 | 路径 | 用途 | 关键点 |
|---|---|---|---|
| POST | `/v1/command` | `pause` `resume` `setSpeed` `setChorePriority` `pipeLink` + 作弊类（`spawnElement` `teleport` `setInstantBuild` `setInvincible` `revealFog` `debug`） | 作弊类**默认不可用**，见 §4.2 |

### 2.10 诊断（可不用）

`GET /v1/debug/mods`（mod 清单）、`GET /v1/debug/vision-sample`（视野采样）。

---

## 3. 常用工作流（照着做）

### 3.1 三十秒看懂殖民地

```bash
curl -s -H "X-Oni-Token: $TOKEN" http://127.0.0.1:8770/v1/meta
curl -s -H "X-Oni-Token: $TOKEN" http://127.0.0.1:8770/v1/state
curl -s -H "X-Oni-Token: $TOKEN" http://127.0.0.1:8770/v1/dupes
curl -s -H "X-Oni-Token: $TOKEN" http://127.0.0.1:8770/v1/alerts
```
判读：`meta.colonyLoaded=false` ⇒ 还在主菜单（没有殖民地可操作，先加载存档——但 `/v1/save/load` 已禁用，只能人工或走 UI）。

### 3.2 看建筑 / 找待拆的

```bash
curl -s -H "X-Oni-Token: $TOKEN" "http://127.0.0.1:8770/v1/buildings?prefabId=Tile&limit=50"
```
每条含 `markedForDeconstruct`。**标记拆解**：`POST /v1/building/select {"x","y"}` → `GET /v1/ui` 找 `拆解Button` → `POST /v1/ui/click {"path":"UserMenuScreen/Buttons/拆解Button","confirm":true}`。

### 3.3 挖一块区域

```bash
curl -s -X POST -H "X-Oni-Token: $TOKEN" -H 'Content-Type: application/json' \
  -d '{"type":"dig","x0":100,"y0":210,"x1":110,"y1":220}' \
  http://127.0.0.1:8770/v1/job
```
先 `GET /v1/area?worldId=0&cx=..&cy=..` 看这块能不能挖（`mineable`）。

### 3.4 打标记（以拖地为例）

```bash
# 1) 先看会标到谁
-d '{"type":"mop","x":117,"y":217,"dryRun":true}'
# 2) 确认后真做
-d '{"type":"mop","x":117,"y":217,"confirm":true}'
```
第二次 `dryRun` 同格应给 `alreadyMarked:1 / wouldMark:0`（幂等）。执行信号看 `/v1/dupes`：
接活的小人 `chore.id` 变成 `Mop`，或 `mop` 响应里 `list[].mopMinionChores > 0`。

### 3.5 建一栋

`GET /v1/catalog` 找 prefabId 与尺寸 → `POST /v1/job {"type":"build","prefabId":"...","x":..,"y":..,"validateOnly":true}` 校验 → 去掉 `validateOnly`、加 `"confirm":true` 真建。
材料不足**不阻塞蓝图**，`materials{need,inStorage,enough}` 只报数。

### 3.6 连/断一根管线或电线

```bash
# 连：把 (117,217) 这一格的 right 方向接上
-d '{"op":"pipeLink","x":117,"y":217,"dir":"right","connect":true}'
# 断
-d '{"op":"pipeLink","x":117,"y":217,"dir":"right","connect":false}'
# 纯读（动作后隔 1s/3s 复读，确认没被重建改掉）
-d '{"op":"pipeLink","x":117,"y":217,"dir":"right","connect":false,"dryRun":true}'
```
判据：`after.getter == expectedGetter`（`IUtilityNetworkMgr.GetConnections`，游戏认账那条）。
`applied:false` 就是没成功；`inconsistent:true` 表示数组与 getter 不一致（辅证冲突）。
只动**单方向**；要求邻格**同层且同 prefabId**（跨层/细线接粗线会被拒）。

### 3.7 点界面

```bash
curl -s -H "X-Oni-Token: $TOKEN" "http://127.0.0.1:8770/v1/ui?clickablesLimit=50"
curl -s -X POST -H "X-Oni-Token: $TOKEN" -H 'Content-Type: application/json' \
  -d '{"text":"继续游戏","confirm":true}' http://127.0.0.1:8770/v1/ui/click
```
⚠️ 这是**直接调控件的点击回调**，不走系统鼠标。**`clicked:true` 不等于界面真的动了** ——
实测点 LoadScreen 的殖民地行返回成功但没跳转，真实鼠标才跳。判定要看后续 `GET /v1/ui` 的 `screens`。

### 3.8 截一张"指定位置"的图

```bash
curl -s -H "X-Oni-Token: $TOKEN" "http://127.0.0.1:8770/v1/shot?w=1200&h=675&centerX=117&centerY=217"
```
响应给 `path`（PNG 绝对路径）、`center{cameraMoved,cameraRestored}`、`viewWorld{minX,minY,maxX,maxY,perPixelX,perPixelY}`
（后者用来在像素↔格子之间换算）。`cameraRestored:true` 才说明相机已还原。

### 3.9 读地图格子

```bash
curl -s -H "X-Oni-Token: $TOKEN" "http://127.0.0.1:8770/v1/map?bbox=110,220,118,224&stride=1"
```
需要先在 `settings.json` 里 `"allowMap": true`（默认 false）。三通道 `element`/`temperature`/`mass`，行主序。

### 3.10 查资源

```bash
curl -s -H "X-Oni-Token: $TOKEN" "http://127.0.0.1:8770/v1/inventory?tag=SandStone"
```
给 `inStorage`（建造只认这个）/ `onGround` / `inWorld`。

---

## 4. 坑与硬限制（必读）

### 4.1 token 与认证
* **全量校验**：本机也算，缺头就 401。
* token 是首次运行生成的 GUID，存在 `mods\config\OniAiBridge\settings.json`，键名小写 `"token"`。
* `/v1/settings` 只回长度；**token 不能通过接口修改或读出**。
* 浏览器打开 `http://127.0.0.1:8770/` 有 WebUI（服务端注入 token，不需要你手动填）。

### 4.2 作弊类 op 默认关闭（三段式闸门）
默认 `settings.allowCheats` 为真，但还要 `DebugHandler` 被打开才算"解锁"。未解锁时 `spawnElement` / `teleport` /
`setInstantBuild` / `revealFog` 会返回 `SANDBOX_OR_DEBUG_REQUIRED`。
打开方式（会改变游戏运行状态、并让该存档**永久无法解锁成就**，只应在测试存档上用）：

```bash
-d '{"op":"debug","enable":true}'     # 走 /v1/command
```
打开后 `/v1/commands` 里这些 op 的 `available` 变 true。

### 4.3 `/v1/save/load` 永久 501
直调 `SaveLoader.Load` 会**先清场景**，失败不回滚，实测会毁掉当前殖民地 ⇒ 硬禁用。要换存档只能人工/走主菜单 UI。

### 4.4 `/v1/map` 默认关
`settings.allowMap=false` 时返回 `403 MAP_DISABLED`。开启后单次上限 64×64 格（4096），`bbox`/`stride` 必须同时给。

### 4.5 响应体积
* `/v1/ui` 的 `clickables` 默认 `compact` 且每屏最多 2000 条（`?clickablesLimit=N` 可调，上限 20000）。
* `/v1/buildings` 有 `limit`（默认值见 API.md），超出用 `truncated:true` + `notEmittedDueToLimit` 表达。
* `/v1/alerts` 有 **200 KB 硬上限**，超了给 `alerts:null` + `truncated:true`，请改用 `?detail=0` 或缩小范围。

### 4.6 `blockingDialog`：只报告，不自动点掉
`/v1/meta` 的 `blockingDialog` 用**白名单**判定（`ConfirmDialogScreen` / `BarterConfirmationScreen` /
`GameOverScreen` / `VictoryScreen` / `DLCBetaMessageScreen` / `OldVersionMessageScreen` / `DemoOverScreen` / `PatchNotesScreen`）。
`present:true` ⇒ 有确认/终止类模态框挡着，**此时不要假设可以操作世界**，自己决定要不要点掉
（`POST /v1/ui/click {"text":"好的","screen":"ConfirmDialogScreen"}`）。
`ResearchScreen` 这类常驻面板**不会**被报（0.95.0 曾误报，已修）。

### 4.7 其它已知不可靠/未实现
* `dupes[].stuckProbe.possible` 从未在真机上触发过 true —— **不要拿它当"卡住"的结论**。
* `IDisconnectable.IsDisconnected()` 恒 false，**不要用**（`pipeLink` 已不再暴露它）。
* 双向冗余**不成立**：连一格不会自动在邻格置反方向位，需要按方向分别操作。
* `/v1/ui/click` 有安全名单：含"退出/quit/exit"或存档迁移的按钮**拒绝点击**。
* `deconstruct` 必须点名目标（歧义会 `409 AMBIGUOUS_TARGET`）。

### 4.8 写操作纪律
1. 先 `dryRun` / `validateOnly`；2. 看 `list[]` 是否点对了目标；3. 再 `confirm:true`；
4. 用**不依赖自身代码的锚**核实（回读 `/v1/buildings`、`/v1/dupes` 的 `chore`、再 `dryRun` 看 `alreadyMarked`）。

---

## 5. 错误码

| HTTP | code | 含义 |
|---|---|---|
| 400 | `BAD_REQUEST` / `INVALID_ARG` | 参数缺失/非法 |
| 400 | `CONFIRM_REQUIRED` | 改状态的请求没带 `confirm:true` |
| 400 | `NEIGHBOUR_NOT_CONDUIT` / `PREFAB_MISMATCH` | 管道连接的两格不是同类 |
| 400 | `BBOX_TOO_LARGE` | `/v1/map` 超过 64×64 |
| 401 | `UNAUTHORIZED` | token 缺失/错误 |
| 403 | `MAP_DISABLED` / `DISABLED` | 该能力在 settings 里关着 |
| 404 | `NOT_FOUND` / `NOT_FOUND_TARGET` | 路径或目标不存在 |
| 405 | `METHOD_NOT_ALLOWED` | 方法用错（每个端点只接受一种） |
| 409 | `NOT_APPLIED` | 动作下发但游戏侧状态没变成预期 |
| 500 | `*_UNAVAILABLE` / `INTERNAL` | 采集/执行异常 |
| 501 | `LOAD_PATH_UNSAFE` | `/v1/save/load` 永久禁用 |
| 503 | `NOT_READY` / `RETRY_LATER` | 主线程泵未就绪 / 太忙，稍后重试 |

---

## 6. 版本与兼容

* `apiVersion` 目前在 `"1.1"`；新增字段是**加法**，不删旧字段。
* 端点集合当前 **34 条**（`grep 'case "/v1/' src/OniAiBridge/Bridge.cs` 可核对）。
* 每次发布都升版本（`/v1/meta` 的 `mod.version` / `mod.stage` 可直接读）。

---

## 7. 还没做 / 已知缺口（诚实清单）

* `/v1/save/load` 永久禁用（见 §4.3）。
* `preview.png` **还缺**（Workshop 列表缩略图，发布前要补一张）。
* `disconnect`（拆断管道/电线）没有独立端点，只有 `pipeLink` 的单方向能力。
* `/v1/inventory` 的 `inWorld` 依赖 `WorldInventory.GetAmount`，在部分存档上可能给 `null`（如实标注，不猜）。
* `blockingDialog` 的 `present:true` 分支**尚未拿到真机样本**（白名单逻辑已上线，见 §4.6）。
