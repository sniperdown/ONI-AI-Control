# ONI 原生操作盘点（M5 调研，只调研不改代码）

> 目的：不要为每个建筑硬编码一条命令，而是建立 **「对象 + 动作」** 的通用模型。
> 数据来源：`Assembly-CSharp.dll` / `Assembly-CSharp-firstpass.dll` 元数据与 IL、游戏自带中文 `.po` 本地化、
> 离线 wiki（`oni-wiki.db`）。**凡是没直接从元数据/文件里读到的，都标「未确认」**。

---

## 1. 分类树（游戏本体为准）

### 1.1 建造菜单：**6 个顶层页签 + ~70 个子分类**
来源：`strings_preinstalled_zh_klei.po` 里 `STRINGS.UI.NEWBUILDCATEGORIES.<KEY>.NAME`，共 **76** 个键；
其中 **6 个的值里带 `<link="BUILDCATEGORY...">`**，就是顶层页签。

| 顶层页签（KEY / 中文 / 英文） |
|---|
| `BASE` 基地 / Base |
| `FOODANDAGRICULTURE` 食物 / Food |
| `HEALTHANDHAPPINESS` 住宿 / Accommodation |
| `INDUSTRIAL` 工业 / Industrials |
| `INFRASTRUCTURE` 实用 / Utilities |
| `LOGISTICS` 物流 / Logistics |

子分类（其余 70 个键，节选代表性分组，**它们挂在哪个顶层 tab 下未确认** —— 该映射在
`PlanScreen.tagCategoryMap : Dictionary<Tag, HashedString>`（nonpub，运行时数据））：

* 门/墙/砖：`DOORS` 门、`TILES` 砖块与干板墙、`LADDERS` 梯子
* 电力：`GENERATORS` 发电机、`BATTERIES` 蓄电池、`WIRES` 电线、`SWITCHES` 开关、`POWERCONTROL`、`ELECTROBANKBUILDINGS` 转换器
* 管道/通风：`PIPES` 管道、`PLUMBINGSTRUCTURES` 水管、`VENTILATIONSTRUCTURES` 通风、`VALVES` 阀、`PUMPS` 泵、`TANKS` 液缸、`CONDUITSENSORS` 管道传感器
* 氧气/温度：`OXYGEN` 氧气、`SCRUBBERS` 净化、`TEMPERATURE` 温度、`UTILITIES` 温度
* 工业/精炼：`REFINING` 精炼、`MANUFACTURING` 制造、`INDUSTRIALSTATION` 工业、`PRODUCERS` 生产
* 农业/养殖：`FARMING` 耕种、`RANCHING` 养殖、`COOKING` 烹饪、`ORGANIC` 有机物、`MICROBE`（未列全）
* 生活：`BEDS` 床位、`DINING` 饮食、`WASHROOM` 厕所、`HYGIENE` 卫生、`SANITATION` 卫生、`MEDICAL` 医疗、`WELLNESS` 健康、`RECREATION` 娱乐、`DECOR` 装饰、`FURNITURE` 家具、`LIGHTS` 照明
* 自动化/逻辑：`AUTOMATED` 自动化、`LOGICGATES` 逻辑门、`LOGICMANAGER` 监测、`SENSORS` 传感器
* 火箭/DLC：`ROCKETRY` 火箭、`ROCKETNAV` 导航、`ROCKETSTRUCTURES` 构造、`ENGINES` 引擎、`CARGO` 货舱、`MODULE` 舱块、`MISSILES`/`METEORDEFENSE` 流星抵御、`TELESCOPES` 望远镜、`TRANSPORT` 运载管道
* 其它：`STORAGE` 存储、`DISTRIBUTORS` 分配、`ADVANCED` 高级、`RESEARCH` 研究、`PRINTINGPODS` 打印舱、`ARCHAEOLOGY` 考古、`MATERIALS` 材料、`EQUIPMENT` 装备、`FITTINGS` 配件、`PORTS`/`BUILDMENUPORTS` 端口、`OPERATIONS` 操作、`CONVEYANCE`（空值）

### 1.2 Tag 体系：**`GameTags` 有 336 个静态 Tag**
- `GameTags`（Assembly-CSharp）共 **336** 个 `public static Tag` 字段 —— 这就是"分类很细"的来源。
  例：`Pickupable`、`Edible`、`Medicine`、`Plant`、`FullyGrown`、`SpawnsInWorld`、`Miscellaneous`、`Seed`…
- 单个对象身上的标签在 `KPrefabID.Tags`（内部 `HashSet<Tag>`）+ `PrefabTag`；
  `TagManager` 只管**显示名**（`GetProperName`）。
