# 验证状态（唯一出处）

> 整理：2026-11-19，lead。依据 `CHANGELOG.md`、`shared/handoff-oni-mod.md`（0.93.6 时整理）、`docs/AI-GUIDE.md`。
> **本文件是"某个东西到底验没验过"的唯一出处。** CHANGELOG 只写"改了什么、为什么"，不再写"验证到哪一步"。
> 版本以 [`../mod/mod_info.yaml`](../mod/mod_info.yaml) 为准（`build.ps1` 会校验三处一致）。

## 0. 读法与规则

| 标记 | 含义 |
|---|---|
| ✅ | **有书面证据**表明它在真机上返回过预期结果。证据列必须能核对（版本 + 数字/字段值） |
| ⚠️ | **没有运行期证据**，或只有旧版本证据 / 只有编译通过 / 只有源码核对。**⚠️ ≠ 坏**，只表示"别拿它当保证" |
| ⛔ | 设计上禁用（不是没做，是故意不给） |

三条硬规则：

1. **没有证据不许写 ✅。** 想改 ✅ 必须同时写清：哪个版本、谁验的、看到的关键数字。
2. **子会话只能写"我这边有没有运行期样本"，不能替根会话写"已验证"。** 装机、实测由根会话（lead）做。
3. **改本文件不需要升 mod 版本**（它不进 dll）；改了 dll 的任何字节才升版本。

---

## 1. 端点矩阵（34 条，全部实装）

### 1.1 元信息 / 健康

| 端点 | 状态 | 证据 / 备注 |
|---|---|---|
| `GET /v1/health` | ✅ | 间接：所有真机验收的第一步；若它坏，后面所有记录都不成立 |
| `GET /v1/meta` | ✅ | 0.95.1 实测 `blockingDialog{present:false,count:0}`；0.93.6 真机验收（0.93.8 自证） |
| `GET /v1/settings` | ⚠️ | 0.29.0 验的是**游戏内设置页**，不是这个端点；未见 `/v1/settings` 的运行期记录 |
| `GET /v1/commands` | ⚠️ | 0.6.0 修过"作弊命令没进列表"（文本替换静默失败），修后无真机记录 |
| `GET /v1/events` | ⚠️ | SSE；0.3.0 起就标"未验证"，之后无记录 |
| `GET /v1/journal` | ⚠️ | 0.96.1 新增（请求对账）；**无运行期样本**，只有编译 + `ReqJournal` 独立单测 13/13。见 §2 与 `docs/WORKBUDDY-CHANNEL.md` §4 |

### 1.2 世界与殖民地

| 端点 | 状态 | 证据 / 备注 |
|---|---|---|
| `GET /v1/state` | ✅ | 0.90.0 用现网数据核 `kind=BionicOil`（6 人）；0.92.4 真机（Cycle 5） |
| `GET /v1/worlds` | ✅ | 0.89.0 / handoff §3：15 个世界、w0 `240×380`、7 个 `32×32` 标 `isStub`、`activeWorldId=7` |
| `GET /v1/area` | ✅ | 0.89.2 / handoff §3：`mineable=419 / unmineable=6 / neutronium=281 / solid=425`，等式成立 |
| `GET /v1/map` | ✅ | M6.89 实测：`bbox=110,220,118,224&stride=1` ⇒ 45 格，氧气 1.2~1.4 kg，温度 295.1~296.1 K |
| `GET /v1/env` | ⚠️ | 无运行期记录；口径差异见 API.md（只算气体格 + 迷雾门禁） |
| `GET /v1/geysers` | ✅ | 0.88.2/0.88.3 实测：72 条里 31 条模板 ⇒ 41 条真泉；`includeInactive=false` 62 ms vs 192 ms |
| `GET /v1/saves` | ✅ | 0.93.8 在线验收：`count=8`、`counts{local:1,cloud:7}`、`scope=local+cloud` |
| `POST /v1/save/load` | ⛔ | 永久 501（M6.17 实测：直调 `SaveLoader.Load` 会先清场景，失败不回滚，16890 次空引用后崩） |

### 1.3 实体

