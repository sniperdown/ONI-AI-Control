# oni-mod 1.0 状态（2026-11-21 收尾）

> 给发版前的自己/别人看：**mod 现在能干什么、不能干什么、证据在哪。**
> 详细版在 `oni-mod/docs/2026-11-21-实测汇总.md`、`WORK-RANGE.md`、`CANCEL.md`、`DESTROY.md`、`SCHEDULE.md`。

## 版本

**0.116.0**（已装、已生效、已提交 `19c0181`）。工作树干净。

## 能干的（都有实测证据）

| 领域 | 能力 |
|---|---|
| **看** | `/v1/state` `/v1/dupes` `/v1/buildings` `/v1/map`（element/mass/temperature 三通道，单次上限 64×64）`/v1/env`（参数是 `x,y,w,h`）`/v1/area` `/v1/elements` `/v1/inventory` `/v1/alerts` `/v1/events`（SSE）`/v1/journal` `/v1/geysers` `/v1/research` `/v1/catalog` `/v1/worlds` |
| **判断能不能干** | `/v1/reach` —— 主判据是**游戏自己的** `Navigator.CanReach`（无小人时降级 BFS 并标 `judge:"bfs-fallback"`）；每格还带 `workPos`：`standableNeighbors` + `verdict`（`will_do` / `no_standable_neighbor`） |
| **派活** | `/v1/job`：`dig` / `deconstruct` / `harvest` / `sweep` / `research` / `disinfect` / `empty_pipe` / `attack` / `capture` / `mop` / `build` / `cancel` / `destroy` |
| **建造** | `build` 的 `placeMode`：`trybuild`（**玩家等价**：蓝图 → 小人搬材料建）/ `instant`（作弊瞬建）/ `defbuild`（作弊直造）/ `dragtool` |
| **撤回** | `type=cancel`：蓝图/拆解走 `GameHashes.Cancel` 事件，挖掘/拖地**销毁 placer**；`cleared` 三态（true/false/**null+pendingFrame**） |
| **删** | `type=destroy`（沙盒「摧毁」，作弊闸门）：`GameUtil.DestroyCell(cell, CellEventLogger.Instance.SandBoxTool, true, true)` |
| **门** | `/v1/door`：`normal`（派活等小人按）/ `instant`（反射 `controlState`，作弊闸门） |
| **作息** | `GET/POST /v1/schedule`：读 24 小时区块 + 挂的人；`assign` 指派、`set_block` 改区块（回读判定） |
| **人** | `/v1/move`（**钉不住**，会被 `MoveToSafety` 拉走）、`/v1/dupes/spawn`（作弊） |
| **UI/截图** | `/v1/ui` `/v1/ui/click` `/v1/ui/widgets` `/v1/shot` `/v1/shot/cameras` `/v1/tools` |

## 还缺的

| 缺口 | 说明 |
|---|---|
| **面板** | 一行没写（**1.0 不做**，主人拍板先发） |
| `SnapshotTool` / `DisconnectTool` | 未接（拍照、断线） |
| 限流 | `/v1/settings` 里 `rateLimitPerMin` 报 0，实际无限流 |
| `/v1/dupes/{id}`、`/v1/catalog/{kind}` | 子路径版本未做（主端点都有） |
| 复制人**优先级** | 未做（作息已做） |
| 多小人分工 / 任务队列 | 没有这一层——现在只能"下标记，然后自己盯" |
| `world.displayName` | 本地化名缺，只给 `name` 与 `typeKey` |

## 这一轮踩到的、必须记住的判据

1. **`placed:true` ≠ 会建成**：蓝图要有"小人能站到且相邻"的格；否则永久挂着没人来
2. **`marked=true` ≠ 会被干**：超范围（`|dx|>2` / `dy` 出 `[-2,+3]`）或没有可站邻格 ⇒ 小人取到 chore 立刻放弃，**标记不消失**
3. **作业范围是"每个站位"的矩形**，不是"小人当前位置"的——它会沿梯子换站位够更远的格（实测 9 个标记全被挖完）
4. **`KDestroyGameObject` / `DestroyCell` 都是帧末生效** ⇒ 同帧回读必然看到旧内容，要报 `pendingFrame`
5. **沙盒会把 `trybuild` 也变成瞬建**（不扣材料）⇒ 想测"小人真去施工"必须先关沙盒
6. **沙盒状态不持久化**：重载/重启后要重新开
7. **装 DLL 必须重启游戏**（DLL 被内存映射锁住）⇒ 重启会丢玩家未保存进度，**动手前先问**