- ⚠️ 336 个 tag 里大量是**物品/元素/状态**，不是"可操作对象"。直接全量暴露给 AI 会造成噪音。

### 1.3 建筑注册表
- `Assets.BuildingDefs : List<BuildingDef>`（public static）、`Assets.GetBuildingDef(string prefabID)`。
- `BuildingDef` 共 **204** 个成员，关键是 `WidthInCells/HeightInCells`、`ObjectLayer`、`Deprecated`、
  `RequiresPowerInput`、`Overheatable`、`Create/Build/TryPlace/Instantiate(...)`。
- ⚠️ **`BuildingDef` 没有 `Category` 字段**（已逐个成员查过）⇒ 菜单分类**不在建筑定义上**，
  而在 `PlanScreen.tagCategoryMap`（运行时、nonpub）。想拿"某建筑属于哪个分类"需要反射该字典。

---

## 2. 动作矩阵（类名 + 方法名）

| 对象 | 动作 | 入口（已核实）/ 备注 |
|---|---|---|
| **任意建筑** | 启用 / 禁用 | `BuildingEnabledButton.IsEnabled`（get/set）、`HandleToggle()`、静态 `EnabledFlag`；内部转 `Operational.Flags[EnabledFlag]` |
| **任意建筑** | 运行状态查询 | `Operational.IsFunctional / IsOperational / IsActive`（getter public，setter nonpub） |
| **门**（`Door`） | 开 / 关 / 锁 / 自动 | `Open()`、`Close()`、`QueueStateChange(ControlState)`；`CurrentState`/`RequestedState`；枚举 `ControlState { Auto, Opened, Locked }` |
| **可拆建筑** | 拆除 / 取消拆除 | `Deconstructable.allowDeconstruction`、`Deconstructable.chore`；**`CancelDeconstruction` 之类未确认** |
| **工作台**（`ComplexFabricator`） | 改配方队列 | `SetRecipeQueueCount(ComplexRecipe,int)`、`IncrementRecipeQueueCount(ComplexRecipe)`、`DecrementRecipeQueueCount(ComplexRecipe,bool)`、`GetRecipeQueueCount`、`IsRecipeQueued`、`GetRecipes()`、`GetRecipe(string)`、`CompleteWorkingOrder()`；`MAX_QUEUE_SIZE` / `QUEUE_INFINITE`；`CancelWorkingOrder()` 是 nonpub |
| **复制人 / 建筑** | 改优先级 | `Prioritizable.GetMasterPriority()` / `SetMasterPriority(PrioritySetting)`、`IsPrioritizable()`、`IsTopPriority()` |
| **任务（chore）** | 派活 | `ChoreProvider.choreWorldMap : Dictionary<int,List<Chore>>`（nonpub）、`Chore.masterPriority : PrioritySetting`、`Chore.MIN/MAX_*_PRIORITY` 常量；**"给某个小人插一条自定义任务"的通用 public API 未确认**（`Workable`/`ChoreProvider.AddChore` 需继续挖） |
| **作息**（`ScheduleManager`） | 排班 / 改班次 | `ScheduleManager.schedules`（nonpub List）、`Schedule.name`（public）、`Schedule.GetCurrentScheduleBlock()`、`GetCurrentBlockIdx()`、`Schedule.onChanged`；`Schedule.blocks` 是 nonpub；**`ScheduleBlock`/`ScheduleBlockType` 的结构未取到（未确认）** |
| **建造 / 放置** | 放置建筑 | `BuildingDef.TryPlace(GameObject,int,Orientation,IList<Tag>,int)`、`Instantiate(...)`、`Build(...)`；注意需要材料列表与格子 |
| **格子（砖块）** | 建造 / 挖掘 | 建造走 `BuildingDef`（`TILES` 类）；挖掘走 `DigTool`/`Workable`（**未确认具体入口**） |

---

## 3. 寻址方案（用户拍板：以格子坐标为主键）

**主键 = `(worldId, x, y)`**；`InstanceID` 只作内部辅助（读档后可能重排，未确认）。

### 3.0 建筑锚点（已核实，IL 证据）
```
Building.GetCell()            IL: ldarg.0; call Grid::PosToCell; ret
Building.GetBottomLeftCell()  IL: ldarg.0; call get_transform; call GetPosition; call Grid::PosToCell; ret
```
两者都是 `Grid.PosToCell(transform.position)`，得到同一个格子 ⇒ **锚点 = 建筑 `transform.position` 所在格**；
方法名表明它就是**左下角格**（多格建筑向 +x/+y 扩展；**多格建筑的实际偏移未实测，标未确认**）。
配套 API（都 public）：
* `Building.PlacementCells : int[]` —— 占据的所有格子
* `Building.PlacementCellsContainCell(int) : bool` —— 点某格 → 判断该建筑是否占它（做"点哪管哪"的关键）
* `Building.Def.WidthInCells / HeightInCells` —— 尺寸（运行时取）
* `Building.Orientation` —— 朝向（建造时需要）

