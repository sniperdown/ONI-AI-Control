# 动作矩阵（M5.1）

> **每个动作必须标注实现风险**，因为这决定我们做它要花多少代价：
> * **公共 API** —— 直接调 public 方法/字段，风险最低；
> * **反射** —— 目标成员是 nonpub（字段/方法），需要 `BindingFlags.NonPublic`，游戏更新可能改名；
> * **未确认** —— 我们还没找到权威入口，或只确认了数据结构没确认"怎么触发"。

**寻址约定（用户拍板）**：主键 `(worldId, x, y)` —— `x,y` 是两个整数格坐标，内部用 `Grid.XYToCell(x,y)` 转 cell。
建筑锚点 = `Building.GetCell()` = `Grid.PosToCell(transform.position)`，即**左下角格**（多格实测待确认）。
判别"点哪管哪"用 `Building.PlacementCellsContainCell(cell)`；尺寸用 `Building.Def.WidthInCells/HeightInCells`。

---

## A. 建筑通用

| 动作 | 入口（已核实签名） | 实现风险 | 需要寻址 | 备注 |
|---|---|---|---|---|
| 启用 / 禁用建筑 | `BuildingEnabledButton.IsEnabled`（public get/set）、`HandleToggle()` | **公共 API** | `(world,x,y)` | 内部走 `Operational.Flags[BuildingEnabledButton.EnabledFlag]` |
| 读运行状态 | `Operational.IsFunctional / IsOperational / IsActive`（public getter） | **公共 API** | `(world,x,y)` | setter 是 nonpub，只能读 |
| 拆除建筑（标记） | `Deconstructable.allowDeconstruction`（public 字段）、`Deconstructable.chore` | **公共 API（字段）+ 未确认** | `(world,x,y)` | "标记拆除"的具体触发方法名未确认（可能通过 user menu action） |
| 取某格上的建筑 | `Building.PlacementCellsContainCell(cell)`、`Building.PlacementCells` | **公共 API** | `(world,x,y)` | 反向查询：cell → 建筑 |
| 取建筑尺寸/朝向 | `Building.Def.WidthInCells/HeightInCells`、`Building.Orientation` | **公共 API** | `(world,x,y)` | |
| 放置建筑 | `BuildingDef.TryPlace(GameObject?,int,Orientation,IList<Tag>,int)`、`Instantiate(...)`、`Build(...)` | **公共 API（参数复杂）** | `prefabID + (world,x,y) + 朝向` | 需材料 `IList<Tag>`；失败原因靠返回值 null + 日志 |

## B. 门（`Door`）

| 动作 | 入口 | 实现风险 | 需要寻址 | 备注 |
|---|---|---|---|---|
| 开 / 关 / 锁 | `Door.QueueStateChange(ControlState)`；便捷方法 `Open()` / `Close()` | **公共 API** | `(world,x,y)` | 枚举 `ControlState { Auto, Opened, Locked }` |
| 读当前/请求状态 | `Door.CurrentState`、`Door.RequestedState` | **公共 API** | `(world,x,y)` | |
| 判断是否开着 | `Door.IsOpen()`、`Door.IsPendingClose()` | **公共 API** | `(world,x,y)` | |
| 仅允许某个复制人通行 | —— | **未确认** | —— | 属 auth/access 系统，本轮未调研 |

## C. 工作台（`ComplexFabricator`）

