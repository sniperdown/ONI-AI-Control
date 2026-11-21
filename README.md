# ONI AI Control

> 仓库名 `ONI-AI-Control` ｜ 中文名「缺氧 AI 操控」
> **内部标识沿用旧名 `OniAiBridge`**（DLL 名、`staticID`、配置目录 `mods/config/OniAiBridge/` 都不变），
> 所以老配置继续有效；只是游戏里显示的 mod 名改成了 `ONI AI Control`。

把《缺氧》(Oxygen Not Included) 的**殖民地状态**与**游戏内操作**通过一个**本机 HTTP API** 暴露出来，
让任意程序（自己的脚本、AI 客户端、别的工具）能读状态、下命令。

* 只监听 `127.0.0.1`，所有请求都要 token（首次运行自动生成）。
* 作弊/调试类操作**默认关闭**，且底层**直接调用游戏自带的 Debug 工具**（不是本项目重写的逻辑）。
* 普通操作走的是**游戏自己的路径**：建造=`trybuild` 蓝图 + 复制人施工；拆/挖/门=`QueueDeconstruction` / `PlaceDig` / `QueueStateChange`；作息=`Schedule.Assign`。
* 目前阶段：**0.116.0** —— 38 条端点（读状态/建筑/地图/环境/区域/世界/泉/复制人/告警/存档 + 挖/建/撤回/摧毁/门/作息/打标记/管线/UI 点击/截图 + 请求对账）。
  能干什么、还缺什么见 [`docs/RELEASE-1.0-STATUS.md`](docs/RELEASE-1.0-STATUS.md)；
  逐项细节以 [`docs/API.md`](docs/API.md) 为准，版本与改动见 [`CHANGELOG.md`](CHANGELOG.md)。

---

## 🤖 如果你是一个 AI，从这一段开始

**别读这份 README 的其余部分**，直接读 **[`docs/AI-GUIDE.md`](docs/AI-GUIDE.md)（发布包里是 `AI-GUIDE.md`）** ——
那是按"陌生 AI 装上就能自己用起来"写的操作手册。三个必知事实先给你：

```
地址      http://127.0.0.1:8770
认证      请求头  X-Oni-Token: <token>
token 在   %USERPROFILE%\Documents\Klei\OxygenNotIncluded\mods\config\OniAiBridge\settings.json
           （键名是小写 "token"，32 位十六进制；token 是全量校验，本机不带也是 401）
```

一条能直接跑的自检：

```bash
TOKEN=$(powershell -NoProfile -Command "(Get-Content \"$env:USERPROFILE\Documents\Klei\OxygenNotIncluded\mods\config\OniAiBridge\settings.json\" -Raw | ConvertFrom-Json).token")
curl -s -H "X-Oni-Token: $TOKEN" http://127.0.0.1:8770/v1/meta
```

`"ok":true` 就是通了，之后照 AI-GUIDE 的 §3 工作流走。

---

> API 文档：[`docs/API.md`](docs/API.md) · **AI 操作手册：[`docs/AI-GUIDE.md`](docs/AI-GUIDE.md)** · 设计文档：[`DESIGN.md`](DESIGN.md) · 变更记录：[`CHANGELOG.md`](CHANGELOG.md)

---

## 安装

1. 从 Releases 或自己构建（见下）拿到 `OniAiBridge/` 文件夹。
2. 放进本地 mod 目录：

```
%USERPROFILE%\Documents\Klei\OxygenNotIncluded\mods\Local\OniAiBridge\
```

3. 启动游戏 → **Mods** 界面勾选 `ONI AI Bridge` → 重载 mod 或重启游戏。
4. 载入一个殖民地。

## 打开 API

首次运行会在下面这个位置生成配置（含随机 token）：

```
%USERPROFILE%\Documents\Klei\OxygenNotIncluded\mods\config\OniAiBridge\settings.json
```

```json
{
  "port": 8770,
  "allowCheats": false,
  "AIEnableFog": true,
  "allowMap": false,
  "token": "<32 位十六进制，自动生成>"
}
```

