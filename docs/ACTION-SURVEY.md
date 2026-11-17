# ONI「下达工作指令」调研（M6 前置，只调研）

> 用户定方向：**这游戏绝大多数操作都要小人去交互，所以"指挥小人"才是核心**。
> 本文件回答：游戏里怎么"打标记 / 下达工作"，有没有统一入口，能不能程序化调用。

---

## 1. 结论先行

1. **没有单一的"下达工作"统入口**（`PlayerController` + 各个 `*Tool` 各自处理拖拽）。
2. **但存在一个统一的模式**：每种"可被标记的对象"都挂着一个组件，组件上有 **public 的 `Mark*` 方法**：
   | 工作类型 | 组件 | public 方法 |
   |---|---|---|
   | 拆除 | `Deconstructable` | `QueueDeconstruction(bool)` / `IsMarkedForDeconstruction()` / `CancelDeconstruction()` |
   | 清扫 | `Clearable` | `MarkForClear(bool, bool)` / `CancelClearing()` |
   | 收获 | `Harvestable` | `SetCanBeHarvested(bool)` / `Harvest()` / `ForceCancelHarvest()` |
3. **挖掘是例外**：不是"给已有组件打标记"，而是**在格子上生成一个 `DigPlacer` 物体**：
   `DigTool.PlaceDig(int cell, int radius)` —— **`public static`**。
4. **优先级**统一走 `Prioritizable.SetMasterPriority(PrioritySetting)`（public）；
   chore 本身也带 `Chore.masterPriority`。
5. **所有 chore 最终都进 `ChoreProvider`**：`AddChore(Chore)` / `RemoveChore(Chore)` 都是 **public** ——
   这是"统一收口"，但**不需要我们手工造 Chore**（`Mark*` 方法会自己建）。

> ⚠️ **§1 这张表只列了 2026-11-14 当时确认的三类**；消毒/清空管道/攻击/捕捉/拖地/拆断 的入口见 **§2（2026-11-16 按"标记模型"重写）** ——
> 其中"拖地/消毒只能模拟 UI"的旧结论**已被判错**（见 §2.2）。

⇒ **API 形状应该是**：`POST /v1/job { "type": ..., "x": .., "y": .., ... }`，
实现 = **按坐标找组件 → 调它的 public `Mark*` 方法**，而不是模拟 UI 拖拽。

---

## 2. 各工作类型的正规入口（**按「标记」模型重写**，M6.75 / 2026-11-16）

> **这一节整体重写。** 用户原话（比旧调研准）：
> 「清空管道也是挂个标记等人清的；shift 的拆断是给管道和电线剪断用的，防止意外连接；消毒也是拉范围然后给建筑打标记；
> 攻击是拉范围给小动物挂标记；拖地也是给地上的水挂标记。**总之都是标记，毕竟这游戏就是指挥小人干活的。**」
>
> 旧版把「消毒 / 拖地 / 攻击 / 清空管道」写成"只能模拟 UI / 风险最高" —— **那是按"找 Tool 类"的方向摸错了地方**：
> `*Tool` 只是玩家拖拽的那层皮，**真正干活的一方是 `*able` / `*Workable` 组件上的 `Mark*` 方法**（与已跑通的 `dig`/`clear`/`deconstruct` 同形状）。

### 2.1 七个操作：谁是"被打标记"的一方（**已核实签名**）

