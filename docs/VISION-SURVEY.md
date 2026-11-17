# 视野（可见性）调研 —— M5.3（只调研，不改代码）

> 结论先行：**ONI 里没有"小人视野"这回事**，也没有"每格已探索"的位图。游戏里有的是**两套完全不同的东西**：
> ① **摄像机视口**（`Grid.Visible` + `GridVisibleArea`，每帧跟着镜头变）；
> ② **迷雾/揭示等级**（按星球/世界整体，`ClusterRevealLevel`，一次性揭示、永久保留）。
> 用户设想的「已探索 + 小人视野」在游戏里对应的是「**世界揭示等级**」+「**当前镜头视口**」，**第二条会随镜头抖动**。
> 下面每条都标了证据来源；没实测的标 **未确认**。

---

## 1. 四套"可见性"数据的语义对照

| 名字 | 是不是存在 | 类型 | 语义 | 谁在写 | 证据 |
|---|---|---|---|---|---|
| `Grid.FogOfWar` | **不存在** | — | — | — | `Grid` 成员逐个查过：没有 `FogOfWar` / `Revealed` 字段 |
| `Grid.Revealed` | **不存在** | — | — | — | 同上 |
| `Grid.Visible` | **存在** | `public static byte[]` | 每格"是否在**摄像机视口**内"（0/1 语义，`>0` 即算） | `GridVisibleArea`（由 `CameraController.Update` 驱动） | `CameraController.Update` IL：`ldsfld VisibleArea; call GridVisibleArea::Update` |
| `Grid.IsVisible(int)` | **存在（公共 API）** | `static bool` | 见下方 IL：`Visible[cell] > 0` **或** "迷雾系统整体关闭" | 只读 | IL 反编译（下一节） |
| `Grid.PreventFogOfWarReveal` | 存在 | `VisFlagsPreventFogOfWarRevealIndexer` | 标记"这些格子不该被迷雾揭示"（反向：被排除） | `PreventFOWRevealTracker` | `Grid` 成员 + 类型表 |
| `ClusterRevealLevel` | 存在 | `enum { Hidden, Peeked, Visible }` | **按星球/世界**的揭示等级（这才是"探索过没有"） | `ClusterFogOfWarManager`（状态机，**具体写入点未确认**） | 枚举成员 + `ClusterFogOfWarManager.InitializeStates` |
| `PropertyTextures.FogOfWarScale` / `IsFogOfWarEnabled` | 存在 | `float` / `bool` | **`IsFogOfWarEnabled` = `FogOfWarScale < 1.0f`**（`0xFE04` 是 **`clt`** 不是 `ceq` —— 我第一版读错了，实测数据纠正：`FogOfWarScale=0.0` 时 `IsFogOfWarEnabled=true`） | `PropertyTextures.OnSpawn` 按 `disableFogOfWar` 写入、`LateUpdate` 喂 shader | IL：`7E <FogOfWarScale>; 22 3F800000; FE 04` = `ldsfld; ldc.r4 1.0; clt` |

### `Grid.IsVisible` 的 IL（关键）
```
7E <Grid::Visible>   02 91 16 30 09     ← ldsfld Visible; ldarg.0; ldelem.u1; ldc.i4.0; bgt.s → 返回 true
28 <PropertyTextures::get_IsFogOfWarEnabled> 16 FE 01 2A   ← 否则：!IsFogOfWarEnabled
17 2A                                                      ← true 分支：ldc.i4.1; ret
```
⇒ **判据**：`Visible[cell] > 0` ⇒ **可见**；否则 **迷雾关闭时也算可见**（`return !IsFogOfWarEnabled`）。
即 `Grid.IsVisible(cell)` = 「在镜头视口内」**或**「本局关了迷雾」。

---

## 2. "人类玩家看得到的区域"到底是哪一个

**是两个独立概念，不是一回事：**

| 人类语义 | 游戏机制 | 稳定性 |
|---|---|---|
| "这块地我去过/探过"（知道那里有什么） | **世界揭示等级** `ClusterRevealLevel`（Hidden/Peeked/Visible），整颗星球一次性揭示 | **稳定**（永久） |
| "我现在屏幕上看得到" | **摄像机视口** `GridVisibleArea` → `Grid.Visible` | **每帧随镜头变** ⇒ 会抖 |