| 端点 | 状态 | 证据 / 备注 |
|---|---|---|
| `GET /v1/buildings` | ✅ | M5.4 抽样：`count=192 / scanned=2827 / filteredByFog=2635`；0.95.0 实测 `markedForDeconstruct`；M6.63 回读 1×2 蓝图。**0.96.0 改成分页（`total`/`hasMore`/`nextOffset`）+ 默认不回 `name`，新行为见 §2（无样本）** |
| `GET /v1/dupes` | ✅ | 0.90.0：24 人、`organic 18 / bionic 6`；0.95.0 实测 `choreTarget` = 刚标记的 `Tile` |
| `GET /v1/alerts` | ✅ | 0.90.1 实测聚合：`alerts=304 / occurrences=2458`；⚠️ 见 §2（三态修正后无样本） |
| `GET /v1/inventory` | ✅ | M6.60 实测：`Cuprite inStorage=114.904 / onGround=4922.171` |
| `POST /v1/building/select` | ✅ | 0.95.0 实测：`select(117,216)` → `/v1/ui` 见 `拆解Button` → 点击生效 |
| `POST /v1/door` | ⚠️ | 无运行期记录 |

### 1.4 建造 / 研究 / 作业

| 端点 | 状态 | 证据 / 备注 |
|---|---|---|
| `GET /v1/catalog` | ✅ | 0.30.4 验收：`count=defsCount=510`、`categories=76（isTop 6）`、JSON 合法 |
| `GET /v1/build-menu` | ⚠️ | 无运行期记录 |
| `GET /v1/materials` | ⚠️ | 无运行期记录 |
| `GET /v1/research` | ⚠️ | 无运行期记录 |
| `POST /v1/job` | ✅ | `dig` M6.2/M6.5；`build` M6.60–M6.63；`harvest` M6.9；`sweep` M6.14；五类标记 0.92.x；`mop` 0.92.6/0.93.8 |
| `GET /v1/marks` | ✅ | M6.9 实测：`okLayers=45 / errLayers=0`；0.39.0 与 `/v1/job` 同源修正后成对验过 |

### 1.5 通用命令

| 端点 | 状态 | 证据 / 备注 |
|---|---|---|
| `POST /v1/command` | ✅ | `pipeLink` 0.94.7 四用例；`spawnElement` 0.93.9；`pause`/`setSpeed` 0.4.0；⚠️ `setChorePriority` 无记录 |

### 1.6 界面 / 截图

| 端点 | 状态 | 证据 / 备注 |
|---|---|---|
| `GET /v1/ui` | ✅ | 0.93.3 实测体积 1,714,519 B → compact 898,280 B；handoff §3：主菜单 22 个按钮、`textSource=LocText` |
| `POST /v1/ui/click` | ✅ | 0.93.0–0.93.3 实测；**限制**：`clicked:true` ≠ 界面真的动（`LoadScreen` 行需真实鼠标，见 AI-GUIDE §3.7） |
| `GET /v1/ui/widgets` | ⚠️ | 调试用，无运行期记录 |
| `GET /v1/shot` | ✅ | 0.93.1 修相机后出图；0.95.0 实测 `centerX=117&centerY=217` ⇒ `viewWorld` 中心 `(117,217)`、`cameraRestored:true` |
| `GET /v1/shot/cameras` | ⚠️ | 0.93.2 改过 `pickedForShot`（由单值变列表），改后无样本。**代码依据 + 可佐证的邻近样本见 §2.1 ②** |

### 1.7 诊断

| 端点 | 状态 | 证据 / 备注 |
|---|---|---|
| `GET /v1/tools` | ⚠️ | 调试用，无运行期记录 |
| `GET /v1/debug/mods` | ⚠️ | 0.7.0 为诊断 `on_managed` 而加，但无"读到什么"的书面结果 |
| `GET /v1/debug/vision-sample` | ✅ | M5.4 用它找 `Visible=0` 的格子（0.32.0 验收方法第 1 步） |

---

## 2. 具体行为 / 字段（端点 ✅ 但字段不一定）