| 操作 | 组件（全名） | 方法签名 | 来源 | 置信度 |
|---|---|---|---|---|
| **挖掘 dig** | 无组件（生成标记物） | `DigTool.PlaceDig(int cell, int radius) : GameObject`（**public static**） | 已实现并实测（0.70.x 起） | ✅ **已核实 + 已在用** |
| **清扫 sweep/clear** | `Clearable` | `MarkForClear(bool, bool)` / `CancelClearing()` | 已实现 | ✅ 已在用 |
| **拆除 deconstruct** | `Deconstructable` | `QueueDeconstruction(bool)` / `IsMarkedForDeconstruction()` / `CancelDeconstruction()` | 已实现 | ✅ 已在用 |
| **消毒 disinfect** | **`Disinfectable`** | **`MarkForDisinfect(bool)`**（public **实例**） | 元数据 + `DisinfectTool.OnDragTool` 的 IL | ✅ **已核实** |
| **清空管道 empty pipe** | **`IEmptyConduitWorkable`**（实现类 `EmptyConduitWorkable`） | **`MarkForEmptying()`**（public，**无参**） | 元数据 + `EmptyPipeTool.OnDragTool` 的 IL | ✅ **已核实** |
| **攻击 attack** | `Attackable`（无 public 方法）⇒ 走 `AttackTool` 的**静态范围入口** | **`AttackTool.MarkForAttack(Vector2 a, Vector2 b, bool)`**（public static） | 元数据 + IL（遍历 `Components.FactionAlignments`） | ✅ **已核实** |
| **捕捉 capture** | **`Capturable`** | **`MarkForCapture(bool)`** / **`MarkForCapture(bool, PrioritySetting, bool)`**（public 实例） | 元数据 + `CaptureTool.MarkForCapture(Vector2,Vector2,bool)` 的 IL（遍历 `Components.Capturables`） | ✅ **已核实** |
| **拖地 mop** | **`Moppable` 没有 `Mark*`**（只有 `OnStartWork/OnCompleteWork/MopCell`） | 标记 = **实例化 `MopTool.Placer` 预制体**到 `Grid.Objects[cell, ObjectLayer.MopPlacer]` | `MopTool.OnDragTool` 的 IL：`KInstantiate(MopTool::Placer)` → `ObjectLayerIndexer::set_Item` → `Prioritizable::SetMasterPriority` | ✅ **已核实（但入口非 public）** |
| **拆断 disconnect** | **`IDisconnectable`**（`Connect()` / `Disconnect()` / `IsDisconnected()`，public 接口） | 入口 **`DisconnectTool.RunOnRegion(Vector3, Vector3, Action<int,GameObject,IHaveUtilityNetworkMgr,UtilityConnections>)`** —— **nonpub**（反射） | 元数据 + `DisconnectTool.OnDragComplete` 的 IL（`SnapToLine` → `RunOnRegion`） | ⚠️ **接口已核实；入口是 nonpub ⇒ 需反射** |

### 2.2 旧结论里被判错的三行（保留痕迹，说明为什么错）

| 旧写法 | 为什么错 |
|---|---|
| ~~拖地 mop：只能模拟 UI / 未确认~~ | **错在只找了 Tool 层**。标记物是 `MopTool.Placer` 预制体（`MopPlacerConfig` → `ObjectLayer.MopPlacer`），`MopTool.OnDragTool` 的 IL 里能看到完整链路；**"模拟 UI"只是其中一种走法**（`MopTool.Instance.OnDragTool(cell, cell)` 本身也是 public）。 |
| ~~消毒 disinfect：只能模拟 UI / 未确认~~ | **错在没往下看组件**。`Disinfectable.MarkForDisinfect(bool)` 是 **public 实例方法**；`DisinfectTool.OnDragTool` 的 IL 明确是 `Grid.Objects[cell,layer]` → `PrimaryElement.DiseaseCount` → `Disinfectable.MarkForDisinfect(bool)`。 |
| ~~攻击/捕捉"只能模拟 UI / 风险最高"~~ | **错在同一件事**。`AttackTool.MarkForAttack` / `CaptureTool.MarkForCapture` 都是 **public static 且自带范围遍历**（IL 里分别是 `Components.FactionAlignments` / `Components.Capturables` + `Grid.PosToXY` 的 bbox 过滤）—— 它们**本身就是"打标记"**，不是 UI 模拟。 |

### 2.3 实现形状（对下一版直接可用）

