# `type=cancel` —— 撤回待办标记（0.114.0）

## 为什么要有

`dig` / `build` / `deconstruct` 挂上去的标记**在 0.114.0 之前没有任何接口能撤回**：

| 症状 | 实测证据（2026-11-21） |
|---|---|
| 蓝图下错了清不掉 | `deconstruct` 对施工中蓝图返回 `NOT_DECONSTRUCTABLE`；`type` 里也没有 cancel |
| 够不到的标记永久挂着 | `deconstruct` / `dig` 不校验可达性，下上就 `marked=true`，小人取到 chore 立刻放弃，**标记不会消失** |
| 面板没有"撤销"按钮的底层 | 下错一次只能玩家手动清 |

## 接口

```
POST /v1/job
{ "type": "cancel", "x": 121, "y": 208, "confirm": true }
```

**不挂在作弊闸门下** —— 取消是玩家天然可以做的普通操作。

## 实现路径（IL 实证，照抄玩家的取消动作）

`CancelTool.OnDragTool(cell, button)` 的 IL 做三件事：

1. `Grid.Objects[cell, layer]` 取该格对象
2. `FilteredDragTool.GetFilterLayerFromGameObject` + `IsActiveLayer` 判断该 layer 是否在当前过滤器里
3. **`EventExtensions.Trigger(go, <hash>)`** —— 这个 hash 就是 `GameHashes.Cancel = 2127324410`

所以本实现**不调任何"Cancel 方法"**，而是对该格上的对象**发 Cancel 事件**，由各组件自己响应：

- `Constructable` → 撤掉蓝图（监听 Cancel）
- `Deconstructable.CancelDeconstruction()` ← 也由 Cancel 事件驱动
- dig 的 `DigPlacer` → 销毁自身（`Diggable` 挂在 placer 上，不在原生格子上）

这样行为与玩家右键取消一致，不自己发明规则。

## 响应

```json
{ "type": "cancel",
  "target": {"worldId":0,"x":121,"y":208,"cell":…},
  "objectsAtCell": 1,          // 该格有几个对象
  "triggered": 1,              // 实际发了几个 Cancel 事件
  "layers": ["Building"],
  "readback": { "digMarked": false, "deconstructMarked": false, "buildingStillThere": false },
  "cleared": true,
  "jobState": "cleared"        // nothing-at-cell / cleared / partially-cleared / no-op
}
```

`readback` 是**回读证据**（不是"我发了事件所以成功"）；`cleared` 只在三样都为 false 时才为 true。

## 注意

- **已建成的建筑不会被拆掉** —— 取消只撤"待办"（蓝图 / 挖标记 / 拆解标记），要拆建筑还是用 `deconstruct`。
- 一次只处理一格；批量取消（框选）还没做。

---

# 实测补充（0.114.1，2026-11-21）

## 两种标记要两种取消方式

| 标记类型 | 载体 | 正确取消方式 | 证据 |
|---|---|---|---|
| 蓝图 / 拆解标记 | 正经建筑上的组件 | 发 `GameHashes.Cancel=2127324410` 事件 | 实测 `cleared=true` |
| **挖掘 / 拖地** | `*Placer` 临时物体 | **销毁载体**（`Util.KDestroyGameObject`） | 只发事件时 `triggered=1` 但 `digMarked` 仍 true |

响应里的 `modes` 字段会写明每一步用的是哪种：`cancel:` 还是 `destroy:`。

## `KDestroyGameObject` 是帧末生效 ⇒ `cleared` 有三种值

同一帧回读会看到旧的标记还在，所以：

```json
{ "cleared": null, "pendingFrame": true, "jobState": "destroy-scheduled", "modes": ["destroy:"] }
```

- `cleared:true` = 本帧回读已确认清掉
- `cleared:false` = 本帧回读确认**没**清掉
- `cleared:null` + `pendingFrame:true` = 销毁已排队、帧末生效，**下一帧**用 `/v1/marks` 核验

实测：`(113,216)` 取消后立刻回读 `digMarked=true`，两秒后 `dig.marked=false, objectsAtCell=0`。

## 三种用例的验收结果

| 用例 | 结果 |
|---|---|
| 悬空蓝图 `(121,208)` → cancel | `cleared=true`，建筑消失（`deconstruct` 对它返回 `NOT_DECONSTRUCTIBLE`，只有 cancel 管用） |
| 挖标记 `(113,216)` → cancel | `destroy-scheduled` → 下一帧 `dig.marked=false` |
| 已建成 `PlasticTile (122,209)` → cancel | `cleared=true` 但**建筑仍在** ✅ 不误拆 |

## 沙盒模式下

沙盒把 `build` 变成瞬建，但**挖掘/取消/拆解依旧走标记**，`cancel` 行为不变（实测）。

---

# 0.116.1 修正（用反编译源码核对后）

## 修掉的谎报

`cleared` 之前只查 `Constructable` ⇒ **已建成的建筑**（没有 Constructable）被算成 `buildingStillThere=false`，
于是报 `cleared=true`。实测 `(122,209)` 的 `PlasticTile` 取消后仍在，但响应说清干净了。

现在回读拆成四个字段：

```json
"readback": { "digMarked": false, "deconstructMarked": false,
              "blueprint": false, "buildingStillThere": true },
"nothingToCancel": true,
"cleared": false,
"jobState": "nothing-to-cancel"
```

- `blueprint` = 那格还有 `Constructable`（蓝图/在建）
- `buildingStillThere` = 那格**有建筑**（不管是不是蓝图）
- `nothingToCancel` = 有建筑但没有待办 ⇒ cancel 无事可做（**不是**"清掉了"）

## 源码核对：`Constructable.OnCancel` 并不销毁蓝图

反编译（`shared/refs/oni-U59-744825/src/Constructable.cs`）里它只做三件事：

```csharp
private void OnCancel(object _ = null)
{
    if (DetailsScreen.Instance != null) DetailsScreen.Instance.Show(show: false);
    ClearMaterialNeeds();
    ClearPendingUproots();
}
```

蓝图的消失是**后续链条**的效果（实测 3 秒内确实消失），不是 Cancel 方法里销毁的。
所以"发 Cancel 事件就能撤掉蓝图"这个**结论**成立，但**机制描述**以前写错了。

## 三种用例的最终验收（0.116.1）

| 用例 | 结果 |
|---|---|
| 悬空蓝图 `(121,208)` → cancel | `cleared=true`, `blueprint=false`，3 秒后格子空 ✅ |
| 已建成 `PlasticTile (122,209)` → cancel | `cleared=false`, `nothingToCancel=true`, `buildingStillThere=true`，建筑仍在 ✅ |
| 挖掘标记 → cancel | `destroy-scheduled` + `pendingFrame`，下一帧 `digMarked=false` ✅ |

---

# 机制更正（反编译实证，0.116.1 之后）

之前写的"蓝图取消走 `Constructable.OnCancel`"只说对了一半：

- **真正删掉蓝图的是 `Cancellable`**：它订阅 `GameHashes.Cancel(2127324410)`，`OnCancel` 里就是 `this.DeleteObject()`；
- `Constructable.OnCancel` 只做三件事：关详情窗、`ClearMaterialNeeds()`、`ClearPendingUproots()` —— **不负责删除**；
- **挖掘的 `DigPlacer` 没有 `Cancellable`** ⇒ 发 Cancel 事件无效（实测 `digMarked` 仍 true），必须销毁 placer。

完整对照表见 [`GAME-MECHANICS.md`](GAME-MECHANICS.md) 第 5 节。
