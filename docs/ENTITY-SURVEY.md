# 实体种类调研：这个存档里到底有什么，我们读到了多少

> **调研方式（重要）**：本文所有数量都是在**运行中的「外世」档（Cycle 6990，24 人，6 个世界，v0.88.3）**上
> 通过**只读 HTTP 接口**实地取到的（`127.0.0.1:8770`，带 `X-Oni-Token`），**没有改动 `src/`、没有新增代码**。
> 标记约定：**【实测】** = 本档实测数字；**【查证】** = 从游戏程序集成员表/类型表静态核对（不是本档实测）；
> **【未验证】** = 只能靠 wiki/经验，本档无法证实。
>
> 采集时间：2026-11-15 22:0x（`serverTime` 见 `/v1/state` 的 `ts`）
> 取值来源：`/v1/meta`、`/v1/state`、`/v1/buildings`（37 页 × 2000 条翻完）、`/v1/geysers`、`/v1/inventory`、`/v1/env`、`/v1/research`、`/v1/marks`

## 一、三列总表：种类 / 本档有没有 / 我们覆盖没覆盖

| 种类 | 本档实测 | 接口覆盖 |
|---|---|---|
| **格子物质**（固/液/气，`Grid.Element/Mass/Temperature`） | **【实测】有**：`/v1/env?around=dupes&r=5` ⇒ `gasCells=3812`、`solidPct=57.372%`、`breathablePct=4.763%` | ✅ `/v1/env`（只统计可见格 + 只算气体格）；`/v1/state.elements` 是**储物箱聚合**（另一口径） |
| **Building（已完工 + 施工中）** | **【实测】有 72,851 条 / 309 种 prefab**（`totalItems=72851`，`underConstruction=0`） | ✅ `/v1/buildings`（含 `underConstruction`） |
| ├ 其中 **Tile 类**（`Tile/TilePOI/GlassTile/CarpetTile/InsulationTile/BunkerTile/MetalTile/RocketWallTile…`） | 【实测】`Tile×703`、`TilePOI×607`、`GlassTile×1320`、`InsulationTile×5753`… | ✅ |
| ├ 其中 **Conduit/Wire/Logic** | 【实测】`SolidConduit×10099`、`InsulatedLiquidConduit×9194`、`LogicWire×4981`… | ✅ |
| ├ 其中 **Rocket 模块/部件** | 【实测】有：`SolarPanelModule×9`、`BatteryModule×7`、`HabitatModuleMedium×7`、`HydrogenEngineCluster×4`、`NoseconeBasic×5`、`RocketWallTile×238`、`LaunchPad×20`、`RocketControlStation×7` | ✅（作为 Building 读出；**没有**"飞船/模块"这个语义层） |
| ├ 其中 **POI/Gravitas 静物** | 【实测】`PropGravitasWall×128`、`GravitasDoor×13`、`GravitasPedestal×7`、`FossilSculpture×2` | ✅（作为 Building） |
| **间歇泉 / 火山** | **【实测】41 个**（`count=41,total=41,filteredTemplates=0,scanMode=false`），element 覆盖 Steam/MoltenNiobium/… 火山：`MoltenTungsten`/`Magma` 类 | ✅ `/v1/geysers`（v0.88.3） |
| **Minion（复制人）** | **【实测】24 个**（`/v1/state.dupes.count=24`，带 `x/y/worldId`、`choreId`） | ✅ `/v1/state` |
| **Creature（小动物）** | **【未验证/读不到】** —— 本档**无法测**：没有任何接口能枚举 `CreatureBrain` | ❌ **完全没有**（`/v1/state` 只有复制人） |
| **Egg（蛋）** | **【查证】是独立实体**：`EggConfig` / `EggShellConfig` / `IncubationMonitor` 存在；**本档数量读不到** | ❌ 读不到（既不在 `BuildingCompletes`，也没有端点） |
| **Seed（种子）** | **【查证】**`PlantableSeed` 类型存在、`Components.PlantableSeeds` 集合存在；**本档数量读不到** | ❌ 读不到 |
| **植物（Plant/Growing）** | **【查证】植物不是 Building**：类型表里**没有 `Plant` 类型**，但有 `Growing`/`Crop`；`Components.Crops` 存在。**本档实测**：建筑清单里**没有**任何作物 prefab（只有 `PlanterBox×6`、`HydroponicFarm×131`、`FarmTile×128` 这些"盆/砖"） ⇒ 植物在 `Plants` 层、**我们完全读不到** | ❌ **完全没有**（`/v1/marks` 的 `harvest` 段能读到**单格**上的 Harvestable，但不是全局清单） |
| **Pickupable（地上散落物）** | **【实测 部分】**：`/v1/inventory?tag=X` 能给出**按元素的**在库/在地上量 + `groundStacks`：<br>`SandStone` onGround 1,548,666kg / **150 堆**；`Dirt` 322,441kg / **788 堆**；`Algae` 199,279kg / **40 堆**；`Cuprite` 6,545kg / **2 堆**；`Water` 692kg / **32 堆** | ⚠️ **只有"按 tag 的聚合"**：要按类型/区域列清单得**逐 tag 请求**（173 个元素 tag）；**没有**按坐标/按 prefab 的清单端点 |
| **Artifact（文物）** | **【查证】**`ArtifactConfig`/`ArtifactType`/`SpaceArtifact` 存在、`Components.SpaceArtifacts` 存在；**本档建筑里只有 `ArtifactAnalysisStation×1`（分析站，不是文物本身）** ⇒ 文物本身读不到 | ❌ 读不到 |
| **Spacedestination（星际目标）** | **【查证】**`SpaceDestination`/`Spacedestination` 类型存在；**不属于任何世界内的格子对象** ⇒ 现在没有端点 | ❌ 读不到（非世界实体） |
| **Comet（流星）** | **【查证】**`Comet` 类型存在；本档建筑里只有 `CometDetector×16`（探测器）。**流星是运行时对象**，数量读不到 | ❌ 读不到 |
| **施工中（蓝图）** | **【实测】0 条**（本次采样时 `underConstruction=0`）；接口已实测可用（此前 0.86.0 验过 `underConstruction=true`） | ✅ `/v1/buildings` |
| **拆到一半（deconstruct 标记）** | **【实测 部分】**：`/v1/marks?x=139&y=210` 能给出 `deconstruct.marked=false` + `objectName:"WireRefined"`；**但这是"单格查询"**，没有"全档哪些建筑被标了拆除"的清单 | ⚠️ 单格可查，**无全局清单** |
| **戴手套/储物里的复制人（StoredMinion）** | **【查证】**`Components.StoredMinionIdentities` 存在 | ❌ 没读 |
| **Sick/Wound（疾病/伤势）** | **【未验证】** | ❌ 没读（`/v1/state` 有部分属性但没疾病维度） |
| **Chore（任务）** | 不是实体，是行为 | ⚠️ 只能从 `dupes[].choreId/choreText` 间接看到"这人在干什么" |
| **温度场 / overlay 数据** | **【实测 部分】**`/v1/env` 给气体格的 avg/min/max 温度；**逐格温度**没有端点 | ⚠️ 聚合可读、逐格不可读 |

