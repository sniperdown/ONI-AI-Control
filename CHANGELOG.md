## [修正] 0.93.6 / M6.90 —— `type=capture` 并不"未实现"：删掉不可达的拒绝分支 + 补回读字段 + 更正过时文档（2026-11-17）

### ① 起点：那句"暂未实现"其实永远执行不到
`Job.cs` 里原有这段：

```csharp
if (t0 == "capture") {
    status = 400; code = "NOT_IMPLEMENTED";
    message = "type=capture 暂未实现（CaptureTool.MarkForCapture 是矩形区域接口，与单格模型不同）";
    return null;
}
```

本次核实（**没有**照任务描述直接采信）：**它从 M6.76 起就是死代码**。
`Execute` 开头就有一行 `if (JobMarks.IsMarkType(t0)) return JobMarks.Execute(t0, ...)`，
而 `JobMarks.IsMarkType` 自 M6.76 起对 `capture` 返回 **true** ⇒ capture 在那一行已被分派走，
下面这段 `if (t0 == "capture")` **永远不可达**。打标记批次（`JobMarks.cs`）里 capture 的解析、执行、
回读早已写全：矩形判据是 `rectType = (type == T_ATTACK || type == T_CAPTURE)`，矩形参数就是 `x0/y0/x1/y1`。

⇒ 本次**没有新增 capture 实现**（它本来就在）。做的是：删掉那段会误导读者的死代码、把 type 列表改对、
补一个回读字段、把三处仍写着"capture 未实现"的文档改掉。

### ② 为什么不用 `CaptureTool.MarkForCapture(Vector2, Vector2, bool)`
该签名**确实存在**（`public static`，已用反射核实）：`CaptureTool.MarkForCapture(Vector2 min, Vector2 max, Boolean mark)`。
但 IL 显示它做的事是：遍历全局组件表 `Cmps<Capturable>.Items` → `Grid.PosToXY(transform.position)` 过滤矩形 →
调**同一个** `Capturable.MarkForCapture(bool, PrioritySetting, bool)`，此外只多做两件：
取**界面优先级**（`ToolMenu.PriorityScreen.GetLastSelectedPriority()`）和弹一个 `fx_capture_splash` 飘字。

⇒ 换成它不会多出任何游戏逻辑，只会引入"优先级取决于界面上次选了什么"这个**不可控输入**和一个 UI 副作用。
所以执行仍落在 `Capturable.MarkForCapture(true)` 上，**矩形语义由 `JobMarks.Resolve()` 的逐格解析提供**，
请求格式与 `attack` **完全一致**（`x0/y0/x1/y1`，没有第二种写法）。

### ③ 新增回读字段（capture 独有，纯 additive）
`Capturable.MarkForCapture` 的 IL **第一个判断就是 `IsCapturable()`**，为假时**静默什么都不做** ⇒
原来的 `marked`（尝试数）可能多于游戏真标上的数。本次新增：
* `captureMarkedCount`：游戏侧**回读**到的已标记数（含此前就带的）。判据 `Capturable.IsMarkedForCapture`，
  它本身就是 `return markedForCapture;` ⇒ **同帧可靠**，不用像 attack 那样等下一帧；
* 逐项 `list[].captureMarked`（bool）；
* 回读抛异常 ⇒ `captureMarkedCount: -1` + `captureMarkedUnavailable`（**不拿 0 / null 冒充**，按本项目约定）。

`marked` 的语义**未改**（仍是尝试数），既有调用方的判读方式不受影响。

### ④ 改动清单
* `src/OniAiBridge/Job.cs`
  - 删除 `if (t0 == "capture") { …NOT_IMPLEMENTED… }` 整段（原 L73–78，**不可达**）；
  - `ImplementedTypes` 补上打标记五类（用 `JobMarks.T_*` 常量），`NotImplementedTypes` 置空
    ⇒ 未知 type 的报错不再谎称"只支持 dig"，也不再声称 capture 未实现；
  - 类头注释与"需要 type"文案同步更正。
* `src/OniAiBridge/JobMarks.cs`：capture 回读（dryRun 也照给，与 attack 的 `attackMarkedCount` 同一套路）+ `note` 文案。
* `docs/API.md`
  - §3.15 类型表 `capture` 行：⛔ 未实现 → ✅ 已实现（**矩形**语义），入口改为 `Capturable.MarkForCapture(bool)`；
  - 工具表 `DisinfectTool` / `AttackTool` / `CaptureTool` / `EmptyPipeTool` / `MopTool` 五行：**未实现 → 正式说明**
    （五行同属 M6.76 打标记批次；只改 capture 会留下"capture 能用、同一批的 disinfect 反而未实现"的自相矛盾）；
  - §3.16「未实现（下一步）」里的 capture 条目 → 划掉并注明已实现；
  - 新增 §3.37.4b（为什么不用 `CaptureTool`，含 IL 依据与已知差异）；§3.37.4 补 capture 执行细节；§3.37.6 补装机判据。
* `README.md`：阶段/版本 + `/v1/meta` 示例。
* `src/OniAiBridge/Bridge.cs`：`ModVersion` 与 `Stage`（`M6.89` → `M6.90`）。
* 版本号三处同升 `0.93.5 → 0.93.6`（`mod/mod_info.yaml` / `OniAiBridge.csproj` / `Bridge.cs`）。

### ⑤ 未改动
其它 job 类型（`dig`/`deconstruct`/`harvest`/`sweep`/`build`/`research`/`disinfect`/`empty_pipe`/`attack`/`mop`）
的行为**一行未改**；`Map.cs` 未动；`qq-plugin/`、`H:\dsh-home` 未动。

**验证到哪一步**：
* **编译通过**：`0 个错误`、`32 个警告` —— 与改动前**完全一致**（逐条 diff 只有 `Job.cs` 行号 +3 的偏移，
  警告代码与标识符全同，**无新增、无消失**）。
* **游戏 API 比对**：只到**反射 / IL 级** —— `CaptureTool.MarkForCapture(Vector2,Vector2,Boolean)`、
  `Capturable.MarkForCapture(Boolean)`、`Capturable.IsMarkedForCapture` 的签名与语义都是从
  `Assembly-CSharp.dll` 里读出来核对过的，不是猜的。
* **运行期：全部未验证**（本 mod 至今未装进游戏跑过）。装机后该看的三条：
  真做后 `captureMarkedCount == marked`；再 dryRun 同格 `alreadyMarked: true`；小动物被装进陷阱/箱。

## [新增] 0.93.5 / M6.89 —— 新端点 `GET /v1/map`：地图降采样（默认关，`allowMap` 开关）（2026-11-17）

### ① 需求与依据（为什么它一直没做）
`DESIGN.md` §2.3 第 4 条把它写死了四条约束：**地图不默认下发**；`/v1/map` 必须带 `bbox` 与 `stride`；
单次硬上限（默认 64×64 格 / 次），超限 `400 BBOX_TOO_LARGE`；**默认只给"元素 id + 温度 + 质量"三通道**。
`DESIGN.md` §3.2 端点表另加一条："必须给 `bbox`；默认关，`allowMap` 开关"。
`docs/API.md` 里从 M0 起就把它标成 ⚠️ 未实现 —— 本次按上面两条约束实现，改动全部落在 `oni-mod/` 内。

### ② 端点：`GET /v1/map?bbox=x0,y0,x1,y1&stride=N`
* **`bbox` 格式**：`x0,y0,x1,y1` —— 4 个整数、逗号分隔、**闭区间**（含两端）。格式**写进了 `docs/API.md` §3.40.2**，
  同时**写进了错误 `message`**（缺参数 / 格式坏两种错都自说明格式 + 给例子），调用方看错误就能改对。
  端点顺序不敏感（`x1<x0` 归一化），容忍 `%2C`。
* **缺 `bbox` 或 `stride` 任一 ⇒ `400 BAD_REQUEST`**（无默认值，不臆测要读哪块）。
* **`stride`**：抽样步长，整数 `>= 1`；不设上限（`bbox` 本身就 ≤ 64×64）。`stride<1` / 非整数 ⇒ `400 BAD_REQUEST`。
* **硬上限 64×64 格**（单边 64、总数 4096），按 **`bbox` 的格数**判定（`stride` **不豁免**）；超 ⇒ `400 BBOX_TOO_LARGE`。
* **部分越界**夹到网格内（`clipped:true` + `bboxRequested` 回显原请求），**整框越界** ⇒ `400 BBOX_OUT_OF_BOUNDS`。
* 默认三通道 `channels.{element,temperature,mass}`；数组长度恒 `width×height`，**行主序**
  （`idx = row*width + col`，`row 0 = y0`、`col 0 = x0`），响应回显 `bbox` / `width` / `height` / `stride` / `indexRule`。
* 温度单 **K**（310.15 K = 37 °C）、质量 **kg**；真空格给 `element:"Vacuum"`、`mass:0`（不是 `null`）。
* **迷雾**：`aiEnableFog=true` 时只输出 `Grid.Visible[cell] > 0` 的格子（门禁就是 `Vision.Allow`，与
  `/v1/buildings`、`/v1/env` 同一个出处，在采集循环里逐格判断）；被滤掉的格子在通道数组里是 `null`（长度不变，
  便于逐格对齐坐标），并如实报告 `fogged` 与 `visibility.{sampled,emitted,filteredByFog,filteredInvalid,filteredCount,rule}`。
* **通道级降级**：某个通道在所有采样点都读不出来 ⇒ `channels.<通道>` 为 `null` + 顶层
  `elementUnavailable` / `temperatureUnavailable` / `massUnavailable`（异常名 + 消息），其余通道照常输出。

### ③ 配置：新增 `allowMap`（**默认 `false`**）
* `BridgeSettings.AllowMap` 新增；`Load()` / `LoadFromDisk()` 正则回读、`Save()` 写盘、
  `SaveAndVerify()` 逐字段回读比对（与 `allowCheats` / `AIEnableFog` 同一套路径，**写盘校验对它同样生效**）。
* 游戏内设置页（`Ctrl+Shift+O`）：新增一行「允许ai读取地图格子数据（/v1/map）」勾选框，
  走既有的**草稿 → `[保存]` 才写盘**流程（保存失败整批回滚，`allowMap` 一起回滚）。
* **关闭时请求不是"静默返回空"**：回 `403 MAP_DISABLED`，`message` 里写明怎么打开。
  判定顺序是 `405 → 403 MAP_DISABLED → 400 BAD_REQUEST → 400 BBOX_TOO_LARGE → …`，关着的时候连参数格式都不看。

### ④ 顺带更正 `/v1/meta`（三个字段，**都是把假的改成真的**）
* `capabilities.map`：原来**硬编码 `false`**；现在如实报 `allowMap`（`false` = 闸门关着，**端点本身存在**）。
* 新增 `data.allowMap`（配置原值）与 `data.mapLimits`（`{maxSide:64, maxCells:4096, channels:[…]}`），让客户端自发现上限与通道集。
* `capabilities.catalog`：原来是 `false`，但 `/v1/catalog` 从 M5.2 起就是可用端点 —— 改为 `true`。
* `Bridge.Stage` 由 `M6.85` 改为 `M6.89`（此前两版没跟，`/v1/meta` 与 `CHANGELOG` 的阶段号一直是错开的）。

### ⑤ 新增 / 改动
* **新增** `src/OniAiBridge/Map.cs`（`MapQuery`，采集与 JSON 组装全在这里；`Bridge.cs` 只加路由与一个取值外壳）。
* `src/OniAiBridge/Bridge.cs`：`case "/v1/map"` + `HandleMap` + `/v1/meta` 上述三个字段 + 版本号。
* `src/OniAiBridge/BridgeSettings.cs`：`AllowMap` 字段 + 三处读写 + 回读校验 + settings.json 写出。
* `src/OniAiBridge/BridgeOptionsScreen.cs`：`_mapToggle` 勾选框（创建 / 草稿 / 保存 / 回滚 / 磁盘回读日志）。
* 版本号三处同升 `0.93.4 → 0.93.5`（`mod/mod_info.yaml` / `OniAiBridge.csproj` / `Bridge.cs`）。
* 文档：`docs/API.md` 新增 **§3.40**（参数、响应示例、错误码与判定顺序、坐标还原、迷雾、降级、与 `/v1/area`·`/v1/env`
  的口径区别、未验证清单）；端点表 `/v1/map` 由 ⚠️ 改为 ✅；§4 错误表补 `BBOX_TOO_LARGE` / `BBOX_OUT_OF_BOUNDS` /
  `MAP_DISABLED` / `MAP_UNAVAILABLE` / `RETRY_LATER`；§7「尚未实现」把 `/v1/map` 划掉。
  `README.md` 更正过时的"M0（骨架）"阶段描述、`/v1/meta` 示例里的版本号与 `capabilities`、`settings.json` 示例，
  以及设置页"点一下立即写盘"的旧说法（M4.4 起是草稿 + `[保存]`）。

### ⑥ 未改动
`/v1/state`、`/v1/buildings`、`/v1/env`、`/v1/area`、`/v1/job`、`/v1/ui` 等既有端点的行为与字段均未改动；
`/v1/settings` 的 GET/PUT 也**没有**加 `allowMap`（与同样没暴露的 `AIEnableFog` 保持一致 —— 它走 settings.json 与设置页）；
端口、token、构建脚本、`tools/check-removed.ps1` 未改动。

**验证到哪一步**：
* **编译通过**：`0 个错误`、`32 个警告`（全部为既有告警 —— `CS0618` 的 Unity API 过时、`CS0219/CS0168/CS0169`
  的未用变量，分布在 `BuildUi/DebugMods/Buildings/Catalog/Env/Alerts/Dupes/Geysers/Saves/State/UiWidgets/UiClick/Ui/
  UiRows/UiShot/BridgeOptionsScreen/Job/Commands/SettingsUi`，**没有一条来自 `Map.cs`**）。
* **三处版本号自检**：`OK: 三处版本号一致 = 0.93.5`。
* **离线脚手架 62 条断言全过**（替身 `Grid`/`Element`/`Vision`/`MainThread`，`Map.cs` **原样拷贝**编译）：
  必填参数与格式错 13 条、64×64 上限 9 条（含"stride 不豁免上限"）、越界与 `clipped` 7 条、归一化与 stride 抽样点 7 条
  （把 `(x,y)` 编码进温度反查抽样坐标）、迷雾 12 条（`filteredByFog`/`emitted`/`null` 占位位置/`rule`）、
  通道降级 6 条、真空与数值格式与计数自洽 7 条、未就绪 1 条。
* **文档示例**：本次新写的 8 段 JSON 示例全部 `ConvertFrom-Json` 可解析。
* ⚠️ **真机未装机、未启动游戏**（本 mod 至今从未在游戏里跑过）：`Grid.Temperature` 的真实量纲、
  真空格的 `Grid.Element` 返回值、4096 格在大存档里的耗时、以及迷雾值与 `/v1/buildings` 是否完全一致，
  **都没有运行时证据**。第一次装机后的核对项写在 `docs/API.md` §3.40.11。

## [变更] 0.93.4 / M6.88 —— `/v1/ui` 的 `clickables` 默认改为 `compact`，`clickablesMode` 补到 `data` 顶层（2026-11-16）

### ① 默认输出模式：全量 → compact
**依据**（0.93.3 真机实测）：`GET /v1/ui` 默认全量 **1,714,519 字节**；同一请求加 `?clickables=compact`
为 **898,280 字节**（降 48%）。默认全量里绝大多数字段是"定位不需要"的。
**改**：
* 不传 `clickables` ⇒ **compact**，每条只写 `kind` / `role` / `text` / `path`（字段名与既有 compact 完全一致，
  不新增、不删除）；
* 显式 `?clickables=full`（等价写法 `?clickables=all`，取值大小写不敏感）⇒ 返回 0.93.3 及以前的完整字段集，
  **取全量的途径保留**；
* `?clickables=compact` 继续正常工作，与不传参行为一致；
* 非法取值（例如 `?clickables=xyz`）⇒ 回落默认 compact，**不报错**（与 `clickablesLimit` 的口径一致）。

### ② `clickablesMode` 写在哪个层级
0.93.3 **实现了**该字段，但位置在**每个界面对象内部**（`data.screens[i].clickablesMode`，与
`clickablesLimit` 相邻），`data` 顶层没有，因此按顶层读取只会得到 `undefined`。
0.93.4 在 **`data` 顶层、紧邻 `screens` 数组之后**补一份同名 `clickablesMode`；界面内那份为兼容保留，
两处同源、取值恒一致。程序读取以 `data.clickablesMode` 为准。

### ③ 与 `clickablesLimit` 正交
模式只决定每条写几个字段，截断只由 `clickablesLimit` 决定；两种模式下四件套都严格成立：
`clickablesEmitted = min(clickableTotal, clickablesLimit)`、`clickablesTruncated = clickableTotal > clickablesLimit`。

### ④ 未改动的部分
行采集（`UiRows.Collect`）、`/v1/shot/cameras`、`/v1/ui/click` 的 `target` / `readback`、端口与 token、
构建脚本均未改动。

**验证到哪一步**：编译通过（`0 个错误`、`2 个警告`，警告为既有的 MSB3277 程序集版本冲突）；三处版本号自检
`OK: 三处版本号一致 = 0.93.4`；手拼 JSON 骨架自检（按源码 `Append` 顺序还原 compact / full × 限流 / 不限流
四种响应并逐个 `JSON.parse`，校验顶层与界面内的 `clickablesMode`、四件套、compact 字段集）；
`JsonCheck.Validate` 在 `/v1/ui` 下发前照常执行。**真机未装机、未启动游戏**。

## [修复] 0.93.3 / M6.87 —— `/v1/ui/click` 的 `target.path` 与 `readback` 补齐、`clickables` 上限 500 → 2000（2026-11-16）

### ① `/v1/ui/click` 的 `target.path` 与 `readback` 是空的
**根因不是取值失败，而是字段根本不存在**：`/v1/ui/click` 按请求内容分流到两套实现 ——
带 `dryRun` / `name` / `index` 的走 `UiShot.Click`（有 `target` 与 `readback`）；
`text` / `path` / `pathContains` / `techId` / `labelClick` 走 `UiClick`，那三条响应里从来没有这两个字段。
真机用 `path` 点 `AllDiagnosticsScreen` 的行（`kind=MultiToggle`、`clickedVia` 正常）时看到 `target.path` 与 `readback` 为空，
就是这个分流造成的 —— 点击本身是成功的，缺的是"点到了谁 + 点完变成什么样"的回填。
**修**：新增 `UiRows.AppendTargetJson` / `UiRows.AppendReadbackJson` / `UiRows.Describe`，四条定位路径统一使用：
* `target.path` 与 `/v1/ui` 的 `clickables[].path` **同源**（同一个 `UiQuery.PathOf`，相对该 `KScreen` 的父级链、同名兄弟带 `#n`），
  另带 `kind` / `role` / `text` / `name` / `screen` / `elementIndex`（= `clickables[].index`）/ `buttonIndex` / `clickVia` / `interactable`；
  `labelClick` 的元素由 `UiRows.Describe` 现算（`elementIndex:-1` + `elementIndexUnavailable` 说明它没经过采集）；
* `readback` 必答 `screenBefore` / `screenAfter` / `interactableAfter`，另带 `screenAfterSource` / `activeScreensAfter[]` /
  `clickedDuringReadback`；点完界面被关掉（目标已销毁）⇒ `screenAfter` 退一步取"点击后仍激活的界面"，
  两者都取不到才写 `screenAfterUnavailable` 说明原因；回读进不去主线程时也写一份 `readback` 并注明"未执行"；
* `UiShot.Click` 的回读改用同一份 `UiRows.AppendReadbackJson`（响应字段名不变，只是取值规则统一）。

### ② `clickables` 被 500 截断（`ToolMenu` 724 个按钮只列出 500 条）
**根因**：`UiQuery.ClickablesLimit = 500`，而采集上限 `CollectLimit` 是 2000 —— 元素采到了，只输出四分之一。
**修**：
* 默认上限 `ClickablesLimit = 2000`（与 `CollectLimit` 同量级）；
* 新增查询参数 `GET /v1/ui?clickablesLimit=N`（上限 `ClickablesLimitMax = 20000`）；
  非数字 / ≤0 / 超范围一律回落默认值、**不报错**，生效值与请求值不同时另给 `clickablesLimitRequested`；
* 采集上限跟着抬到 `max(CollectLimit, 生效上限)`，否则"想输出更多"也采不到更多；
* 四件套恒自洽：`clickablesEmitted = min(clickableTotal, clickablesLimit)`、`clickablesTruncated = clickableTotal > clickablesLimit`；
* 体积兜底：`?clickables=compact` 时每条只写 `kind` / `role` / `text` / `path`（约降六成），`clickablesMode` 标明模式；
  **默认仍是全字段、不丢元素**（精简是显式选择，不是默认行为）。

### ③ 真机核对项（0.93.3 未装机，未启动游戏）
`GET /v1/ui` 默认参数下 `ToolMenu` 应为：`clickableTotal = 724`、`clickablesEmitted = 724`、
`clickablesTruncated = false`、`clickablesLimit = 2000`（724 < 2000）。724 取自真机实测的按钮数；
`clickableTotal` 与 `buttonCount` 是否同一个数、以及 `target.path` 是否与 `clickables[].path` 字面一致，
都要进游戏读一次确认（若 `ToolMenu` 还含 `KToggle` 类页签，`clickableTotal` 会略大于 `buttonCount`）。

**验证到哪一步**：编译通过（0 error）+ 手拼 JSON 的骨架自检（`Append` 序列按源码还原后逐个 `JSON.parse`，含 `/v1/ui` 整份响应、
三条点击响应、`target` / `readback` / 精简条目的片段）；真机未验。

**未改动的部分**（0.93.2 已真机通过）：`/v1/shot/cameras`、`/v1/ui` 的行采集（`UiRows.Collect`）、端口与 token、构建脚本。

## [修复] 0.93.2 / M6.86 —— `/v1/ui` 的行（`MultiToggle`）能列也能点 + `/v1/shot/cameras` 的 `pickedForShot` 变成真实列表（2026-11-16）

### ① `/v1/ui` 里"行"不可点
**根因**：`/v1/ui` 只列 `KButton`、`/v1/ui/click` 也只认 `KButton`；而 ONI 的"行"挂的是 **`MultiToggle`**
（`ColonyDiagnosticScreen.DiagnosticRow` / `AllDiagnosticsScreen.SpawnRow` / `ReportScreenEntryRow.toggle` /
`TableScreen` 的格子 —— 反编译 `Assembly-CSharp.dll` 逐类型核对），它实现了 `IPointerClickHandler`
但不在 `KButton` 集合里，所以既列不出来也点不到。
**修**：
* 新增 `UiRows`（判定按接口 `IPointerClickHandler` / `ISubmitHandler`，同一 GameObject 多组件按
  `KButton > KToggle > MultiToggle > Button > Toggle > 其它` 只留一个；`path` 与 `/v1/ui` 同源）；