（节选。完整字段以实际生成的文件为准：还有 `FixPlanScreenPriorityNRE` 与 4 个阈值/精度项。
`AIEnableFog` = 有迷雾（AI 只报看得见的地方）；`allowMap` = 允许 `GET /v1/map` 读地图降采样，**默认关**，
关着时该端点回 `403 MAP_DISABLED`。）

端口被占用时会自动顺延（8770..8779），实际端口在游戏日志 `Player.log` 里搜 `[OniAiBridge] listening`。

## 第一个请求

```bash
TOKEN=$(python -c "import json,os;print(json.load(open(os.path.expanduser('~/Documents/Klei/OxygenNotIncluded/mods/config/OniAiBridge/settings.json')))['token'])")
curl -s -H "X-Oni-Token: $TOKEN" http://127.0.0.1:8770/v1/meta | python -m json.tool
```

```json
{
  "apiVersion": "1.1",
  "ok": true,
  "data": {
    "mod": { "name": "ONI AI Bridge", "version": "0.93.6", "staticID": "OniAiBridge", "stage": "M6.90" },
    "server": { "port": 8770, "loopbackOnly": true },
    "game": { "buildText": "U59-744825-S", "buildVersion": 744825, "unityVersion": "6000.3.5f2" },
    "pumpReady": true,
    "colonyLoaded": true,
    "cheatsAllowed": false,
    "debugModeOn": false,
    "capabilities": { "state": true, "events": true, "map": false, "catalog": true, "commands": true },
    "aiEnableFog": true,
    "allowMap": false,
    "mapLimits": { "maxSide": 64, "maxCells": 4096, "channels": ["element", "temperature", "mass"] }
  },
  "error": null
}
```

（`capabilities.map: false` 表示 `allowMap` 关着 —— **端点本身存在**，打开后变 `true`。）

## 从源码构建

需要 .NET SDK 6+，以及游戏的 `Managed` 目录（**本仓库不包含任何游戏 dll**）：

```bash
dotnet build src/OniAiBridge/OniAiBridge.csproj -c Release \
  -p:OniGameDir="<ONI_INSTALL_DIR>" -o dist/OniAiBridge
```

或直接：

```powershell
$env:ONI_GAME_DIR = "<ONI_INSTALL_DIR>"   # 例如 ...\steamapps\common\OxygenNotIncluded
pwsh -File build.ps1
```

产物在 `dist\OniAiBridge\`（含 `mod.yaml` / `mod_info.yaml`，UTF-8 无 BOM，脚本会回读校验）。

## 常见问题

**`/v1/meta` 连不上**
先看 `Player.log`（`%USERPROFILE%\AppData\LocalLow\Klei\Oxygen Not Included\Player.log`）里有没有
`[OniAiBridge]` 开头的行。没有 → mod 没被加载（检查路径与 Mods 界面的勾选）；
有 `listening on ...:<port>` → 用那个端口。

**`pumpReady: false`**
Harmony 补丁没生效（版本不匹配或别的 mod 抢先改写了 `Game.Update`）。此时 `colonyLoaded` 一律为 `false`
（不会给 `null`），`/v1/state` 返回 `503 NOT_READY` —— 这是如实上报，不是 bug。

**本地 mod 突然完全不加载（日志里连 `[OniAiBridge]` 都没有）**
先看 `Documents\Klei\OxygenNotIncluded\mods\mods.json` 里我们那条的 `enabledForDlc`：
**空数组 `[]` 表示"哪个 DLC 都不启用" ⇒ 游戏不加载它**（这是 ONI 的行为，不是 mod 的 bug）。
把它补成 `["EXPANSION1_ID"]` 再启动即可。**别去改代码。**

**`401 UNAUTHORIZED`**
token 不对。它就在 `settings.json` 里；换了 token 后需要重载 mod。

**游戏内怎么打开设置页？**
按 **`Ctrl+Shift+O`**（主菜单和游戏内都可以，不会叠加多层窗口；弹的是游戏官方对话框样式）。这是当前**唯一稳定入口** ——
M4.1 实测发现官方 Mods 列表里本地平台(`Local`)的条目不会渲染，所以「管理」按钮虽然挂成功了却看不见。

**设置页能改什么？**
按 `Ctrl+Shift+O` 打开后，可以改：`alertStress` / `alertCaloriesKcal` / `alertBladder` / `amountEpsilon` / `port`
五个数值框，以及 `allowCheats`（作弊闸门）、`AIEnableFog`（迷雾）、`allowMap`（允许 `GET /v1/map`）三个勾选框。
**改的都是内存草稿，点 `[保存]` 才一次性校验 + 写盘 + 回读**（任一项校验失败 ⇒ 整批不写并回滚）；
另有「打开配置目录」（直接弹资源管理器）和「关闭」。每次保存 `Player.log` 里有三段日志
（① 切换 → ② 写盘+回读校验 → ③ 磁盘回读），可以对账。

**想知道"我的 mod 到底被游戏认成什么样"**
`curl -H "X-Oni-Token: $TOKEN" http://127.0.0.1:8770/v1/debug/mods`
会逐条列出 `modManager.mods` 里的原生状态（`platform/status/availableContent/isLocal/shouldDisplayMod`…），
这是排查"条目看不见 / 反射没生效"这类问题的探头。