## 二、`ObjectLayer` 全 45 层（**这是游戏自己的分层依据，最权威**）

**【查证】** 取自 `Assembly-CSharp` 的 `ObjectLayer` 枚举，顺序即枚举顺序：

| # | 层 | 装什么（按名字 + 本项目经验） | 我们的可读性 |
|---|---|---|---|
| 0 | `Minion` | 复制人 | ✅ `/v1/state.dupes` |
| 1 | `Building` | 一般建筑 | ✅ `/v1/buildings` |
| 2 | `Backwall` | 背景墙 | ✅（建筑） |
| 3 | `Pickupables` | **地上散落物** | ⚠️ 只能按 tag 聚合 |
| 4 | `Canvases` | 画布/艺术品建筑 | ✅（建筑） |
| 5 | `Plants` | **植物** | ❌ **读不到** |
| 6 | `FillPlacer` | 填充工具占位（UI 工具） | 不需要 |
| 7 | `DigPlacer` | **挖掘标记** | ✅ `/v1/marks.dig` |
| 8 | `MopPlacer` | **擦拭标记** | ⚠️ 单格可读（clear 段） |
| 9 | `FoundationTile` | 地基砖 | ✅（建筑） |
| 10 | `PlasticTile` | 塑料砖 | ✅ |
| 11 | `ReplacementTile` | 待替换砖（建造中） | ✅（`underConstruction` 侧） |
| 12 | `GasConduit` | 气体管道 | ✅ |
| 13 | `GasConduitTile` | 管道穿墙件 | ✅ |
| 14 | `ReplacementGasConduit` | 待替换气管 | ⚠️ |
| 15 | `GasConduitConnection` | 管道接口 | ✅（建筑） |
| 16 | `LiquidConduit` | 液体管道 | ✅ |
| 17 | `LiquidConduitTile` | 液管穿墙件 | ✅ |
| 18 | `ReplacementLiquidConduit` | 待替换液管 | ⚠️ |
| 19 | `LiquidConduitConnection` | 液管接口 | ✅ |
| 20 | `SolidConduit` | 固体运输管 | ✅ |
| 21 | `SolidConduitTile` | 固管穿墙件 | ✅ |
| 22 | `ReplacementSolidConduit` | 待替换固管 | ⚠️ |
| 23 | `SolidConduitConnection` | 固管接口 | ✅ |
| 24 | `LadderTile` | 梯子砖 | ✅（建筑） |
| 25 | `ReplacementLadder` | 待替换梯 | ⚠️ |
| 26 | `Wire` | 电线 | ✅ |
| 27 | `WireTile` | 穿墙电线 | ✅ |
| 28 | `ReplacementWire` | 待替换电线 | ⚠️ |
| 29 | `WireConnectors` | 接线柱 | ✅ |
| 30 | `LogicGate` | 逻辑门 | ✅（建筑） |
| 31 | `LogicWire` | 逻辑线 | ✅ |
| 32 | `LogicWireTile` | 逻辑线穿墙 | ✅ |
| 33 | `ReplacementLogicWire` | 待替换逻辑线 | ⚠️ |
| 34 | `TravelTube` | 运输管道 | ✅ |
| 35 | `TravelTubeTile` | 管道穿墙 | ✅ |
| 36 | `ReplacementTravelTube` | 待替换 | ⚠️ |
| 37 | `TravelTubeConnection` | 管道接口 | ✅ |
| 38 | `AttachableBuilding` | 附着建筑（如部分挂件） | ✅（建筑） |
| 39 | `Gantry` | 发射架 | ✅ |
| 40 | `Mover` | **移动中的对象**（复制人/动物搬运时短暂出现） | ❌ |
| 41 | `Rover` | 漫游车 | ❌ 读不到 |
| 42 | `Critter` | **小动物** | ❌ **读不到** |
| 43 | `ReplacementBackwall` | 待替换背景墙 | ⚠️ |
| 44 | `MovePlacer` | 移动工具占位（UI） | 不需要 |
| — | `NumLayers = 45` | 层数上限 | — |