**没有"小人视野半径"这回事**：`Grid` 上没有 per-duplicant 的可见性数组；光照是另一套
（`Grid.LightIntensity` / `Grid.LightCount` / `ExposedToSunlight`），跟"能不能看见"不是同一个东西
（灯照亮的黑暗处，玩家照样看得见，只是暗）。

**因此**：如果按 `Grid.IsVisible` 过滤"人类能看到什么"，得到的是**玩家此刻镜头里的东西** ——
玩家把镜头挪开，那格立刻变"看不见"。**这不是我们要的"人类视角"。**

---

## 3. `revealFog` 作弊到底改了什么

已核实：
- `DebugHandler.RevealFogOfWar : bool`（`public static`），在 `DebugHandler.OnKeyDown` 里被**读 + 写**（`+52D` 读、`+535` 写）⇒ 这是**一个调试热键切换的全局布尔**。
- 迷雾"整体开关"的判据是 `PropertyTextures.FogOfWarScale == 1.0f`（`IsFogOfWarEnabled`）。

**未确认**：`RevealFogOfWar == true` 之后**具体谁把它变成"看不见迷雾"**。我查过 `FogOfWarPostFX`、
`ClusterFogOfWarManager`、`GridVisibleArea`、`VisibleCells` 的 IL，**都没有引用** `RevealFogOfWar`。
⇒ 两种可能（都未确认）：
1. 它通过 `PropertyTextures.FogOfWarScale` 或某个 shader 参数生效（渲染层"看不见"，但数据不变）；
2. 它在别处（未扫到的类型）被消费。
**这条决定"普通模式用哪套数据"，必须实测确认**（见 §5 的验证办法）。

---

## 4. 推荐的"普通模式可见性判定"（分成两层，别混）

**层 A：已探索（稳定，可用于 `/v1/state` 过滤）—— 推荐**
```csharp
// 伪代码：以"世界"为单位判断，而不是逐格
bool worldKnown = ClusterRevealLevel 属于 { Peeked, Visible };   // 具体取值方式：未确认（见下）
```
* `ClusterRevealLevel` 枚举已核实；**"某颗星球当前等级怎么取"未确认**（候选：`ClusterFogOfWarManager` 的
  状态机内部字典、或 `WorldContainer`/`ClusterGrid` 上的字段）。实现前必须把这条查清或实测。

**层 B：当前视口（不稳定，不建议做过滤条件）**
```csharp
bool onScreen = Grid.IsVisible(cell);   // = Visible[cell] > 0 || !IsFogOfWarEnabled
```
* **公共 API、零反射风险**，但它跟着镜头走 ⇒ 只适合"临时查询"，不适合当"人类能看见什么"的判据。

**作弊模式（全图）**：`DebugHandler.RevealFogOfWar = true`（**公共字段**）或直接绕过过滤。
⚠️ 改这个 field 是**写游戏状态**，且是调试通道 —— 属于作弊层，须受 `allowCheats + (sandbox|debug)` 闸门。

---

## 5. 未确认项（不编）

1. **`ClusterRevealLevel` 的读取入口**：枚举存在，但"给定一颗星球/一个格子，现在是什么等级"的
   公共 API **没找到**；`ClusterFogOfWarManager` 只暴露 `InitializeStates`。
2. **`RevealFogOfWar` 的消费点**：扫了 4 个候选类型都没引用（见 §3）。
3. **`Grid.Visible` 的写入点**：`GridVisibleArea`/`VisibleAreaUpdater` 都没直接出现 `Grid::Visible` 的
   写指令；推测由 `GridVisibleArea` 的批量 loop 写（`GridArea` 结构），**未逐指令确认**。
4. **`PreventFogOfWarReveal` 的实际用途**：类型与索引器都在，但"哪些格子会被标记"未确认。
5. **`Peeked` 与 `Visible` 的区别**（望远镜/雷达"看到轮廓" vs 真正揭示）未确认。