* `/v1/ui` 每个界面新增 **`clickables[]`**（`kind` / `role` / `clickVia` / `path` / `index` / `buttonIndex` /
  `state` / `isOn` / `rect` …），`buttons[]` 保持旧口径（只含 `KButton`）；
* `/v1/ui/click` 按类型分派：`KButton.SignalClick` / `KToggle.Click` / **`MultiToggle.OnPointerClick`** /
  UGUI `Button.onClick` / UGUI `Toggle.OnPointerClick` / `ExecuteEvents` 兜底；
  `text` 与 `labelClick` 的候选扩到全部可点击元素（`labelClick` 改成"向上找最近的可点击组件"）；
  新增可选 `kind` 过滤；响应新增 `kind` / `clickedVia`。

### ② `/v1/shot/cameras` 的 `pickedForShot` 是空的
**根因**：该字段只是**单个名字**（`Snap.Str(PickWorldCamera())`，没命中就是 `null`），来源只有
`Camera.allCameras`（**只含 enabled**）+ 名字黑名单，没有用游戏自己的相机注册表
`CameraController.Instance`，也没有产出列表。
**修**：来源改为**三路并集**（`CameraController.Instance` 具名相机与 `cameras` 列表 /
`Camera.allCameras` / `FindObjectsOfType<Camera>(true)` 含 disabled 与未激活）；
`pickedForShot` 变成**按分排序的相机对象列表**，新增 `pickedCount` / `chosenForShot` /
`registryAvailable`，每台相机带 `role` / `score` / `usableForShot` / `unusableReason` /
`orthographicSize` / `fieldOfView` / **`view`（世界坐标视野范围）**；`/v1/shot` 响应新增 `cameraRole`。

**验证到哪一步**：编译通过（0 error）+ 游戏源码核对；**未装机**（按指示）。
装机判据：① `GET /v1/ui` 里诊断界面出现 `role:"row"` / `kind:"MultiToggle"` 的条目；
② 用该条目 `path` 调 `/v1/ui/click {"dryRun":true}` 能命中、`confirm:true` 能选中/聚焦到目标；
③ `GET /v1/shot/cameras` 的 `pickedForShot` 非空、`chosenForShot.name` 应为 `baseCamera`、
`view` 与本周期画面范围相符。

## [修复] 0.93.1 / M6.85 —— `/v1/ui/click` 屏幕名匹配 + path 唯一化 + `/v1/shot` 相机/黑图（2026-11-16）

### ① `/v1/ui/click`：带 `dryRun`/`index` 的请求进不到正确分支（表面像"分派反了"）
实测：`{"screen":"MainMenu","index":4,"dryRun":true}` ⇒ `400 NO_TARGET「该界面下没有按钮」`。
**根因**：那句文案其实是**新实现**的；真正错的是 **screen 名匹配** —— `KScreen` 的屏幕名在 **`screenName` 属性**里，
而 0.93.0 只比了 `GameObject.name`（实战是 `MainMenu_SpacedOut/...` 这种 prefab 名）⇒ 没有任何 screen 命中 ⇒ 候选 0。
**修**：`ScreenMatches()` 依次比 `screenName` → `displayName` → `GameObject.name` → `screenName` 子串。

### ② `path` 不唯一（主菜单 10 个菜单项全是同一预制体克隆）
0.93.0 的 path 是 `…/MainMenuButtons/ButtonTemplate` × 10 ⇒ 永远 409、无法唯一定位。
**修**：`PathOf` 对**同名兄弟**追加序号 ⇒ `…/ButtonTemplate#4`；
`/v1/ui` 与 `click` 用**同一个** `PathOf`，所以列出来的 path 必定可点。

### ③ `/v1/shot` 选错相机（`overlayNoDepth`）⇒ 全黑
**修**：
* `PickWorldCamera()`：枚举 `Camera.allCameras`，排除 `overlay/nodepth/ui/canvas`，按 `depth*100 + (targetTexture==null?50:0) + (非正交?5:0)` 取最高；
* 渲染优先 **`ScreenCapture.CaptureScreenshotAsTexture()`**，失败退回 `RenderTexture` + 选好的相机；
* **黑图拦截**：`AverageLuma` < 1/255 ⇒ **`500 SHOT_BLACK`**（message 带 camera 名与 renderPath），**不再把黑图当成功**；
* 响应新增 `renderPath` 与 `avgLuma`；
* 新增只读路由 **`GET /v1/shot/cameras`**（列出 name/depth/cullingMask/targetTexture/enabled/orthographic/rect + `pickedForShot`）。

**新增依赖**：`UnityEngine.ScreenCaptureModule.dll`。

**验证到哪一步**：编译通过 + API 核对；**未装机**（按指示）。
装机判据：① 主菜单 10 个按钮 path 两两不同；② `click` 带 `dryRun`/`index` 能返回候选/命中；③ `/v1/shot` 的 `camera` 不是 `overlayNoDepth`、`avgLuma ≥ 1`、打开 PNG 非黑。
## [新增] 0.93.0 / M6.84 —— `/v1/ui` 名字读全 + `/v1/ui/click` 三级定位/dryRun + `/v1/shot` 游戏内截图（2026-11-16）

### ① `/v1/ui` 按钮名
旧版只找**按钮自身/直接子级**的 `LocText` ⇒ 一半 `text:null`。
现在三级回退（取到就停）：**递归 `LocText`** → **递归 `UnityEngine.UI.Text`** → **`GameObject.name`** → null；
并给每个按钮 **`name` / `path`（相对 KScreen 的父级链）/ `index` / `rect`（屏幕坐标，`GetWorldCorners`→`WorldToScreenPoint`）**。

### ② `/v1/ui/click`
`path` > `name` > `index`；**`dryRun`**（只报候选）；`confirm` 强制；
不可见 ⇒ `400 NOT_VISIBLE`；不可交互 ⇒ `400 NOT_INTERACTABLE`；多命中 ⇒ `409 AMBIGUOUS_TARGET` + 候选；无 ⇒ `404 NO_TARGET`；
真点走 `UnityEngine.UI.Button.onClick.Invoke()`，退路 `KButton.SignalClick(KKeyCode.Mouse0)`；
回读 `readback.screenBefore/screenAfter/interactableAfter`。
（带 `dryRun`/`name`/`index` 走新实现，老的 `techId`/`pathContains` 路径保持不动。）

### ③ `/v1/shot`
`Camera.main` → `RenderTexture` → `cam.Render()` → `Texture2D.ReadPixels` → **`ImageConversion.EncodeToPNG`** → 写 `shots/*.png`；
返回绝对路径 + 宽高 + 字节数（**不塞 base64**）；像素上限 **4194304**（超 ⇒ `400 TOO_MANY_PIXELS`）；
`Camera.main==null` ⇒ **`409 NO_CAMERA`**（不静默黑屏）；可选 `cx/cy/cw/ch` 裁剪；**不改变游戏状态**。

**新增依赖**：`UnityEngine.ImageConversionModule.dll`（csproj 已加引用）。

**验证到哪一步**：编译通过 + API 核对（`KScreen`/`KButton`/`RectTransformUtility.WorldToScreenPoint`/`ImageConversion.EncodeToPNG`）；
**未装机实测**（按指示）。**已知未验证**：`cam.Render()` 离屏渲染在 ONI 的 URP 下是否会得到正确画面（可能需 `cam.RenderWithShader`/后处理缺失）；
`Camera.main` 在游戏内应非 null（菜单场景才会 null）。
## [修复] 0.92.6 / M6.83 —— 撤回 `mop` 的 501：**placer 路径有效，错的是回读判据**（2026-11-16）

### 实机证据（端到端，Cycle 5「第七次重启」）
小僵禁用厕所跑 5 分钟 → 小人尿地板 → 在**小人脚下**扫到真小水渍 `(111,217) liquidMassKg=0.037 onFloor=1`。
真拖后：当帧 `marked=0`（判据错）→ **3 秒后 `mopPlacerPresent` 变 1 并留住** → **+20 秒水渍没了，小人任务出现 `阿玛里=Mop`**。
⇒ `Util.KInstantiate(MopPlacer)` **是有效的**：placer → 工作 → 小人接 chore → 拖干净。

### 回读错在哪
`HasMopChore(cell, worldId)` 去**该格物体**上找 `ChoreProvider`；但 ONI 的拖地 chore 挂在**接活的小人**身上
（`阿玛里=Mop`）⇒ 永远 false。0.92.0–0.92.4 的"三次失败"全因这个。

### 0.92.6 改动
* **撤回 0.92.5 的 `501 NOT_IMPLEMENTED`**（`JobMarks.cs` 里已无该分支，grep 命中 0）；
* **`marked` 改按 `MopPlacerPresent(cell)`** 判定（placer 留住 = 真的排上工作）；placer 没留住 ⇒ 进 `failedDetails`；
* 新增 `list[].mopMinionChores` = 当前有 `Mop` chore 的**小人数量**（真正的执行信号）；
* `list[].mopChoreFound` 降级为**仅"格上物体"快照，不代表成败**（文档与代码注释都标注）；
* `docs/API.md §3.38` 从「已知未实现」改写为**正面结论**，并写入"mop 正确测试条件"（小水渍 <150kg + 下方固体；造法=禁用厕所）。

**验证到哪一步**：编译通过 + 判据复核（placer 持久性由 lead 实测确认）；`mopMinionChores` 是新增只读字段、未装机实测。
## [变更] 0.92.5 / M6.82 —— `mop` 真做改为 `501 NOT_IMPLEMENTED`（0.92.0–0.92.4 的 mop 从未真正生效）（2026-11-16）

**实测结论（lead）**：0.92.4 上 `(85,181)`（100.015kg、`onFloor=1`、`maxMopAmt=150`）真做 ⇒
`marked=0, failed=0, jobState=nothing-marked, mopChoreFound=false`；隔 3 秒再查仍 `false`。
⇒ 判据与目标都对，**"placer → Workable"这一步没发生**。

**三次走法全失败（都实测过）**
| 版本 | 走法 | 失败点 |
|---|---|---|
| 0.92.0 | 反射 `MopTool.OnDragTool`（未激活） | 工具未激活 |
| 0.92.1 | `ActivateTool` → `OnDragTool` → `DeactivateTool` | 激活后仍不落地 |
| 0.92.2 | `Util.KInstantiate(MopPlacer)` + 写 `ObjectLayer.MopPlacer` | placer 一秒后消失（临时物被消费，未转 Workable） |

**0.92.5 变更**
* `type:"mop"` + 真做（无 dryRun）⇒ **`501 NOT_IMPLEMENTED`**，`message` 说明需要复刻
  `OnDragTool`(造 placer) + `OnDragComplete`(转 Workable) + 工具激活/复原；
* **只读的 `dryRun` 保留**（返回 `mopGating` + 合法目标），这部分有价值；
* 不模拟鼠标、不写死 placer 生命周期 —— **到此为止**。

**其余 4 个 type 保持不动**（`disinfect` / `empty_pipe` / `attack` 含 `choreObservedImmediately`+`ownColonyExcluded` / `capture` 全部实测通过）。

**验证到哪一步**：编译通过；`501` 分支是纯控制流（`type == T_MOP && !dryRun`），未装机实测（按指示不装）。
## [修复] 0.92.4 / M6.81 —— `attack` 同帧回读改为不判失败 + 默认排除己方殖民者 + `mop` 判据（2026-11-16）

### ① `attack`：同帧回读不能当失败
实测：三次 1×1 真打全报 `marked=0, failed=1`，但 8 秒后整片 chore 从 21 涨到 23（`Squirrel` 3→8）
⇒ `MarkForAttack` **确实挂了 chore**，chore 要到下一帧才进 `choreWorldMap`。
**改**：工具调用没抛异常 ⇒ `marked = targets.Count`；新增 `choreObservedImmediately`（同帧回读数，可能 0，不代表失败）；
**`failed` 只留给工具调用抛异常**；`note` 写明"请隔 1–2 秒再 dryRun 复核"。
理由：假失败会导致调用方**重复下发**。

### ② 安全：`attack`/`capture` 默认排除己方殖民者
实测块 `(40..103,160..223)` 的 57 个目标里有 `Minion @(94,165)` 等自家殖民者。
**改**：默认排除（`MinionIdentity` 或 prefabId 含 `Minion`）；数量报 `ownColonyExcluded`；
要打自己人必须显式 `includeOwnColony:true`，此时响应带 `hazard`。

### ③ `mop`：判据改为游戏口径
`Moppable` 是 `MopPlacer` 预制体自带的（`MopPlacerConfig.CreatePrefab` IL），**不是前置条件**；
真正判据 = 非固体 + `IsLiquid` + **下方格是固体** + **`Mass <= MopTool.maxMopAmt`（150.0f）**。
响应新增 `mopGating`。文档写明"验证环境是小水渍（几 kg、在地面上），不是水池"。

**验证到哪一步**：编译通过 + 元数据/IL 核对；**未装机**。
`mop` 仍走"实例化 MopPlacer"路径，**尚未实测确认能排上工作**；若再失败，按文档约定改标 `NOT_IMPLEMENTED`。
## [修复] 0.92.3 / M6.79 —— `mop` 回读判据改为 Mop chore + dryRun 也给 attackBBox/attackMarkedCount（2026-11-16）

### `mop`：实现没错，**回读判据错了**
0.92.2 实测：真做 `(86,180)` 返回 `marked=1`，一秒后同格 `mopPlacerPresent:false`。
⇒ `MopPlacer` 只是**拖拽期间的临时标记物**（松手即被消费），不能当最终状态。
0.92.3：`alreadyMarked` / `marked` 一律改看 **`HasMopChore(cell, worldId)`** ——
逐层取对象上的 `ChoreProvider`，在 `choreWorldMap[worldId]` 里找 `choreType.Id` 含 `"Mop"` 的 chore（与 `HasAttackChore` 同手法）。
新增列表字段 **`mopChoreFound`**；`mopPlacerPresent` 降级为**临时观察**（`extra` 里标 `_observationalOnly`，不参与计数）；
没有 chore 时进 `failedDetails`。

### `dryRun` 一致性
0.92.2 的 dryRun 给 `attackBBox:null` / `attackMarkedCount:-1`；0.92.3 让两者**走同一段 `BuildAttackBBox`**，
`attackMarkedCount` 在 dryRun 时给解析时的真实回读值（不再出现 -1）。

**验证到哪一步**：编译通过 + `ChoreProvider.choreWorldMap` 判据复核（与 0.92.2 的 attack 同源，已真机验证过）；
**`mop` 的 chore 是否真的在 `choreType.Id` 里带 "Mop" 未验证**（未装机）——
若 `mopChoreFound` 恒 false 而游戏里确实排上了拖地任务，把 chore 的真实 Id 告诉我，我改匹配串。
## [修复] 0.92.2 / M6.78 —— `mop` 改预制体路径 + `attack` 包围盒世界坐标修正 + 回读字段（2026-11-16）

### `mop`：`Activate`+`OnDragTool` 也不行（实测）⇒ 改**预制体路径**
0.92.1 真做 `(86,180)`：`marked=0 failed=0 jobState=nothing-marked`，再 dryRun 同格 `mopPlacerPresent:false`。
0.92.2：反射读 `MopTool.Instance.Placer`（兜底 `Assets.GetPrefab(MopPlacerConfig.ID)`）
→ `Util.KInstantiate(prefab, Grid.CellToPos(cell))` → `Grid.Objects[cell, ObjectLayer.MopPlacer] = go` → `SetActive(true)` → 可选 `SetMasterPriority`
→ **回读 `MopPlacerPresent(cell)`，只有真落地才计 `marked`**。
**完全不调用任何工具方法**（`Activate`/`OnDragTool`/`DeactivateTool` 全移除），无需复原工具状态。

### `attack`：包围盒用了**格子序号**而不是世界坐标（这是 21 只动物被误标的根因）
0.92.1 把 `x0/y0/x1/y1` 直接当世界坐标传给 `AttackTool.MarkForAttack` ⇒ 一次 `1×1` 覆盖一大片
（实测 `MoleDelicacy`/`Chameleon`/`Drecko`/`Squirrel`/`Bee`/`Staterpillar`/`HatchHard` 共 21 只被标）。
0.92.2：`Grid.XYToCell` → `Grid.CellToPos` → `Vector2(p.x,p.y)`；
响应新增 **`attackBBox`**（`worldXY` + `cells` + `cell0/cell1`）供与请求 `rect` 对照。

### `attack` 的两个字段与静默失败
* `attackMarkedCount` = 调用后回读到的带 Attack chore 的目标数；`marked` = 它；`alreadyMarked` = 解析时本来就带 chore 的数；
* **「调用了但没标上」现在逐条进 `failedDetails`**（`未标记→仍未标记` 的目标），`failed` = 这类目标数量，不再 `failed=0` 假装成功。

**验证到哪一步**：编译通过 + 元数据核对（`Util.KInstantiate(GameObject, Vector3)`、`Assets.GetPrefab`、`MopPlacerConfig.ID` 均已核）；
**真机未验证**（未装机）。装机判据：`mop` 真做 ⇒ `marked=1` 且再 dryRun `mopPlacerPresent:true`；
`attack` 1×1 真做 ⇒ `attackBBox.worldXY` 跨度≈1、`attackMarkedCount` 只增 1。
## [修复] 0.92.1 / M6.77 —— `mop` 未生效 + `attack`/`mop` 计数矛盾 + `attack` 回读（2026-11-16）

**0.92.0 真机实测**：`disinfect` / `capture` / `empty_pipe` ✅；`mop` 与 `attack` 各有一个 bug。

### bug 1：`marked` 与 `failed` 同时为 1
```
{"targets":1,"marked":1,"failed":1,"failedDetails":[{"error":"InvalidOperationException: mop 走工具逃生口，不应逐目标调用"}]}
```
根因：`mop`/`attack` 是"一次工具调用完成全部"，却仍进了逐目标循环 ⇒ 每个目标都抛那个异常。
**修**：这两类**整段跳过逐目标循环**，只调一次工具；`marked` = 回读确认数，`failed` 只在工具调用真抛异常时 +1。

### bug 2：`mop` 实际没生效（0.92.0 报 marked=1 但 `MopPlacer` 层没有对象）
根因：未 `Activate` ⇒ `MopTool.OnDragTool` 不落地。
**修**：`PlayerController.Instance.ActivateTool(MopTool.Instance)` → `OnDragTool` → **finally 反射 `DeactivateTool` 复原**；
**逐格回读** `ObjectLayer.MopPlacer`，只有真落地才计 `marked`；响应新增 `list[].mopPlacerPresent`。

### bug 3：`attack` 没有回读
**修**：`list[].attackMarked` = 该对象身上是否已有 `Attack` chore
（`ChoreProvider.choreWorldMap` 是 public 字段，`Chore.choreType.Id`/`gameObject` public）；
`ApplyAttack` 调用后统计出现 chore 的目标数，**回读为 0 时把原因写进 `failedDetails`**，不假装成功。

**验证到哪一步**：编译通过 + 元数据核对（`ChoreProvider` 集合名在 `Components` 上不存在 ⇒ 改为对象自身 `GetComponent<ChoreProvider>()`，
这是本轮唯一因编译失败而修正的判据）；**真机未验证**（未装机）。
装机后判据：`mop` 真做 ⇒ `marked=1` 且 `mopPlacerPresent:true`，再 dryRun 同格 ⇒ `alreadyMarked:true`；
`attack` 真做 ⇒ 目标 `attackMarked:true`（若为 false 且 `failedDetails` 有原因，说明游戏侧拒绝）。
## [新增] 0.92.0 / M6.76 —— `/v1/job` 打标记批次：`disinfect` / `empty_pipe` / `attack` / `capture` / `mop`（2026-11-16）

**依据**：`docs/ACTION-SURVEY.md` §2（2026-11-16 按「标记模型」重写版）—— 用户原话"总之都是标记"。
**`disconnect` 本版不做**（入口 `DisconnectTool.RunOnRegion` 是 nonpub，且要先设计"剪哪条连接"的参数）。

**新增 5 个 type**（`dig`/`build` 未动）
| type | 入口 | 形状 |
|---|---|---|
| `disinfect` | `Disinfectable.MarkForDisinfect(bool)` | 单格 |
| `empty_pipe` | `EmptyConduitWorkable.MarkForEmptying()` | 单格 |
| `attack` | `AttackTool.MarkForAttack(Vector2,Vector2,bool)` | 矩形 |
| `capture` | `Capturable.MarkForCapture(bool)` | 矩形 |
| `mop` | `MopTool.OnDragTool(cell,0)`（反射） | 单格 |

**硬要求全部落实**
* `dryRun` 与真做**共用同一段解析**（`JobMarks.Resolve`，L271）：先解析 `targets[]`，dryRun 就返回；否则逐个 Mark；
* `confirm:true` 强制（与 build 一致）；`dryRun` 不需要 confirm；
* `alreadyMarked` 逐目标报出（disinfect 反射读 `isMarkedForDisinfect`、capture 读 `IsMarkedForCapture`、mop 看 `MopPlacer` 层；**empty_pipe 无公开回读 ⇒ 标 `alreadyMarkedUnavailable=1`**）；
* `NO_TARGET` / `RECT_NOT_SUPPORTED` / `CONFIRM_REQUIRED` / `BAD_REQUEST` / `RETRY_LATER`；
* 每个 Mark 单独 try/catch ⇒ `failed[]`，一个失败不中断整批；
* `worldId` 给了就只在该世界解析；`x,y` 一律 `Grid.XYToCell` 换算；`priority` 走 `Prioritizable.SetMasterPriority`（`PriorityClass` 反射构造）。

**两个编译期发现（写进代码注释与文档）**
1. `MopTool.OnDragTool(int,int)` 元数据标 **public 但编译器 CS0122 拒绝访问** ⇒ 反射调用；本版**不调用 Activate/Deactivate**（没激活就没状态要复原）；
2. `PriorityClass` 无法按名引用（CS0103）⇒ 反射构造 `PrioritySetting`（与 `PlanScreenFix` 同一手法）。

**验证到哪一步**：本版**未装机**（按指示）⇒ 只有"编译通过 + 元数据/IL 核对"；
判据写在 `docs/API.md §3.37.6`（dryRun 该看到什么、真做后 `/v1/marks` 该回读到什么）。
**已知风险**：`empty_pipe` 无幂等回读；`mop` 未激活工具是否能生效未验证；`attack` 会真的伤害小动物（必须先 dryRun）。
## [修复] 0.91.1 / M6.74 —— `isActive` 更正：`activeWorldId` 是对的，错的是比较对象（2026-11-16）

**撤回 0.91.0 的前提**：我曾判断"`activeWorldId=7` 而玩家在 w0 ⇒ 不可信"。**前提错了** ——
小僵当时在看「筛土路星」`MiniRegolithMoonlet` = **w7**（`/v1/state` 的 `colony.worldId=7` + `world.name` + `typeKey=STRINGS.WORLDS.REGOLITHMOONLET.NAME` 三处一致）
⇒ **`ClusterManager.Instance.activeWorldId` 是可信的**；0.91.0 的"以摄像机所在世界判定"已**撤销**（`cameraWorldId` 仅留作附加信息）。