> ✅ 说明：`/v1/marks` 的 `scan{okLayers:45,errLayers:0}` 证明**我们确实逐层扫过全部 45 层**
> （那 45 就是这里的 45；`errLayers:0` 说明没有一层读失败）。

## 三、重点四问的答案

1. **小动物（Creature）现在完全读不到 —— 存档里有多少只？**
   **答：本档实测不出来。** 【查证】游戏里 `CreatureBrain` / `CreatureCalorieMonitor` 类型存在，
   生物的"脑"在 `Components.Brains`（`Cmps<Brain>`，public 静态字段）；**但没有端点枚举它**。
   ⇒ 结论：**是能力缺口，不是"没有动物"**。要回答这个问题，需要新端点（见第五节）。

2. **地上散落物（Pickupable）能不能按区域/按类型读？**
   **答：按"元素 tag"能读，按"区域"和"按 prefab"读不到。**
   【实测】`/v1/inventory?tag=Dirt` ⇒ `onGround=322,441kg, groundStacks=788`；
   `tag=Algae` ⇒ `199,279kg / 40 堆`；`tag=SandStone` ⇒ `1,548,666kg / 150 堆`。
   **但**：`groundStacks` 是**按 tag 的堆数**，不是按位置的清单；也不能一次拿到"所有 tag"的分布（`/v1/materials?minKg=1` 已有全量聚合，见下）。
   ⇒ **"去哪捡"这个问题现在答不了**（没有 `Pickupable` 的坐标清单）。

