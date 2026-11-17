$ErrorActionPreference = 'Stop'
Set-Location 'H:\deepseek-harness\workspace\oni-mod'
$add = @'

### 3.37.11 `mop` 的**真正判据**：不是"液体格"，而是"**小水渍 + 在地面上 + ≤ 150kg**"（M6.80，0.92.4）

#### 结论（先回答三个问题）
1. **`(86,180)` 那格没有 `Moppable`** —— 也不需要：**`Moppable` 是"标记之后才出现的"，不是前置条件**。
2. **`Moppable` 是 `MopPlacer` 预制体自带的**：`MopPlacerConfig.CreatePrefab()` 的 IL 调
   `CommonPlacerConfig.CreatePrefab` → 给预制体加 `Moppable`（写 `amountMoppedPerTick = 20f`）+
   `Workable`（`synchronizeAnims`）。而这个预制体正是 `MopTool.OnDragTool` 里 `KInstantiate(MopTool.Placer)` 造出来的。
   ⇒ **`Moppable` = 拖地的"结果"，不是"可拖的判据"**。所以"找 `Moppable` 再调它的 Mark"这条路**不存在**。
3. **你那批液体格里，`(86,180) 486kg` 天然不可拖**（超阈值），而 **`(85,181) 100.015kg` 和 `(86,182) 96.493kg` 是可拖的**（前提：下方一格是固体）。

#### 游戏真正的可拖判据（`MopTool.OnDragTool` 的 IL，逐条）
| # | 条件 | 不满足时游戏的反应 |
|---|---|---|
| ① | 该格**不是固体**（`Grid.Solid[cell] == false`） | 静默跳过 |
| ② | `Grid.Element[cell].IsLiquid` | 静默跳过 |
| ③ | **下方一格是固体**（`Grid.Solid[Grid.CellBelow(cell)]`）—— 必须在"地面"上 | 弹 `MOP.NOT_ON_FLOOR` |
| ④ | **`Grid.Mass[cell] <= MopTool.maxMopAmt`** | 弹 `MOP.TOO_MUCH_LIQUID` |
| ⑤ | 通过后：`KInstantiate(MopTool.Placer)` → 写 `Grid.Objects[cell, ObjectLayer.MopPlacer]` → 定位 → `SetMasterPriority` | — |

**`MopTool.maxMopAmt = 150.0f`**（`..cctor` 的 IL 常量 `0x43160000` = 150.0）
⇒ **486kg 超了 3 倍多**，所以那格永远不可拖；100kg / 96kg 两格才是合法的拖地目标。

#### 0.92.4 的改动
* `mop` 解析从"是液体"改为 **②③④ 三条全过**（①由格子有效性覆盖），并统计被挡的格数：
  ```json
  "mopGating":{"maxMopAmt":150,"liquidCells":3,"skippedTooMuch":1,"skippedNotOnFloor":0}
  ```
  `liquidCells` = 扫描到的液体格数；`skippedTooMuch` = 超 150kg 被挡；`skippedNotOnFloor` = 下方不是固体被挡；
  剩下的就是 `targets`（`list[]` 里带 `liquidMassKg` / `onFloor=1` / `maxMopAmt`）。
* **`Moppable` 不作为判据**（原因见上）；`marked` 仍以 **Mop chore** 回读为准。
* **验证环境**（照小僵的建议记在这里，不当代码逻辑）：**mop 的正确验证环境是"小水渍"（几 kg，且在地面上），不是水池。**
  当前这批液体格：`(86,180) 486kg` ✗、`(85,181) 100.015kg` ✓、`(86,182) 96.493kg` ✓ —— 后两格才该拿来试。
  小僵提的"拆厕所造尿渍"是**造环境**的办法，我们不需要真去做，先试那两格即可。
'@
$api = Get-Content 'docs\API.md' -Raw
[System.IO.File]::WriteAllText((Join-Path (Get-Location) 'docs\API.md'), ($api.TrimEnd() + "`r`n" + $add), (New-Object System.Text.UTF8Encoding($false)))
"API.md 行数: " + (Get-Content 'docs\API.md').Count

$entry = @'
## [修复] 0.92.4 / M6.80 —— `mop` 判据改为游戏口径（小水渍 + 在地面 + ≤150kg）（2026-11-16）

**实机结论**（lead 实测，游戏真正跑起来 0/30/90 秒）：真做拖地 `(86,180)` 后水一点没少、无 Mop chore、无 placer
⇒ **不是回读判据问题，是解析判据选错了对象**。

**核实结果（元数据 + IL）**
1. `(86,180)` 上**没有** `Moppable`，也不需要 —— **`Moppable` 是"标记之后才出现的"**：
   `MopPlacerConfig.CreatePrefab()` 的 IL 给预制体加 `Moppable`（`amountMoppedPerTick = 20f`）+ `Workable`，
   而该预制体由 `MopTool.OnDragTool` 的 `KInstantiate(MopTool.Placer)` 产生。
   ⇒ "找 `Moppable` 再调它的 Mark"这条路**不存在**。
2. 游戏真正的可拖判据（`MopTool.OnDragTool` IL）：非固体 → `Element.IsLiquid` → **下方一格是固体** →
   **`Mass <= MopTool.maxMopAmt`**，否则弹 `MOP.NOT_ON_FLOOR` / `MOP.TOO_MUCH_LIQUID`。
   **`MopTool.maxMopAmt = 150.0f`**（cctor 常量 `0x43160000`）。
3. ⇒ 那池 **486kg 天然不可拖**（超 3 倍）；`(85,181) 100.015kg`、`(86,182) 96.493kg` 才是合法目标。

**0.92.4 改动**：`mop` 解析按上述三条过滤（不再用"是液体"），新增响应块
`"mopGating":{"maxMopAmt":150,"liquidCells":N,"skippedTooMuch":N,"skippedNotOnFloor":N}`；
`marked` 仍以 **Mop chore** 回读为准；`Moppable` 不作判据。

**文档也写入了验证环境**：mop 的正确验证环境是**小水渍（几 kg，在地面上）**，不是水池。

**未实机验证**：0.92.4 未装机 ⇒ `(85,181)` / `(86,182)` 上是否真能标上、`mopGating` 数字是否与预期一致，等实测。

'@
$c = Get-Content 'CHANGELOG.md' -Raw
[System.IO.File]::WriteAllText((Join-Path (Get-Location) 'CHANGELOG.md'), ($entry + $c), (New-Object System.Text.UTF8Encoding($false)))
"CHANGELOG 行数: " + (Get-Content 'CHANGELOG.md').Count