### 3.1 为什么对外用 x,y 整数
`Grid.XYToCell(x,y)` → cell 索引；`Grid.CellToXY(cell) -> Vector2I`；`Grid.CellToPos` 给浮点（只用于表现层）。
**对外用两个整数（人读友好）**，内部转 cell。多星球必须带 `worldId`（`WorldContainer`）。


| 对象 | 候选 id | 证据 | 跨存档稳定性 |
|---|---|---|---|
| 建筑实例 | `KPrefabID.InstanceID : int` | firstpass；`InvalidInstanceID` 表示无效；`KPrefabIDTracker` 做映射 | **未确认**（instanceID 是否读档后重排需实测） |
| 建筑类型 | `KPrefabID.PrefabTag : Tag` / `BuildingDef.PrefabID` | 已核实字段存在 | 高（类型级） |
| 复制人 | `MinionIdentity.arrivalTime : float`（public）+ `name`（nonpub 字段） | 已核实字段 | **arrivalTime 优先**（名字可重名；arrivalTime 与到达顺序绑定，未确认唯一性） |
| 复制人（更稳） | `personalityResourceId : HashedString` | 已核实 | 高（性格资源 id） |
| 格子 | `Grid.XYToCell(int,int)` / `Grid.CellToXY(int) -> Vector2I` / `Grid.CellToPos(int)` | 已核实方法签名 | 高（但注意世界：`WorldContainer` 需一起给） |

**建议**：对外 API 用「`worldId` + `cell(x,y)`」定位格子类对象；建筑用「`prefabID` + `x,y`」或 `instanceID`；
复制人用「`arrivalTime`（+ name 便于人读）」。所有 id 都**不要**暴露 Unity 的 `GetInstanceID()`（临时、跨会话不稳定）。

---

## 4. 对不上的地方（wiki ↔ 游戏本体）

1. **wiki 几乎没有分类体系**：`Category:` 页面只有 **8 个**（发电机/娱乐建筑/可食用物/过滤介质/金属矿石/密封圈/升华物/纤维），
   远不足以描述游戏的 6 顶层 + 70 子分类 ⇒ **分类树只能用游戏本体**（`NEWBUILDCATEGORIES` + `PlanScreen.tagCategoryMap`）。
2. **wiki 的建筑信息框几乎没有字段**：`建筑信息框` 模板只用到 `img / multipage / base / so / pagename / labels`，
   说明参数是模板代码自动生成的，**不是**可读的结构化数据 ⇒ 参数（功率、产热、材料）只能从 `BuildingDef` 取。
   （反例：`物品信息框` 有"分类"这个字段，物品侧是有类别的；建筑侧没有。）
3. **wiki 讲的是"机制/技巧"，不是"动作"**：例如「机械气闸」页写的是"绿信号开门、红信号关闭并锁定"、
   "通电开门更快"、"打开时不导热" —— 这些是**规则**，需要人工翻译成 `Door.QueueStateChange(ControlState)` 之类的动作，
   不能直接当 API 清单用。
4. **命名体系不同**：wiki 用中文名（机械气闸/手动气闸/隔热门），游戏用 `prefabID`（`Door`/`PressureDoor`/…）
   ⇒ 需要一张 **中文名 ↔ prefabID** 映射表；映射可从 `.po` 的 `STRINGS.BUILDINGS.PREFABS.<ID>.NAME` 生成（**这轮未生成**）。
5. **tag 数量 vs 可操作性**：`GameTags` 336 个，但其中大部分不是"能被指挥的对象" ⇒ 需要一层"可操作对象"白名单，
   不能把 tag 全量当分类。

---

## 5. 需要用户/游戏内确认的问题（拿不准的，不猜）

1. **顶层分类**就按游戏的 6 个页签（基地/食物/住宿/工业/实用/物流）？还是要更细的 70 个子分类？
2. **AI 的操作粒度**：走"下标记 → 让小人执行"（真实，但要接 chore/路径/材料），还是"直接改状态"（快，但像作弊）？
3. **建造/拆除**是否允许 AI 直接做？还是只允许"打标记"（像玩家拖动拆除工具）？
4. **复制人寻址**用 `arrivalTime` 还是名字？重名时怎么办？
5. **工作台配方**用 `ComplexRecipe.id` 还是产物 `Tag`？
6. **门的三态**（Auto/Opened/Locked）是否够用？还要不要"仅允许某复制人通行"（这属于额外 auth 系统，未调研）？