3. **蛋、种子、文物、飞船模块：是独立类，还是都算 Building/Pickupable？**
   * **蛋**：【查证】独立 —— `EggConfig`、`EggShellConfig`、`IncubationMonitor` 存在；蛋是 `Pickupable`（未验证）。
   * **种子**：【查证】独立类型 `PlantableSeed` + `Components.PlantableSeeds`（public）⇒ 独立类，**但通常是 Pickupable 的子集**（未验证）。
   * **文物**：【查证】`ArtifactConfig` + `SpaceArtifact` + `Components.SpaceArtifacts` ⇒ 独立组件，**挂在 Pickupable 上**（未验证）。
   * **飞船模块**：【实测】**就是 Building**：`SolarPanelModule×9`、`BatteryModule×7`、`HabitatModuleMedium×7`、`NoseconeBasic×5` 都在 `/v1/buildings` 里，另外还有 `RocketModuleCluster`/`PassengerRocketModule` 类型。
   ⇒ **结论**：模块=Building（已覆盖）；蛋/种子/文物=**挂在 Pickupable 上的独立组件**（现在只按元素聚合，**看不到它们是什么**）。

4. **施工中的建筑能读了，那"拆到一半"算不算另一种状态？**
   **答：是另一种状态，而且我们只有"单格视角"。**
   【实测】`/v1/buildings` 的 `underConstruction` 只表示"在建"；
   **被标 `deconstruct` 的建筑仍是正常 Building**，不会出现在 `underConstruction` 里。
   `/v1/marks?x&y` 能查单格的 `deconstruct.marked` + `objectName`，**但没有"全档被标拆除的建筑清单"**。
   ⇒ 想让 AI 知道"哪些建筑正在被拆"，需要新端点或给 `/v1/buildings` 加一个 `markedForDeconstruct` 字段。

5. **有没有"既不是格子物质也不是对象"的东西？**
   **有，而且不少**：
   * **温度场/压力场**本身就是格子属性（`Grid.Temperature/Mass`）—— 我们只有聚合（`/v1/env`），**逐格没有**；
   * **Overlay 数据**（如"氧气 overlay"颜色）不是独立实体，是渲染；
   * **星际目标**（`SpaceDestination`）：不在任何世界里，**没有任何格子坐标**；
   * **流星 `Comet`**：运行时临时对象，有坐标但生命周期短；
   * **Chore/研究/科技**：不是世界实体（`/v1/research` 单独覆盖了科技）。

## 四、本档实测到的"接口读不到"的清单（按种类）

| 读不到的种类 | 为什么读不到 |
|---|---|
| Creature / Rover | 没有枚举 `Components.Brains` 的端点；`Grid` 层扫也没做全图 |
| 植物（`Plants` 层） | 植物不是 Building；`/v1/marks.harvest` 只覆盖单格 |
| Egg / Seed / Artifact（作为"东西"的身份） | 只按元素 tag 聚合，**丢失 prefab 身份** |
| Pickupable 的坐标/区域分布 | `/v1/inventory` 是全局聚合，`groundStacks` 只有数量 |
| 全档"被标拆除"的建筑清单 | `/v1/marks` 是单格查询 |
| 逐格温度/压力 | `/v1/env` 只给聚合（avg/min/max） |
| StoredMinion（储物/火箭里的复制人） | 未做 |
| 疾病/伤势 | 未做 |

## 五、若要补齐：建议的诊断端点（**未实施，等你点头**）

> ⚠️ 按你的要求：**我没有改 `src/`**。下面是我建议的最小改动，**只加一个只读端点**，不改任何现有行为。

**建议 1（优先级最高）：`GET /v1/_debug/layers` —— 每层对象数 + 关键层的 prefab 分布**
* 数据来源（**都是 public 静态集合，成员表已核**）：
  `Components.Pickupables`（`Cmps<Pickupable>`）、`Components.Brains`（`Cmps<Brain>`）、
  `Components.Crops`、`Components.PlantableSeeds`、`Components.SpaceArtifacts`、
  `Components.Harvestables`、`Components.Geysers`（`CmpsByWorld<Geyser>`）、`Components.StoredMinionIdentities`；