### 最省事的实测办法（下一步建议，不动代码也能做）
在游戏里跑一次"**摄像头对比实验**"，用现有 `/v1/state` 的格子数据 + `Grid.IsVisible` 打点：
- 玩家镜头停在殖民地中心 → 记录 `Visible==1` 的格子范围；
- 把镜头拖到未探索区域（迷雾黑屏）→ 再记录；
- 若第二次那些格子 `Visible==1` 但屏幕上是黑的 ⇒ `Grid.Visible` **就是视口**（本文结论）；
- 再按调试键开 `RevealFogOfWar` → 看 `Visible` 是否变化 ⇒ 直接确定 §3。

---

## 6. 风险（如果普通模式按可见性过滤）

| 风险 | 说明 | 缓解 |
|---|---|---|
| **镜头抖动** | `Grid.Visible` 随镜头变 ⇒ 同一个格子前后两次请求结果不同，AI 会以为"东西消失了" | **不要**用它做 `/v1/state` 的过滤；只在显式查询"当前屏幕内"时用 |
| **新建建筑未照亮** | 刚建/刚挖的格子在视口外 ⇒ 被过滤掉 ⇒ AI 看不到自己刚下的指令结果 | 过滤要按"世界已揭示"而不是"视口"；或对"自己刚操作过的格子"豁免 |
| **误伤已知区域** | `ClusterRevealLevel` 是**按星球**的，一颗星球"已揭示"后整颗都可读 ⇒ 过滤粒度太粗（可能泄露该星球全部地形） | 若要细粒度，需要 per-cell 迷雾 mask（**未确认有没有**：`FogOfWarMask` 是实体上的组件，不是格子位图） |
| **主菜单/未进殖民地** | `PlanScreen`/`ClusterFogOfWarManager` 可能为 null（M5.2 已踩过） | 一律"拿不到 ⇒ 降级字段 + 原因"，别当成功 |
| **作弊层写状态** | `RevealFogOfWar = true` 是改游戏状态 | 必须受闸门；且退出作弊后是否自动恢复**未确认** |

---

## 7. 一句话给实现层的结论

**"人类看到什么"在 ONI 里没有单一答案**：
- 想要**稳定**的过滤 ⇒ 只能用「世界揭示等级」（粒度粗，读取入口未确认）；
- 想要**精细** ⇒ 只有 `Grid.IsVisible`，但它是**镜头视口**，会抖，不适合当状态过滤；
- **"小人视野"不存在**（无 per-dupe 可见性数组），所以用户这个提法需要先和用户对齐：
  他说的"小人当前视野"，实际想要的大概是**「只返回屏幕上/已探索区域里的信息」**还是**「按小人位置半径」**——
  这两者实现完全不同，**必须先问清**。
---

## 8. 补充（M5.3b 探"层 A 读取入口"的结果）

### 8.1 入口找到了，**全是公共 API，零反射**
| 入口 | 签名 | 粒度 |
|---|---|---|
| `ClusterGrid.Instance` | `public static ClusterGrid`（字段） | 单例 |
| `ClusterGrid.GetCellRevealLevel` | `public ClusterRevealLevel (AxialI)` | **每颗星球** |
| `ClusterGrid.IsCellVisible` | `public bool (AxialI)` | 每颗星球 |
| `ClusterGrid.GetRevealCompleteFraction` | `public float (AxialI)` | 每颗星球（揭示进度） |
| `ClusterMapScreen.GetRevealLevel` | `public static ClusterRevealLevel (ClusterGridEntity)` | 每个星图实体 |
| `ClusterGridEntity.Location` | `public AxialI`（字段） | 实体的星图坐标 |
| `ClusterGridEntity.IsVisibleInFOW` | `public ClusterRevealLevel`（属性） | 每个实体 |
| `WorldContainer.IsSurfaceRevealed` / `RevealSurface()` | `public bool` / `void` | 每个世界 |

### 8.2 但粒度是"每颗星球"，**不是每格**
`AxialI` 是**星际地图的六边形坐标**（一个星球一个格子），不是世界内的 x,y。
⇒ `GetCellRevealLevel` 回答的是「**这颗小行星在星图上被揭示到什么程度**」，不是「世界内这个格子去过没有」。

### 8.3 决定性事实：**游戏里没有"逐格已探索"的位图**
- `Grid` 上只有 `Visible`（摄像机视口，见 §1）；
- 逐格迷雾只存在于**沙盒工具**：`SandboxFOWTool`（`recentlyAffectedCells`、画/擦迷雾）；
- 存档层（`SaveLoader` / `SaveGame`）里搜不到 Fog/Reveal 的逐格数据。