| 项 | 状态 | 证据 / 备注 |
|---|---|---|
| 建造：材料不足不拦蓝图 | ✅ | M6.56 实测；`materials{...}` 只报数 |
| 建造：`PostProcessBuild` 抛异常但已放下 | ✅ | M6.57 实测：返回成功 + `warningKind:"POST_PROCESS_FAILED"` |
| 建造：同格重复 ⇒ `409 CELL_OCCUPIED` | ✅ | M6.58 实测（顺序改成"占用检查优先"） |
| 建造：1×2 蓝图（预览不在目标格） | ✅ | M6.63 实测：`visualizerMoved=true / from=136864 / to=138121 / footprint=1x2` |
| `dig` 区域硬等式 | ✅ | M6.5 实机：`58 + 0 + 17 = 75`；handoff §3 `markedStillPresent=419 / markedVanished=0` |
| 标记五类 `dryRun` 与真做同源 | ✅ | 0.92.x 实测（`JobMarks.Resolve` 单一入口） |
| `mop` 端到端（placer → 小人接活 → 水消失） | ✅ | 0.92.6：`(111,217) 0.037kg` ⇒ 3 s `mopPlacerPresent=1` ⇒ +20 s 水消失、`阿玛里=Mop` |
| `mop` 幂等（`alreadyMarked 1 / wouldMark 0`） | ✅ | 0.93.8 同一格四请求表（A/B/C/D） |
| `attack` 1×1 不误标 / 不标自己人 | ✅ | 0.92.2 实测（21 只 → 只标目标）；0.92.4 `ownColonyExcluded=5` |
| `pipeLink` 断/连 + 邻格/跨层拦截 | ✅ | 0.94.7 四用例：`3→1`、`1→3`、`400 NEIGHBOUR_NOT_CONDUIT`、`404 NOT_FOUND_TARGET` |
| `spawnElement` 同帧未确认 ⇒ `200 pending` | ✅ | 0.93.9 实测：`applied:false / pending:true / readback.element=Oxygen`，1.2 s 后变 `Water 25kg` |
| `blockingDialog` 的 `present:true` 分支 | ✅ | **0.95.1 实测（WorkBuddy 验收，2026-11-19 02:58:49）**：模组差异确认框弹出时 `GET /v1/meta` ⇒ `blockingDialog{present:true,count:1,screens:["ConfirmDialogScreen"]}`；点掉后同一小时 02:59:06 变 `{present:false,count:0,screens:[]}`。原文 `shared/2026-11-19-workbuddy-acceptance/run.log` 第 15/16/19 行（不是本会话装机验的，见 §5） |
| `blockingDialog` 不误报 `ResearchScreen` | ✅ | 0.95.1 实测：20 个 screen 激活、`ResearchScreen` 在列，仍 `present:false` |
| `path#n` 同名兄弟唯一化 | ✅ | 间接：0.93.3 用 `path` 点到了 `AllDiagnosticsScreen` 的行 |
| `screenName` 匹配（`ScreenMatches`） | ⚠️ | 0.93.1 改动；依据只是"`/v1/ui` 能列出 MainMenu"，没直接读过 `screenName` 的值 |
| `clickables` 默认 compact + `data.clickablesMode` | ⚠️ | compact 本身 0.93.3 实测过；**默认值**与顶层字段是 0.93.4 改的，未装机。**代码依据见 §2.1 ③（静态唯一分支，但按规则不升 ✅）** |
| `worlds.isActive`（`gridWorldId == activeWorldId`） | ⚠️ | 0.91.1 改动后无样本；0.90.x 曾因比较对象错全部 false |
| `worlds.displayName`（`Strings.Get(worldType)`） | ⚠️ | 0.91.0 改动后无样本 |
| `alerts` 三态（broken/disabled/stopped） | ⚠️ | 0.91.0 改动后无样本；0.90.2 的 `broken` 14 组全是误报 |
| `alerts` 200 KB 截断分支 | ⚠️ | 未构造出该规模 |
| `dupes.stuckProbe.possible` | ⚠️ | **从未触发过 true**；不要拿它当"卡住"的结论 |
| `empty_pipe` 幂等回读 | ⚠️ | 无公开回读位，响应里是 `alreadyMarkedUnavailable=1` |
| `mopMinionChores`（`choreType.Id` 含 `"Mop"`） | ⚠️ | 0.92.6 新增字段，未装机；匹配串是从 `阿玛里=Mop` 推断的 |
| `spawnElement` 的 `readback == null` ⇒ 409 分支 | ⚠️ | 构造不出来（合法格里 `Grid.Element` 总有值），只做代码保留 |
| `shot` 的黑图拦截 `500 SHOT_BLACK` | ⚠️ | 0.93.1 加了门，但该版未装机；后来出图正常，门从未触发 |
| `buildings` 分页 `total` / `hasMore` / `nextOffset`（0.96.0） | ⚠️ | 新加，**无运行期样本**（只有编译通过、0 error）。静态自证：`total = count + skipped + notEmittedDueToLimit`、`hasMore ⇔ nextOffset >= 0`、`nextOffset = offset + count`。见 `Buildings.cs` 类注释与 `tools/smoke.ps1` 新加的三条分页自检 |
| `buildings` 的 `offset` 只在命中集计数（0.96.0 行为变更） | ⚠️ | 旧实现先跳 `offset` 再判 `prefabId` ⇒ 带 `prefabId` 翻页会漏/重；新实现把 `prefabId` 提前。**无运行期样本**；验法见 §3 第 11 条 |
| `buildings` 默认不回 `name`（`?names=1` 才回，0.96.0） | ⚠️ | 无运行期样本。静态可判：`name` 只在 `withNames==true` 且**命中窗口内**时才拼（`Buildings.cs`，emit 段）；顶层 `namesIncluded` 如实报告 |
| 响应信封回显 `reqId`（0.96.1） | ⚠️ | 无运行期样本。静态可判：`Ok()` / `Error()` / `ErrorDetails()` / `Health()` / `Meta()` 五处都拼 `ReqJournal.ReqIdJson()`；响应头另带 `X-Oni-Req-Id` |
| 同 `reqId` 的 POST/PUT 回放（幂等，0.96.1） | ⚠️ | 无运行期样本。逻辑层有单测（回放命中 / `503` 不记账 / 超限 body / 环形淘汰 13/13）；**真机上"没有第二次执行"没验过** |
| `503 RETRY_LATER` 不记账不回放（0.96.1） | ⚠️ | 单测覆盖（`Record(503)` 后再 `TryReplay` 必 miss）；真机未验 |

