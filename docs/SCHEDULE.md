# `/v1/schedule` —— 作息表读取与修改（0.116.0）

## 读

```
GET /v1/schedule
```

```json
{ "type":"schedule", "currentHour": 0, "count": 2,
  "schedules": [
    { "name":"默认标准日程表", "isDefaultForBionics": false,
      "blocks":[ {"hour":0,"group":"Worktime","blockName":"工作时间"}, … 24 条 ],
      "assigned": ["吉滋镆"] }
  ] }
```

- `blocks` 是**每天 24 小时**各自的区块，`group` 是 `ScheduleGroup` 的 id（`Worktime` / `Sleep` / `Recreation` / `Anything` …）
- `assigned` 是挂在这张表上的复制人名字
- `currentHour = -1` ⇒ 游戏暂停时 `ScheduleManager.GetCurrentHour()` 返回 -1（不是错误）

## 改

```
POST /v1/schedule {"action":"assign","dupe":"吉滋镆","schedule":"默认标准日程表"}
POST /v1/schedule {"action":"set_block","schedule":"默认标准日程表","hour":3,"group":"Sleep"}
```

- `schedule` 省略 ⇒ 用第一张表
- 两条都是**玩家在"作息"界面里能做的事**，所以**不挂作弊闸门**
- 响应里 `ok` 是**回读判定**（`assign` → 该复制人现在的表名是否等于目标；`set_block` → 该小时的 `GroupId` 是否等于请求值），不是"我调用了所以成功"

## 实测（档「首都」cycle=8，mod 0.116.0）

| 请求 | 结果 | 反射读出的真实签名 |
|---|---|---|
| `assign` 吉滋镆 → 默认标准日程表 | `ok=true`，回读 `dupeScheduleNow=默认标准日程表` | `Schedule.Assign(Schedulable schedulable)` |
| `set_block` hour=3 → `Sleep` | `ok=true`，回读 `group=Sleep, blockName=就寝` | `Schedule.SetBlockGroup(Int32 idx, ScheduleGroup group)` |

`sig` 字段把这些签名**原样回报**，类型变了能立刻看出来。

## 实现要点（给下一个改这份代码的人）

- `ScheduleManager.Instance` 是**静态字段**；表在它的 `schedules` 字段里（`List<Schedule>`）
- 复制人 → 作息表：`MinionIdentity.GetComponent<Schedulable>()`，再由 `Schedule.Assign(schedulable)`
- `ScheduleGroup` 是 `Resource`，从 `Db.Get().ScheduleGroups` 按 id 取（`TryGet`/`Get`/枚举 三种形态都试）
- 全部走反射 + 回显签名：这族的参数类型在编译期不确定（`Schedulable` vs `Ref<Schedulable>`），猜错一次就崩