1. **凡是"单对象 Mark"**（消毒 / 清空管道 / 捕捉）：
   `遍历矩形格子 → 每格取组件（`GetComponent<T>()` / `Grid.Objects[cell, layer]`）→ 用 instanceId 去重 → 逐个调 Mark*`。
   **不需要范围 API**，单格/单对象就够。
2. **凡是"工具本身 public static 且接受矩形"**（攻击 / 捕捉）：
   直接传 `PhysicsUnit` 的矩形（`Grid.CellToPos` 两个角），**一次调用覆盖整片**。
3. **拖地**：两条路 ——
   (a) 反射取 `MopTool.Instance` 的 nonpub `Placer` 字段 → `KInstantiate` → 写入 `Grid.Objects[cell, ObjectLayer.MopPlacer]` → `SetMasterPriority`（与 `MopTool.OnDragTool` 同链路，但**要自己复刻定位/动画细节**）；
   (b) 直接 `MopTool.Instance.Activate()` + `OnDragTool(cell, 0)`（**public 实例方法**，与玩家拖拽同一条路；代价是会动到当前工具状态，必须 `Deactivate` 复原）。
   ⇒ **建议 (b)**：代码量小、与玩家等价；但要在 finally 里恢复工具状态（我们在 `build` 上已经有 `toolRestored` 的现成写法）。
4. **拆断**：只能反射 `DisconnectTool.RunOnRegion`（nonpub）并传自己的 `Action<int,GameObject,IHaveUtilityNetworkMgr,UtilityConnections>` 回调，
   在回调里对带 `IDisconnectable` 的对象调 `Disconnect()`；或模拟 UI 的 `OnDragComplete`（public，但需要工具处于激活态且 `SnapToLine` 行为）。
   ⚠️ **这一条是本批次唯一的"非标记"操作**，也是唯一必须反射的。

### 2.4 结论（决定下一版做几个）

**7 类里：4 类可以只用「单对象/单格 Mark」或"工具 public static 范围入口"就实现**
（**消毒** `Disinfectable.MarkForDisinfect` / **清空管道** `IEmptyConduitWorkable.MarkForEmptying` / **攻击** `AttackTool.MarkForAttack` / **捕捉** `Capturable.MarkForCapture` 或 `CaptureTool.MarkForCapture`），
**1 类（拖地）需要动工具状态**（public `OnDragTool`，成本低但要 finally 复原），
**1 类（拆断）必须反射**（`DisconnectTool.RunOnRegion` nonpub，且它是唯一改连接关系而不是打标记的），
另有 1 类（收获 harvest）此前已单独调研（见 §8）。

⇒ **下一版建议一口气做 4 个纯 Mark 的**（消毒 / 清空管道 / 攻击 / 捕捉），
拖地放同一批（走 `OnDragTool` 逃生口），**拆断单独一批**（反射 + 需要设计"剪哪条连接"的参数）。
## 3. 统一收口：`ChoreProvider`

```
ChoreProvider.AddChore(Chore)       // public
ChoreProvider.RemoveChore(Chore)    // public
Chore.masterPriority : PrioritySetting
```
**存在**，但 **`Chore` 的构造参数极多**（IL 里 `new Chore(...)` 有十几个参数）⇒ 不建议手工造。
`Mark*` 方法内部会自己创建对应的 chore（例如 `Deconstructable.QueueDeconstruction` 会建一个 `Chore`）。

**推论**：API 层不需要碰 `Chore`，只需要 `Mark*` + `Prioritizable`。

---

## 4. `*Placer` 预制体（挖掘/拖地这类"临时标记物"的机制）
DLL 里出现的名字：`DigPlacer`、`MopPlacer`、`TilePlacer`、`AreaPlacer`、`MovePickupablePlacer`
（另有纯视觉的 `ElecPhysPlacer`/`ElecVisPlacer`/`GasPhysPlacer`/`LiquidPhysPlacer` 等）。