### 2.1 三条「只有源码依据」的欠账 —— 逐条核对（2026-11-19，子会话 oni-mod-2）

> 只做**源码核对**：写清"哪一行、什么条件成立、还缺什么"。**没有一条靠核对就升 ✅**（§0 规则 1）。
> ① 后来找到了运行期样本，已升级，依据也记在这里。

#### ① `blockingDialog.present:true` —— **已找到运行期样本，升 ✅**

`present:true` 的成立条件（源码）：`Bridge.cs:980` 只收 `BlockingScreenNames` 里的类名（白名单在 `Bridge.cs:1011-1020`，
含 `ConfirmDialogScreen`），`Bridge.cs:984` 用 `names.Count > 0` 决定 `present`。
运行期样本（不是本次装机）：0.95.1 / M7.3，WorkBuddy 于 2026-11-19 02:58:49 取 `/v1/meta` ⇒
`{"present":true,"count":1,"screens":["ConfirmDialogScreen"]}`；02:59:06（点掉后）⇒ `{"present":false,"count":0,"screens":[]}`。
原文：`shared/2026-11-19-workbuddy-acceptance/run.log` 第 15、16、19 行。
**这条同时证明"不是常真"**（同一局游戏内先 true 后 false），比单向样本更强。

#### ② `GET /v1/shot/cameras` 的 `pickedForShot` 列表 —— 仍 ⚠️

