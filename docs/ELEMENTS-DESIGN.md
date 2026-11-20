# 材料性质暴露（Elements / Materials） — 1.0 设计方案

> ## ✅ 实施状态（2026-11-20 更新）
> **已实现并装机（0.97.0 / M7.5）**：
> * `GET /v1/elements`（本次实测：`?tag=` 7–15 ms / 1.2 KB；全量列表 212 条 28.4 KB / 7–40 ms）
> * `GET /v1/build-materials?prefabId=`（Electrolyzer 17 条 16 KB / 6–17 ms）
> * **候选集用的是 UI 同一个方法** `MaterialSelector.GetValidMaterials(类别Tag, false)`——不是自己按
>   `Element.materialCategory` 筛。差多少：Electrolyzer 自筛 13 条 / UI 17 条；Tile 自筛 13 条（**漏 Isoresin**）/ UI 16 条。
> * `unlocked` 用 `DiscoveredResources.Instance.IsDiscovered(tag)`。⚠️ **实测存疑**：本档 `IgneousRock` 地上
>   9406kg（enough=true）却报 `unlocked=false` ⇒ 这个标志更像"已登记为已发现"，**不等于"研究解锁"**，待下轮查清。
> * 实测值与 wiki 交叉核对一致：Cuprite 比热 0.386 / 导热 4.5 / 熔点 1083.85°C / 硬度 25。
>
> 下文的"四件事"保留为决策记录；§5 的字段白名单已按"15 字段 + props 嵌套"落地。

> 写于 2026-11-20 · 对应游戏 `U60-764025-SCRPA` / mod 0.96.1
> **本轮只出方案，未改任何代码。**
> 口径（主人定）：**1.0 只解决"能查到"**——把材料性质变成可查询的数据。
> **不做判断/推荐逻辑**（不写"温度高该选哪个材料"这类东西），那是 1.0 之后的事。

---

## 1. `Element` 上到底有哪些字段

**来源**：游戏本体 `Assembly-CSharp.dll` 元数据（PEReader + MetadataReader 读的字段名与类型，
非记忆、非反编译臆测）。下表是 `Element` 类**全部 42 个业务字段**（另有 3 个 `<name>k__BackingField` 与若干常量）。

### 1.1 热学

| 字段 | 类型 | 单位（游戏内口径） | 说明 |
|---|---|---|---|
| `specificHeatCapacity` | `float32` | DTU/(g·°C) | 比热容 |
| `thermalConductivity` | `float32` | DTU/(m·s·°C) | 导热率 |
| `defaultValues` | `PhysicsData` | 结构体 | 见下：默认温度/质量/压力 |
| `molarMass` | `float32` | g/mol | 摩尔质量 |

`PhysicsData`（`Element.defaultValues` 的类型）只有三个字段：

| 字段 | 类型 | 说明 |
|---|---|---|
| `temperature` | `float32` | 默认温度（**内部单位 K**，wiki 上显示 °C） |
| `mass` | `float32` | 默认质量（kg/格） |
| `pressure` | `float32` | 默认压力（**仅用于初始化**，不参与相变判定，见 §2） |

### 1.2 相变

| 字段 | 类型 | 说明 |
|---|---|---|
| `lowTemp` | `float32` | 低温相变点（K） |
| `lowTempTransitionTarget` | `SimHashes` | 低温转变产物（元素 id） |
| `lowTempTransition` | `Element` | 上面那个产物的 `Element` 引用 |
| `lowTempTransitionIdx` | `uint16` | 内部索引 |
| `lowTempTransitionOreID` | `SimHashes` | 伴生矿 id（如冰→水时夹带） |
| `lowTempTransitionOreMassConversion` | `float32` | 伴生质量换算比 |
| `highTemp` | `float32` | 高温相变点（K） |
| `highTempTransitionTarget` | `SimHashes` | 高温转变产物 |
| `highTempTransition` | `Element` | 产物引用 |
| `highTempTransitionIdx` | `uint16` | 内部索引 |
| `highTempTransitionOreID` | `SimHashes` | 伴生矿 id |
| `highTempTransitionOreMassConversion` | `float32` | 伴生质量换算比 |