**真正的 bug**：`WorldContainer.id` 与 `Grid.WorldIdx` 的 worldId **不是同一套编号**，0.90.x 直接拿 `wc.id` 比 `activeWorldId` ⇒ 全 false。

**修法（0.91.1）**
```
gridWorldId = Grid.WorldIdx[Grid.XYToCell(offsetX+width/2, offsetY+height/2)]
isActive    = gridWorldId == activeWorldId     // gridWorldId<0 时退回 wc.id == activeWorldId
```
响应新增 `gridWorldId`；顶部保留 `activeWorldId`；`cameraWorldId` 标注"不参与判定"。

**未实机验证**：0.91.1 未装机 ⇒ w7 是否 `isActive:true`、`gridWorldId` 是否为 7，等实测。

**保持不动**：建筑三态（`HitPoints<=0` / `BuildingEnabledButton.IsEnabled` / 其余）与 `displayName`（`Strings.Get(worldType).String`）两处按 0.91.0 保留。
## [修复] 0.91.0 / M6.73 —— `building_broken` 全是误报（三态化）+ `/v1/worlds` 可读名与 `isActive`（2026-11-16）

### 一、`building_broken` 是误报（P0）
实测 0.90.2：14 组 `building_broken` **没有一个是真坏** ——
`FarmStation`（`enabled=false`，玩家关的）、`SpiceGrinder`（`enabled=true,operational=false`，空闲）等；
涉及 `FarmStation/RanchStation/SpiceGrinder/MilkFeeder/SweepBotStation/RailGunPayloadOpener/WarpConduitReceiver/SolidConduitInbox/HabitatModuleMedium/RocketInteriorGasOutputPort/SuitMarker`。
**根因**：判据是「没在运行」（`!IsFunctional`），与 `building_stopped` 同一套，只是严重度标高了。

**三态化**（0.91.0）
| kind | 判据 | severity |
|---|---|---|
| `building_broken` | **`BuildingHP.HitPoints <= 0`**（聚合条目带 `hp`/`maxHp`） | warning |
| `building_disabled` | **`BuildingEnabledButton.IsEnabled == false`** | info（配置，非故障） |
| `building_stopped` | 其余（`!operational && enabled && hp>0`） | info |

> `Operational` **没有** `IsBroken` 属性（成员表核对：仅 `IsFunctional/IsOperational/IsActive`）⇒ "坏"用血量判，
> 不再用"没在运行"冒充损坏；`BuildingHP` 不存在的建筑永不判 broken。

### 二、`/v1/worlds` 给不出星球名
* `displayName`：`overrideName` → **`Strings.Get(worldType).String`**（`worldType` 即 `STRINGS.WORLDS.<X>.NAME` 完整 key）→ 都没有则 **`null`**（不拿内部名冒充）；
* 新增 **`typeKey`**；
* **`isActive` 全是 false 的原因**：实测 `ClusterManager.activeWorldId = 7` 而玩家在 w0 ⇒ 该值不可信。
  改为 **摄像机所在格的世界**（`cameraWorldId`）判定，退回 `activeWorld.id` / `activeWorldId`；三个值都输出。

**未实机验证**：本版未装机 ⇒ 三态的最终条数、`displayName` 的中文串、`isActive` 是否命中 w0，都等装机实测。
## [修复] 0.90.2 / M6.72 —— `/v1/alerts` 半截 JSON（0.90.1 被 JsonCheck 拒绝下发）（2026-11-16）

**症状**：`GET /v1/alerts`（含 `?detail=1`）→ **HTTP=000 SIZE=0**（连接直接关），其它端点正常。
`Player.log`：`[alerts] 生成的 JSON 非法（已拒绝下发）: Unexpected end of content while loading JObject. Path 'info', line 1, position 306. | 长度=306`
—— 前 240 字符正好停在 `"counts":{"alerts":304,…,"buildingGroups":3…`。

**根因**：0.90.1 重写输出段时，**旧代码 `data = sb.ToString();` 残留**且排在 `data = text;` **之后**，
把完整 JSON（前缀 + `alerts` 数组 + 尾段）**覆盖成只剩前缀**（≈306 字节、末尾无 `}`）。
**`JsonCheck` 的判断完全正确，本版未改动它。**

**修复（只改 `Alerts.cs`）**
1. 删除残留赋值 + 紧随的重复 `Log.Info`（截断点）；
2. catch 现在打 **完整异常前 500 字**（`[alerts] 采集异常（完整栈前 500 字）: …`）；
3. 下发前加**首尾 `{` `}` 自检**：不配对 ⇒ `500 INTERNAL` + 长度说明（不替代 JsonCheck，只是提前一层）。

**聚合方向已验证有效**：`counts.alerts=304 / occurrences=2458`（2458→304），`detail=1` 时 `2762/4916`。

**未实机验证**：本版未装机（0.90.2 装机后应由 500/000 恢复为 200 + 聚合后的 `counts`）。
## [改进] 0.90.1 / M6.71 —— `/v1/alerts` 建筑类聚合 + `?detail=1` + 200KB 上限（2026-11-16）

**实测问题（0.90.0）**：`building_stopped` 刚进档 **2447** 条、跑 15s 后 **1152** 条，响应体 **559KB → 280KB**；
其中大量是被动件（`SolidConduitBridge×480`、`LiquidConduitBridge×321`、`SolidConduitInbox×224`、`SolidTransferArm×197`…），
一栋一条会把 **22 条真 broken** 淹掉。

**改动（只动 `Alerts.cs`，`Bridge.cs` 仅加 `?detail` 解析一行）**
* 建筑类**按 `(kind, prefabId, worldId)` 聚合**：`count` + `sample`（≤3 个坐标）；复制人类仍一 dupe 一条；
* 顶部 `counts` 给 **`alerts`（聚合后条数）** 与 **`occurrences`（聚合前原始条数）**，两个都给；
* `?detail=1` 返回逐条明细（此时不再输出聚合条目）；
* **200 KB 硬上限**：超限 ⇒ `alerts:null` + `truncated:true` + `truncatedReason`（**不静默截断**）；
* 尾段（阈值表/source/note）抽成 `AppendTail`，全量与截断两条路径共用。

**刻意不做**：不改成"只报说得清原因的停工"（那要判断"该不该工作"，会引入新误判）。

**未实机验证**：本版未装机 ⇒ 聚合后的条数/响应体大小未实测；预期 `building_stopped` 从 1152 条降到**十几个**（每 prefabId 一条）。
## [新增] 0.90.0 / M6.70 —— `/v1/dupes`（含寻路与卡住探针）+ `/v1/alerts`（两套身体阈值）（2026-11-15）

**新增两个只读端点**
* `GET /v1/dupes[?idle=1]`：每人 `name/dbId/**kind**/gender/cell/x/y/worldId/chore/amounts/amountMeta/path/stuckProbe`。
* `GET /v1/alerts`：按 `critical → warning → info` 排序，带 `value` + `threshold` + `unit` + `hint`。

**⚠️ 本批最重要的实测事实：小人不是同一套身体**
| | `kind:"organic"`（本档 18 人） | `kind:"bionic"`（本档 6 人） |
|---|---|---|
| 有 | `Calories/CaloriesKcal/Bladder/Stamina…` | `BionicOil/BionicGunk/BionicInternalBattery/BionicOxygenTank…` |
| 没有 | — | **没有** `Calories/CaloriesKcal/Bladder/Stamina` |

⇒ 两条设计后果：
1. **`amounts` 只输出实际存在的键，不补 0**（0.89.x 的 `/v1/state` 把缺失 `Calories` 填 0，会把仿生人报成"快饿死"）；
2. **`kind` 判据 = 有没有 `BionicOil`**（实测 6/18 与游戏内一致）；两套阈值分开，告警带 `subjectKind`。

**仿生人三项（电池/油/氧气罐）的单位未确认**（实测电池 566,566~624,104，不像百分比）
⇒ 判据用 **`value / GetMax()` 比例**（20% warning / 10% critical），与单位无关；
`max` 读不到时标 `thresholdUnavailable:true` **只给 value、不给阈值**。
`amountMeta[id] = {min,max,units}` 供调用方自己换算（`units` 是 `Amount.units` 枚举名）。

**寻路**：`Navigator.path` 的真实类型是 **`PathFinder.Path`（struct）** ⇒ 不能判 `!= null`（用 `nodes == null / Count == 0`）；
只返回 `nodes[0]` 与 `nodes[Count-1]` 两格。
**卡住探针**：移动类 chore（前缀 `MoveTo`）+ `hasPath` + 连续 **5s** `pathLen` 未减少且 `cell` 未变 ⇒ `possible:true`，
但**明确不是结论**（等开门/排队/就地工作都可能命中），且第一次查询只建立基线。

**本版不提供**：`building_no_material`（缺料无可靠单一判据，宁可省略）、`dupe_stuck_candidate` 进告警流（可靠性低）。

**未实机验证**：本版**未装机** ⇒ 两个新端点的真实响应未 curl（0.89.2 在跑，`/v1/dupes`、`/v1/alerts` 均 404）；
已验证：编译通过、三处版本一致、`kind` 判据用 `/v1/state` 现网数据核过（`BionicOil` 命中 6 人，且这 6 人确实没有 `Calories`）。
## [修复] 0.89.2 / M6.69 —— 「已标记」判据单点化 + dig 末尾自复核 + neutronium 字段修正（2026-11-15）

**实测现象（0.89.1）**：同块 `dig.marked=419`，紧接着 `area.alreadyDigMarked=180`、`mineable` 419→239（等式仍成立）。
**根因**：`Area` 判"已标记"用 `Grid.Objects[cell, ObjectLayer.DigPlacer] != null`，**太窄**。
**修法**：`CellRules.IsMarkedForDig` 改为**全层搜 `Diggable`**（复用 `JobQuery.FindDiggableAt`），
与 `/v1/marks` 的 `dig.marked` **同一实现**；`Area` 与 `dig.alreadyMarked` 都调它。

**dig 新增请求末尾复核**（不用外部隔几秒抽查）：
* `markedStillPresent`：请求结束时 bbox 内**实际还在**的挖标记数（-1 = 复核失败）
* `markedVanished`：`marked - markedStillPresent`（null = 复核失败）
⇒ 若 `markedVanished` 大 ⇒ `PlaceDig` 返回非 null 但标记随后被撤（游戏自我清理）；接近 0 ⇒ 之前的 180 就是判据窄。
代价：每次 dig 多扫一遍 bbox（预计 +几十毫秒）。

**`cells.neutronium` 修正**：0.89.1 恒 0，因为只比了 `SimHashes.Neutronium`，而游戏里那片不可破坏边界元素叫 **`Unobtanium`**；
现在两个名字都认。字段仍只作信息、不参与可挖判定。装机后该档 `cx=0,cy=0` 的 `neutronium` 应≈281。

**未实机验证**：本版未装机 ⇒ "180 应该变成多少"、"markedVanished 实际多大"都**等实测**；
已验证：编译通过、三处版本一致、`IsMarkedForDig` 被 `Area` 与 `dig` 各调用一次。
## [修复] 0.89.1 / M6.68 —— 可挖判据单点化（area 与 dig 同源）+ `isStub` + `applied.bounds`（2026-11-15）

**实测矛盾（0.89.0）**：同一块（w0 `cx=0,cy=0`）`/v1/area` 报 `mineable=144`、`/v1/job dig` 实际 `marked=419`。
根因：`Area` 自己写了一套更严的判据（把 `Unobtanium` 也当"挖不动"），而 `dig` 走游戏判定。

**修法**：新建 **`src/OniAiBridge/CellRules.cs`** 作为"某格能不能挖"的**唯一出处**：
`有效格 + 实心（Grid.IsSolidCell 兜底 Grid.Solid）+ 无建筑 + 无挖标记` ⇒ 可挖。
`Area` 与 `dig` 区域分支都调它（`dig` 的实心判断已改为 `CellRules.IsSolidForDig`）。

**中子质不拦**（重要）：实测 `dig` 只按"实心+无建筑"预判，中子质能否挖由 `DigTool.PlaceDig` 最终裁定 ⇒
`area.mineable` 是**上界**、`dig.marked` 是**实际**，差额会体现为 `rejected.byReason.NOT_DIGGABLE`；
`Area.cells.neutronium` 只作信息字段。`unmineable` 现在只表示"实心但有建筑"。

**其它两处**
* `GET /v1/worlds` 新增 **`isStub`**（判据写死：`name` 为空 且 `width<=32` 且 `height<=32`）——本档 15 个里 7 个是 32×32 空壳；
  `dupeCount` 明确标未实现（`dupeCountUnavailable:true`，恒 0，下一批次接入）。
* `dig` 响应 `applied.bounds` 不再出现空串：有标记给 `{x0,y0,x1,y1}`，`applied=0` 给 `null`。

**未在游戏里验证**：本版**没有装机**（小僵要求）⇒ 三个改动点的真实响应未 curl；已验证编译通过、三处版本一致、
`CellRules` 被 `Area`/`Job` 各调用一次（`grep` 可复核）。
## [新增] 0.89.0 / M6.67 —— 定位主干：`/v1/worlds` + 区块规则 + `/v1/area` 数字档 + 按块/框挖（2026-11-15）

**新增三个只读端点 + 一个改造**
* `GET /v1/worlds`：每颗小行星一条（`worldId/name/displayName/offsetX/offsetY/width/height/chunksX/chunksY/isActive`）。
  几何字段**全部运行时读 `WorldContainer`**（`WorldOffset`/`Width`/`Height` 都是 **public 属性**；`worldOffset` **字段**才是 nonpub —— 用属性）；
  `displayName` 取 `overrideName`，为空给 `null`；`dupeCount` 目前恒 0（未接统计，已在 note 说明）。
* **区块规则**（`Chunks.cs`，全项目唯一出处）：块 32×32、原点=世界左下角、Y 向下、`cols=ceil(width/32)`、`rows=ceil(height/32)`、
  **末块照常算一块并报告真实 w/h**；块越界 ⇒ `400 BAD_CHUNK`（不静默裁剪）。
  例：world 0 = 240×380 ⇒ **8 列 × 12 行 = 96 块**，末列宽 16、末行高 28。
* `GET /v1/area`：区域**数字档**（不返回逐格数组）。`cells.{total,scanned,mineable,unmineable,vacuum,gas,liquid,solid}` +
  `elements[]`（前 10 + `other`，带 `count`/`massKg`）+ `buildings[]`（按 `prefabId`，**按 instanceId 去重**）+ `bbox`/`chunk`。
  上限 `maxCells` 默认 4096；超了 `AREA_TOO_LARGE`。
  **口径**：逐格（固/液/气/真空都算），与 `/v1/env`（只算气体格 + 迷雾门禁）**不同**，响应 note 已写明。
* `POST /v1/job {"type":"dig"}` 支持 **`cx/cy` 块寻址**与 `x0..y1` 框寻址；响应新增 `marked`/`skipped`/`cellsScanned`/`chunk`（旧字段不删）。

**行为变更（需注意）**：区域/块挖现在**强制 `confirm:true`**（不给 ⇒ `400 CONFIRM_REQUIRED`，message 带格数）。
单格 `dig` 不受影响。理由：一次标上千格属于"手滑会毁存档"的操作。

**所有新响应都挂 `JsonCheck.Validate`**（0.88.1 的坏 JSON 就是漏挂 `geysers` 造成的）。

**未在游戏里验证**：本版**没有装机**（小僵要求），所以三个新端点**尚未实机 curl**；
已验证的是"编译通过 + 成员名元数据核对 + 区块算术用独立脚本核过（240×380 ⇒ 8×12、末块 16×28）"。
## [改进] 0.88.3 —— `/v1/geysers` 默认 `includeInactive=false`（实测：被埋的泉仍 active）（2026-11-15）

**实测（同一场景 worldId=0）**
| 扫描 | 耗时 | total | filteredTemplates | count |
|---|---|---|---|---|
| `includeInactive=true` | 192 ms | 33 | 31 | 2 |
| `includeInactive=false` | **62 ms** | 2 | 0 | 2（同样两口泉） |

**结论**：✅ 被物块掩埋的泉**仍是 active 对象** ⇒ `false` 足够；`true` 只是多扫 31 个 prefab 模板再过滤（192ms vs 62ms）。
⇒ **默认改为 `false`**；`?includeInactive=1`（或 `true`）保留为逃生口。
`filteredTemplates` **保留**（`true` 时才可能非 0，是"丢了多少模板"的唯一可观测点）。

**注意 `total` 语义随实参变化**：`true` ⇒ `total` 含模板（小档 33 / 大档 72）；`false` ⇒ 只含真泉（小档 2）。

**同时确认的事实**：污水泉在 `(139,210)`、`buried=true`、`stateDerived=idle`；
`stateDerived` 真实数据出过 `idle`/`preErupt`；全程游戏 `Responding=True`，**此前卡死与本端点无关**（Harmony Finalizer 签名问题，0.87.0 已修）。
## [修复] 0.88.2 —— `/v1/geysers` 排除预制体模板 + `stateDerived:"unknown"`（2026-11-15）

**实测暴露**：0.88.1 的 `count=total=72` 里，**31 条是内存里的 prefab 原型**
（`x=0,y=0,cell=0,worldId=0,elementId=null,geyserTypeId=null`，`prefabId` 每种泉各一条）⇒ 真实泉只有 **41 个**。
根因：`FindObjectsOfType<Geyser>(true)` 的 `includeInactive=true` 会把未激活的模板也算进来。

**修法（两道过滤，均可观测）**
1. 主判据：`geyser.GetMyWorldId()` 无效（`<0`/`255`）⇒ 丢；
2. 兜底：`x==0 && y==0 && cell==0 && worldId==0` ⇒ 丢（⚠️ 代价：真在 (0,0) 的泉也会被丢）。
两者计入响应新字段 **`filteredTemplates`**；对数关系：`count + filteredTemplates + filteredByWorld == total`。

**新增 `?includeInactive=0`**（默认 true）：用于实测"去掉 `true` 会不会丢真泉"，响应 `scanMode` 写明本次用哪种扫描。
（保留 `true` 的理由：被物块掩埋/未激活的泉不该被漏掉；模板由上面两道过滤兜住。）

**`stateDerived` 增 `"unknown"`**：三个计时器都有值但**全为 0** ⇒ `unknown`（尚未开始/未初始化），
与 `null`（取不到）区分；`note` 已说明。

**实测基线（0.88.1，供 0.88.2 对照）**：`stateDerived` 分布 dormant 13 / erupting 16 / idle 10 / preErupt 1；
`buried` true 43 / false 29（含模板）。改成 0.88.2 后**真实泉应约 41 条**，`filteredTemplates` 应约 31。
## [修复] 0.88.1 —— `/v1/geysers` JSON 非法（双开括号）+ `stateDerived`（2026-11-15）

**Blocker（实测暴露）**：`GET /v1/geysers` 返回 `{"count":72,{"total":72,…}` —— **两个 `{` 一个 `}`**，
`ConvertFrom-Json` 报 `Invalid property identifier character: {. Path 'data.count'`。
根因：`sb.Insert(0, "{\"count\":…")` 在**已经以 `{` 开头**的缓冲前面又插了一个 `{`。
修法：改为 `sb.Replace("{", "{\"count\":" + emitted + ",", 0, 1)` —— **只替换第一个 `{`**，括号配平。

**防复发**：`Geysers.List` 返回前调 `JsonCheck.Validate(data, "geysers")` ——
坏 JSON 会被拦成 `500` 并写 `Player.log`，**不再发出去**（这条机制本来就有，新端点漏挂了）。

**新增 `stateDerived`**（推导值，**不冒充原始阶段**）：
`remainingEruptPreTime>0` ⇒ `preErupt`；`remainingEruptTime>0` ⇒ `erupting`；
`remainingIdleTime>0` ⇒ `shouldGoDormant ? "dormant" : "idle"`。原始 `state` 仍为 `null` + `stateUnavailable`。

**实测基线（0.88.0 实测，本版沿用）**：`FindObjectsOfType<Geyser>(true)` 在 Cycle 6990「外世」档拿到 **72 个泉**，
跨 6 个世界；`elementId` 覆盖 `Steam`/`MoltenNiobium`/`MoltenTungsten`/`Magma`/`CarbonDioxide`；
`buried` 有 true 有 false（**判据有区分度，不是恒真恒假**）。
## [新增] 0.88.0 / M6.64 —— `GET /v1/geysers`（间歇泉，只读）（2026-11-15）

**为什么加**：间歇泉**不在** `Components.BuildingCompletes` 里（实测全档 234 条建筑里没有任何 `Geyser*`/`Volcano*`），
`/v1/buildings` 永远读不到；而间歇泉长在中子质上、被物块掩埋，挖开才激活 ⇒ 必须"埋着也能读到坐标"。

**枚举**：`UnityEngine.Object.FindObjectsOfType<Geyser>(true)`（含 inactive）。
`Geyser` 类型经 `layout.exe types` 确证存在于 **Assembly-CSharp**（同族还有 `GeyserConfig` / `GeyserConfigurator` / `GeyserType`）。

**字段可得性（如实）**
* ✅ `worldId` / `x` / `y` / `cell` / `prefabId`（`KPrefabID.PrefabTag`）/ `elementId`（`configuration.geyserType.element`）/
  `geyserTypeId` / `buried`（`Grid.IsSolidCell`）/ `remainingIdleTime` / `remainingEruptTime` / `remainingEruptPreTime` /
  `lifeTime` / `shouldGoDormant` —— 成员均经元数据核对，编译通过；
* ⚠️ `state`：`Geyser` **没有公开的 phase/state 字段**；反射尝试 `smi.phase`/`smi.state`，**读不到就给 `null` + `stateUnavailable`**（不编）；
* ❌ `nextEruptionCycle`：**不提供**（拿不到周期数）。

**版本**：0.88.0（三处一致，`build.ps1` 校验通过）。
**未做**：没有在游戏里跑过（小僵要求"别装、别启动游戏"）⇒ 端点**尚未实机验证**，
仅完成"编译通过 + 成员存在性核对"。
## [验收记录] M6.60–M6.63 —— 建造链路收口：材料口径 / 假失败 / CELL_OCCUPIED / 优先度 NRE / **1x2 蓝图**（2026-11-15）

### M6.60 材料三口径（`/v1/inventory`）
`inStorage`（储物箱可用）/ `onGround`（地上散落 `Pickupable` 且不在 `Storage`）/ `inWorld`（`WorldInventory`，常 `null` + `inWorldUnavailable`）。
**实测**：`Cuprite` `inStorage=114.904`、`onGround=4922.171` ⇒ 玩家说的"5 吨"= 两者合计，缺口闭合。

