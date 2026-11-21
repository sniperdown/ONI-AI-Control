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