### 1.3 压力 / 流动（液体为主）

| 字段 | 类型 | 单位 | 说明 |
|---|---|---|---|
| `maxMass` | `float32` | kg/格 | 每格最大质量（液体的"一格最多装多少"） |
| `maxCompression` | `float32` | — | 压缩上限 |
| `flow` | `float32` | — | 流动系数 |
| `viscosity` | `float32` | — | 粘滞 |
| `minHorizontalFlow` | `float32` | kg | 开始水平流动的最小质量 |
| `minVerticalFlow` | `float32` | kg | 开始垂直流动的最小质量 |
| `solidSurfaceAreaMultiplier` / `liquidSurfaceAreaMultiplier` / `gasSurfaceAreaMultiplier` | `float32` | — | 各状态表面积系数（换热用） |

### 1.4 力学 / 建造

| 字段 | 类型 | 说明 |
|---|---|---|
| `strength` | `float32` | 结构强度（砌墙/砖用的就是它，wiki 上"承压公式"里的 `s`） |
| `hardness` | `uint8` | 硬度（挖掘难度：2/10/25/50/80/150/200…，wiki 与游戏一致） |
| `materialCategory` | `Tag` | 建造类别（`BuildableRaw` / `BuildableMetal` …）——**与 `build` 的 `materials` 参数同一口径** |
| `oreTags` | `Tag[]` | 矿石标签集合 |
| `buildMenuSort` | `int32` | 建造菜单排序权重 |
| `refinedMetalTarget` | `SimHashes` | 精炼产物（原矿→金属） |

### 1.5 其它

| 字段 | 类型 | 说明 |
|---|---|---|
| `id` / `tag` / `idx` | `SimHashes` / `Tag` / `uint16` | 身份三件套（`tag.Name` 就是 `Cuprite` 这种字符串） |
| `state` | `State` | `Solid` / `Liquid` / `Gas` |
| `lightAbsorptionFactor` | `float32` | 吸光率 |
| `radiationAbsorptionFactor` | `float32` | 辐射吸收率 |
| `radiationPer1000Mass` | `float32` | 每 1000kg 的自辐射 |
| `toxicity` | `float32` | 毒性 |
| `sublimateId` / `sublimateRate` / `sublimateEfficiency` / `sublimateProbability` / `sublimateFX` | `SimHashes`/`float32`/… | 升华为气体（如污染土→污染氧） |
| `offGasPercentage` | `float32` | 释放气体比例 |
| `elementComposition` | `ElementComposition[]` | 组成（每条 `<elementID>:string` + `<percentage>:float32`） |
| `attributeModifiers` | `List<AttributeModifier>` | 建造后给的加成（装饰 +10%、过热温度 +15°C 这类就是它） |
| `disabled` / `dlcId` | `bool` / `string` | 是否禁用 / 属于哪个 DLC |
| `substance` | `Substance` | 外观资源（贴图/颜色/音效），**与决策无关**，不建议暴露 |

### 1.6 真实值示例（⚠️ 标着"wiki 值"的尚未运行时核对）

`【wiki 值，U52 口径】` `Cuprite`（铜矿，`BuildableRaw` / 金属矿石）：

| 性质 | 值 |
|---|---|
| 比热容 `specificHeatCapacity` | **0.386** DTU/(g·°C) |
| 导热率 `thermalConductivity` | **4.5** DTU/(m·s·°C) |
| 熔点（`highTemp`，wiki 显示 °C） | **1083.85 °C**（≈ 1357 K） |
| 硬度 `hardness` | **25** |
| 加成 | 装饰 +10% |

对照 `SandStone`（砂岩）：比热容 **0.800**、导热 **2.9**、熔点 **926.85 °C**、硬度 **10**。

