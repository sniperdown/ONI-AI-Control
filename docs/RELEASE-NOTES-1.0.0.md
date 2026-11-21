# ONI AI Control 1.0.0

把《缺氧》(Oxygen Not Included) 的**读**、**判断能不能干**、**动手**三层做成一套本机 HTTP 接口，
让任意程序（脚本 / AI 客户端 / 别的工具）能读状态、下命令。

> 仓库 `ONI-AI-Control` ｜ 游戏内显示名 `ONI AI Control` ｜ **内部标识仍是 `OniAiBridge`**
> （DLL 名、`staticID`、配置目录 `mods/config/OniAiBridge/` 都没变，老配置继续有效）

## 这一版有什么

- **只监听 `127.0.0.1`**，所有请求要 token（首次运行自动生成）
- **38 个端点**：状态 / 建筑 / 地图逐格（元素·质量·温度）/ 环境 / 区域 / 世界 / 泉 / 复制人 / 告警 / 存档
  + 挖 / 建 / 拆 / 撤回 / 摧毁 / 门 / 作息 / 打标记 / 管线 / UI 点击 / 截图 / 请求对账
- **普通操作走游戏自己的路径**：
  建造 = `trybuild` 蓝图 + 复制人施工；拆 = `QueueDeconstruction`；挖 = `PlaceDig`；
  门 = `QueueStateChange`；作息 = `Schedule.Assign`
- **作弊功能默认关闭**，必须在 `settings.json` 里开 `allowCheats` 且游戏处于沙盒/debug 才放行
- **能不能干有判据**：`/v1/reach` 用游戏自己的 `Navigator.CanReach`；每格带
  `workPos.{standableNeighbors,verdict}` —— `verdict=no_standable_neighbor` 就是"这标记永远不会被干"

## 安装

1. 把 `OniAiBridge` 整个目录放进
   `%USERPROFILE%\Documents\Klei\OxygenNotIncluded\mods\Local\`
2. 游戏内开 mod（`ONI AI Control`）
3. 首次运行会在 `mods/config/OniAiBridge/settings.json` 生成 token，接口地址 `http://127.0.0.1:8770`

## 已知边界（诚实清单）

- **没有图形面板**（1.0 只做接口层）
- `SnapshotTool` / `DisconnectTool` 未接
- 无限流
- 复制人**优先级**未做（作息已做）
- **没有任务队列 / 多小人分工** —— 现在只能"下标记，然后自己盯"

## 这一版纠正的判据（都在实测后改的）

1. `/v1/reach` 主判据换成游戏 `Navigator.CanReach`（自研 BFS 会漏整条对角通道、且没算角色 2 格高）
2. 作业范围以 `OffsetGroups.Standard = InitGrid(-2,2,-3,3)` 为准 ⇒ **5×7**，且目标要有可站邻格
3. `placed:true` ≠ 会建成；`marked=true` ≠ 会被干
4. `KDestroyGameObject` / `DestroyCell` 帧末生效 ⇒ `cleared` 三态 + `pendingFrame`
5. 沙盒会把 `trybuild` 也变成瞬建（不扣材料）

详细说明见仓库 docs/：`RELEASE-1.0-STATUS.md`、`API.md`、`GAME-MECHANICS.md`、
`WORK-RANGE.md`、`CANCEL.md`、`DESTROY.md`、`SCHEDULE.md`、`REVERSE-ENGINEERING.md`。

## 安装包

- `OniAiBridge.dll`
- `mod.yaml` / `mod_info.yaml`

（解压后放进 `mods/Local/OniAiBridge/`）