⇒ 用户说的「只能看你去过的地方」，**在游戏数据模型里没有逐格对应物**。
`aiRevealFog=false` 若按层 A 实现，实际效果只是「**未揭示的星球不给信息**」，
而 `/v1/state` 只报当前殖民地世界（必然已揭示）⇒ **对 state 几乎不产生任何过滤效果**。

### 8.4 两条可选路线（需拍板）
| 路线 | 做法 | 成本 | 效果 |
|---|---|---|---|
| **A. 按星球（粗粒度）** | 用 `ClusterGrid.Instance.GetCellRevealLevel(entity.Location)` 过滤星图/其它星球信息 | 低（公共 API，~10 行） | 对 `/v1/state` **几乎无效果**；对"星图/其它星球查询"有效 |
| **B. 自建"去过的地方"位图** | 自己维护 `HashSet<int>` / `bool[Grid.CellCount]`：按复制人位置（半径 R）或 `Grid.Visible` 采样累积；只存内存（或落盘一份） | 中（需定：采样频率、半径 R、是否持久化、是否含探照/望远镜揭示区） | **真正实现"去过的地方"**；但**从 mod 启动那刻起才开始记录**，之前的历史区域不知道 |

**注意**：B 里若用 `Grid.Visible` 当采样源，等于把"玩家镜头扫过"当"去过"（会随镜头抖动，且玩家不看的区域永远不算去过）；
若用"复制人位置 + 半径"，则更像"小人探索过"，但**墙体/遮挡不判定**（游戏没有现成的视线遍历可复用，`TestLineOfSightFixedBlockingVisible` 是公开方法但语义未确认）。

---

## 9. 【重要更正】实测推翻了 §1/§2 的结论：`Grid.Visible` 是**累积的"曾经可见"位图**

### 9.1 实测数据（lead 在真实游戏里采的）
```
① 殖民地已加载，第一个小人在 cell=141303 (x=111,y=222)
   gridVisibleRaw=255  isVisible=true  fogOfWarScale=0.0  isFogOfWarEnabled=true
② 镜头用中键拖到很远（小人都不在屏幕上了），同一格再采
   gridVisibleRaw=255  isVisible=true            ← 没变！
③ 对照组：从没去过的格 (x=300,y=100)
   gridVisibleRaw=0    isVisible=false  validCell=true
```
⇒ **`Grid.Visible[cell]` 一旦点亮就保持**：它是「**曾经可见过**」的累积位图，不是"当前视口"。
⇒ 值域是 **0 / 255**（byte 全满），**判据用 `> 0`**，不是 `== 1`。
⇒ 与玩家体验一致：**去过的地方永久亮着**。

### 9.2 因此本文 §1「`Grid.Visible` = 摄像机视口，会随镜头抖」是**错的**
错误的来源：我从 `CameraController.Update → GridVisibleArea.Update` 的调用链推断"视口"；
但 `GridVisibleArea.Update` 只重算区域矩形、**不直接写 `Grid.Visible`**，真正的写入是"只置位、不清零"的累积逻辑（写入点仍未逐指令定位）。
**教训**：调用链能证明"谁参与"，不能证明"写入语义"（只加 vs 先清再写）；这类语义必须实测。

### 9.3 这对实现是好消息
`AIEnableFog=true`（要迷雾）时，判据可以就一行：
```csharp
bool seen = Grid.Visible[cell] > 0;      // 优先用 Grid.IsVisible 时要小心它的 fog 分支
```
**不需要自建位图。**
（`Grid.IsVisible(cell)` = `Visible[cell] > 0 || !IsFogOfWarEnabled` —— 迷雾关闭时它对**所有格**返回 true，
所以"要迷雾"的场景应**直接用 `Grid.Visible[cell] > 0`**，不要用 `IsVisible`。）

### 9.4 仍未确认（下一个采样要回答）
**"去过"的粒度是"走过"还是"看到过"？** —— 即：玩家屏幕上看得见、但小人从未走过的格子，是不是 255？
- 若 **是** ⇒ 等于"玩家看到过"，与人类一致，**完美**；
- 若 **否** ⇒ AI 会比玩家知道得少（玩家明明看见，AI 看不到），需要改判据（例如并上 `CameraController` 当前视口）。

