# 游戏机制速查（反编译实证，构建 U59-744825）

> 来源：`shared/refs/oni-U59-744825/src/`（反编译参考，见 [REVERSE-ENGINEERING.md](REVERSE-ENGINEERING.md)）。
> 这里只收**我们踩过坑、且以前靠猜**的机制。每条都注明在哪个文件，方便复核。

## 1. 作业范围：`OffsetGroups` 里的偏移表

**文件**：`OffsetGroups.cs`

```csharp
public static CellOffset[] Standard = InitGrid(-2, 2, -3, 3);   // x: -2..2, y: -3..3
```

- **标准作业范围 = 5 格宽 × 7 格高**（以目标格为中心，`|dx| ≤ 2`、`|dy| ≤ 3`）
- 挖掘 `Diggable` 用的不是 `Standard`，而是：

```csharp
SetOffsetTable(OffsetGroups.InvertedStandardTableWithCorners);   // OffsetTable.Mirror(...24 组...)
```

`InvertedStandardTableWithCorners` 是**一组有序的落脚点链**（不是纯矩形）：例如目标上方 3 格，
它给的候选是 `(0,3),(0,2),(0,1)` 这种"从远到近"的链，让走位选择有优先级。
`OffsetTable.Mirror` 负责把 `+x` 的偏移镜像出 `-x`，所以左右对称。

**我们实测值的定位**：之前测出 `|dx| ≤ 2`、`−2 ≤ dy ≤ +3`（5×6），
那是**真范围的子集**（真范围 `−3..+3`）—— 少的那一格是执行条件（脚下/头顶净空、落脚点链能否走通），
不是范围本身。

## 2. `MoveToSafety`：是传感器驱动的 chore，不是"干完活回撤"

**文件**：`SafeCellMonitor.cs` + `MoveToSafetyChore.cs`

```csharp
root.ToggleUrge(Db.Get().Urges.MoveToSafety);
safe.EventTransition(GameHashes.SafeCellDetected, danger, smi => smi.IsAreaUnsafe());
danger.EventTransition(GameHashes.SafeCellLost, safe, smi => !smi.IsAreaUnsafe())
      .ToggleChore(smi => new MoveToSafetyChore(smi.master), safe);
```

- 它是**状态机 + 传感器**（`SafeCellSensor`）驱动的：小人**失去安全落脚点**时进 `danger`，拿到 `MoveToSafety` chore；
- 到达安全格（`SafeCellLost`）才回 `safe`；
- ⇒ 我们之前"干完活就回撤"的解释**是错的**：真实原因是**它脚下的地变不安全了**（挖掉脚下的格子之后必然触发）。

**对 AI 的含义**：竖井越挖越深时，小人会反复被这个 chore 拉走 —— 要**从下往上挖**或先搭梯子/脚手架。

## 3. 取消待办：真正删对象的是 `Cancellable`

**文件**：`Cancellable.cs`

```csharp
protected virtual void OnCancel(object _) { this.DeleteObject(); }
```

- `Cancellable` 订阅 `GameHashes.Cancel(2127324410)`，收到就**删除自己**；
- 蓝图（建造计划）身上有 `Cancellable` ⇒ 我们发 Cancel 事件就能撤掉它；
- **挖掘的 `DigPlacer` 没有 `Cancellable`**（实测：发 Cancel 事件后 `digMarked` 仍为 true）
  ⇒ 必须像玩家那样**销毁 placer 对象**（我们的 `type=cancel` 对 placer 正是这么做的）；
- `Constructable.OnCancel` 只做三件事（关详情窗 / `ClearMaterialNeeds()` / `ClearPendingUproots()`），
  **不负责删除**。

同一批订阅 Cancel 的组件（便于日后查）：`Cancellable`、`Clearable`（清清扫标记）、`Carvable`、
`Deconstructable`、`Demolishable`、`Disinfectable`、`Harvestable`、`Uprootable`、`Movable`、
`ReorderableBuilding`、`EmptyConduitWorkable`。

## 4. 门的控制状态：规范路径是 `ApplyRequestedControlState()`

**文件**：`Door.cs`

```csharp
private void ApplyRequestedControlState()
{
    if (requestedState != controlState)
    {
        controlState = requestedState;
        ApplyControlState();   // → RefreshControlState() + OnOperationalChanged() + Trigger
        Open();
        Close();
    }
}
```

⇒ `/v1/door` 的 `instant` 要**先写 `requestedState`、再调 `ApplyRequestedControlState()`**；
只写 `controlState` + `ApplyControlState()` 会漏掉 `Open()/Close()`（门可能只改了状态、动画/物理没跟上）。

## 5. 挖/拆/清 的标记载体（避免再问一遍）

| 标记 | 载体 | 取消方式 |
|---|---|---|
| 挖 | `DigPlacer`（`ObjectLayer.DigPlacer`）+ `Diggable` 组件 | **销毁 placer**（无 `Cancellable`） |
| 拆 | 建筑上的 `Deconstructable`（`QueueDeconstruction(bool)`） | `GameHashes.Cancel` 事件 |
| 清扫 | 物品上的 `Clearable`（`CancelClearing()`） | `GameHashes.Cancel` 事件 |
| 建造计划 | 建筑的 `Constructable` + `Cancellable` | `GameHashes.Cancel` 事件（由 `Cancellable` 删除） |