- `DigTool.PlaceDig` 就是"实例化 `DigPlacer` 并登记到 `Grid.Objects`"。
- 这类 placer 物体是**标记的载体**，玩家取消时销毁它（所以 `Diggable` 组件挂在 placer 上，不是挂在原生格子上）。

---

## 5. 建议的 API 形状（M6 草案，未实现）

```
POST /v1/job
{ "type": "dig|deconstruct|sweep|harvest|build|door|capture",
  "x": 111, "y": 222, "worldId": 0,
  "priority": 5,                 // 可选，走 Prioritizable.SetMasterPriority
  "prefabId": "Tile",            // 仅 build
  "orientation": "neutral",      // 仅 build
  "instant": false }             // 作弊层（暂不实现）
```

响应统一：
```
{ "type": ..., "target": { "worldId","x","y","cell","prefabId" },
  "accepted": true,          // 已打上标记/已发起
  "queued": { "chore": "Deconstruct", "priority": 5 },   // 若可读
  "before": {...}, "after": {...},   // 可回读的类型（如门/拆除标记）
  "pending": false, "note": "..." }
```

**关键判据沿用 DESIGN §10.6q**：`accepted`（已受理）≠ `applied`（已生效）≠ `pending`（等小人）。

---

## 6. 未确认项（别当成结论）

1. `Clearable.MarkForClear(bool, bool)` **两个 bool 各是什么**（疑似 `markAsSweep` / `force`），未实测。
2. `Harvestable.SetCanBeHarvested(true)` 与 `Harvest()` 的差别、以及是否会立刻生成 chore。
3. `DigTool.PlaceDig` 在 **`DigTool.Instance == null`**（HUD 未初始化）时的行为：IL 显示会读 `Instance.IsActiveLayer`，
   推测 **NRE**，未实测。
4. `PrioritySetting` 的构造（`new PrioritySetting(priority_class, value)`?）与取值域（`Chore.MIN_PLAYER_BASIC_PRIORITY` 等常量已在元数据里）。
5. 拖地（mop）/ 消毒（disinfect）是否有非 UI 入口 —— 目前只看到 `*Placer` 与实例工具。
6. 所有 `Mark*` 方法**是否有等级/前置条件**（研究、材料、权限），失败时是静默还是抛异常 —— 未实测。

---

## 7. 给实现的一句话

**"手"的通用实现 = 按坐标找组件 → 调该组件的 public `Mark*` 方法 → 用 `Prioritizable` 调优先级 → 回读组件状态。**
`DigTool.PlaceDig` 与 `CaptureTool.MarkForCapture` 是两个"静态捷径"，其余类型走组件方法。
**不要模拟 UI 拖拽** —— 那条路会绑死到 `PlayerController` 与工具选中状态。
---

## 8. `harvest` 改造方案（M6.7 前置调研，2026-11-14）

### 8.1 关键发现：**我们现在的 `harvest` 用错了字段**
| 字段 | 类型 | 语义 | 证据 |
|---|---|---|---|
| `HarvestDesignatable.isMarkedForHarvest` | **public 字段** | **玩家"标记收获"的状态** | `HarvestDesignatable` 成员 |
| `HarvestDesignatable.MarkedForHarvest` | **public 属性**（get/set） | 同上（推荐用这个） | `get_MarkedForHarvest` / `set_MarkedForHarvest` |
| `Harvestable.canBeHarvested` | public 字段 | **这株植物此刻是否长熟了**（自然状态） | `Harvestable` 成员 |

⇒ **"标记收获"应该写 `HarvestDesignatable.MarkedForHarvest = true`**（public，零反射）；
`Harvestable.canBeHarvested` 只是**成熟度**，把它当"已标记"是错的 ——
我们现在的实现（`SetCanBeHarvested(true)`）**语义不对**：那等于"把这株植物硬说成熟"，不是"派活去收"。