### M6.56 材料不足**不阻塞蓝图**
小僵原话："肯定算的，而且就算材料不够也要算，不然没法提前规划了。"
`trybuild` 下 `INSUFFICIENT_MATERIAL` 彻底不再出现；`defbuild` 仍拦；成功响应带 `materials{need,inStorage,onGround,total,enough,used,hint}`（**只报数，不参与判断**）。

### M6.57 假失败修正（`PostProcessBuild` 抛异常但蓝图已放下）
实测：`REFLECT_INVOKE_FAILED` 但回读 `Tile underConstruction=true` —— 两次调用放下两个蓝图。
改为：异常不早退 → 回读（prefab 过滤 + ±1 占地）→ **有对象就按成功返回 + `warning/warningKind:"POST_PROCESS_FAILED"`**；真没建才报失败。

### M6.58 `CELL_OCCUPIED` 顺序
顺序改为 **占用检查 → `IsValidPlaceLocation` → 材料（只报数）→ `TryBuild`**，同格重复稳定得到
`409 CELL_OCCUPIED`（`details` 带 cell/prefabId/underConstruction），不再被游戏的 `BUILD_INVALID` 抢先。

### M6.61 `PlanScreen.GetBuildingPriority()` NRE —— Harmony **Finalizer** 兜底
事实链：`PlanScreen.Instance` / `ToolMenu.Instance` / `ToolMenu.PriorityScreen` **三者都非 null**，
但 `GetBuildingPriority()` 在 `[0x00006]` 抛 NRE（`PlanScreen` 成员表无 priority 字段 ⇒ 不再猜是哪个成员）。
Finalizer：`__exception == null` 时**原样返回、不碰 `__result`**；抛异常时反射构造 `PrioritySetting(PriorityClass.basic, 5)` 兜底并 `Log.Warn`。
开关 `settings.json` 的 **`FixPlanScreenPriorityNRE`**（默认 true；false ⇒ 只记录不吞）。
**实测**：`trybuild Tile` 的 `warningKind` 消失，`gateTrace.planScreenPriorityProbe` 由 `THREW:…` 变 `OK`，Player.log 有 warn。

### M6.62/M6.63 **1x2 建造解决：预览不在目标格**
`gateTrace` 实测 `visualizerCell=136864 (124,215)` vs 目标 `cell=138121 (109,217)`，`visPosMatches=False`。
单格路径直接用传入 `cell`（所以 1x1 一直成功，掩盖了问题）；**多格建筑靠预览位置算占地** ⇒ 静默不建。
0.86.0：`TryBuild` 前把 `visualizer` 挪到 `Grid.CellToPos(cell)`，`finally` 恢复原位。
**实测**：`MineralDeoxidizer @ (109,217)` ⇒ `placed:true / jobState:"under-construction" / footprint=1x2`，
`visualizerMoved=true / from=136864 / to=138121 / builtItemAtCell=true`；
回读 `GET /v1/buildings?x0=109&y0=217&x1=109&y1=217` ⇒ `count=1 MineralDeoxidizer underConstruction=true`。
⇒ **"画蓝图 → 小人来建"的玩家等价路径成立**（不是作弊生成）。

**本轮不变的约定**：`null` ≠ `false`；失败不得伪装成功；`enough`/`warning` 只作信息、不改变判定语义；
"看起来成功"的结论必须有不依赖自身代码的锚（`/v1/buildings` 回读、Player.log、`planScreenPriorityProbe` 由 THREW 变 OK）。
## [验收记录] M6.14–M6.17 —— `sweep` 歧义 / UTF-8 请求体 / 按对象去重 / 存档端点（2026-11-15）

### M6.14 `sweep` 单格歧义检查（0.40.7）
**验了什么**：一格有多堆碎屑时不再"静默只清一堆"。
**怎么验的**：实测 `(162,190)` 等格；区域 `(110,200)-(135,225)`（676 格）逐格对照。
**实测数据**：
```
区域：{"applied":0,"alreadyMarked":0,"ambiguousSkipped":6,
      "rejected":{"count":670,"byReason":{"NO_TARGET":670}}}
硬等式：676 = 0 + 0 + 6 + 670 ✅
逐格扫同一片区域，正好找到 6 格 AMBIGUOUS_TARGET —— 与 ambiguousSkipped:6 一致
```
单格 message 自带 `（M6.14 新增歧义检查：以前会静默只清一堆并返回 ok）`。

### M6.15 请求体 UTF-8（0.41.1）
**验了什么**：中文参数不再变 `?`。
**根因**：`Bridge.cs:116` `Encoding.ASCII.GetString(buf, 0, got)` —— 整个请求（头+体）按 ASCII 解码。
**怎么验的**：`POST {"file":"外世/绝对没有这个.sav","confirm":true}` ⇒ `404 SAVE_NOT_FOUND "file=外世/绝对没有这个.sav"`。
**附带**：入口加 `[req] body(80)=…` 日志（前 80 字符原样），用于以后区分"解码前错"还是"解码后错"。

### M6.16 歧义按 `instanceId` 去重 + 点名三 type 共用（0.40.7/0.40.8）
**验了什么**：同一个对象注册在多层时，不再被数成多个候选（假歧义）。
**证据**：一格三个候选 `Minion @ Minion / @ Pickupables / @ Mover`，`instanceId` **全是 -496656** ⇒ 1 个对象被数 3 次。
**改法**：`EnumerateCellObjects` 按 `GetInstanceID()` 去重，`Layers` 收全部层；`layer`/`filterLayer` 匹配对任意一层生效。
**未验证**：`(159,29)` 两个 `Tile` 是否同 `instanceId` —— **游戏崩了没验成**（见 M6.17）。

### M6.17 存档端点（0.41.0–0.41.2）
**`GET /v1/saves`**：可用，但**只扫本地**（`scope:"local-only(cloud_save_files 未扫)"`）；
`colony/cycle/dups` 恒 `null`（存档头未解析，原因写在 `colonyInfoUnavailable`）。
**`POST /v1/save/load`**：**已禁用（501 LOAD_PATH_UNSAFE）**。
**为什么**：`SaveLoader.Load` 内部 `ClearScene()` → `SaveManager.Load()`，**先毁后建**；
实测在游戏内调用撞单例冲突（`SaveGame` vs `SaveGame(Clone)`）⇒ 返回 false，**但场景已被清空、无法回滚**，
随后每帧空引用 16890 次直至崩溃；主菜单下 `SaveLoader.Instance` 为 null 又调不动。
**⇒ 这是"失败也会造成破坏"的接口，在找到安全入口前不开放。**
## [验收记录] M6.9 `/v1/job` harvest + `/v1/marks` 同源修正 —— **已通过**（2026-11-15，0.39.0 实机）

**版本**：0.39.0 · dll 168960 B · SHA256 `090E8170FEF994CB5C9C89709A19D782475010EEA825D52EAE4E045AE0268657`

### 这一轮拿到的真东西（三件）

#### ① 一个真 bug：两个接口对同一格给出相反结论
`/v1/marks` 读的是 `Harvestable.CanBeHarvested`（**成熟度**），却当成"已标记"返回；
`/v1/job` 读的是 `HarvestDesignatable.MarkedForHarvest`（**标记**）。
实测 `(162,190)` `VineMother`：`job` 说 `markedForHarvest:true`，`marks` 说 `harvest.marked:false`。
⇒ **不是数据矛盾，是定义错误**（两接口读的不是同一个字段）。0.39.0 起两边统一读 `HarvestDesignatable`。

修正后成对实测（同一时刻取两次）：
```
① GET  /v1/marks (162,190)  {"marked":true,"objectName":"VineMother","layer":"Building",
                              "readyToHarvest":true,"choreCreated":false,
                              "hasHarvestable":false,"count":1}     objectsAtCell=5
② POST /v1/job harvest      markedForHarvest:true hasHarvestable:false choreCreated:false
                            choreUnavailableReason:"该对象没有 Harvestable 组件（只有 HarvestDesignatable）⇒ MarkForHarvest 只置标记，不会产生 chore"
③ GET  /v1/marks (162,190)  同上，一字不差
成对判定：job.markedForHarvest=True  marks.harvest.marked=True  一致=True
```

#### ② 一条硬证据：`choreCreated:true`（带对照组）
```
(75,245) BasicFabricPlant（芦苇，已成熟）
→ markedForHarvest:true readyToHarvest:true choreCreated:true hasHarvestable:true
  alreadyMarkedBefore:true shortCircuited:true  objectsBefore:6 objectsAfter:6 placedDelta:0
```
对照组（证明 `choreCreated` 不是常量）：
| 格子 | prefabId | hasHarvestable | choreCreated |
|---|---|---|---|
| `(162,190)` | `VineMother` | false | **false** |
| `(75,245)` | `BasicFabricPlant` | true | **true** |

⚠️ **证据等级限定**：该次请求 `alreadyMarkedBefore:true` + `shortCircuited:true` ⇒
**`MarkForHarvest()` 在本次请求里并未执行**，"造 chore 的是它"属**推断链**（链上有实测支撑，非单次直接观察）。
要变成单次观察：**一株没标过的熟植物，首次调用看到 `alreadyMarkedBefore:false` + `choreCreated:true`**。

#### ③ 两条跑不通的路径：**诚实记录"未验"，不是"让它过"**
- `NOT_RIPE` 那批 —— **环境限制**（该存档植物长期未生长，全没熟）；后来找到熟芦苇后已补验；
- **清单 #5「唯一可拆 ⇒ 直接执行」—— 结构限制，疑不可达**：
  实测 5 个"只含 1 个建筑"的格子**全部 409**，因为同一对象注册在多层
  （`Tile` → `Building`+`FoundationTile`；电线 → `Wire`+`WireTile`）⇒ 只要格里有地砖或电线，可拆数就 ≥2。
  **保留代码，标"理论路径、未遇到、可能不可达"，不构造合成场景。**

### 其它已验证（0.39.0）
- **`/v1/buildings` 超时语义修正**：`504 TIMEOUT` → **`503 RETRY_LATER` + `Retry-After: 1`**，
  并给结构化 `details{pumpAgeMs,queueDepth,waitedMs,wasBusy}`；六个端点统一（`TIMEOUT` 错误码全库归零）。
  实测数据：`totalItems=72792`，`Buildings.Query` 本身 **5~13 ms**（22000 条 13 ms），
  慢的是 **`MainThread.Invoke` 排队**（大存档加载期主线程忙）⇒ 语义应是"还没排上队"，不该叫"超时"。
- **`MISSING.` ⇒ `null`**：`LiquidConduitRadiant @ LiquidConduitTile`（`filterLayer=Default`）返回 `filterLayerName: null`。
- **`/v1/marks`**：`okLayers:45 / errLayers:0`，四类标记状态都能读。

### 本轮新增 DESIGN 判据
- **§10.6aa** 工具的输出是"视图"，不是"事实"（dump 说 `public chore`、编译器报 `CS0122`，根因：dump 只有两态）
- **§10.6ab** 命令被如实拒绝，比命令"看起来成功"值钱（`409 NOT_APPLIED` 正例）
- **§10.6ac** 能力"合法地不适用"时，要解释为什么，而不是沉默或报错（`choreUnavailableReason`）
- **§10.6ad** **短路返回的字段，证明不了这次调用做了什么**（`choreCreated:true` + `shortCircuited:true` 的陷阱）
## [验收记录] M6.5 `/v1/job` dig 区域模式 —— **已通过**（2026-11-14，0.36.0 实机）

**版本**：0.36.0 · dll 157184 B · SHA256 `3E5B3FD4E9BB99AA499E68DF8FBC90275DBF2CAEC4A303FF80F07CED26B1AA31`

### 实测（两种模式都跑了）

**① 单格 `{"type":"dig","x":100,"y":250}`**
```json
accepted:true jobState:"marked" markedObject DigPlacer#-446982 diggableFound:true
alreadyMarkedBefore:false shortCircuited:false
objectsAvailable:true objectsBefore:0 objectsAfter:1 placedDelta:1
solidBefore:true solidAfter:true priorityRequested:null partial:false
```
⇒ **7 个字段一个不少**：单格路径**没有退化**（此前只有代码级证据，这次实机确认）。

**② 区域 `{"x0":98,"y0":242,"x1":112,"y1":246}`**
```json
requested: cells=75 worldId=0
applied:   count=58 bounds={98,242,112,246} isContiguous=false
alreadyMarked: 0
rejected:  count=17 byReason={OUT_OF_WORLD:0, HAS_BUILDING:5, NOT_DIGGABLE:12, READBACK_FAILED:0}
elapsedMs: 5
```
⇒ **硬等式成立：`58 + 0 + 17 = 75 = requested.cells`**

### 结论
- ✅ **区域模式可用**，且**等式成立**（不是"看起来差不多"）。
- ✅ **`rejected.byReason` 分类出了具体原因**：5 格有建筑（`HAS_BUILDING`）+ 12 格非实心（`NOT_DIGGABLE`）。
- ✅ **`isContiguous:false` 是正确信息**：该区域里确实有缝（建筑 + 空地），所以 `bounds` 会偏大 ——
  这正是"发大矩形、实际覆盖偏小"要靠它看出来的场景。
- ✅ **单格路径 7 个字段实机确认仍在**（`markedObject / diggableFound / alreadyMarkedBefore / shortCircuited /
  objectsAvailable / objectsBefore / objectsAfter / placedDelta`）。
- ✅ **`alreadyMarked:0` 与已知事实一致**：之前那批标记在 (100,250)/(105,190)/(110,230)，不在本矩形内。
- ✅ **`objectsBefore:0 → objectsAfter:1`**：计数在单格路径上也对。

### 未验
- `priority` 逐个设的行为（本次未传 `priority`）；
- `listCells:true` 的逐格列表输出；
- `REGION_TOO_LARGE` / `TOO_MANY_CELLS` 两条上限分支（未构造该规模请求）。
## [验收记录] M6.2 `/v1/job` `type=dig` —— **已通过**（2026-11-14，0.33.3 实机）

**版本**：0.33.3 · dll 135680 B · SHA256 `E101EC21C8AEDAD9F8F5A8CE26E988D9F64C5992440B4CF3B8DAE081EA6FF982`

### 实测（游戏里真跑，不是推断）
```
第一次（三个全新格）:
(100,250) accepted=True already=False short=False id=-446630 delta=1 avail=True
(105,190) accepted=True already=False short=False id=-446662 delta=1 avail=True
(110,230) accepted=True already=False short=False id=-446694 delta=1 avail=True
第二次（同三格，期望判重）:
(100,250) already=True short=True id=-446630 delta=0 avail=True before=1 after=1
(105,190) already=True short=True id=-446662 delta=0 avail=True before=1 after=1
(110,230) already=True short=True id=-446694 delta=0 avail=True before=1 after=1
```

### 结论
- ✅ **端点可用**：`POST /v1/job` `type=dig`，三次全新放置全部 `accepted:true` / `jobState:"marked"`。
- ✅ **幂等成立**：同坐标第二次 `alreadyMarkedBefore:true`、`shortCircuited:true`、`placedDelta:0`，
  且 **`instanceId` 两次完全一致**（-446630 / -446662 / -446694）⇒ 短路确实**没有调用 `PlaceDig`**。
- ✅ **计数可用**：`objectsBefore/After = 1/1`（不再是 `-1`），`objectsAvailable:true`。
- ⚠️ **最硬的教训**：`Grid.ObjectLayers[cell]` 这条访问路径在 **0.33.1 / 0.33.2 是错的**
  （拿不到已存在的标记物 ⇒ `alreadyMarkedBefore` 恒 false、短路形同虚设）；
  **0.33.3 改用 `Grid.Objects[cell, layer]` 后正常**（层数取 `ObjectLayer.NumLayers`，不硬编码层号）。
- ⚠️ **未验**：`0.33.4` 里新增的**逐层 try/catch 诊断分支**（`errLayers>0` 那条）**从未被触发过**。
  该版未发布，留作"下次出问题时的定位工具"。

### 为什么这次算"真过了"（双锚）
- **锚 1（游戏产出）**：两次 `instanceId` 一致 ⇒ 游戏侧没有重复建标记物；
- **锚 2（本服务实现）**：`alreadyMarkedBefore/shortCircuited/placedDelta` 全部符合预期；
- **两个锚同时成立**才算过。**上一轮只有锚 1 成立、锚 2 是假的**，这正是"把自家查询当 ground truth"的代价（见 DESIGN §10.6u）。

### 测试方法的演进（值得记）
旧法：比对"上次记下的 instanceId"（依赖存档还在，用户退游戏没保存就失效）。
新法：**先全新放三个**（拿新 ID + 期望 `delta=1`），**再同坐标发**（期望 `delta=0` + ID 不变）
⇒ 一次同时验证「新建会建」与「重复不建」**两个方向**，比旧法更强。
## [验收记录] M5.4 /v1/buildings —— **过滤确认在运行**（抽样验证，非全量）（截至 0.32.1）

### 实测数据（同一会话，前后两次查询）
```
第一次（殖民地刚加载完）: count=192  scanned=192   filteredByFog=0     filteredInvalid=0
第二次（世界跑完加载）  : count=192  scanned=2827  filteredByFog=2635  filteredInvalid=0  skipped=0
采样: (60,222)/(65,222)/(70,222)/(300,100) → Visible=0    (111,222) → Visible=255
日志: [buildings] count=192 scanned=2827 filteredByFog=2635 filteredInvalid=0 skipped=0 … | 硬等式 OK
等式: 192 + 2635 + 0 + 0 = 2827 ✓
```

### 结论（措辞按此，不要写过头的）
- ✅ **过滤逻辑确认在运行**：`filteredByFog=2635` ⇒ 有 2635 个建筑因 `Grid.Visible[cell]==0` 被挡在结果之外。
  总建筑数约 `192 + 2635 = 2827`。
- ✅ **不误伤**：抽查可见建筑所在格 `Visible` 全为 `255`。
- ✅ **账平**：`scanned == count + filteredByFog + filteredInvalid + skipped`，日志 `硬等式 OK`。
- ⚠️ **这是抽样验证，不是全量验证**：没有把 2635 个逐个采样核对"它确实在 `Visible=0` 的格子上"。
- ⚠️ **`filteredInvalid` 分支仍未被走过**（两次都是 0）。

### 第一次 `filteredByFog=0` 的真因（**不是 bug，但也不是"样本太小"这么含糊**）
第一次查询：`scanned=192, filteredByFog=0`。**`scanned` 本身就比后来小 14 倍**（192 vs 2827），
说明**建筑注册尚未完成**（`Components.BuildingCompletes` 还没登记全）。
因此 `filteredByFog=0` 的**双重原因**是：
1. **当时只登记了 192 个**（扫描基数不完整）；
2. **这 192 个恰好全在亮处**（门禁没被触发）。
⇒ 准确说法是「**扫描基数尚未完整**」，**不能简单说"样本太小"**。判据见 DESIGN §10.6o。

### 关于"是不是没开去雾就只能看到 192 个" —— **不是**
判据是 **`scanned=2827`**：**扫描是全量的**（2827 个建筑都进了循环），`count=192` 是**筛完剩下的**。
如果"只能看到 192 个"，那 `scanned` 也会是 192。
**但用户的直觉指出了真问题**：扫描虽然是全量的，**"全量"本身随时间增长**（192 → 2827）。

### 仍未确认
- `DebugHandler.RevealFogOfWar` **到底改不改 `Grid.Visible`**：实测没验证成
  （按 Backspace 打开的是 debug 工具菜单，不是去雾开关）。**此条保留"未确认"。**
  但**当前功能不需要它** —— 过滤有效性已由 `filteredByFog=2635` 证明。
## [0.32.1] - 2026-11-14 — M5.4a 拆 `filteredCount`：`filteredByFog` + `filteredInvalid`

**未装**。起因：`filteredCount` 把"被迷雾滤掉"和"算不出坐标（读不到）"混成一个数 ——
调用方看到 `filteredCount=5` 会以为"有 5 个建筑藏在黑地里"，实际可能是"5 个建筑算不出坐标"。
**这是会让人做错判断的字段**（与 0.32.0 立的"`null` 只表示概念不适用"是同一条道理）。

### 变更
| 字段 | 含义 |
|---|---|
| `filteredByFog` | 被迷雾挡掉（`Grid.Visible[cell] == 0`） |
| `filteredInvalid` | 算不出有效格子（`GetCell()` 失败或 `Grid.IsValidCell == false`）⇒ **与迷雾无关** |
| `filteredCount` | **两者之和**（向后兼容保留；文档写明它是"和"） |

`visibility` 对象里也同时给了拆分后的两个字段。

### 硬等式（写进文档 + 日志自检）
```
scanned = count + filteredByFog + filteredInvalid + skipped        // 通用
offset=0 时：scanned = count + filteredByFog + filteredInvalid
两种模式（AIEnableFog=true/false）下 filteredInvalid 应相等（与迷雾无关）
同一 offset/limit：count_all = count_fog + filteredByFog
```
为让等式**精确**成立，`limit` 判定提前到"扫描之前"（否则被 break 掉的那一条会算进 `scanned` 却不属于任何一类）。
日志里直接带判定结果：`| 硬等式 OK / FAIL`。
### 设计判断（升为通用规矩）：**`null` 只能表示"这个概念不存在"，不能表示"没查到"**
本版 `enabled` 对"没有 `BuildingEnabledButton` 的建筑"（砖块、门的一部分等）返回 **`null`**，而不是 `true`。
理由：调用方看到 `null` 会去查文档；看到 `true` 会直接当"开着"用 —— 给它 `true` 就是**编数据**。
**规矩**：任何字段的 `null` = **概念不适用**；"取不到 / 读失败"必须另用 `unavailable` / `*Unavailable` 之类的降级字段表达。
两者混用会让调用方**无法判断该不该重试**。
## [0.32.0] - 2026-11-14 — M5.4 `GET /v1/buildings`：第一个带迷雾过滤的场景查询

**运行时未验证**（未装）。

### 端点
```
GET /v1/buildings?offset=0&limit=500      // limit 默认 500，上限 2000
```
返回：
```json
{ "buildings":[ {"prefabId","name","x","y","cell","worldId","operational","enabled"} ],
  "count": N, "offset": 0, "limit": 500, "scanned": M,
  "truncated": false,
  "fogged": true,
  "visibility": { "aiEnableFog": true, "filteredCount": K, "rule": "Grid.Visible[cell] > 0" } }
```

### 设计要点
- **门禁在采集循环里**：逐个建筑 `Vision.Allow(cell)` 判定通过才继续采集，**没有"先全查再 filter"**；
  无效格也算"看不见"。
- **`fogged` + `visibility.filteredCount`**：调用方一眼能看出"这 42 个是全部，还是被迷雾滤掉 K 个之后剩下的"。
  `rule` 字段直接把当前判据写进响应（`"Grid.Visible[cell] > 0"` 或 `"none (omniscient)"`）。