| 动作 | 入口 | 实现风险 | 需要寻址 | 备注 |
|---|---|---|---|---|
| 加/减队列 | `SetRecipeQueueCount(ComplexRecipe,int)`、`IncrementRecipeQueueCount(ComplexRecipe)`、`DecrementRecipeQueueCount(ComplexRecipe,bool)` | **公共 API** | `(world,x,y)` + recipe | `MAX_QUEUE_SIZE` / `QUEUE_INFINITE` |
| 查队列 | `GetRecipeQueueCount(ComplexRecipe)`、`IsRecipeQueued(ComplexRecipe)`、`get_HasAnyOrder`、`get_NextOrder`、`get_OrderProgress` | **公共 API** | `(world,x,y)` | |
| 查配方表 | `GetRecipes() : ComplexRecipe[]`、`GetRecipe(string)`、`GetRecipesWithCategoryID(string)` | **公共 API** | `(world,x,y)` | recipe 的稳定 id 未确认（`ComplexRecipe.id` 待核） |
| 立即完成当前单 | `CompleteWorkingOrder()` | **公共 API** | `(world,x,y)` | 作弊向；正常玩法靠小人工作 |
| 取消当前单 | `CancelWorkingOrder()`（nonpub） | **反射** | `(world,x,y)` | |

## D. 复制人与优先级

| 动作 | 入口 | 实现风险 | 需要寻址 | 备注 |
|---|---|---|---|---|
| 复制人定位 | `MinionIdentity.arrivalTime`（public 字段）、`personalityResourceId`（nonpub） | **公共 API（arrivalTime）+ 反射（personality）** | `minionId` | 名字是 nonpub 字段 ⇒ 反射 |
| 改优先级 | `Prioritizable.SetMasterPriority(PrioritySetting)`、`GetMasterPriority()`、`IsTopPriority()` | **公共 API** | `(world,x,y)`（建筑/工作对象） | 也可配合 `PrioritizeTool` |
| 派自定义任务 | `ChoreProvider.choreWorldMap : Dictionary<int,List<Chore>>`（nonpub）、`Chore.masterPriority` | **反射** | `(world,x,y)` + 小人 | **"插入自定义 chore"的公开入口未确认**（要深入 `ChoreProvider`/`Workable`） |

## E. 作息（`ScheduleManager` / `Schedule`）

| 动作 | 入口 | 实现风险 | 需要寻址 | 备注 |
|---|---|---|---|---|
| 读所有作息表 | `ScheduleManager.schedules : List<Schedule>`（nonpub） | **反射** | —— | |
| 读表名 / 当前班次 | `Schedule.name`（public 字段）、`GetCurrentScheduleBlock()`、`GetCurrentBlockIdx()` | **公共 API** | —— | |
| 改班次内容 | `Schedule.blocks : List<ScheduleBlock>`（nonpub） | **反射** | —— | **`ScheduleBlock` / `ScheduleBlockType` 结构未取到 ⇒ 整条"改作息"仍是未确认** |

## F. 建造 / 挖掘

| 动作 | 入口 | 实现风险 | 需要寻址 | 备注 |
|---|---|---|---|---|
| 放建筑 | `BuildingDef.TryPlace/Instantiate/Build` | **公共 API（参数复杂）** | `prefabID + (world,x,y) + 朝向` | |
| 挖格子 / 拆砖 | `DigTool` / `Workable` 子类 | **未确认** | `(world,x,y)` | 本轮未追到权威入口 |

---

## 统计（这份矩阵里）

| 风险等级 | 条目数 | 明细 |
|---|---|---|
| **公共 API** | **17** | A 段 6、B 段 3、C 段 4、D 段 2、E 段 1、F 段 1 |
| **反射** | **4** | C 段 1（`CancelWorkingOrder`）、D 段 1（`ChoreProvider.choreWorldMap`）、E 段 2（`schedules`、`blocks`） |
| **未确认** | **2** | B 段 1（"仅允许某复制人通行"）、F 段 1（挖格子/拆砖的权威入口） |
| 混合标注 | 2 | "拆除（标记）"= 公共字段 + 触发方法未确认；"复制人定位"= `arrivalTime` 公共、`personalityResourceId` 反射 |

合计 23 条动作（17 公共 + 4 反射 + 2 未确认），另 2 条带混合备注。
**统计口径**：上表每一行算一条；混合行按"主入口"归类，备注里写明另一半。