源码：端点入口 `UiShot.cs:699 Cameras()`；`pickedForShot` 在 `UiShot.cs:740` 起按 `usable` 列表逐条输出，
`pickedCount` = `usable.Count`（`:747`），`chosenForShot` = `usable[0]`（`:751-753`，为空时 `null`），
`registryAvailable`（`:764`）。`usable` 的构造：`:714` 先取 `UsableForShot`（= `ScoreAndClassify` 判 reason==null，
`:585-627` 的分数/不可用原因表）；`:719-727` 一台都不可用时**兜底**成"enabled 且 activeInHierarchy"；
`:730-735` 分数降序、同分按名字排序 ⇒ `pickedForShot[0]` 是确定的。`:782` 还有个 `JsonCheck.Validate` 门禁
（拼坏了会直接 500，不会把半截 JSON 发出去）。
**邻近但不同端点的运行期样本**：0.95.1 `/v1/shot?centerX=110&centerY=219` 返回
`"camera":"baseCamera","cameraRole":"base","renderPath":"RenderTexture"`（run.log 第 330 行）——
说明"角色判定 + 主相机可被选中"这段（与 cameras 端点共用的 `CollectCameras` / `ScoreAndClassify`）真机成立，
但 **`Cameras()` 自己的 JSON 形状、排序、兜底分支没被测过** ⇒ 保持 ⚠️。
**缺什么**：`GET /v1/shot/cameras`，核对 `pickedForShot[0].name=="baseCamera"`、`chosenForShot` 与
`pickedForShot[0]` 是同一台、`pickedCount==pickedForShot.length`、`cameras[].usableForShot` 与 `pickedForShot` 集合一致。

#### ③ `clickables` 默认 compact + `data.clickablesMode` —— 仍 ⚠️

源码：默认值在 `Bridge.cs:475`（`bool cCompact = true;` "默认 = compact"）；只有 query 里**出现** `clickables=`
才会被覆盖（`Bridge.cs:487-494` → `Ui.cs:81-88 ParseClickablesMode`：缺省/空串 ⇒ true，
`full`/`all` ⇒ false，其它任何值 ⇒ true 兜底）。输出侧：`Ui.cs:103` `cMode = compact ? "compact" : "full"`，
`Ui.cs:215` 写进顶层 `data.clickablesMode`，`Ui.cs:257` 每个 screen 内再写一份同值副本；
compact 每条只拼 `kind/role/text/path`（`Ui.cs:203` → `UiRows.AppendJsonCompact`）。
⇒ "不传 `clickables=` ⇒ `compact` + 顶层 `clickablesMode:"compact"`" 是**静态唯一路径**（没有第二个分支能产出别的值），
但按 §0 规则 1 仍**不升 ✅**（需要一次真机 `GET /v1/ui` 的记录）。
**缺什么**：`GET /v1/ui`（不带 `clickables=`），看 `data.clickablesMode=="compact"`、`clickables[0]` 只有 4 个字段且**带 `path`**。
`tools/smoke.ps1` 的 `ui: screens + clickablesMode` 那一条已收紧成**必须 == compact**（0.96.0），跑一次 smoke 就能拿到这条证据。

---

## 3. 待验证清单（债，按风险排）

> 每条都写"怎么验"，验完把 §1/§2 对应行改成 ✅ 并写证据。

| # | 项 | 怎么验 | 风险 |
|---|---|---|---|
| 1 | `blockingDialog.present:true` | ~~游戏内弹一个真确认框~~ ⇒ **已有样本**（0.95.1 WorkBuddy，见 §2.1 ①），待根会话换本版复验一次即可 | ✅ 已结 |
| 2 | `/v1/shot/cameras` 的 `pickedForShot` 列表 | `GET /v1/shot/cameras`，看 `chosenForShot.name=="baseCamera"`、`view` 与画面范围相符；源码已核对，见 §2.1 ② | 中：选错相机 ⇒ 黑图 |
| 3 | `clickables` 默认 compact | `GET /v1/ui`（不传参）看 `data.clickablesMode=="compact"`、每条 4 字段；源码已核对，见 §2.1 ③ | 中：AI 拿不到 `path` 就点不了 |
| 4 | `worlds.isActive` / `displayName` | `GET /v1/worlds`，当前世界那一条 `isActive:true`、`displayName` 是中文星球名 | 中：选错世界 |
| 5 | `alerts` 三态 | 弄坏一栋建筑 / 关一栋建筑，看三条 kind 是否分开 | 中：误报会淹没真告警 |
| 6 | `screenName` 匹配 | `/v1/ui/click {"screen":"MainMenu","dryRun":true}` 能出候选 | 中：定位不到 |
| 7 | `mopMinionChores` | `mop` 真做后隔 2 s 查 `/v1/job` 同格 | 低：已有 `mopPlacerPresent` 兜底 |
| 8 | `empty_pipe` 幂等 | 同格连发两次 `dryRun`，第二次是否 `alreadyMarked` | 低：无回读位，可能只能接受 |
| 9 | `alerts` 200 KB 截断 | 大档刚进图时 `?detail=1` | 低 |
| 10 | `GET /v1/events` / `/v1/commands` / `/v1/env` / `/v1/materials` / `/v1/research` / `/v1/build-menu` / `/v1/door` / `/v1/settings` / `/v1/tools` / `/v1/ui/widgets` / `/v1/debug/mods` | 各打一次，确认真实返回形状与 API.md 一致 | 低-中：文档可能与实现漂移 |
| 11 | `/v1/buildings` 分页（0.96.0） | `?limit=2000&offset=0` 拿 `total`/`nextOffset`，再按 `nextOffset` 翻到 `hasMore:false`，核对：两页 `total` 相同、`sum(count)==total`、最后一页 `nextOffset==-1`；再带一个 `prefabId` 翻两页确认无重复（`cell` 不重复） | 中：翻不完整张图 |
| 12 | `/v1/journal` + `reqId` 回放（0.96.1） | ① `GET /v1/health` 带 `X-Oni-Req-Id: t1`，看响应体 `reqId:"t1"`；② 同 id 再发 `POST /v1/job`（`dryRun:true`），看响应头 `X-Oni-Replayed: 1` 且 `/v1/marks` 回读**没有第二次执行**；③ `GET /v1/journal?reqId=t1` 能查到；④ 关掉游戏后用 `tools/oni_client.ps1` 发一次，须 `exit 5` 而不是假装成功 | 中：重试会干两遍 / 失败被当成功 |