---

## 10. 【终局】写入点找到了：`Grid.Visible` 不是光照，是「复制人视野扫过时的最大可见度」

### 10.1 证据链（IL）
**① 真正的写入者：`GridVisibility`（挂在复制人/生物身上的组件）**
```
GridVisibility.OnCellChange()          // 实体移动时触发（cellChangeHandlerID 注册）
  if (gameObject.HasTag(GameTags.Dead)) return;
  cell = Grid.PosToCell(this);
  if (!Grid.IsValidCell(cell)) return;
  if (Grid.Revealed[cell]) return;                       // ← 已透露过就跳过（省性能）
  Grid.PosToXY(pos, out x, out y);
  GridVisibility.Reveal(x, y, radius, innerRadius);      // ← 划一圈视野
  Grid.Revealed[cell] = true;                            // ← 布尔"已透露"
  FogOfWarMask.ClearMask(cell);                          // ← 清掉这格的迷雾遮罩
```
**② `GridVisibility.Reveal(x,y,radius,innerRadius)` 里生成渐变的来源**
```
t = Mathf.Lerp(1f, 0f, <distance 归一化>);     // innerRadius→radius 之间线性衰减
Grid.Reveal(cell, (byte)(255f * t));           // ← 这就是 11/34/56/…/254 的阶梯
```
**③ `Grid.Reveal(cell, amount)` 的累积语义（只增不减）**
```
Visible[cell] = Math.Max(Visible[cell], amount)   // 0x9C = stelem.i1
if (Grid.PreventFogOfWarReveal[cell]) return;
if (Grid.OnReveal != null) Grid.OnReveal(cell)
```
⇒ 所以「镜头挪开还是 255」不是"记住了镜头"，而是**从没有人把它减回去**（Max 语义）。

### 10.2 三个可用判据（都是公共 API，零反射）
| 判据 | 类型 | 含义 | 推荐度 |
|---|---|---|---|
| `Grid.Revealed[cell]` | `VisFlagsRevealedIndexer` → `bool` | **布尔「这格被小人的视野透露过」** | ★★★ **首选**（语义最干净） |
| `Grid.Visible[cell] > 0` | `byte[]` | 曾经达到过的最大可见度（1..255） | ★★ 等价，值域更细（可用于"看得多清楚"） |
| `Grid.PreventFogOfWarReveal[cell]` | bool 索引器 | 该格**禁止**被透露（如某些特殊区域） | ★ 辅助排除 |

### 10.3 结论：**不需要自建位图**，也不需要 `ClusterRevealLevel`
- 用户要的「只能看你去过的地方」= **`Grid.Revealed[cell]`**，而且它就是**按小人视野**算的
  （`GridVisibility` 在实体移动时触发），跟用户的直觉完全一致。
- 之前的「每颗星球 `ClusterRevealLevel`」是**星图层面**的揭示，跟世界内的探索是两套，别混用。
- `FogOfWarMask.ClearMask(cell)` / `RevealFogOfWarMask(cell)` 是**渲染层**的迷雾遮罩，公共 API 也在。

### 10.4 之前的两次误判（都记下来，避免再犯）
1. **「`Grid.Visible` = 摄像机视口，随镜头抖」** —— 错。调用链（`CameraController → GridVisibleArea`）只能证明"谁参与"，
   不能证明写入语义；真正的写入者是 `GridVisibility`，且是 `Max` 累积。
2. **「`IsFogOfWarEnabled = (FogOfWarScale == 1f)`」** —— 错。`0xFE04` 是 **`clt`**（小于），正确关系是
   **`IsFogOfWarEnabled = FogOfWarScale < 1f`**（实测：`FogOfWarScale=0` 时 `IsFogOfWarEnabled=true`，与 `clt` 一致）。

### 10.5 仍存的小未知（不影响实现）
- `GridVisibility.radius` / `innerRadius` 的**具体数值**（每个生物可配；未逐个查）。
- `PreventFogOfWarReveal` 由谁标记（`PreventFOWRevealTracker` 有 `preventFOWRevealCells` 列表），哪些格会被排除**未确认**。
- 若某格只被"非小人"实体（如野生生物）透过视野，是否也算 `Revealed=true` —— **未确认**（`GridVisibility` 不区分类别）。
---
