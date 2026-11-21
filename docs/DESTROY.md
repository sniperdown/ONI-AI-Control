# `type=destroy` —— 沙盒「摧毁」（0.114.2）

## 与 `deconstruct` 的区别（别混用）

| | `deconstruct` | `destroy` |
|---|---|---|
| 谁干 | 复制人（派活） | 没人干，**立即** |
| 需要路 / 站得住 | 要 | 不要 |
| 材料 | 退回 | 不退回 |
| 闸门 | 普通操作 | **沙盒作弊**（`allowCheats` && 沙盒/debug） |
| 用途 | 正常游戏里拆东西 | 沙盒改地形 |

## 接口

```
POST /v1/job {"type":"destroy","x":107,"y":216,"confirm":true}
```

作弊闸门未开时：`403` + `Gate.UnavailableCode()` / `UnavailableReason()`（与 `/v1/dupes/spawn` 同一套）。

## 底层：一句 `GameUtil.DestroyCell`

IL 实证：`SandboxDestroyerTool.OnPaintCell` 的方法体只有 `call GameUtil.DestroyCell`。
反射读出的**真实签名**（响应里的 `sig` 字段会原样回报）：

```
GameUtil.DestroyCell(Int32 cell, CellElementEvent eventSource,
                     Boolean deleteMinions, Boolean destroyNaturalBackwall)
```

参数怎么填（实测踩出来的）：

| 参数 | 值 | 为什么 |
|---|---|---|
| `cell` | 目标格 | — |
| `eventSource` | `CellEventLogger.Instance.SandBoxTool` | 摧毁工具传的就是它；传默认值会少记事件 |
| `deleteMinions` | `true` | IL 里是 `ldc.i4.1`；顺手清掉该格上的小动物 |
| `destroyNaturalBackwall` | **`true`** | **传 false 时对天然物块无效**（实测：泥土格 `after.solid` 仍为 true，格子里什么都没变） |

## 帧末生效 ⇒ `cleared` 三态（与 `cancel` 同一条规矩）

```json
{ "invoked": true, "cleared": null, "pendingFrame": true, "jobState": "destroy-scheduled" }
```

实测：`(106,216)` 是 `Dirt/800kg`，调用当帧回读仍 `solid=true`，**两秒后**变 `Vacuum`；
`(127,208)` 的 `TileComplete` 同理消失。所以调用方要**下一帧再核**（`/v1/map` 或 `/v1/buildings`）。

## 响应示例

```json
{ "type": "destroy",
  "target": {"x":107,"y":216,"cell":…},
  "sig": "GameUtil.DestroyCell(Int32 cell, CellElementEvent eventSource, Boolean deleteMinions, Boolean destroyNaturalBackwall)",
  "invoked": true, "error": null,
  "before": {"element":"Dirt","massKg":800,"building":null},
  "after":  {"solid":true,"building":false},
  "cleared": null, "pendingFrame": true, "jobState": "destroy-scheduled" }
```

`before` 是**摧毁前的事实**（哪来的元素/建筑），`after` 是当帧回读（因此 `pendingFrame` 时它必然还是旧的）。
