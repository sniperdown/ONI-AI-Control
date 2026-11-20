# OniAiBridge — AI 操作手册

> 读者是**一个没有任何上下文的 AI**。你不需要先读别的文档，也不需要有人给你 token。
> 版本：0.96.1 / M7.4（API `apiVersion: "1.1"`）。人看的接口细节在 [`API.md`](API.md)，本文件是**动手版**。

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

0. **建电网正确的是 `build` + `wirePath`（0.99.x 起，实测能通电）**
   ```json
   {"type":"build","prefabId":"Wire","wirePath":[[112,204],[113,204],...,[117,204]],"confirm":true,"materials":["Cuprite"]}
   ```
   * **一次请求给整条路径**：逐格建线，**建完立刻按游戏原配方双向连线**
     （`DirectionFromToCell` → 双向 `CanAddConnection` → 双向 `AddConnection`；
     出处 `UtilityBuildTool.ApplyPathToConduitSystem`，IL 已核）。
   * 返回里看 `pathDiag`（每对 `CONNECTED(dir/inv)` 或 `NO_MGR`/`CANNOT_ADD(guard)`）。
   * **单格 build 不连线**（`mask=0` 孤岛，与游戏一致）；要连就必须给 ≥2 格的 `wirePath`。
   * 建成后可用 `/v1/wire` 读掩码与 `networkId` 复核；**所有格子 `networkId` 相同 = 一条网**。
   * ⚠️ 建线是蓝图，要等小人建完（几秒到一分多钟）才会进网络；**蓝图阶段 `networkId` 是 -1，别误判为失败**。
   * ⚠️ 目前**不会连到路径外的既有电网**（游戏拖拽有这一步，我们还没抄）；从零布线够用。
0. **⚠️ `operational=true` 不代表通电！**（最容易骗到结论的一条）
   `operational` 是 `Operational.IsOperational` = "**建筑完好、在运转**"，**缺电时照样是 true**。
   判断"电有没有通"只能看：**电池 `joules` 是否在涨**（`/v1/buildings` 的 `joules` / `joulesCapacity`）、
   或用电器是否真的在工作。**别拿 `operational` 当供电判据** —— 它比 `placed:true` 更危险，
   `placed` 至少还能看见建筑，`operational` 直接骗到结论层。
1. **token 是全量校验**：即使从 `127.0.0.1` 发请求，不带 `X-Oni-Token` 也是 `401`。
2. **键名是小写 `"token"`**，不是 `"Token"`；按大写找会拿到空串然后一路 401。
3. **写操作先 `dryRun`（但只对部分接口有效）**：`dryRun` **只在**打标记批次（`/v1/job` 的
   `disinfect`/`empty_pipe`/`attack`/`capture`/`mop`）、`/v1/command {"op":"pipeLink"}`、`/v1/ui/click` 上生效。
   **`dig` / `deconstruct` / `harvest` / `sweep` / `build` 不认 `dryRun`** —— 单格 dig 带了 `dryRun:true` 也会
   **真的**打出挖掘标记（【实测 2026-11-20】10 格 dryRun ⇒ 地图上真的多了 10 个 `DigPlacer`，N 从 13 变 23），
   区域 dig 则要求 `confirm:true`。别把 `dryRun` 当成通用保险。
4. **建造必须两步走（0.97.0 起）**：先 `GET /v1/build-materials?prefabId=X` 看候选材料 + `enough`，
   再 `POST /v1/job type=build` **必须带 `"materials":["<选中的 tag>"]`**。
   不传就用 `DefaultElements()` 默认项，**可能本档 0kg**（Tile→Isoresin、Electrolyzer→ZincOre），
   而 API 仍返回 `placed=true` —— 那栋蓝图会**永远建不完**。判据看 `enough`，不看 `placed`。
   **兜底（0.97.1）**：万一手快没查就 `build`，响应在「没传 materials」或「传了但不够」时会自带
   `candidates[]`（`tag` / `available` / `enough` / `discovered`，够的在前）—— 那就是给你换材料的清单；
   带了足够材料时**不附**。
5. **`discovered` 的语义**：= **该资源是否曾进入本档的 `WorldInventory` 账本**（不是"地图里有"，
   也不是"研究解锁"）。**入箱不是必要条件**（实测 `inStorage=0` 时已 `true`）。
   `inWorld` 是同一本账的「数量」读数，与它是**同一事件的两条观测，不是互为因果**；
   `inWorldUnavailable=true` 是**我们自己**的判定（账本说 0 但地上有货 ⇒ 不显示 0 而显示 null），
   不是读取失败。
6. **查材料性质用 `/v1/elements`**：温度给 K 和 °C 双份（`highTempK` / `highTempC`），
   `attributeModifiers` 是"这个材料给什么加成"的事实（`Decor x1.1`），**不是推荐**。
7. **「条件不满足但可以提前规划」的操作，一律不拒、只标**（0.97.3 起，硬规矩）：
   游戏本身允许"先画草图后补条件"（实心岩石上放蓝图、挖通再建），**拒了 AI 就没法写计划**。
   所以这类操作**照常执行**，但在响应里把"现在能不能成"和"缺什么"标清楚：
   * `build` 位置建不成 ⇒ `placed:true` + **`buildable:false`** + `placementMissing`
     （`ground_support` 缺地面 / `solid_cells` 有实心方块要挖开 / `unknown`）+ `buildBlockedReason`
   * `build` 材料不足 ⇒ 同样只报 `materials.enough:false`，不拦（M6.56 定过）
   * `candidates[]` 里的 `enough` / `discovered` 都只是标注，不是拒绝理由
   **判断"做成了没有"一律看回报字段（buildable / enough / chore / 回读），不要看 HTTP 200。**