---

## 4. 已知不可靠（别拿它当结论）

1. `dupes[].stuckProbe.possible` —— 从未 true。
2. `IDisconnectable.IsDisconnected()` —— 恒 false，与真实状态矛盾，`pipeLink` 已不再暴露它。
3. 双向冗余 —— 不成立：连一格不会自动在邻格置反方向位。
4. `/v1/ui/click` 的 `clicked:true` —— 只表示"回调调到了"，不表示界面真的动了（`LoadScreen` 殖民地行要真实鼠标）。
5. `/v1/save/load` —— 永远 501，不要试。
6. `/v1/ui` 默认体积 —— 曾经 1.7 MB；现在默认 compact，但 `?clickables=full` 仍可能很大。

---

## 5. 记录里已知的矛盾（以谁为准）

| 矛盾 | 处理 |
|---|---|
| 0.93.6 写"本 mod 至今未装进游戏跑过"（CHANGELOG L280） vs 0.92.6/0.93.8 的实机记录 | **以 0.93.8 为准**：它开头自证"两条都是 0.93.6 真机验收时挖出来的"。0.93.6 那句是子会话视角（它确实没装机），不是项目事实 |
| handoff §2 的数字（0.93.6 / API.md 2912 行 / CHANGELOG 2068 行） | 已过期；以 `mod_info.yaml` 与本文件为准 |
| handoff 第 154 行说"0.93.2~0.93.4 由别的会话改" | 说明写手**不止两个**；版本归属按证据，不按记忆 |
| `Bridge.Stage` 曾两版没跟（0.93.5 时才发现停在 M6.85） | 现在由 `tools/bump-version.ps1` 一起改（`-Stage`） |
| 2026-11-19：`blockingDialog.present:true` 由 ⚠️ 升 ✅，**依据不是本会话装机**，而是 WorkBuddy 那轮验收留下的日志（`shared/2026-11-19-workbuddy-acceptance/run.log:15/16/19`，0.95.1） | **以那次真机返回为准**（有原文、有版本、有前后对照）；本版（0.96.x）装机后仍建议复验一次（§3 第 1 条） |

---

## 6. 怎么更新本文件

1. 拿到证据（真机返回 / `Player.log` 行 / 数字）之后，才把 ⚠️ 改 ✅，并在证据列写：**版本 + 一句可核对的事实**。
2. 新增端点或字段 ⇒ 在表里加一行，默认 ⚠️，并写"怎么验"。
3. 发现本文件写错了 ⇒ 直接改，并在 §5 记一行（谁、哪版、原话错在哪）。
4. 发版流程：`tools/bump-version.ps1` → `build.ps1` → 装机 → `tools/smoke.ps1` → 更新本文件。**smoke 红的版本不装。**