* 输出：`{"layers":[{"layer":"Pickupables","count":…}], "byPrefab":{"Dirt":…}}`；
* **成本估计**：只遍历这几个集合（不是 25 万格 × 45 层），应该是**毫秒级**；
* 用途：一次性回答"本档有多少只动物/多少堆东西/多少植物/多少文物"。
* **改动面**：新建 `src/OniAiBridge/Diagnostics.cs` + `Bridge.cs` 加一条路由（约 40 行）。

**建议 2：`/v1/buildings` 增加 `markedForDeconstruct` 字段**
* 每个建筑读 `Deconstructable`（若存在）判断是否已被标记拆除；
* 只加字段、不改语义；成本可忽略（已在遍历建筑）。

**建议 3：`GET /v1/pickups?x0&y0&x1&y1&limit=`（按区域列散落物）**
* 数据来源：`Components.Pickupables` + `Grid.PosToCell`，按 bbox 过滤，输出 `prefabId/element/mass/x/y`；
* 这是"去哪捡材料"的唯一可行解；
* 成本：遍历全部 Pickupable（本档 `Dirt` 就有 788 堆 ⇒ 全档可能数千），按 bbox 过滤后输出，**需分页**。

## 六、如果只能补 3 个（按对 AI 决策的价值排序）

1. **`/v1/_debug/layers`（实体总账）** —— 现在 AI **根本不知道有没有动物/多少植物/多少文物**，
   而这些直接决定"要不要派人抓动物""要不要建牧场""种子够不够"。**没有它，AI 对世界的认知是残缺的。**
2. **`/v1/buildings` 的 `markedForDeconstruct`** —— 让 AI 知道"我正在拆的东西拆完没有"，
   避免重复下令（我们已经在 `deconstruct` 的 `409` 里踩过一次"同一格反复标"的坑）。
3. **`/v1/pickups?bbox`（散落物按区域）** —— 材料不在箱子里而在**地上**（本档 `SandStone` 地上 1,548 吨、`Dirt` 322 吨），
   AI 要"就近取材/派人搬"就必须知道**东西在哪**；现在只有"有多少"。

> 若只做一件：**第 1 件**。它把"世界有哪些东西"从"读不到"变成"可枚举"，收益最大、风险最低（纯读、只遍历几个集合）。

## 七、附：本次调研用到的原始数据（可复核）

```
/v1/buildings  总计 72,851 条 / 309 种 prefab（37 页 × 2000，耗时 ≈41s）
  最大宗：SolidConduit 10,099 / InsulatedLiquidConduit 9,194 / InsulationTile 5,753 / InsulatedGasConduit 5,657
          LogicWire 4,981 / WireRefinedHighWattage 4,737 / WireRefined 3,856 / ExteriorWall 3,424
  特殊：RocketWallTile 238 / SolarPanelModule 9 / HabitatModuleMedium 7 / NoseconeBasic 5 / MonumentTop 16
        ItemPedestal 32 / ArtifactAnalysisStation 1 / EggIncubator 2 / PlanterBox 6 / HydroponicFarm 131
/v1/geysers     count=41, total=41, filteredTemplates=0, scanMode=FindObjectsOfType<Geyser>(false)
/v1/state       dupes=24, elements=173, cycle=6990, world=0 (240×380, VanillaSandstoneDefault)
/v1/inventory   SandStone onGround 1,548,666kg/150堆；Dirt 322,441kg/788堆；Algae 199,279kg/40堆；
                Cuprite 6,545kg/2堆；Water 692kg/32堆
/v1/env?around=dupes&r=5   gasCells=3812, solidPct=57.372%, breathablePct=4.763%
/v1/marks?x=139&y=210      objectsAtCell=10, scan{okLayers:45,errLayers:0}, deconstruct.objectName=WireRefined
```

> **一句话结论**：**Building / Geyser / Minion / 元素物质 / 建造状态** 已经覆盖得不错；
> **Creature / 植物 / Pickupable 的身份与位置 / Egg·Seed·Artifact 身份 / 拆除进度** 是五块真空，
> 其中 **Creature 与植物** 连"有多少"都答不出来 —— 这是最该先补的。