8. **证据要分等级标**：「**实测读数**」（真机读到数字）/「**IL 可读**」（反编译能看到的方法体）/
   「**推断**」（没抓到直接证据的猜想）三种必须分开写。**别把推断写成实锤** ——
   曾经把 `inStorage` 变化当成 `discovered` 翻真的原因，就是因为没分等级。
9. **状态变化无法归因**：你读到的状态里**没有"谁改的"** —— 玩家手操 / 别的 mod / 你上一轮的残留 /
   游戏自然行为，端点一律区分不了。**把变化说成"系统自然发生的"之前，先排除人工操作**；
   能写的只有"变化前后两次读数 + 时间 + 我做了什么"。这是信息不存在，不是 bug。

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

## 2. 端点总表（35 条，全部实装）

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
| GET | `/v1/journal` | **请求对账**（0.96.1） | `?reqId=` 查单条（含那次响应体）/ `?since=&limit=` 列账目；见 §4.9 |

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
| GET | `/v1/buildings` | 建筑清单 | 参数 `offset`/`limit`/`prefabId`/`names=1`/`x0,y0,x1,y1`；**分页**看 `total`+`hasMore`+`nextOffset`；每条含 `markedForDeconstruct`、`underConstruction`、`operational`（`name` 需 `?names=1`） |
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
| GET | `/v1/materials` | 材料清单（**箱子里的量**） | 看库存 |
| GET | `/v1/elements?tag=Cuprite` | **元素性质字典**（比热/导热/相变/硬度/强度/加成…） | 查任意材料性质 |
| GET | `/v1/build-materials?prefabId=Tile` | **这个建筑能用哪些材料** + 本档实况（穷举，含 0kg） | **建东西前必查** |
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
* `/v1/buildings` 有 `limit`（默认 500、上限 2000）。**要翻完整张图**（实测大地图 72851 条）：
  从 `offset=0` 开始，每次请求后看 `hasMore`，为 `true` 就把 `offset` 换成 `nextOffset` 再来一次，
  `total` 是第一页就该记下的**总条数**（`ceil(total/limit)` 页）。`offset ≥ total` ⇒ `count:0` 且不报错。
  每条**默认不带 `name`**（省体积；要逐条中文名加 `&names=1`）；要不要翻完，先看第一页的 `total` 再决定。
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

### 4.9 请求对账：`reqId` + `/v1/journal`（0.96.1）

你的 shell 如果**一次只活一条命令**（超时就得重来），写操作就带上 `X-Oni-Req-Id` —— 这样**不会把活干两遍**：

```bash
REQ="wb-$(date +%Y%m%d%H%M%S)-$RANDOM"
curl -sS --max-time 8 --retry 2 --retry-delay 1 \
     -H "X-Oni-Token: $TOKEN" -H "X-Oni-Req-Id: $REQ" \
     -X POST -H 'Content-Type: application/json' \
     -d '{"type":"mop","x":111,"y":220,"dryRun":true}' \
     "http://127.0.0.1:8770/v1/job"
# 注意：这里示例故意用 mop（支持 dryRun）。dig **不支持** dryRun —— 带 dryRun 也会真打标记。
# 没拿到响应 ⇒ 先查账，别急着重发：
curl -sS -H "X-Oni-Token: $TOKEN" "http://127.0.0.1:8770/v1/journal?reqId=$REQ"
```

三条规则：
1. **同一个 `reqId` 的 POST/PUT 重发 = 回放上次响应**（响应头 `X-Oni-Replayed: 1`），**不会执行第二次**；
   `503 RETRY_LATER` 例外 —— 它意味着"动作尚未执行"，重试是安全的、也是应该的。
2. 响应体顶层有 `reqId` 字段，回执、日志、账本三处能对上号。
3. `found:false` 只说明"没有记录"（账本只在内存、留 128 条、mod 重载即清空），**不等于一定没执行**。

Windows 上更省事的做法：`pwsh -File tools/oni_client.ps1 -Method POST -Path '/v1/job' -BodyFile <json>`
（自动 reqId + 有界重试 + 自动对账 + 明确的退出码 0/2/3/4/5）。


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
* 端点集合当前 **35 条**（`grep 'case "/v1/' src/OniAiBridge/Bridge.cs` 可核对）。
* 每次发布都升版本（`/v1/meta` 的 `mod.version` / `mod.stage` 可直接读）。
* **验证状态**（哪些真机验过、哪些只有编译）见 [`VERIFIED.md`](VERIFIED.md)。**没有证据的项，不要当成保证。**
* 装机后先跑 `pwsh -File tools/smoke.ps1`（23 条只读自检，`grep "Check '" tools/smoke.ps1` 可核对）；改版本用 `tools/bump-version.ps1`（一次改三处）。

---

## 7. 还没做 / 已知缺口（诚实清单）

* `/v1/save/load` 永久禁用（见 §4.3）。
* `preview.png` **还缺**（Workshop 列表缩略图，发布前要补一张）。
* `disconnect`（拆断管道/电线）没有独立端点，只有 `pipeLink` 的单方向能力。
* `/v1/inventory` 的 `inWorld` 依赖 `WorldInventory.GetAmount`，在部分存档上可能给 `null`（如实标注，不猜）。
* `blockingDialog` 的 `present:true` 分支**尚未拿到真机样本**（白名单逻辑已上线，见 §4.6）。