> ⚠️ 这些数值来自缺氧中文维基（U52 口径），**我们跑的是 U60**。实施时必须**从运行时读**（`ElementLoader`/`Assets`
> 里的真对象），不能把 wiki 数字写死。本文档引用它们只为说明"字段长什么样、量级多少"。

---

## 2. 主人的问题：**相变温度会不会随气压变化？**

### 结论：**不会。** 相变只由温度决定，气压不参与。

依据三条（前两条是硬证据，第三条是反证）：

1. **元数据层面：`Element` 的相变相关字段里没有任何压力输入。**
   只有 `lowTemp` / `lowTempTransitionTarget` / `highTemp` / `highTempTransitionTarget`（+ 伴生矿参数），
   全部是"温度阈值 + 产物"，没有 `pressure` 参与。唯一的压力字段 `PhysicsData.pressure` 是**默认初始化量**，
   `maxMass` / `maxCompression` 是"每格最多装多少 / 压缩上限"，都不出现在相变判定里。

2. **wiki 层面：相变点是固定温度 + 3K 迟滞。**
   Water 标 99.35 °C，**实际到 102.35 °C 才汽化**；Steam 标 99.35 °C，**实际到 96.35 °C 才凝结**。
   两边各 3K 迟滞，且描述里没有"随压力变化"这一维（[Elements – ONI Wiki](https://oxygennotincluded.fandom.com/wiki/Elements)）。
   中文 wiki 的液体表也只给"凝固点/沸点/比热/导热/标准密度"，同样没有压力维（[液体 – 缺氧中文维基](https://oni-jp.playing.wiki/d/%b1%d5%c2%ce)）。

3. **反证：压力在游戏里影响的是"受压破坏"，不是沸点。**
   液体页给的公式是 `max pressure = MPT * (1 + t * s * k)`（MPT = 该液体每格最大质量，`s` = 墙体材料 `strength`，
   `t` = 墙厚，`k` = 强度系数）——**压力决定砖块会不会裂，不决定水会不会开**。

### 诚实标注

我**没有逐行读 IL**（`ElementLoader` 里相变判定的实现体），所以这是"元数据结构 + wiki 双重一致的强证据"，
不是"源码某行确认"。若要钉死，运行时验证方法是：同一元素、不同气压下加热，看相变温度是否移动
（`/v1/map` 能读到每格的 `element` / `temperature` / `mass`，可做这个实验）。

---

## 3. 数据量

| 项 | 数 | 依据 |
|---|---|---|
| 元素条目 | **≈213** | `SimHashes` 枚举 214 个成员（含 `value__`）；实际元素略少（`Void` 之类不是物质） |
| 单条 JSON（精选 12~15 字段） | **≈400–600 B** | 按 §1 的字段名 + 数字估 |
| 全量（213 条 × 精选） | **≈100–130 KB** | 213 × 500B |
| 全量（213 条 × 全 42 字段） | **≈250 KB** | 字段多一倍、含数组/嵌套 |

**"同一 tag 在不同状态下值不同"**：
ONI 里**同一物质的不同相是不同的 `SimHashes` 条目**——`Water`（液）、`Steam`（气）、`Ice`（固）是**三条独立元素**，
各自有自己的比热/导热/相变点。所以"按元素一条"就够，**不需要再叠一个"状态"维度**；
但文档里要写清这三条是同一族（互相的 `lowTempTransitionTarget` / `highTempTransitionTarget` 指来指去）。

---

## 4. 暴露方式：三选一

### A. 扩 `/v1/materials`
现状（实测）：`/v1/materials` 返回的是**库存**——
`{"tag":"SandStone","kg":9156.41}` 这种，语义是"箱子里有多少"，14 条，还带 `storagesScanned` / `minKg` 过滤。

| 利 | 弊 |
|---|---|
| 不用加端点；「查材料」时库存和性质一起回来 | **语义打架**：这个端点是"库存"，塞进"物理性质"会让 `kg` 和 `thermalConductivity` 挤在一层，调用方要自己分辨 |
| | 库存是**动态**的（跟着殖民地变），性质是**静态**的；混在一个响应里没法缓存 |

### B. 新端点 `/v1/elements?tag=Cuprite` ✅ 推荐
```
GET /v1/elements?tag=Cuprite            → 单条全字段
GET /v1/elements?tags=Cuprite,SandStone → 批量（建东西前对比两种材料）
GET /v1/elements                        → 列表（只有 tag/state/category/比热/导热，轻量）
```
| 利 | 弊 |
|---|---|
| 语义单一（"元素性质"就是元素性质），可缓存（静态数据，跟存档无关） | 多一个端点（34 → 35） |
| 按需查，单条 500B，对 AI 便宜 | 需要定"暴露哪些字段"的白名单（见 §5） |
| 批量参数让"对比两种材料"一次搞定 | |

### C. 一次性静态表（导成 JSON 放 `docs/`）
| 利 | 弊 |
|---|---|
| 零运行时开销；AI 可以离线读；不占端点 | **会腐**：游戏更新就过期（这次 U59→U60 就是例子），而且**不反映 DLC 差异**（`dlcId` 字段） |
| 可以整表 grep | 213 条 × 42 字段 ≈ 250KB，全量塞进上下文很贵 |

### 推荐：**B 为主 + C 作为可选快照**
理由：AI 的用法是**按需查**（建东西前查一两个 tag），不是"全量知识库"。
B 保证"查到的就是当前这局游戏的真值"，C 只能当离线参考且必须标注版本与生成时间。

**不做 A**：会让一个已经在用的端点语义变浑，而且库存与性质的生命周期不同。

---

## 5. 实施建议（下一轮，不含本轮）

### 字段白名单（建议 15 个，约 500B/条）
```
tag, state, name(中文),
specificHeatCapacity, thermalConductivity, molarMass,
lowTemp, highTemp, lowTempTransitionTarget, highTempTransitionTarget,
maxMass, strength, hardness,
materialCategory, oreTags
```
可选 `?all=1` 给全 42 字段（默认不给——AI 读起来贵，和 `/v1/buildings` 默认不回 `name` 一个道理）。

### 单位必须写明
游戏内部用 **K** 和 **DTU**；wiki/玩家习惯 **°C**。响应里两者都给（如 `lowTempK` + `lowTempC`），
或者在 `units` 块里统一声明——**不能像现在某些字段那样让调用方猜**。

### 工作量估计
| 项 | 量 |
|---|---|
| 新文件 `Elements.cs` | ~150–200 行（按 tag 找 `Element` + 组装 JSON + `JsonCheck.Validate`） |
| 路由 | `Bridge.cs` 加 1 个 case（1 行）+ `capabilities.elements`（1 行） |
| 文档 | `docs/API.md` 加 1 节、`AI-GUIDE.md` 端点表加 1 行 |
| 版本 | 三处版本号 +1 |
| **合计** | **1 个新文件 + 3 处小改动**，半天内可完成并自测 |

### 未定项（要主人拍）
1. 端点路径叫 `/v1/elements` 还是 `/v1/materials/props`？
2. 单位：只给 K（内部口径）还是 K + °C 双给？
3. 要不要一并暴露 `attributeModifiers`（装饰/过热加成）？它影响"选材"但属于"决策数据"，与 1.0 口径的边界需要确认。
4. `disabled` / `dlcId`（DLC 专属元素）要不要过滤掉？

---

## 附：怎么复现本文档的元数据

```
node <PEReader 脚本> …   # 见 shared/tmp/oni-dump-types.ps1 / oni-field-types.ps1 的用法
```
（一次性工具，未入库；需要时可重新写。`Element` 的 42 个字段就是用它读出来的。）