- **上限与分页**：默认 `limit=500`、上限 `2000`、支持 `offset`；超限时 `truncated: true`。
- **字段刻意精简**（字段一多，AI 读起来就贵）：只有 `prefabId/name/x/y/cell/worldId/operational/enabled`。
  温度、材料、是否在用等**留到后续版本**。
- **原生判定**：`operational` ← `Operational.IsOperational`；`enabled` ← `BuildingEnabledButton.IsEnabled`。
  **没有 `BuildingEnabledButton` 的建筑**（砖块、门的一部分等）⇒ `enabled: null`，
  **不假装 true**（避免调用方误判"它是开着的"）。
- 枚举源：`Components.BuildingCompletes`（已建成的建筑），不是配置表。
- 采集异常时**返回已采集的部分**并 `Log.Warn`（不整体 500）。

### 日志
`[buildings] count=N/scanned=M filteredByFog=K offset=0 limit=500 truncated=False fogged=True`
＋ 每次响应一行 `[vision] AIEnableFog=true ⇒ 仅 Visible>0（迷雾内）  [buildings]`。

### 验收方法（lead 提的"一眼验证"）
1. `/v1/debug/vision-sample?x=&y=` 找一块 `gridVisibleRaw == 0` 的格子，记下那格上的建筑；
2. `/v1/buildings` 里**不应出现**它，且 `visibility.filteredCount ≥ 1`；
3. 把 `AIEnableFog` 关掉（设置页取消勾选并保存，或直接改 `settings.json` 后重启 mod）→ 该建筑**应出现**，
   且 `fogged: false`、`rule: "none (omniscient)"`。
## [0.31.0] - 2026-11-14 — M5.3 `AIEnableFog`：视野迷雾开关（判据 `Grid.Visible[cell] > 0`）

**运行时未验证**（未装）。判据来自用户：「**有亮度就算视野，毕竟对于人来说就是如此的**」
⇒ 用 `Grid.Visible[cell] > 0`（宽松：边缘一点点亮也算），**不用** `Grid.Revealed`（严格布尔）。

### 配置项
`AIEnableFog`（bool，**默认 `true`**）：勾上 = 有迷雾（AI 只报看得见的地方）；不勾 = 全图。
- 读写走 `BridgeSettings`：解析 + 写盘 + **回读校验（`SaveAndVerify` 逐个字段比对）**；
- **键名按用户指定用 `AIEnableFog`**（游戏内部叫 FogOfWar，此处刻意不改名）；
- **与 `allowCheats` 完全独立**：allowCheats 管"能不能动手改状态"，这个管"能看多少情报"。
  设置页里两者之间**刻意空一行**（`Separator`），避免让人以为有关联。

### 设置页
新行文案：`开启ai视野迷雾，不勾选则能看到全图`，勾选框在右（与 allowCheats 同款 `PCheckboxLite`）。
纳入 M4.4 的"草稿 + [保存] 才写盘"流程：勾选只改内存草稿，保存时与其它字段一起校验/写盘/回读，
失败整批回滚（`AIEnableFog` 也在回滚集合里）。

### 视野门禁（唯一实现）
```csharp
Vision.FogOn      => settings.AIEnableFog（配置缺失时按 true 兜底：宁可少给，不多给）
Vision.Allow(cell) => !FogOn || Grid.Visible[cell] > 0     // 出错按"看不见"处理
Vision.ModeName    => "fogged" / "omniscient"
```
**硬要求**：必须在**采集循环里逐项判断**，不许"先全查再 filter"（漏一个出口就泄露全图）。
`/v1/catalog`（静态字典）与 `dupes[]`（小人在哪你当然知道）**不过滤**。

### `/v1/meta`
新增两个字段：`aiEnableFog`（配置原值）+ `vision`（派生结果 `"fogged"` / `"omniscient"`）。

### 断言日志
`GET /v1/state` 每次响应打一行：
`[vision] AIEnableFog=true ⇒ 仅 Visible>0（迷雾内）  [state]` 或 `… ⇒ 全图（omniscient）  [state]`。

### ⚠️ 现状：过滤逻辑已就位，但**当前没有任何"逐格场景数据"可过滤**
`/v1/state` 现有三块内容，逐项判定：
| 块 | 内容 | 是否按视野过滤 | 理由 |
|---|---|---|---|
| `colony` | 周期/暂停/速度/世界名与世界尺寸 | **否** | 全局调度与舞台信息，不含"某格有什么" |
| `dupes[]` | 每个小人的 `cell` / 需求 / 当前 chore | **否** | lead 明确：小人在哪你当然知道 |
| `elements[]` | `WorldInventory` 的**元素总库存（kg）** | **否** | 是"仓库里有多少"，不是"哪块地有什么" |
⇒ 目前 `Vision.Allow()` 只被 `/v1/debug/vision-sample`（诊断端点）调用（多一个 `allowByFogGate` 字段）。
**第一个真正需要滤镜的场景查询（如 `/v1/buildings`、`/v1/map`）尚未存在** —— 那是独立议题，等指令。

### 顺带记录（写进 API 文档）
`GridVisibility.OnCellChange` 只有 `GameTags.Dead` 一个过滤，**没有 Minion/Creature 区分**
⇒ **野生生物走过也会点亮格子**（前提是该 prefab 挂了 `GridVisibility`）。
⇒ "AI 能看到只有野生动物去过的地方"是**特性不是 bug**（人也看得到动物踩过的痕迹）。
## [验收记录] M5.2 /v1/catalog 建筑字典 —— 已通过（2026-11-14）

**版本**：0.30.4 · dll 110592 B · SHA256 `93CA51D40FC3098B05621C8CC4BBE43EBF1EA0B8C1536DC550823E1BEF066C8C`
**验收环境**：主菜单（未进殖民地）· 原始响应 71549 字节（JSON 合法）

### 1. 数字三处一致：**510 / 76 / 6**
```
data.count = 510   data.defsCount = 510   data.buildings.length = 510
data.categories = 76   其中 isTop:true = 6
```
**判据（以后所有"生成类接口"都照这条）**：`count` / `defsCount` / 数组实际长度**三处必须相等**，
且日志里那行数字必须是同一口径 —— 实测日志 `buildings=510/510（写出/总数） bytes=71449` 与响应一致。
这条来自一次真实事故（0.30.1 漏 `{` 导致 `count=1` 而日志 `buildings=510`，两边对不上才发现问题）。

### 2. 降级字段两个都有值（`有原因 ⟺ 字段存在`）
```
data.categoriesUnavailable    = "PlanScreen.Instance 为 null（不在游戏内？）"
data.topCategoryUnavailable   = "找不到 BuildMenuCategoriesScreen 实例（建造菜单未打开过）"
```
**自检**：`CheckDegradeField(json, field, reason)` —— 有原因但字段没进响应、或无原因却有字段，都打
`[catalog] 降级字段不一致：…`。本次**无告警**，说明字段与原因同步。
**为什么必须进响应**：`categories: []` 会让接 API 的人以为"这游戏没有分类"，而真相是"当前拿不到"——
这决定对方**要不要重试**。所以：有原因 ⇒ 字段在且有文本；成功 ⇒ 字段**不出现**（不是空字符串）。

### 3. `PlainText` 剥掉 Unity 富文本标记
`data.buildings[AdvancedApothecary].name = "核能配药桌"`（无 `<`），`width=3 height=3` ✓
剥除规则：所有 `<...>`（`<link="X">`、`</link>`、`<i>`、`<b>`、`<color=…>` 等）；离实验证 5 组样例全对：
`<link="ADVANCEDAPOTHECARY">核能配药桌</link>` → `核能配药桌`；`藻类 <link="X">琉璃砖</link>` → `藻类 琉璃砖`。
**升为通用规矩**：API 返回**数据**，不返回要渲染的富文本；使用方想渲染自己加。

### 4. 手写 JSON 护栏（`JsonCheck`）
- 挂在 **5 个出口**：`meta` / `health` / `commands` / `debug-mods` / `catalog`。
- 实现：`Newtonsoft.Json`（游戏随包 7.0.0.0，不额外打包）解析一遍 + **自己扫尾随逗号**
  （实测 Newtonsoft 对 `{"a":1,}` 是宽容的，会放过）。
- 失败 ⇒ `Log.Warn` + 抛 `InvalidOperationException` ⇒ 端点 500，**不把坏 JSON 发出去**。
- **离线断言 10 条全过**：正常✔通过；**漏对象开括号（0.30.1 的真 bug 形状）**✔拒绝；漏逗号✔拒绝；
  尾随逗号（对象/数组）✔拒绝；字符串内逗号不误报✔；转义引号后逗号不误报✔；未转义引号✔拒绝；
  截断✔拒绝；中文+转义✔通过。
- **意义**：漏 `{` 这类"能返回 200 但客户端解析失败"的错，**本可以被它当场挡住**。

### 5. 已知边界（**不是 bug**）
**主菜单下 `PlanScreen.Instance == null`** ⇒ `tagCategoryMap` 拿不到 ⇒ 每条建筑 `categories: []`、
`topCategory` 为 `null`，并由 `categoriesUnavailable` 说明原因；`BuildMenuCategoriesScreen` 实例也只在
**打开过建造菜单**后存在 ⇒ `top` 全 `null` + `topCategoryUnavailable`。
**进殖民地（或打开一次建造菜单）后这些都会变成真实值**。
⇒ 本次验收就是在主菜单做的，所以两个 `Unavailable` 有值是**符合预期**的。

### 6. 已知小瑕疵（不修，记一笔）
日志 `[catalog] 前N条: …` 打的是 **`d.Name` 原名（含 `<link=…>` 标记）**，而响应里是**剥过**的名字。
⇒ 看日志会误以为"剥标记没生效"（验收时差点因此去查）。
**以后顺手修**：日志也走 `PlainText()`，保持"日志与响应同口径"。
## [0.30.4] - 2026-11-14 — M5.2e 三个真问题 + 手写 JSON 护栏生效

> 版本说明：`0.30.3` 是**未发布的中间版本**（构建成功但 `Escape` 被误插校验，运行时会炸），
> 已被 `0.30.4` 取代，从未安装。`0.30.2` 是被实机验证过"JSON 合法"的版本。