**改了 `settings.json` 没反应**
配置只在 mod 加载时读一次。改完请重载 mod（Mods 界面）或重启游戏。

**`amounts` 里卡路里/体温是 0 或者没有这个 key**
刚载入殖民地、游戏还处于**暂停**时，模拟没跑过一帧，Calories/Temperature 这类模拟驱动的值会是 0 或缺失。
**这不是 bug**，取消暂停后下一次请求就正常；也可以用 `/v1/state` 的 `colony.isPaused` 判断。

## 给贡献者

* **不要在 `using System;` 之后直接用 `Action` / `DateTime`**：`Assembly-CSharp.dll` 自己定义了这两个
  全局类型，会把 BCL 的遮掉，症状是 `CS1660 / CS0117` 这类看起来莫名其妙的编译错误。一律写
  `System.Action`、`System.DateTime`（详见 `DESIGN.md` §1.4）。
* 所有游戏对象访问必须经 `MainThread.Invoke(...)`（HTTP 线程直接碰 Unity 对象会偶发崩溃/坏档）。
* `mod.yaml` / `mod_info.yaml` 必须 **UTF-8 无 BOM**，否则游戏解析报错（`build.ps1` 已强制）。
* **用文本替换/正则改代码，替换后必须立刻回读校验，MISS 就 fail fast。**
  原因是一次真实事故：给 `CommandsJson()` 插入作弊 op 列表时锚点没匹配上，`String.Replace()`
  在"找不到"时静默返回原串，编译照样通过，于是"命令都在"这个结论被报了出去而实际一条都没加。
  规矩：**不要**直接调用 `.Replace()`；用 [`tools/set-text.ps1`](tools/set-text.ps1)
  （命中数检查 + 唯一性检查 + 回读校验 + 非零退出），或在自己的补丁脚本里 print `OK/MISS` 并 `throw`。
* **报"某个功能已实现"必须有运行时证据**（接口实际返回、界面实际变化、日志实际行），
  **编译通过不算证据**。只读内存、反编译、编译成功都只能算"看起来对"。
* **UI 类改动的验收标准是两条，缺一不可**：① 能打开 / 能关 / 不叠层；② **关键字段的值肉眼可见且正确**。
  "能打开的空白页"不算完成 —— 真发生过（`InfoDialogScreen.AddLineItem(text, tooltip)` 的第二参数是
  tooltip 不是值，把值全塞进悬浮提示里，屏幕上只剩标签）。

## 许可证

MIT，见 [`LICENSE`](LICENSE)。