### 8.2 一格上可能同时有几个 `Harvestable`？
- `Harvestable` 是**组件**，挂在**对象**上 ⇒ **一株植物一个**；
- `HarvestDesignatable.area : OccupyArea` ⇒ 植物**占多格**（占地区域），
  所以"格 → 植物"是**多对一**（多个格属于同一株），而"格 → 多个植物"理论上也可能是**多对多**（相邻植物区域重叠时）。
- ⇒ **`harvest` 与 `deconstruct` 同构：格级寻址不够，必须点名。**
  主键仍应是**`ObjectLayer` 枚举名**（对象级），`prefabId` 作为别名（同名植物常见）。

### 8.3 建议形状（与 deconstruct 对齐）
```
{"type":"harvest","x":..,"y":..}                        → 唯一 ⇒ 执行；多个 ⇒ 409 + candidates
{"type":"harvest","x":..,"y":..,"layer":"Plant"}        → 按层点名
{"type":"harvest","x":..,"y":..,"prefabId":"PrickleFlower"}
```
`candidates[]`：`{prefabId, layer, filterLayer, filterLayerName, markedForHarvest, readyToHarvest}`
- `markedForHarvest` ← `HarvestDesignatable.MarkedForHarvest`（**public**，不需要反射）
- `readyToHarvest` ← `Harvestable.canBeHarvested`（成熟度，**只读展示**，不用于判定"已标记"）

`jobState`：`marked`（已标记收获）→ 以后可加 `done`（`HarvestDesignatable` 消失/作物已收）

### 8.4 与现有实现的差异（改造清单）
1. 入口：`SetCanBeHarvested(true)` → **`HarvestDesignatable.MarkedForHarvest = true`**；
2. 回读：`Harvestable.canBeHarvested` → **`HarvestDesignatable.MarkedForHarvest`**；
3. 选择：`FindHarvestableAt`（逐层扫取第一个）→ **`EnumerateCellObjects` + 候选 + 点名**（复用 deconstruct 那套）；
4. 响应：`alreadyMarked` 语义改为"已被标记收获"（不是"已成熟"）。

## 9. `sweep` 反射回读的可靠性（调研，不改代码）

### 9.1 结论：**同一次调用后立刻反射读，是可靠的**
证据（`Clearable.MarkForClear` IL 顺序）：
```
+63  02 17 7D Clearable::isMarkedForClear     // this.isMarkedForClear = true
+6A  …     GlobalChoreProvider::RegisterClearable(...)   // 之后才登记 chore
+7B  …     RefreshClearableStatus(true)
```
⇒ **字段在方法返回前就已写入**，方法同步执行（不是延迟到下一帧）。
⇒ 我们的做法（调用后立刻反射读）**读到的一定是新值**，不存在"读到旧值"的窗口。

### 9.2 那风险在哪
| 风险 | 说明 | 现有缓解 |
|---|---|---|
| **字段改名** | 游戏更新把 `isMarkedForClear` 改名 ⇒ 反射拿到 null | 已返回 `unavailable: "field-not-found: …"`（可区分） |
| **反射被禁** | 极端环境（AOT/裁剪） | 返回 `unavailable: "reflection-error: <Type>: …"` |
| **读错对象** | 多对象同格时接到别的 `Clearable` | `sweep` 现在也是"逐层扫取第一个" ⇒ **同样需要候选 + 点名** |

### 9.3 建议
1. **反射保留**（`Clearable` 确实没有 public 读入口，`isMarkedForClear` 是 nonpub）；
2. **加"前后对比"作为自检**：记 `before = 读()`，调用后 `after = 读()`；
   若 `before == after` 且 `before == false` ⇒ 明确报 `READBACK_FAILED`（而不是"成功"）；
3. **`sweep` 也走候选 + 点名**（与 deconstruct/harvest 同构），因为碎屑在 `Pickupables` 层，一格可能有多堆。