### 1. 日志把 `HashSet` 当数字拼了（真错）
0.30.2 的日志行 `buildings=" + emitted + ...` 里 `emitted` 是分类去重用的 `HashSet<string>`，
打出来是 `System.Collections.Generic.HashSet`1[System.String]/510`。改成 `emittedBuildings`（int）。
现在日志：`buildings=<写出>/<总数> bytes=<长度> categoriesErr=… topErr=…`。

### 2. `categoriesUnavailable` / `topCategoryUnavailable` 必须与原因"同进同退"
新增 `CheckDegradeField(json, field, reason)`：有原因但字段没进响应 ⇒ `Log.Warn("[catalog] 降级字段不一致：…")`；
没原因却有字段 ⇒ 同样告警。**判据写死在代码里**：`categoriesErr != null ⟺ categoriesUnavailable 存在`。

### 3. 名字剥掉 Unity 富文本标记（通用规矩）
新增 `Catalog.PlainText()`：去掉所有 `<...>`（`<link="X">`、`</link>`、`<i>`、`<b>`、`<color=…>` 等）。
用在**所有给人看的字符串**（建筑名、分类名）。实测 5 组样例：
`<link="ADVANCEDAPOTHECARY">核能配药桌</link>` → `核能配药桌`；
`藻类 <link="X">琉璃砖</link>` → `藻类 琉璃砖`；`<i>手动气闸</i>` → `手动气闸`。
**规矩**：API 返回数据，不返回要渲染的富文本；使用方想渲染自己加。

### 4. 手写 JSON 的下发前护栏（`JsonCheck`，本轮已生效）
`JsonCheck.Validate(json, tag)`：`Newtonsoft.Json`（游戏随包 7.0.0.0，不额外打包）解析一遍，
再自己扫"尾随逗号"（Newtonsoft 对尾随逗号是宽容的，实测 `{"a":1,}` 能过）。
失败 ⇒ `Log.Warn` + 抛 `InvalidOperationException` ⇒ 端点回 500，**不把坏 JSON 发出去**。
挂在 5 个出口：`meta` / `health` / `commands` / `debug-mods` / `catalog`。

**离实验收（10 条断言全过）**：正常 JSON ✔ 通过；**漏对象开括号（0.30.1 真 bug）** ✔ 拒绝；
漏逗号 ✔ 拒绝；尾随逗号（对象/数组）✔ 拒绝；**字符串内逗号不误报** ✔；转义引号后逗号不误报 ✔；
未转义引号 ✔ 拒绝；截断 ✔ 拒绝；中文+转义 ✔ 通过。

### 5. 途中自查出的坑（记一笔）
第一次挂护栏时，脚本把校验插进了 **`Bridge.Escape()`**（转义函数，产物不是 JSON）⇒ 运行时会对每个字符串抛异常。
发现方式：检查"每个 `JsonCheck.Validate` 属于哪个方法"，发现一个落在 `internal static string Escape`。
已还原 `Escape`（只转义、不校验），把校验挂到 `Health()`。**⇒ 插桩后要核对"落在哪个方法里"，别只看编译通过。**
## [0.30.2] - 2026-11-14 — M5.2c 修 `/v1/catalog` 的 JSON 非法（`count=1` 真因）

**实机现象**（0.30.1）：日志 `buildings=510`，但接口 `count=1`、`categories=0`，
且 `categoriesUnavailable` / `topCategoryUnavailable` **字段存在但为空**。

### 真因：**每个建筑对象漏了开括号 `{`**
生成循环里写了 `sb.Append('}')` 结尾，却没写开头的 `sb.Append('{')` ⇒ 整段 JSON 非法：
`"buildings":["prefabId":"A",...,"prefabId":"B",...]`。
非法 JSON 送到客户端后，解析器只能得到部分/错误结果 —— 于是 `count` 与 `categories` 都读成了错值，
而两个降级字段"存在但空"也是同一个原因（键能扫到、值解析不出来）。

### 修法
1. **补 `sb.Append('{')`**（每个建筑对象的开括号）。
2. **计数口径统一**：新增 `emittedBuildings`，响应里 `count` 用它；另加 **`defsCount`**（`Assets.BuildingDefs` 总数，含 null 项）。
3. **日志改成 `buildings=<写出>/<总数>（写出/总数）`**，并在两数不等时 `Log.Warn("[catalog] 条数不一致：…")`。
4. **新增前 3 条日志**：`[catalog] 前N条: <prefabId> | <name> | <w>x<h>`（若前几条 prefabId 为 `<null>`，说明是取字段的问题；正常则说明是序列化问题）。

### 顺带记录的教训（第 5 次同型）
**"日志里说的数字"和"接口返回的数字"必须相等** —— 0.30.1 正是靠日志那行 `buildings=510` 才发现不对。
⇒ 以后所有"生成类接口"都必须在响应里带上**与日志同一口径的计数**（这次是 `count` + `defsCount`）。

### 未变
`isTop` 的 76 条逻辑没动（本轮没验到它出错）；`/v1/state` 没动；`categoriesUnavailable` / `topCategoryUnavailable`
的产生条件没动（本轮只是让 JSON 合法后它们能正确出现）。
## [0.30.1] - 2026-11-14 — M5.2b `categories` 改成 **76 条完整清单**

**运行时未验证**。上一版 `categories[]` 只从 `tagCategoryMap` 的**值**收集（都是子分类），
6 个顶层页签只作为 `top` 字段的值出现、本身不是条目 ⇒ 接口回答不了"顶层有哪几个"。
按"能一次答完的别让人家查两次"改成完整清单。

### 变更
- `categories[]` = **6 条 `isTop:true`（`top` 指向自己）+ 70 条 `isTop:false`（`top` 指向所属顶层）**，共 **76**。
- 6 个顶层键与 70 个子分类键**编译进 dll**（`TopKeys` / `KnownSubKeys`，来源 `.po` 里
  `STRINGS.UI.NEWBUILDCATEGORIES.<KEY>.NAME`；顶层 = 值带 `<link="BUILDCATEGORY...">` 的那 6 个）。
  理由：用户要求"拿不到顶层归属时，顶层条目照样出" ⇒ 不能只依赖运行时反射。
- 子分类 = **编译进的 70 个 ∪ 运行时 `tagCategoryMap` 的值**（并集）⇒ 游戏新增分类也不会漏报。
- `top` 字段：能反射到 `categorizedCategoryMap` 就填真实顶层；反射失败则 `null` + 顶部
  `topCategoryUnavailable`（顶层条目仍照常出，`isTop:true`）。
- `isTop` 不再恒为 `false`（上一版的已知未确认已消除）。

### 验收（lead 会数）
`categories` **76 条**、其中 **6 条 `isTop:true`**；`count` ≈ 593；`AdvancedApothecary` 宽高与分类正确。
## [0.30.0] - 2026-11-14 — M5.2 只读建筑目录 `GET /v1/catalog`

**运行时未验证**（验收：游戏开着 `curl /v1/catalog`）。**没有改任何现有端点、没有碰视野逻辑。**

### 新端点
`GET /v1/catalog`（只读；走主线程；结果**内存缓存一次**，不落盘）
```json
{ "buildings":[ {"prefabId","name","width","height","objectLayer","categories":[],"topCategory"} ],
  "count": N,
  "categories":[ {"key","name","isTop","top"} ],
  "categoriesUnavailable": "反射失败：<原因>",      // 仅失败时出现
  "topCategoryUnavailable": "…" }                  // 仅失败时出现
```

### 数据来源（都已在元数据/IL 里核实）
- 建筑：`Assets.BuildingDefs`；`PrefabID`/`Tag` 来自**基类 `Def`**（不在 `BuildingDef` 自己身上，
  所以之前按 `=BuildingDef` 过滤看不到）；尺寸 `WidthInCells/HeightInCells`；层 `ObjectLayer`。
- 分类：`PlanScreen.tagCategoryMap : Dictionary<Tag,HashedString>`（**nonpub ⇒ 反射**）；
  IL 实证键就是 `BuildingDef.Tag`（`tagCategoryMap[def.Tag]`）。
- 顶层分类：`BuildMenuCategoriesScreen.categorizedCategoryMap`（**nonpub**，顶层→子分类列表）反查；
  该屏幕只在打开过建造菜单后存在，取不到就 `topCategory: null` + `topCategoryUnavailable`。
- 分类中文名：`Strings.Get("STRINGS.UI.NEWBUILDCATEGORIES.<KEY>.NAME")`（运行时本地化，不硬编码）。

### 降级（硬要求 3）
`tagCategoryMap` 拿不到 ⇒ 每条建筑 `categories: []` + 顶层字段 `categoriesUnavailable: "反射失败：<原因>"`，
**不抛 500**；`topCategory` 同理有 `topCategoryUnavailable`。

### 已知未确认（写在这里免得以后误判）
- `isTop` 目前一律 `false`：分类表是**从 `tagCategoryMap` 的值里收集的**（都是子分类键），
  6 个顶层页签（`BASE`/`FOODANDAGRICULTURE`/`HEALTHANDHAPPINESS`/`INDUSTRIAL`/`INFRASTRUCTURE`/`LOGISTICS`）
  只出现在 `top` 字段里、没有作为 entries 出现 ⇒ 若要"76 条 + 6 条 isTop=true"，
  需把顶层键也并进列表（**待用户确认是否这个形态**）。
## [验收记录] M4.4 设置页「攒改动 + 保存才写盘」—— 部分通过（①②③ 已验；④⑤ 待验）

**版本**：0.29.0 · stage M4.4 · dll 101888 B · SHA256 `878D5665A2A899B9D7159CCCD135C42ED619132D248FB3E31A0B30FA10D032C5`

### ① 「改了不保存 ⇒ 磁盘必须不动」—— 已验（**本版最重要的语义变化**）
实测（用户手点）：
```
[设置页] 文本变化[alertStress]: "60"（草稿，未写盘）
settings.json 里 alertStress 仍为 50        ← 关键判据
目录里 0 个 .bak 文件
```
**判据是磁盘实际内容，不是接口返回值** —— 这条专门用来防"看起来保存成功其实没写/提前写了"。

### ② 点 `[保存]` ⇒ 写盘 + 六项全量回读 —— 已验
```
[设置页] 已备份: …\settings.json.bak-20261114-192340
[设置页] ② 写盘成功，已回读校验一致
[设置页] ③ 磁盘回读: allowCheats=False stress=60 kcal=1450 bladder=85 eps=0.05 port=8770
settings.json → alertStress: 60
```
- **六项全量回读**（`allowCheats` / `alertStress` / `alertCaloriesKcal` / `alertBladder` / `amountEpsilon` / `port`）
  是 DESIGN §10.6l「比较字段必须与可写字段 1:1」的落实 —— 只要有一个可写字段没进比对，校验就是盲的。
- 写盘前**先备份**再写，顺序正确。

### ③ 备份文件 —— 已验
命名 `settings.json.bak-<yyyyMMdd-HHmmss>`，本次为 `settings.json.bak-20261114-192340`，**303 B** 落地。
以后配置写坏可据此回滚（同目录直接换回即可）。

### ④ 非法值（`abc` / 超范围）⇒ 整批不写盘 —— **待验**
代码已实现（`SaveAll` 逐字段 `float.TryParse` + 范围检查，任一失败即
`Log.Warn` + 状态行「`<字段>` 无效：`<原因>`（整批未写盘）」并直接 return，磁盘不动），但**用户尚未实际试过**。

### ⑤ 改完不保存直接关闭 ⇒ 丢弃 —— **待验**
代码已实现（`onDeactivateFn` 里 `_dirty` 为真时 `Log.Warn("[设置页] 关闭时有未保存改动，已丢弃（没有写盘）")`），
**用户尚未实际试过**。

### 本版其它已落地变更（功能层面）
- 底部仅 `[保存] [打开配置目录] [关闭]`；`[−]` / `[+]` / `[下一个字段 →]` **已从界面移除，但代码保留为回退路径**
  （`AdjustStep 已下线` / `CycleField 已下线`，由 `tools/check-removed.ps1` 的 B 类清单标为 `KEPT`）。
- 删除只读行：`沙盒(存档)` / `沙盒(运行时)` / `Debug 模式` / `作弊闸门` 与底部提示句；
  由 `check-removed.ps1` 的 A 类（产物零残留）+ C 类（源码不再创建）双重把关。
- `port` 进入控件表（1024–65535），保存后若变更会提示「需重启 mod 才生效」。

### 定稿声明
**游戏内设置页到此功能完整**：4 个数值 + 端口 + 1 个布尔，保存式写入；不再增加功能。
外部工具 `tools/settings-ui/`（零依赖 Node 版，已冒烟通过）**继续搁置**，等用户明确要求再接。
### 工程规矩：新增「删除项残留检查」（tools/check-removed.ps1，build.ps1 第 4 步自动跑）
删功能不能只删代码 —— **要在产物里证明真的没了**。脚本用**两张清单**，判据严格分开：
- **A 类：已删除的界面文案，dll 里残留必须为 0**（`沙盒(存档)`/`沙盒(运行时)`/`作弊闸门`/`勾选立刻写盘`/`下一个字段`/`当前字段：`）
- **B 类：有意保留的代码路径，允许存在**（`AdjustStep 已下线`/`CycleField 已下线` —— 回退用，删了反而退不回去）
- **C 类：源码层面的行创建调用不应再出现**（`AddOption("−`、`AddOption("+`、`AddOption("下一个字段`、
  `L("Debug 模式"`、`L("沙盒(存档)"` 等）

**踩到的误报**：`Debug 模式` 这个词在 `/v1/commands` 的 debug op 说明与 `Gate.UnavailableReason()` 文案里是
**合法存在**的，在 dll 里搜词必然误报 3 次 ⇒ 判据从"搜词"改成"**设置页是否还在创建那一行**"（C 类）。
这条已写进脚本头部注释。
## [0.29.0] - 2026-11-14 — M4.4 设置页：草稿 + 保存（按用户六条要求重排）

**运行时未验证**（用户手点验）。本版是**一批明确的 UI 调整**，一次做完再报。

### 1. 底部按钮只剩三个
`[保存] [打开配置目录] [关闭]` —— `[−]`/`[+]`/`[下一个字段 →]` 已从界面移除；
`AdjustStep` / `CycleField` **代码保留但不再调用**（只打一行"已下线"，便于回退）。

### 2. 删掉的行
`沙盒(存档)` / `沙盒(运行时)` / `Debug 模式` / `作弊闸门` / 底部那句"勾选立刻写盘…"。
保留：`配置文件`、`写盘可用`、`token`、`版本` 四行（dll 内已核对 `沙盒(存档)` 残留 0 次）。

### 3. 端口改成数值输入框
`port` 进入与其它数值项同一张控件表（`Ctrls[]`，`1024–65535`，整数）；
保存后如果端口变了，状态行会明确提示「**需重启 mod 才生效**」（列表器已绑定旧端口）。

### 4. 行为改成「攒改动 + 保存才写盘」（本版重点）
- 所有控件（勾选框、4 个数值、端口）改动时**只改草稿**：日志 `文本变化[<key>]: "…"（草稿，未写盘）`，
  状态行显示「有未保存改动（<key>）」；**不再每次改动就写盘**。
- `[保存]` 一次性流程：
  1. **逐字段校验**（`float.TryParse` + 范围检查 + 按小数位取整）——任一项失败 ⇒
     `Log.Warn` + 状态行「`<字段>` 无效：`<原因>`（整批未写盘）」，**其余字段保持原样、磁盘不动**；
  2. **写盘前备份** `settings.json.bak-<yyyyMMdd-HHmmss>`（新 `BridgeSettings.BackupConfig`）；
  3. 应用全部值 → `SaveAndVerify`（写盘后重读文件逐字段比对）；
  4. **失败则整批回滚内存值**并提示；成功才清 `_dirty`、刷新行文字、状态行「已保存」。
- **关闭时若有未保存改动**：`onDeactivateFn` 里 `Log.Warn("[设置页] 关闭时有未保存改动，已丢弃（没有写盘）")`
  （ONI 没有确认框，所以至少留日志 + 关闭前状态行一直显示"有未保存改动"）。

### 5. allowCheats 文案
改为 `allowCheats（在此选项开启后，ai 可以在沙盒模式或 debug 模式下使用作弊指令）`，勾选框仍在右侧。

### 6. `▶` 已去掉
既然没有"当前字段"概念，行文字不再有前缀标记。

### 硬要求对照
`Log.Warn`（本版新增代码无 `Log.Error`）/ 保存逻辑整体 try/catch / 写盘前备份 / 回读校验失败整批回滚。
## [0.27.0] - 2026-11-14 — M4.3c-2b 输入框内部布局修正（数字只露 3/4 的真因）

**运行时未验证**（用户手点验）。

### 真因
上一版只改**根**的 `sizeDelta`（150×32 → 80×26），但克隆体内部的
`Text Area` / `Text` / `Placeholder Text` **还带着 FileNameDialog 那套 anchor/padding**：
外框小了、文字层没缩 ⇒ 文字错位并被 rect 裁剪，用户看到"数字只露出约 3/4"。

### 本版修法
1. **内部拉满**（关键）：
   - `Text Area`：`anchorMin=(0,0) anchorMax=(1,1)`、`offsetMin=(4,1) offsetMax=(-4,-1)`（左右 4px 内边距）；
   - `Text`：铺满 `Text Area`（offset 全 0），`alignment = MidlineRight`（数字靠右更自然）；
   - `Placeholder Text`：同 `Text Area` 的内边距。
2. **字号夹紧**：`if (text.fontSize > 18f) text.fontSize = 16f;`，避免被 26 高的框裁掉。
3. **根尺寸** `80×26`（上一版已是）。
4. **定位右边缘对齐**：`右边缘 = content 右边缘 - 12`，中心点 = `右边缘 - sizeDelta.x/2 × lossyScale.x`
   ⇒ 只调宽度不会让整列跑偏（上一版已改）。
5. 新增一次性结构日志：`[设置页] 输入框内部子物体: Text Area, Text, ...`
   与 `[设置页] 输入框文字: fontSize=… alignment=…`，便于确认内部结构是否如预期。

### 判据（用户手点）
屏幕上应看到：**窄白框（80×26）、数字完整居中/靠右显示、右边距与对话框边缘一致**；
四个字段各一个框，`amountEpsilon` 能输 `0.25` 并写盘成 `0.25`（不是 `0.25000001`）。
## [0.26.0] - 2026-11-14 — M4.3c-2 四个数值项各一个输入框（尺寸 80×26、右边缘对齐）

**运行时未验证**（用户手点验）。M4.3c-1 只有"当前字段"一个框；本版**每行一个**。

### 变更
- **4 个输入框**：`alertStress` / `alertCaloriesKcal` / `alertBladder` 用
  `TMP_InputField.ContentType.IntegerNumber`；**`amountEpsilon` 用 `DecimalNumber`**（小数路径单独验）。
  每个框各自 `onValueChanged / onSelect / onDeselect / onEndEdit`，日志**带字段名**：
  `文本变化[alertStress]: "8"` / `焦点获得[amountEpsilon]` / `提交输入[amountEpsilon]: "0.25"`。
- **尺寸 `150×32` → `80×26`**（用户反馈太宽太高；数字最长 `10000`、`0.05`，80 宽足够）。
- **定位改为"右边缘对齐"**：`右边缘 = content 右边缘 - 12`，中心点由 `sizeDelta.x / 2 × lossyScale.x` 推出。
  ⇒ 以后**只调宽度不会再让整列跑偏**（旧写法是 `rightEdge.x - 90` 的固定中心偏移）。
- **去重按字段分开**：`_lastSubmitted[i]`（原来只有一个字符串，4 个框会互相干扰）。
- `SyncFieldText()` 现在同步**全部**框，但**跳过正在聚焦的那个**（不打断输入）。
- **不再自动抢焦点**：M4.3c-1 里那个"打开即 `TryActiveFocus`"的诊断调用已移除
  （`TryActiveFocus` 方法保留，仅作诊断备用，未自动调用）。
- 保留诊断三件套：`ProbeRaycast`（根/子物体的 Graphic 与 `raycastTarget`、`CanvasGroup`）、
  `EnsureClickSurface`（根上无 Graphic 时补透明点击区）、`InputFieldClickProbe`（点击是否到达）。
  这三个都是**被动日志**，不影响交互。

### 实测背景（写在这里，避免以后又误判）
**自动化点击（`desktop_control`）对 Unity 的 EventSystem 不生效**：M4.3c-1 用自动点击点 3 次都没有
`焦点获得`，而用户手点一次就进去了。⇒ **涉及"点进去打字"的 UI 验收必须让用户手点**，
别用自动化点击把可用功能判成失败。
## [0.24.0] - 2026-11-14 — M4.3c-1 数值输入框（克隆官方 FileNameDialog.inputField）

**运行时未验证**。本版只给**当前字段**（默认 `alertStress`）一个输入框，其余 3 项仍只读；
`[−][+]` 按钮保留为辅助。

### 实现
- **克隆官方输入框**（不手搓 TMP_InputField）：
  `ScreenPrefabs.Instance.FileNameDialog` 的 **nonpub 字段 `inputField`**（反射取）→
  `Util.KInstantiateUI(template.gameObject, dialog 根, true)` → `GetComponent<KInputTextField>()`。
  该 prefab 自带 TMP + viewport + caret + placeholder，避免自己凑这些字段。
- **数字限制**：`contentType = TMP_InputField.ContentType.IntegerNumber`、`lineType = SingleLine`
  （与 PLib 的做法一致：用 TMP 自带校验，不自己过滤字符）。
- **定位**：与勾选框同一套 —— 挂 **dialog 根**（`content` 有 VerticalLayoutGroup 会压尺寸），
  世界坐标放到"content 右边缘内侧 + 与数值行同高"，`SetAsLastSibling()`，延迟一帧再定位一次。
- **焦点**：照 `FileNameDialog.OnActivate` 的做法但**逐行判空**：
  `inputField.Select()` / `ActivateInputField()` 由用户点击触发；`onSelect/onDeselect` 里
  记 `焦点获得/焦点丢失`，并 `if (CameraController.Instance != null)` 才设 `DisableUserCameraControl`
  （主菜单可能没有相机控制器）。
- **提交**：`onEndEdit`（回车与失焦都会触发）→ **`_lastSubmitted` 去重**（文本没变就跳过，避免双写与两条 ② 日志）
  → `float.TryParse(InvariantCulture)` → 失败则**回滚显示、不写盘**。
- **统一写值入口 `ApplyValue(fieldIdx, wanted)`**：按钮与输入框共用
  （clamp → 按小数位取整 → 写盘 → `SaveAndVerify` → 独立读盘回读 → 原地刷新行 + 同步输入框文本）。
- **降级**：任一环节失败（`FileNameDialog` 为空 / 字段不存在 / 实例化失败 / 组件缺失）⇒
  `_inputFieldUnavailable = true`，状态行明写「**输入框不可用（反射失败），用 [−][+] 改**」，
  日志 `Log.Warn` 一句；**设置页照常可用**，不会出现"没有控件的空页"。

### 日志（五段，用于判断"点进去打不了字"到底卡在哪）
`[设置页] 输入框已创建…` / `焦点获得` / `文本变化: "…"` / `提交输入: "…"`（或 `文本未变，跳过（去重）`）/
`① 调整 … ② 写盘成功… ③ 磁盘回读 …`。
失败时：`输入框不可用（<原因>）⇒ 保留 [−][+] 按钮改数值`。

### 新增程序集引用
`Unity.TextMeshPro`（`TMP_InputField.ContentType/LineType` 需要）。
## [验收记录] M4.3a / M4.3b 设置页可编辑 —— 已通过（2026-11-14）

**结论：A 方案已满足功能需求；B 方案（每行右侧 `[−][+]`）待用户反馈后再决定是否升级。**
设置页当前能力 = **1 个布尔（`allowCheats`）+ 4 个数值（`alertStress` / `alertCaloriesKcal` /
`alertBladder` / `amountEpsilon`），都能改、能存、能原地刷新**。

### M4.3a（0.22.0）实测证据
```
[设置页] ① 调整 alertStress 60 → 55
[设置页] ② 写盘成功，已回读校验一致
[设置页] ③ 磁盘回读 alertStress = 55
```
- 屏幕：`▶ alertStress  55` **原地刷新**，不闪、不重开对话框；
- `settings.json`：`"alertStress": 55`；
- 起始 4 行显示 60 / 1500 / 90 / 0.10 全对。

### M4.3b（0.23.0）四条判据全绿
```
切换当前字段 → alertCaloriesKcal
① 调整 alertCaloriesKcal 1500 → 1450   ③ 磁盘回读 = 1450
切换当前字段 → alertBladder
① 调整 alertBladder 90 → 85            ③ 磁盘回读 = 85
切换当前字段 → amountEpsilon
① 调整 amountEpsilon 0.10 → 0.05       ③ 磁盘回读 = 0.05
切换当前字段 → alertStress              ← 循环回到第一个 ✔
```
`settings.json` 最终：`amountEpsilon 0.05 / alertStress 55 / alertCaloriesKcal 1450 / alertBladder 85`
- ① 三个以上「下一个字段」能循环回首项 ✔
- ② `▶` 每次只在当前行 ✔
- ③ 四个字段各点一次 `[−]`，四值都真的入盘 ✔
- ④ 日志字段名跟随当前字段（未写死）✔
- `amountEpsilon` 显示与写盘都是干净的 `0.05`（`Math.Round(…, Decimals)` 生效）✔

### 明确不做的两件（等用户发话）
1. **B 方案（每行右侧 `[−][+]`）**：纯 UI 优化，**没被抱怨过的交互不提前重做**（白引风险）。
2. **`port` 可编辑**：改端口要重绑 HTTP 监听，用户正在使用该 API ⇒ **必须由用户明确要求 + 选好时机**（例如他自己要重启 mod 时）再做。
## [0.23.0] - 2026-11-14 — M4.3b 4 个数值项全部可编辑（循环切换）

**运行时未验证**。M4.3a 只让 `alertStress` 可编辑，本版把
`alertStress / alertCaloriesKcal / alertBladder / amountEpsilon` **全部接入同一套**。

### 变更
- `EditableCount`：`1` → **`Fields.Length`**（4）；`[下一个字段 →]` 现在真的循环 `1→2→3→4→1`，
  切换只 `SetText` 刷两行（旧行去掉 `▶`、新行加上 `▶`），**不重建页面**。
- `AdjustStep` 里**按字段小数位取整**：`newV = Math.Round(newV, f.Decimals)`。
  原因：float 累加会出 `0.15000000000000002`，既难看、写盘也可能变长尾数；
  取整后内存值干净，`Save()` 写出 `"amountEpsilon": 0.15`。
- 按钮标签从 `[− 5] [+ 5]` 改为 **`[−] [+]`**：步长随"当前字段"变化，写死在标签里会过期；
  步长与范围改由状态行显示（`当前字段：amountEpsilon（步长 0.05，范围 0–10）`）。
- 日志里的字段名取自 `f.Key`（**不是写死的 alertStress**），每次调整都打印真实字段名。

### 判据（验收用）
1. 点 3 次「下一个字段」能循环回第一个；
2. 每次切换后 `▶` 只在当前那一行；
3. 对每个字段各点一次 `[−]`，4 个值都真的写进 `settings.json`；
4. 日志 `① 调整 <字段名> A → B` 的字段名跟着当前字段变。
## [0.22.0] - 2026-11-14 — M4.3a 数值项可编辑（只加 alertStress 一个字段）

**运行时未验证**。本版按"一次只加一个字段"：**只有 `alertStress` 可编辑**，
另 3 个数值项（`alertCaloriesKcal` / `alertBladder` / `amountEpsilon`）只做只读显示。

### 新增
- 4 行数值（官方 `AddLineItem`），并**把每行的 `InfoScreenLineItem` 句柄拿回来**：
  取 `contentContainer` 最后一个子节点 → `GetComponent<InfoScreenLineItem>()`，
  之后用它的**公开方法 `SetText(string)`** 原地刷新（不自造 LocText、不重建对话框）。
- 选中行前缀 `▶ `，另有一行状态：`当前字段：alertStress（步长 5，范围 0–100）`。
- 底部**固定 5 个官方 `AddOption` 按钮**（不随字段数膨胀）：
  `[− 5] [+ 5] [下一个字段 →] [打开配置目录] [关闭]`。
- 每次 `[−]/[+ ]`：算新值 → clamp 到范围 → 原地刷新显示 → 写盘 → `SaveAndVerify` → 独立读盘回读；
  失败则**回滚内存值**并 `Log.Warn`。三段日志沿用：`① 调整 → ② 写盘 → ③ 磁盘回读`。

### 配套改动（关键，否则"校验"是假的）
`BridgeSettings` 之前 `LoadFromDisk()` 只解析 `port/allowCheats/token`，
所以数值项写盘后**根本没有被校验**。本版补上：
- `LoadFromDisk()` 解析 `amountEpsilon / alertStress / alertCaloriesKcal / alertBladder`（`ReadFloat`）；
- `SaveAndVerify()` 对 4 个数值项做**带容差比较**（`|差| <= 0.0005`），不一致就把具体字段与两个值写进 error。

### 硬性要求对照
① 新增代码全部 `Log.Warn`；② 每个回调整体 try/catch（`AdjustStep` / `CycleField` / `AddNumberRows` / `AddActionButtons` 各自独立）；
③ 未新增自造控件（只用官方 `AddLineItem` + `AddOption`）；④ 数值由我们自己改，不依赖官方状态机；
⑤ 一次只加一个字段。
## [0.21.1] - 2026-11-14 — M4.2p 按 PLib `PCheckBox` 的做法对齐（先比较、再 ChangeState）

**这版不再自己发明**，而是把 PLib 的实现逐行对出来照抄。已核实的 PLib 事实（IL）：

| 环节 | PLib 的做法（IL 证据） |
|---|---|
| 设 `MultiToggle` 字段 | `mToggle.states = GenerateStates(ColorStyleSetting)`、`mToggle.toggle_image = <Image>`；`onClick` 用 **`Delegate.Combine`** 追加（不覆盖已有） |
| 声音开关 | 用 **detour 直接写私有字段**：`UIDetours.PLAY_SOUND_CLICK/PLAY_SOUND_RELEASE`（`Set(mToggle, true/false)`） |
| 读/写状态 | `UIDetours.CURRENT_STATE` = `IDetouredField<MultiToggle,int>`（**私有 `state` 字段的反射读写**），`UIDetours.CHANGE_STATE` = `DetouredMethod<Action<MultiToggle,int>>`（即公开的 `ChangeState(int)`） |
| 翻状态 | `SetCheckState(GameObject go, int state)`：`go.GetComponent<MultiToggle>()` → **`if (CURRENT_STATE.Get(t) != state) CHANGE_STATE.Invoke(t, state)`**（先比较、不等才翻） |
| 通知业务 | 回调委托 `OnChecked(GameObject checkbox, int state)`（把新状态交出去，业务方自己决定写盘/刷新） |
| **有没有用官方 `NextState()`** | **没有**；也**从没碰 `stateDirty`** |

⇒ 结论：**PLib 也不依赖 `MultiToggle` 的点击自动翻状态**，它自己算目标、自己 `ChangeState(int)`，
并且**先比较再翻**以避免无效刷新。这与我们实测"官方 `OnPointerClick` 不翻"完全一致。

### 本版改动（就一处）
`onClick` 里改成和 PLib 同语义：`if (target != cur) toggle.ChangeState(target);`（原来是无条件 `ChangeState`）。
其余（try/catch、探针、NextState patch）保持不变。
## [0.21.0] - 2026-11-14 — M4.2o 自己翻状态（实证：官方 OnPointerClick 不翻）

**又推翻一次，这次是实测数据说话**：
```
[probe] OnPointerClick 进入: AllowCheatsBox state=1 statesLen=2
[probe] toggle click: current=1 => 目标=0
[设置页] ① → False   ② 写盘成功
[probe] ②.5 准备读回磁盘 → ②.6 disk=False → ②.7 回调结束      ← 回调完整跑完、无异常
[probe] OnPointerClick 返回: AllowCheatsBox state=1 statesLen=2   ← 状态没变
                                  ← NextState 进入/返回 依旧缺失
```
三条硬事实：
1. `②.5/②.6/②.7` 全打出 ⇒ onClick **没有异常**（上一轮"异常截断"的假设被证伪）；
2. `NextState` 的 patch 从未触发 ⇒ 官方**没有调用**它；
3. `OnPointerClick` 返回时 `state` 仍是 1 ⇒ 官方点击处理**不推进状态**。

（更正记录：0.18.0 里我按 IL 认为"`OnPointerClick` 末尾会调 `NextState`"，据此判 0.17.0 的手动翻是"双重翻转"——**这个判断是错的**。
另一种可能是 `NextState` 被 Mono 内联导致 patch 看不到调用，但既然状态确实不变，结论一致：得自己翻。）

### 本版做法
- `onClick` 顺序改为：**算 `target` → `toggle.ChangeState(target)` → 读回真值 → 再写盘**
  （写盘写的就是界面真实状态，不再是"预期值"）；
- 新增确认行：`[probe] 手动翻后 CurrentState=<n>（期望=<n>）`；
- **删掉"下一帧断言"**（不再指望官方翻，它只会误报）；
- `NextState` / `OnPointerClick` 的 Harmony 探针**保留**（哪天官方真的会翻，日志会告诉我们，那时再去掉手动翻）。

### 若"翻后 CurrentState=期望值"但画面仍空
那问题就与"翻不翻"无关，而是**视觉没跟着 state 更新**（`additional_display_settings` 的 sprite 套用），
需单独修 —— 两者必须分开判断。
## [0.20.0] - 2026-11-14 — M4.2n 给点击回调整体 try/catch（异常被 Unity 吞掉导致 NextState 不执行）

**破案过程（实机 + 探针，见 0.19.0）**：
```
[probe] OnPointerClick 进入: AllowCheatsBox state=0 statesLen=2
[probe] toggle click: … current=0 => 目标=1
[设置页] ① 勾选框点击 → True
[设置页] ② 写盘成功，已回读校验一致          ← ③ 磁盘回读 这行【缺失】
[probe] OnPointerClick 返回: AllowCheatsBox state=0 statesLen=2
                                             ← NextState 进入/返回 两行【都没有】
```
⇒ **`NextState()` 从来没被调用**：异常发生在我们 `onClick` 回调里（② 之后、③ 之前），
Unity 事件系统**静默吞掉异常**，`OnPointerClick` 从这里返回，末尾的 `NextState()` 自然没执行。
我们前四五版一直在猜 `NextState` 的语义 —— 实际它压根没被调用。

### 修法
- `PCheckboxLite` 的 `toggle.onClick` **整体 try/catch**，catch 里
  `Log.Warn("[设置页] onClick 回调异常（已捕获，保证官方 NextState 仍会执行）: " + e)`；
- `BridgeOptionsScreen.OnAllowCheatsClicked` 也整体 try/catch，并在 ②/③ 之间加打点：
  `[probe] ②.5 准备读回磁盘` → `[probe] ②.6 读回完成 disk=…` → `[probe] ②.7 回调结束`，
  同时把 `LoadFromDisk()` 单独包一层 try/catch（它是 ② 与 ③ 之间唯一的调用）。

### 通用教训（写进 DESIGN §10.6i）
> Harmony/Unity 事件回调里抛出的异常会被 Unity 事件系统**静默吞掉**：你只看到"后续逻辑没执行"，
> 看不到任何堆栈。症状 = `OnPointerClick` 的 Prefix/Postfix 都打了、中间那段没打、官方后续逻辑也没执行。
> 规矩：**任何挂在第三方组件点击/事件上的自定义回调，必须整体 try/catch 并至少 Warn 出堆栈。**
## [0.19.0] - 2026-11-14 — M4.2m `Log.Error` 触发 ONI 自动禁用 + NextState 探针

**"崩溃"的真相**：不是崩溃 —— 是**我们自己的 `Log.Error` 把 ONI 惹了**。
Player.log 里那行 `ONI AI Bridge's assembly declared the method OniAiBridge.Log:Error in the stack trace,
adding to referenced mods list` 之后，游戏弹「游戏出现错误」屏，并把 `mods.json` 里本 mod 的
`enabled` 改成 `false`（`crash_count` +1）。

### 机制（ONI 的行为）
`Debug.LogError`（我们 `Log.Error` 的实现）会被 KMod 当成"这个 mod 出错了"：
① 记进"出错 mod"名单 → ② 下次启动弹错误屏、把相关 mod 列成红字 → ③ **自动把 mod 取消勾选**。

### 修法
- **16 处 `Log.Error(...)` 全部改为 `Log.Warn(...)`**（`Log.cs` 的 `Warn` 现在接受可选 `Exception` 参数）；
- `Log.Error` 本体保留（兼容），但**内部改走 `Debug.LogWarning`** 并带 `[error-as-warning]` 前缀，
  以后谁再用它也不会害得 mod 被禁用；
- 断言"点击后状态与预期不符"**降级为 Warning 且不再纠正**（纠正会成为第二个变量）。

### 停用清单核对（装前）
`mods.json` 里本 mod：`enabled: true`、`crash_count: 0`、`enabledForDlc: ["EXPANSION1_ID"]`；
全库仅 `Darkness Not Excluded`（2964969010）`crash_count: 1` 且 `enabled: false`（与我们无关）。
备份：`mods.json.bak-20261114`、`mods.json.bak-20261114-1808`。

### 新增探针（定案 NextState 为何不生效）
`ToggleProbe.cs`：Harmony patch
`MultiToggle.NextState` 与 `MultiToggle.OnPointerClick` 的 **Prefix/Postfix**，各打
`[probe] NextState 进入/返回: <name> state=<n> statesLen=<n>`。
两种可能一眼分开：
* **看不到 `NextState 进入`** ⇒ 我们的 `onClick` 抛异常打断了 `OnPointerClick` 后半段（或提前 return）；
* **看到"进入 state=1 / 返回 state=1"** ⇒ `NextState` 被调用但没生效。

### 已排除（IL 实证）
* `get_CurrentState` 只有 7 字节：`ldarg.0; ldfld state; ret` ⇒ **返回的就是索引**，不是 id；
* `ToggleState` **没有 `id` 字段**（Name/sprite/color/color_on_hover/use_color_on_hover/use_rect_margins/
  rect_margins/additional_display_settings）⇒ "索引 vs id" 假设排除。
## [0.18.0] - 2026-11-14 — M4.2k 改用官方语义（回调只写盘，翻状态交给 NextState）+ 断言

**这版修的是"兜底掩盖真因"**：上一版手动 `ChangeState` 能让用户点出效果，但那是绕过。
**⚠️ 教训（比功能本身值钱）：兜底能跑通 ≠ 理解正确。**
看到"加了兜底就好了"时，第一反应应该是回去查**为什么原本不生效**——
这次两次翻转叠加正好回到原值，看起来像成功，实际是假象；
如果当时多问一句"兜底为什么能救"，可能早一轮就找到真因。
IL 看清后，真因与我先前的判断**相反**（我一度以为 `NextState()` 在 `onClick` 之前）。

### IL 实证（`MultiToggle`，Assembly-CSharp）
- `NextState()`（24 字节）= `ChangeState((state + 1) % states.Length)`
  （`ldfld state` → `+1` → `ldfld states` → `ldlen` → `rem`）。
- `ChangeState(int, bool)` = `if (force) stateDirty = false; ChangeState(state);`
- `ChangeState(int)`（351 字节）开头有守卫：`if (!stateDirty && arg == state) return;`
  之后才 `stateDirty = false; state = arg;` 再套 `toggle_image.sprite/color`、`rect_margins`、
  `additional_display_settings`（`image_target.sprite/color`），最后 `RefreshHoverColor()`。
- **`OnPointerClick` 的顺序：`AllowClick` → (doubleClick/`onClick.Invoke()`) → **最后**才 `NextState()`**
  （98 字节 IL 末尾是 `ldarg.0; call NextState; ret`）。

### 推论（这解释了"点三次都是 False"）
回调里读到的 `CurrentState` 永远是**点击前**的值（因为状态推进在回调之后），
所以上一版"读完发现没变就手动翻"实际是在**官方翻之前**又翻了一次 ⇒ 两者叠在一起，
最终状态/视觉与写盘的布尔值可能不一致。

### 本版做法
- `onClick` 里**只算目标值**：`target = (CurrentState + 1) % states.Length`，用它写 settings；
- **不自己改状态**，把翻转交给官方 `NextState()`（它在本回调返回后立刻执行）；
- 下一帧 `MainThread.Enqueue` **断言** `CurrentState == target`：
  成功打 `[assert] 状态已由官方 NextState 翻转到 N ✔`；
  不符则 `Log.Error` 并**用官方 `ChangeState(target)` 纠正一次**（不再静默绕过）。

### 判据
点一下：日志出现 `[probe] toggle click: … => 目标（官方 NextState 将设置）=1` 与
`[assert] 状态已由官方 NextState 翻转到 1 ✔`，方框出现对勾，`settings.json` 与 `/v1/meta.gate.allowCheats` 同步为 true。
## [0.17.0] - 2026-11-14 — M4.2j 勾选框状态翻转探针 + 兜底

**现状**：勾选框已可见、可点击、三段日志齐全、写盘校验通过，但**状态没翻转**
（点三次都读到 `False`，`settings.json` 里仍是 `false` ⇒ 真的没翻，不是翻了没写盘）。

### 本版（只加探针 + 兜底，不改主逻辑）
- 记录"点击前的状态"：因为 `OnPointerClick` 是**先 `NextState()` 再 `onClick.Invoke()`**，
  回调里读到的 `CurrentState` 已是点击后的值，拿不到"点击前" ⇒ 我用闭包变量 `lastKnownState` 自己记。
- 点击时打印：
  `[probe] toggle click: statesLen=<toggle.states.Length> beforeClick=<n> afterNextState=<n>`
  （**打印的是组件里的 `states.Length`，不是我本地数组长度**）
  `[probe] after ChangeState/manual: CurrentState=<n>（已勾选/未勾选）`
- **兜底**：若 `afterNextState == beforeClick` 且 `statesLen > 1`，就手动
  `ChangeState((before + 1) % statesLen)`；已翻成功时**不再手动翻**（避免翻两次）。
- 创建时也打一行：`[probe] toggle created: statesLen=... currentState=... initialStateIndex=... toggle_image=...`

### 同时记录一条自动化测试的坑
`worldPos` **不等于屏幕像素**：日志 `worldPos=(2232.52, 958.95)` 实际落在屏幕 `(2232, 1207)`，差约 250px
——canvas `scaleFactor=1.33` 且 dialog 根还有一层 Layout 偏移。
⇒ **别信 `worldPos` 去点**：要么 `RectTransformUtility.WorldToScreenPoint(canvas.worldCamera, pos)` 转换，
要么直接截图定位。已写进 DESIGN §10.6f。
## [0.16.0] - 2026-11-14 — M4.2i 勾选框改挂 dialog 根（避开 content 的 LayoutGroup）

**真因（probe 数字）**：位置算对了（`target=(2232.52, 960.95)`、`isLastSibling=True`、`frameImage rectSize=24×24`、
color 白、enabled=True），但挂到 `content` 之后 `rect` 被重置为 **0×0**（`sizeDelta=(0.00, 0.00)`）；
而上一版挂在 `official-row` 里 `sizeDelta` 是 `(24.00, 24.00)`。
⇒ **`content` 上有 `LayoutGroup`（垂直布局），它接管所有直接子节点的尺寸与位置**；
`official-row` 只有 `LayoutElement`，所以那版尺寸能保住（但被 `LocText` 遮住）。

### 修法
- 勾选框父级改为 **dialog 根 `screen.transform`**（屏幕根，没有 LayoutGroup，尺寸 2887×1624）；
- 位置仍用**世界坐标**（`x = content 右边缘内侧 12px`、`y = 该行中心`），换父级不影响世界坐标；
- 挂完 **`SetAsLastSibling()`**（在 dialog 根下是最后一个 ⇒ 盖住整个对话框内容）。
- 备选方案（未采用，留档）：仍挂 `content` 但给控件加 `LayoutElement` 并设 `preferredWidth/Height=24`，
  LayoutGroup 会尊重 preferred size。

### 新增探针判据（写进 DESIGN §10.6e）
挂控件前先打父级组件与 `LayoutGroupCount`：
`[probe] content components: RectTransform, CanvasRenderer, VerticalLayoutGroup, ... LayoutGroupCount=1 ← 会接管子级尺寸/位置`
`[probe] dialog-root components: ... LayoutGroupCount=0`
之前一直没打这条，是个疏漏。
## [0.15.0] - 2026-11-14 — M4.2h 勾选框改挂 content 右侧并置顶（修遮挡）

**真因（用户实测）**：勾选框**不是透明的**，它一直画出来了，但：
1. `official-row` 只有 **178.56 宽**（就是那行文字的宽度），勾选框贴行右边缘（`anchors=(1,0.5)`、`-12,0`）
   正好压在「…总开关」几个字上；
2. 它绘制在同级 `LocText` **之下**，所以文字盖住它。

### 修法
- **父级从 `official-row` 换成 `content`**：勾选框不再受"行宽 178.56"限制；
- 定位改用**世界坐标**（避免行锚在左下/pivot 上、勾选框锚在中心导致换算错误）：
  `x = content 右边缘内侧 12px`、`y = 该行中心的世界 y`；
- 挂上后 **`SetAsLastSibling()`**（最后绘制 ⇒ 盖在文字之上）；
- deferred 那一帧再算一次并重申位置，日志打出 `isLastSibling` 与 `siblingIndex`；
- **颜色这版没动**（上一版 0.14.0 已把 state 颜色改成不透明白色，这里保持不变，避免又多一个变量）。

### 判据
勾选框出现在**对话框右侧**、与「allowCheats（作弊总开关）」**同一行高度**、**不被文字遮挡**、能点。
## [0.13.0] - 2026-11-14 — M4.2f 勾选框挂载时机修正（等布局算完再挂）

**根因（探针实测，不是猜）**：`content` 与 `official-row` 的 `rect` 都是 **0×0**、`anchorMin=anchorMax=(0,0)`
——我们在布局还没算的时候就把控件挂进去并定位，父级尺寸为 0，位置当然无效。
勾选框本身完全正常：`sizeDelta=24×24`、`active=True`、`parentChain` 正确、`lossyScale=1.33`（= canvas `scaleFactor`）。
行上组件只有 `LayoutElement`+`LocText`，**没有 LayoutGroup** ⇒ 之前"布局组吃掉控件"的怀疑可以排除。

### 修法（A+B 都上）
1. `AddLineItem(...)` 加行；
2. `LayoutRebuilder.ForceRebuildLayoutImmediate(content)` 先把布局撑开；
3. 再把勾选框挂到那一行（`anchor=(1,0.5)`、`anchoredPosition=(-12,0)`）；
4. 挂完再 `ForceRebuild` 一次；
5. `MainThread.Enqueue` 延迟一帧兜底：重算 + 重申位置 + 重打数字。

### 日志（三段，便于判据）
`[probe] Rebuild(before-attach) 完成` / `[probe] official-row(before attach): row=…` /
`[probe] official-row(after attach): row=(x,y,width,height) …` / `[probe] checkbox(after attach): …` /
`[probe] Rebuild(deferred) 完成` / `[probe] official-row(deferred): …` / `[probe] checkbox(deferred): …`
**判据：`row` 的 width/height 不再为 0，且勾选框出现。**
## [0.12.0] - 2026-11-14 — M4.2d 勾选框版修崩溃（不再自造 LocText）+ canvas 不再借 ToolTip

**运行时未验证**。仍是"最小验证"范围：页面上只有一个勾选框（`allowCheats`）。

### 修复（崩溃，实机堆栈确认）
- **`LocText` 不能在 `AddComponent` 之后再赋文本**：`LocText.Awake()` 会 `new StringKey(this.text)`，
  而 `Awake` 在 `AddComponent` 当场同步执行，此时字段还是 null ⇒ `NullReferenceException`
  （堆栈：`StringKey..ctor` → `LocText.Awake` → `GameObject:AddComponent` → `AddAllowCheatsRow`）。
  ⇒ 行文字**一律交给官方 `AddLineItem`**（M4.1e 已验证可显示 label+value），
  自建的勾选框**不含任何文字控件**（只有 Image + MultiToggle）。
  *（备查：Unity 侧的通用绕法是"先建 inactive 物体 → AddComponent → 赋值 → SetActive(true)"，
  本次按"最小改动"没采用，留在 DESIGN §10.6c 备选。）*
- **canvas 不再从 `ToolTipScreen` 借**：上一版日志出现 `usedCanvasOf=ToolTipScreen`，而 tooltip 是被频繁
  重置/销毁的悬浮屏，我们的界面可能跟着消失。现在按**分数**挑活着的 `KScreen`：
  `MainMenu`:100 &gt; `ModsScreen`:90 &gt; `OptionsMenuScreen`:80 &gt; 其它 `KModalScreen`:70 &gt; `Hud`:60 &gt; 其它:10；
  **`ToolTipScreen` / `HoverTextScreen` / `NameDisplayScreen` 直接判 -1 排除**。
  探测日志会打出每个候选的 `类型:分数+canvas/-nocanvas` 以及最终 `picked=...`。

### 勾选框行怎么摆
先 `AddLineItem("allowCheats（作弊总开关）", "点右侧方框切换")` 走官方路径生成行，
再反射拿 `contentContainer`、取**刚追加的那一行**（`childCount-1`），把自建的勾选框挂到该行、
右对齐（`anchor = 右中`，`anchoredPosition = (-8, 0)`）。拿不到行就退回挂内容区（日志会写明挂到哪）。

### 已知限制
文字行在勾选后不会原地刷新（本版只保证勾选框自身状态与写盘正确）；重开设置页即刷新。
## [0.11.0] - 2026-11-14 — M4.2c 勾选框版（照抄 PLib 做法）

**运行时未验证**。本版按"最小验证"策略：**页面上只有一个勾选框（`allowCheats`）**，其余仍是纯文本行。

### 更正一条旧结论（重要，留痕）
**之前 DESIGN §10.6/§10.5 里"M4 必须用官方 prefab 实例化控件、不能自建"这条判断是错的**，现予推翻：
- 真正的根因是**字段没填**：`KButton.UpdateColor` NRE ← 缺 `ColorStyleSetting`；`LocText` ← 缺 `TextStyleSetting`。
- 证据：PLib 的 `PCheckBox` **同样是自己 `new GameObject` 造**（`PUIElements.CreateUI` 只做
  `new GameObject` + `SetParent` + `localScale=one` + `SetAnchors` + `layer = LayerMask.NameToLayer("UI")`），
  它只是把上述字段都填对了。20+ 个 mod 用 PLib 都正常，说明这条路可行。

### 新增 `PCheckboxLite`（独立类，便于以后复用到端口输入/滑块）
照抄 PLib 配方的要点：
- **精灵是 ONI 自己的资源**，按名取（`Assets.GetSprite(HashedString)`，`HashedString` 有 `op_Implicit(string)`）：
  `overview_jobs_skill_box`（外框）、`overview_jobs_icon_checkmark`（对勾）、`overview_jobs_icon_mixed`（半选，本版未用）。
- **`MultiToggle.states`**：两个状态（Unchecked/Checked），`ToggleState` 与 `StatePresentationSetting` 的
  **每个字段都填**（含 `on_click_override_sound_path`、`sound_parameter_*`、`has_sound_parameter`、`use_rect_margins`、
  `rect_margins`、`additional_display_settings[].{name,image_target,sprite,color,color_on_hover,use_color_on_hover}`）。
- **尺寸换算照抄 PLib 的 ×4f**（IL 里 `0x40800000` = 4.0f），不自己发明。
- **点击语义**：`MultiToggle.OnPointerClick` 的 IL 是**先 `NextState()` 再 `onClick.Invoke()`**
  ⇒ 回调里只读 `CurrentState`，**绝不自己再 ChangeState**（会翻两次）。写盘失败时才回滚一次。
- 不碰 `KButton`/`KImage` 的 prefab 初始化，只用 `UnityEngine.UI.Image`，所以不会重现 M4.1b 那次 NRE。

### 自建行的挂载方式
不用 `InfoDialogScreen.AddUI<T>`：它要求 `T : MonoBehaviour` **且会克隆**，克隆体的 `onClick` 闭包仍指向
模板实例（点克隆改到模板，隐蔽 bug）。改为**反射 private 字段 `contentContainer`**，把自建行原样挂上。

### 调试日志（本版重点）
```
[设置页] 勾选框已创建: frame=OK check=OK; states=2 initial=0
[设置页] 行已挂到内容区（反射路径），parent=...
[设置页] ① 勾选框点击 → true
[设置页] ② 写盘成功，已回读校验一致
[设置页] ③ 磁盘回读 allowCheats = True（内存=True）
```
另：标签的 `TextStyleSetting` 是**从对话框里已有 LocText 借的**（不自己造字体资产），借不到会 warn。

### 降级
M4.2 的按钮版源码路径已被本版替换，但 `dist` 里的 0.10.0 记录与 CHANGELOG 保留；若勾选框实测失败，
按 CHANGELOG 0.10.0 的实现回退（`AddOption` 按钮式）。
## [0.10.0] - 2026-11-14 — M4.2 设置页可编辑（按钮式）+ 立即写盘

**运行时未验证**（装后按四条件验：能开关/值可见/控件可点/真写盘）。

### 新增
- **设置页可编辑**：`allowCheats` 用**官方 `AddOption` 按钮**切换（没有 `KCheckbox`；`MultiToggle` 需要 prefab
  模板而官方没给可复用引用，所以这期用"按钮切换"而不是勾选框）。点击一次 → 取反 → **立即写盘** → 下一帧重建页面。
- **「打开配置目录」按钮**：`Process.Start("explorer.exe", configDir)`，try/catch 包住，
  失败只写日志不崩（这是本 mod **唯一**一处启动外部程序）。
- **「关闭」按钮**（primary=false 面板）。
- `BridgeSettings` 新增两个方法：
  - `LoadFromDisk()`：**纯读**解析 settings.json（不生成 token、不写盘），用于回读校验；
  - `SaveAndVerify(out string error)`：写盘后重新读文件逐字段比对（`allowCheats`/`port`），不一致返回原因。
- `MainThread.Enqueue(Action)`：把动作排到主线程下一帧（用于"改完重建页面"，避免与 `Deactivate` 抢时序）。

### 编辑动作的日志（三段，便于验证"真的写进去了"）
```
[设置页] ① 切换 allowCheats -> true
[设置页] ② 写盘成功，已回读校验一致      （失败则是 [设置页] ② 写盘失败：<原因>）
[设置页] ③ 磁盘回读 allowCheats = True（内存=True）
```

### 依据（反编译核实）
- `InfoDialogScreen.AddOption(string, Action<InfoDialogScreen>, bool primary)`：按 `primary` 选
  `leftButtonPrefab`/`rightButtonPrefab` 与对应 panel，`KInstantiateUI(prefab, panel, true)` 后只做
  `set_text` + `add_onClick` ⇒ **按钮是官方 prefab 实例化的、且是往面板追加的**（可放多个）。
- 游戏内**不存在 `KCheckbox`**；`MultiToggle` 的 `states/onClick/ChangeState` 齐全但没有官方可复用 prefab 引用。
- mod 版本 0.9.1 → 0.10.0，`stage` → `M4.2`。
## [0.9.1] - 2026-11-14 — M4.1e 设置页显示修复（值不见了）

**运行时未验证**（装后要验的只有一件事：设置页每一行能看到"标签 + 值"）。

### 修复
- **`AddLineItem(text, tooltip)` 的第二个参数是 tooltip，不是值** —— 这就是"只有标签、没有值"的原因。
  IL 实测：`AddLineItem` 里 `lineItemTemplate → InfoScreenLineItem`，然后 `SetText(arg1)` / `SetTooltip(arg2)`，
  而 `InfoScreenLineItem` 内部**只有一个 `LocText`** ⇒ 一行只能显示一条字符串，我们传的值全跑到悬浮提示里去了。
  现在把 `label` 与 `value` **拼成同一条字符串**（`label.PadRight(14) + value`），
  tooltip 只用来放长内容（配置文件完整路径）。
- 每行渲染前打印 `[设置页] 行: <整行文字>`，这样"传了没显示"和"根本没传"在日志里能一眼分开。
- 新增几行有用信息：`token` 显示"长度 32（前 6 位…）"、`版本` 显示 `mod 0.9.1 · 阶段 M4.1e · API 1.1`。
- mod 版本 0.9.0 → 0.9.1，`stage` → `M4.1e`。

### 教训（改写进 README 的验收标准）
**"能打开" ≠ "内容正确"。** 这是第三次同类事故（`on_managed` 静默成功、M3 命令表静默失败、本次只有标签没有值）。
UI 类改动的验收标准从此固定为两条：
1. 能打开、能关、不叠层；
2. **关键字段的值肉眼可见且正确**（不是"传进去了"，而是"显示出来了"）。
## [0.9.0] - 2026-11-14 — M4.1d 主菜单 canvas 兜底

**运行时未验证**（上一版已实测：热键能触发，但主菜单下 `GameScreenManager.Instance.ssOverlayCanvas` 为空 ⇒ 打不开）。

### 修复
- **设置页父 canvas 改成多路兜底**（依次尝试，全部失败才交给 ONI 默认父节点）：
  1. `GameScreenManager.Instance.ssOverlayCanvas`（游戏内有效）
  2. `GameScreenManager.Instance.ssCameraCanvas`
  3. **任意活着的 `KScreen` 的 `canvas`** —— 主菜单里 `ModsScreen` / `MainMenu` 本身就是 KScreen，
     它们的 `KScreen.get_canvas`（缓存字段 `_canvas`）在主菜单是有效的
  4. 任意 `renderMode == ScreenSpaceOverlay` 的 `Canvas`
  5. 仍为空 ⇒ `StartScreen(prefab, null)`，由 `Util.KInstantiateUI` 的默认分支处理（IL 已核实允许 null）
- **失败日志改成带探测链路**：`打开设置页失败：… canvas 探测=ssOverlayCanvas=null; ssCameraCanvas=null;
  liveKScreens=7; usedCanvasOf=ModsScreen; …`，成功时也会打印实际用了哪个 parent。
- 依据（全部反编译核实）：`KScreenManager.StartScreen(prefab, parent)` 的实现是
  `Util.KInstantiateUI(prefab, parent, false).GetComponent<KScreen>().Activate()`；
  `KScreen.get_canvas` 只读缓存字段，故只对活着的 KScreen 有意义。
- 新增程序集引用 `UnityEngine.UIModule`（`Canvas` / `RenderMode` 需要）。

### 已知限制（写进 DESIGN §10.10）
- **方案 A（官方「管理」按钮）只在 Steam 平台成立**：实机点到的本地 mod 按钮是**「浏览」→ 打开资源管理器**，
  说明 `ModsScreen` 对 `IsLocal` 条目根本不渲染 `on_managed` 按钮。发布到创意工坊（platform=1）后才有「选项」按钮。
  开发期只能靠热键 `Ctrl+Shift+O`。
- mod 版本 0.8.0 → 0.9.0，`stage` → `M4.1d`。
## [0.8.0] - 2026-11-14 — M4.1c 设置页改用官方对话框 prefab

**运行时未验证**（上一版 M4.1b 的热键已实测可触发，但自造 UI 崩了；本版换掉 UI 构建方式）。

### 修复（崩溃，日志实证）
- **设置页 UI 不再从零 `new GameObject` + `AddComponent`。** M4.1b 的 `KButton`/`LocText` 裸造在
  `KButton.UpdateColor` / `KButton.OnSpawn` 里抛 `NullReferenceException` —— ONI 的控件依赖 prefab 侧的
  `ColorStyleSetting` 等初始化，裸造出来内部字段是 null（`LocText` 同样 NRE）。
  现在改用**官方 prefab**：`ScreenPrefabs.Instance.InfoDialogScreen` +
  `KScreenManager.Instance.StartScreen(prefab.gameObject, GameScreenManager.Instance.ssOverlayCanvas)`，
  然后用 `SetHeader` / `AddLineItem` / `AddPlainText` / `AddOption` 填内容，`onDeactivateFn` 清引用。
  （`KScreenManager.StartScreen(GameObject, GameObject) → KScreen` 已在 firstpass 元数据核实。）
- **删掉 `UiBuilder.cs`**（它就是崩溃源），并移除 `SettingsUi` 里自造按钮的代码路径。

### 修复（主菜单返回 503）
- `Global.Update`（常驻单例，主菜单也跑）现在**同时驱动主线程泵**，所以 `/v1/state`、`/v1/debug/mods`
  在**主菜单**也能答；顺带给 `EventHub.Tick()` 加了 `Gate.HasColony` 守卫，没殖民地就不采样
  （否则主菜单会推空快照/无意义 diff）。

### 变更
- **方案 B（Harmony 往 Mods 屏加按钮）暂缓**：它需要官方 prefab 的 KButton，自造必 NRE；
  热键 `Ctrl+Shift+O` 已覆盖"打开设置页"这个需求。`/v1/meta.settingsUi.detail` 会如实写明。
- 程序集引用精简：`Unity.TextMeshPro` / `UnityEngine.UI` 不再需要（已随 `UiBuilder` 一起移除）。
  refs 现在是：`netstandard 2.1 / Assembly-CSharp / Assembly-CSharp-firstpass / 0Harmony 2.4.2.0 /
  UnityEngine.CoreModule / UnityEngine.InputLegacyModule`。
- mod 版本 0.7.0 → 0.8.0，`stage` → `M4.1c`。
## [0.7.0] - 2026-11-14 — M4.1b 诊断端点 + 热键兜底

**运行时未验证**（装后先看 `GET /v1/debug/mods` 定案，再按 `Ctrl+Shift+O` 试开设置页）。

### 新增
- **`GET /v1/debug/mods`（诊断端点，故意不放进公共 `/v1/meta`）**：把
  `Global.Instance.modManager.mods` 的每一条原样吐出来 ——
  `title / staticID / platform / status / availableContent / isLocal / hasOnlyTranslationContent /
  onManaged / manageTooltip / shouldDisplayMod`，外加 `managerModsCount`、`shouldDisplayTrue`、
  `shouldDisplayUnknown`、`platforms[]`（含 `KMod.Local.folder`）。
  `shouldDisplayMod` 是**反射调用游戏自己的 `ModsScreen.ShouldDisplayMod(mod)`**；当前没有活着的
  `ModsScreen` 实例时如实报 `null` 并在 `note` 里说明。
  用途：M4.1 出现过"反射设 `on_managed` 没抛异常、但列表里看不到条目"——**"没抛异常"不等于"生效"**，
  这类反射/私有 API 操作必须配可观测探头。
- **`Ctrl+Shift+O` 热键唤出设置页（C2）**：不依赖 Mods 列表里有没有我们的条目。
  实现要点：`Global.Update`（常驻单例，**主菜单也跑**）+ `Game.Update`（游戏内）**两个都挂** postfix；
  `BridgeOptionsScreen.Open()` 本身幂等，不会叠层。热键写进 `/v1/meta.settingsUi.hotkey`。
  依据（已核实）：Assembly-CSharp 引用 `UnityEngine.Input`（legacy），Managed 下**没有** `Unity.InputSystem`
  程序集，所以用 `Input.GetKey`；若将来切换成新 Input System 会抛异常，热键只记一次警告后自动停用，
  不影响其它功能。
- 新增程序集引用 `UnityEngine.InputLegacyModule`。

### 变更
- mod 版本 0.6.0 → 0.7.0，`stage` → `M4.1b`；`apiVersion` 仍 1.1（纯新增端点与字段）。
## [0.6.0] - 2026-11-14 — M4.1 游戏内设置页（只读）+ 三段式闸门

**运行时未验证**（本轮只编译验证；装后要验的是 `KModalScreen` 的打开/关闭与压栈行为）。

### 新增
- **游戏内设置页入口**，两级实现互为备份，取哪个写进 `/v1/meta.settingsUi`：
  - **方案 A（优先）**：反射设 `KMod.Mod.manage_tooltip` + `on_managed`，复用**官方**「管理」按钮
    （官方按钮在 `ModsScreen.BuildDisplay` 里：`"ManageButton"` + `MODS::MANAGE(_LOCAL)` + `isInteractable = is_managed`，
    点击挂 `on_managed`；两个属性的 setter 是 non-public，所以只能反射）。
    ⚠️ `get_is_managed` 的 IL 是 `manage_tooltip != null` ⇒ **两个必须一起设**，只设回调按钮仍是灰的。
  - **方案 B（兜底）**：Harmony postfix `ModsScreen.BuildDisplay`，自己在 Mods 屏加一个「AI Bridge 设置」按钮。
  - 两条都失败 ⇒ `settingsUi.available=false` + `detail` 写明原因，**不静默**；json 配置仍然可用。
- `BridgeOptionsScreen : KModalScreen`：当前**只读**展示配置（配置路径 / 写盘可用性 / allowCheats / 端口 /
  token 掩码 / 沙盒与 debug 闸门状态 / 设置页入口）+ 关闭按钮。编辑功能留到 M4.2。
- `GET /v1/meta` 新增 `gate{sandboxEnabled, sandboxModeActive, debugModeEnabled, allowCheats, cheatsUnlocked,
  unavailableCode, unavailableReason}`，外部可自己算闸门；新增 `settingsUi{available, kind, detail}`。
- `GET /v1/commands` 现在**永远列出全部命令**（含作弊 6 条），每条带 `available` / `unavailableCode` /
  `unavailableReason`（人类可读）。不再"藏命令"。

### 变更（破坏性）
- **闸门改成三段式**：`allowCheats == true` **且**（`SaveGame.Instance.sandboxEnabled` ||
  `Game.SandboxModeActive` || `DebugHandler.enabled`）。两个条件分别对应两个错误码：
  - `403 ALLCHEATS_DISABLED`：settings.json 的 `allowCheats` 没开（文案明写「allcheats 未启用」）
  - `403 SANDBOX_OR_DEBUG_REQUIRED`：需要在沙盒模式或 debug 模式下使用
- **移除** 旧的 `403 CHEATS_DISABLED` / `403 DEBUG_MODE_OFF`（语义被上面两个码取代）。
  迁移：客户端把 `CHEATS_DISABLED` → `ALLCHEATS_DISABLED`，`DEBUG_MODE_OFF` → `SANDBOX_OR_DEBUG_REQUIRED`。
- `apiVersion` 1.0 → **1.1**（新增字段 + 错误码变更；`cheatsAllowed`/`debugModeOn` 保留但已弃用，建议改读 `gate`）。

### 工程规矩固化（不是功能变更）
- 新增 `tools/set-text.ps1`：把"文本替换"封装成 命中数检查 + 唯一性检查 + 回读校验 + 非零退出，
  专治 `String.Replace()` 静默失败（M3 的 `/v1/commands` 事故就栽在这里）。
- README「给贡献者」新增两条硬规矩：① 替换后必须回读校验，MISS 即 fail fast；
  ② 报"已实现"必须有**运行时证据**，编译通过不算证据。

### 修复
- **M3 的 `/v1/commands` 其实没有包含作弊命令**：那一版往 `CommandsJson()` 插入作弊列表时锚点没匹配上，
  替换静默失败而我当时没有回读校验 —— 所以已装的 0.5.0 只列了 4 条普通命令。本版补上了，并且
  这次每处替换都做了包含校验。教训写进 DESIGN §10.9 的脚注。
- 新增 `UnityEngine.UI` / `Unity.TextMeshPro` / `UnityEngine.TextRenderingModule` 引用（构建 UI 需要）；
  `LocText` 实测就是 `TMPro.TextMeshProUGUI` 的子类，`alignment` 字段类型是 `TMPro.TextAlignmentOptions`
  （不是 `TextAnchor`）。
# Changelog

本项目遵循 [语义化版本](https://semver.org/lang/zh-CN/)。
**API 版本（`apiVersion`）与 mod 版本独立**：加字段/加端点 = minor；改语义/删字段 = major（迁移方式写在对应条目里）。

## [0.5.0] - 2026-11-14 — M3 作弊层（调度内置 Debug 工具）

**运行时未验证**（装机后首次实测）。

### 新增（`requiresCheats: true`，默认关闭）
| op | 底层调用的游戏入口 | 说明 |
|---|---|---|
| `debug` | `DebugHandler.SetDebugEnabled(bool)` / `get_enabled` | 查/开/关 Debug 模式；是其它作弊 op 的前置条件 |
| `setInstantBuild` | `DebugHandler.ToggleInstantBuildMode()` + `InstantBuildMode` 回读 | 瞬建 |
| `setInvincible` | `DebugHandler.InvincibleMode` | 无敌 |
| `revealFog` | `DebugHandler.RevealFogOfWar` | 开图 |
| `spawnElement` | `DebugPaintElementScreen.Instance`（设 `element`/`mass`/`temperature`）+ `DebugTool.DoReplaceSubstance(cell)` | 指定格刷元素 |
| `teleport` | `Navigator.Stop` + `Transform.position = Grid.CellToPos(cell)` | 挪复制人（实验性） |

* 双闸门：`settings.allowCheats == true` **且** `DebugHandler.enabled == true`；缺 `allowCheats` 回
  `403 CHEATS_DISABLED`，缺 Debug 模式回 `403 DEBUG_MODE_OFF`（`debug` op 自身只需 `allowCheats`，否则永远打不开）。
* **回读校验**沿用 M2.1：每个 op 执行后重新读实际状态，不一致回 `409 NOT_APPLIED`。
* 严格参数校验：元素名不存在 `404 NOT_FOUND_TARGET`、格子越界/质量非正/温度非法 `400 INVALID_ARG`、
  复制人不存在 `404 NOT_FOUND_TARGET`——一律不静默降级。
* `/v1/commands` 的每条作弊 op 都带 `requiresCheats:true`，`note` 里明写后果（会破坏存档一致性、不可撤销）。

### 实测确认的类型细节
- `DebugPaintElementScreen.element` 是 **`SimHashes` 枚举**（不是 `Tag`）—— 由编译器证实（`Tag → SimHashes` 隐式转换报错），
  所以设置时用 `element.id`。
- `DebugTool.DoReplaceSubstance(int cell)` **只接受格子号**，元素/质量/温度取自调试面板当前选择。
- `DebugGoToSubState.GoToCursor()` 没有直接接受坐标的入口，`Navigator` 也没有 Teleport 方法
  ⇒ 传送只能走 Transform 定位，属实验性，文档里标注"可能被导航系统拉回，请隔 1 秒用 `/v1/state` 复查"。

### 已知限制
- `spawnElement` 依赖 `DebugPaintElementScreen.Instance` 已存在（玩家需在游戏里打开过一次调试面板的元素绘制页），
  否则返回 `503 NOT_READY` 并说明原因——不假装成功。
  **注意这是"每会话"前置条件**：`Instance` 不跨进程存活，每次重开游戏都要再打开一次那个面板。

## [0.4.0] - 2026-11-14 — M2.1 setSpeed 语义修正 + 回读校验

**运行时未验证**（本轮只改代码；上一版 M2 已实测通过，除 setSpeed 外）。

### 变更（破坏性）
- **`colony.speed` 改为对外人类语义 `1|2|3`（=1x/2x/3x）**，不再是 `SpeedControlScreen.GetSpeed()` 的
  0 基原始值。原始值移到新增字段 **`colony.speedRaw`**（0|1|2）。
  - 迁移：原来把 `speed:0` 当"停止"的客户端改读 `speed`（现在最小是 1），或继续用 `speedRaw`。
  - `diff` 里 `path:"colony.speed"` 的 `from/to` 也一并换成 1|2|3。
- mod 版本 0.3.0 → 0.4.0；`stage` 从 `M2` 改为 `M2.1`。

### 修复
- **`setSpeed` 参数语义错了**：`SpeedControlScreen.SetSpeed(int)` 是 **0 基**（0=1x、1=2x、2=3x），
  之前的实现把外部的 1|2|3 直接传进去 ⇒ 请求 `speed:3` 落到 `switch` 的 default 分支被判成 0，
  实际掉回 1 倍速，但接口还回 `applied:true`。
  证据两条独立对齐：
  1. **IL**：`SetSpeed` 里 `speed = value & 3`，随后 `switch(speed)` 三个 case 分别 `slowButton/mediumButton/fastButton.Select()`，
     越界值走 default 只调 `OnSpeedChange()`；
  2. **界面实测**：请求 `speed:2` 时界面显示**三倍速**。
  现在：`SetSpeed(speed - 1)`，对外仍是 1|2|3。
- **回读校验（新机制）**：所有改状态的 op 执行后重新读一次实际值，与请求不一致就回
  `409 NOT_APPLIED` 而不是 `applied:true`：
  - `setSpeed` → 回读 `GetSpeed()+1`
  - `pause` / `resume` → 回读 `IsPaused`
  这是唯一能自动发现"看起来成功其实没生效"这类问题的手段。

### 保留
- `setSpeed` 在暂停状态下也能改倍速（实测 `isPaused:true` 时 `setSpeed 2` 返回 `applied:true speed:2`，
  游戏保持暂停）。这是正确行为，文档已写明"不改变暂停状态"。

## [0.3.0] - 2026-11-14 — M2 推送 + 命令层

**运行时未验证**（装机等下一次停机）。

### 新增
- **`GET /v1/events`（SSE）**：主线程每 1000 ms 采样一次，推送四类事件，全部带 `id:` = seq：
  - `snapshot`：连接时先给一份完整状态（等于 `/v1/state` 的 `data`）
  - `diff`：字段级变化 `{path, from, to}` 或生命周期 `{kind:"dupe.spawned|dupe.died|dupe.chore"}`
  - `alert`：阈值告警集合变化（`stress_high` / `calories_low` / `bladder_full`，阈值可在 settings.json 覆盖）
  - `heartbeat`：15 s 保活
  - 续传：`Last-Event-ID` 头或 `?since=<seq>`，服务端保留最近 256 条事件；最多 8 个并发订阅者。
- **`GET /v1/commands`**：自描述命令清单（op / args / requiresCheats / available / note）。
- **`POST /v1/command`**：普通操作（不含作弊）：
  - `pause` / `resume`（`SpeedControlScreen.TogglePause`，带 `IsPaused` 守卫，幂等）
  - `setSpeed {speed:1..3}`
  - `setChorePriority {dupe, priority:1..9, priorityClass?}` → `Chore.masterPriority = new PrioritySetting(...)`
- `settings.json` 新增：`amountEpsilon`（默认 0.1，属性变化阈值）、`alertStress`(60)、`alertCaloriesKcal`(1500)、`alertBladder`(90)。
- `/v1/meta` 的 `server` 增加 `sseSubscribers` / `lastEventSeq`；`capabilities` 里 `events`/`commands` 置 true。

### 变更（行为）
- `/v1/meta` **在主菜单也能答**：`buildText`/`unityVersion`/`unityBundleVersion` 来自 OnLoad 时的缓存，
  不再依赖主线程泵；`colonyLoaded` 未知时给 **`false`**（不再给 `null`），`debugModeOn` 也不再是 `null`。
- `/v1/state` 的 `amounts` 从"写死的 7 项"改为**该复制人身上真实存在的全部 AmountInstance**
  （因此多出 ImmuneLevel / Decor / RadiationBalance / 5 种病菌等）。**原有 7 个 key 与
  `CaloriesKcal`/`TemperatureC` 的位置不变**，属纯增量。
- `colony.world.size` 在 `WorldContainer.WorldSize` 返回 0×0 时不再输出 `[0,0]`，改为 `null` + `sizeNote`。

### 踩坑记录
- `PriorityClass` 是**嵌套类型**：真名 `PriorityScreen.PriorityClass`（元数据 `declaring type = PriorityScreen`）。
- `ObjectDisposedException` 也要写全名（`System.` 前缀），否则同样被遮蔽。

### 尚未实现
- 作弊/内置 Debug 调度（M3）；`/v1/map`、`/v1/catalog`（未排期）。
- 指派挖掘/建造需要走 `DigTool`/`BuildTool` 的 UI 工具层，M2 未做（不假装支持）。

## [0.2.0] - 2026-11-14 — M1 真正读数据

`GET /v1/state` 上线（colony / dupes / elements 三块）。**运行时仍未验证**（dll 不能热重载，等用户退出游戏后一次性安装）。

### 新增
- `GET /v1/state`：
  - `colony`：`cycleRaw`/`cycle`/`timeSinceStartOfCycle`/`timePlayed`/`frame`/`isNight`/`isPaused`/`speed`/
    `worldId`/`world{name,typeKey,isStartWorld,size}`。
  - `dupes`：每个活复制人的 `name`/`dbId`/`gender`/`cell`/`chore{text,id}`/`amounts{Stress,Calories,Temperature,Bladder,Stamina,Breath,HitPoints}`，
    另给派生字段 `CaloriesKcal`（÷1000）与 `TemperatureC`（−273.15）。
  - `elements`：活动世界 `WorldInventory` 的元素存量（kg），只报 ≥1 kg、按量降序、最多 40 条。
- 每块数据自带 `ts`；采集整体走主线程泵，HTTP 线程不碰 Unity 对象；采集超 3000 ms 返回 `504 TIMEOUT`。
- `capabilities.state` 置为 `true`。

### 变更（破坏性）
- **`/v1/meta` 的 `game.applicationVersion` 已移除**：它在 ONI 里恒为 `"1.0"`（Unity bundleVersion，不是游戏版本），
  会误导客户端。改用：
  - `game.buildText`（`BuildWatermark.GetBuildText()`，例 `U59-744825-S`）
  - `game.buildVersion`（从 buildText 解析出的整数，例 `744825`）
  - `game.unityVersion`（例 `6000.3.5f2`）
  - `game.unityBundleVersion`（保留原值并附 `unityBundleVersionNote` 说明它不是游戏版本）
  - `mod.compiledAgainstGameBuildText` / `mod.compiledAgainstGameBuild`（编译时对齐的版本）
  迁移：原来读 `data.game.applicationVersion` 的客户端改读 `data.game.buildVersion`（或 `buildText`）。

### 修复
- `/v1/meta` 不再输出无意义的 `pumpTicks` 占位字段。

### 验证到的 API（编译期，全部为本机 Assembly-CSharp 实测存在的成员）
`GameClock.Instance/GetCycle/GetTimeSinceStartOfCycle/GetTimePlayedInSeconds/GetFrame/IsNighttime`、
`SpeedControlScreen.Instance/IsPaused/GetSpeed`、`ClusterManager.Instance/activeWorldId/GetWorld`、
`WorldContainer.worldName/worldType/IsStartWorld/WorldSize/worldInventory`、
`Components.LiveMinionIdentities.Items/Count`、`MinionIdentity.GetProperName/nameStringKey/gender/modifiers`、
`Modifiers.amounts` → `Amounts.GetValue(string)`、`ChoreDriver.GetCurrentChore` → `Chore.GetReportName/choreType.Id`、
`WorldInventory.GetAmount(Tag, bool includeRelatedWorlds)`、`ElementLoader.elements`、`Element.tag.Name`、
`Grid.PosToCell`、`BuildWatermark.GetBuildText`。

## [0.1.0] - 2026-11-14 — M0 骨架

首个可编译、可打包的骨架。**运行时尚未验证**（未安装进游戏，等用户确认）。

### 新增
- mod 入口 `KMod.UserMod2` 实现（`ModEntry`），`OnLoad` 全程 try/catch，失败不影响游戏启动。
- 主线程泵：Harmony postfix 挂 `Game.Update`（非公开实例方法），HTTP 线程只投递委托，
  每帧最多排干 64 个任务。
- 本地 HTTP 服务：`TcpListener` 只绑 `127.0.0.1`，端口 8770 起顺延最多 10 个，无第三方依赖。
- `GET /v1/meta`：`apiVersion` / mod 版本 / 端口 / `pumpReady` / `colonyLoaded` / `cheatsAllowed` /
  `debugModeOn` / `capabilities`。
- `GET /v1/health`。
- token 鉴权（`X-Oni-Token` 头或 `?token=`，首次运行生成并写入 `settings.json`）。
- `build.ps1`：构建 + 组装 mod 文件夹 + 强制 UTF-8（无 BOM）并回读校验。

### 已知问题
- 运行时行为全部未验证（`/v1/meta` 的真实返回、`OnLoad` 是否被调用、泵是否在跑）。
- `colonyLoaded` 目前只按 `GameClock.Instance != null` 判断，尚未做更严格的载入判定。
- 无速率限制（`/v1/meta.server.rateLimitPerMin` 报 0）。
- 未提供 `preview.png`（创意工坊上传前需要补）。

### 踩过的坑（记录下来免得重复踩）
- `Assembly-CSharp.dll` 定义了全局类型 `Action`、`DateTime`，会遮蔽 BCL 同名类型
  → 症状 `CS1660` / `CS0117` / `CS0149`，对策：BCL 类型一律写全名。
- `mod_info.yaml` 的 `minimumSupportedBuild` 是 **int32**（`KMod.PackagedModInfo`），
  主菜单显示的 `U59-744825-S` 只能取其中的 `744825`。
