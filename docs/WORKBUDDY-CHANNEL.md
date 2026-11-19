# WorkBuddy ↔ OniAiBridge 的通信渠道：现状、不可靠在哪、最小可靠方案

> 写于 2026-11-19（子会话 oni-mod-2，按 lead 的追加任务）。**0.96.1 起**
> 「WorkBuddy 那一侧」与「mod 这一侧」之间的链路有一份可对账的约定。
> 接口细节在 [`API.md`](API.md) §3.41；动手版在 [`AI-GUIDE.md`](AI-GUIDE.md) §4.9。
> **验证状态以 [`VERIFIED.md`](VERIFIED.md) 为准**（本文件说的是"设计成什么样"，不是"验过没有"）。

---

## 1. 现状：先把事实摆清（2026-11-19 摸底）

### 1.1 对方是什么、跑在哪

* **WorkBuddy** = 腾讯 CodeBuddy 团队出的桌面智能体（另一个厂、另一套上下文、零共享），
  模型 Hy4 flash。跑在**同一台 Windows** 上，从桌面窗口工作。
* **它的工具 shell 只活一轮**：每条命令一个新进程，命令结束进程就没。所以它**起不了常驻监听** ——
  `2026-11-19-workbuddy-acceptance/TASK.md` §5 那条"监听 `127.0.0.1:47911` 当指令通道"的附加项，
  三轮都没做成。**结论：它是客户端，别指望它当服务端。**

### 1.2 现在两边靠什么通信（实际路径）

```
人 ──粘贴任务文本──▶ WorkBuddy 输入框          ← 这是唯一的"下行"
WorkBuddy ──(一轮 shell) curl──▶ http://127.0.0.1:8770/v1/*   [X-Oni-Token]
          ◀── JSON ──
WorkBuddy ──自己写的 oni_call.sh──▶ shared/tmp/workbuddy/run.log   ← 唯一的"结构化"留痕
人 ──读 run.log / 读它的界面报告──                                   ← 唯一的"上行"
```

| 项 | 位置 / 值 |
|---|---|
| 服务端 | OniAiBridge 的 HTTP（`Bridge.AcceptLoop`，**只绑 loopback**） |
| 端口 | 默认 `8770`；`Bridge.TryBind` 在 `8770..8779` 里取第一个能绑上的，**实际端口看 `/v1/meta` 的 `data.server.port`** |
| 鉴权 | 请求头 `X-Oni-Token`（也接受 `?token=`）；token 在 `%USERPROFILE%\Documents\Klei\OxygenNotIncluded\mods\config\OniAiBridge\settings.json` 的 `token` 键（32 位十六进制） |
| 端点 | 34 条（0.96.1 起 35 条，多了 `/v1/journal`），手册 [`AI-GUIDE.md`](AI-GUIDE.md) |
| 它的留痕 | `shared/2026-11-19-workbuddy-acceptance/run.log`（自由格式：`[时间] CMD … => HTTP n \| RESP …`） |
| 历史材料 | 同目录：`TASK.md` / `ACCEPTANCE.md` / 三轮 `REPORT-*.md` / `oni_call.sh` / `shots/` |

### 1.3 「不可靠」的具体表现（每条都有原文）

1. **没有任何请求 id。** `run.log` 的一行只有时间戳 + 命令回显；要和游戏侧
   `Player.log`、要和"这个 `ok:true` 是哪次请求的"对上，只能靠时间戳猜。同一秒两条请求就无法区分。
2. **响应丢了无法认领。** 一次连接超时/断开之后，"执行了但回执丢了"和"根本没执行"在客户端**长得一模一样**
   —— 它只能二选一：当失败（可能白做）或当成功（可能没做）。
3. **超时没有边界、重试不安全。** 它的 `curl` 不带 `-m`、也不重试（只在排障时临时用过 `-m 10`）。
   而 mod 的 `503 RETRY_LATER` 语义明确是**动作尚未执行**，在它眼里却和普通失败没区别。
   更糟的是：**写操作没有幂等键**，同一个动作重发就是真做第二遍（`/v1/job` 打标记、
   `/v1/build` 放蓝图都没有"这次是不是重发"的概念）。
4. **没有确认机制。** `ok:true` 不是"做成了"的证据（见 `VERIFIED.md` §4），"做成了没有"只能靠下一次 GET 回读；
   写操作**没有可查的回执**。
5. 附带一条**语义坑**（不是渠道问题，但同一轮暴露）：`dig`/`sweep` 的 `dryRun` 当时被静默忽略，
   它按手册先 dryRun 反而真标了 31 格。⇒ 渠道再可靠也救不了"服务端说一套做一套"，这类 bug 优先级更高。

---

## 2. 最小可靠方案（0.96.1，全部在 mod 侧）

目标只四个字：**对得上号**。不引入消息队列、不改 WorkBuddy 的代码（它没有可编程入口，
只会 `curl`），也不做持久化。

### 2.1 请求 id（reqId）—— 一切的基础

```http
POST /v1/job
X-Oni-Token: <token>
X-Oni-Req-Id: wb-20261119-abc12345        ← 也可以写成 ?reqId=wb-20261119-abc12345
Content-Type: application/json

{"type":"dig","x":111,"y":220}
```

* 服务端把 id **清洗**（只留 `[A-Za-z0-9._:-]`，最长 64）后**原样回显**：
  响应体顶层 `reqId` 字段（`/v1/health`、`/v1/meta`、全部 `/v1/*` 都有），响应头也带 `X-Oni-Req-Id`。
* 不给就服务端自己生成 `srv-…`（这种 id 永不重复 ⇒ 永不回放，等价于 0.96.0 的行为）。

### 2.2 幂等回放 —— 让"重试"变安全

* 同一个 `reqId` 的 **POST / PUT** 再来一次 ⇒ 服务端**直接回放上次那条响应**（响应头 `X-Oni-Replayed: 1`），
  **不再执行**。
* `503 RETRY_LATER` **不记账、不回放** —— 它的语义就是"动作尚未执行，请重试"，回放了反而把重试堵死。
* 记账时点是**先记账、再写 socket**：写 socket 失败正是"动作做了、回执没送到"，
  那条账就是重试时该回放的东西；反过来先写后记就会漏掉这一格。

### 2.3 事后对账 —— 失败必须可查

| 请求 | 用途 |
|---|---|
| `GET /v1/journal?reqId=<id>` | 单条查账，**带那次响应体** ⇒ "到底执行了没有、结果是什么" |
| `GET /v1/journal?since=<seq>&limit=<n>` | 列最近账目（不带响应体），排查"刚才那批里哪几条落了地" |

响应形状见 [`API.md`](API.md) §3.41。

### 2.4 客户端：`tools/oni_client.ps1`

把"生成 reqId → 超时 → 有界重试 → 对账 → 失败可见"这五步固定下来，不靠人记：

```powershell
pwsh -File tools/oni_client.ps1 -Path '/v1/health'
pwsh -File tools/oni_client.ps1 -Path '/v1/buildings?limit=2000&offset=0' -Raw
pwsh -File tools/oni_client.ps1 -Method POST -Path '/v1/job' `
     -BodyFile 'H:\...\dig.json' -Log 'H:\...\run.log'
```

* 只重试**必定没执行**的情形：连不上 / 超时 / `503`；最多 `-MaxRetries` 次（默认 2 ⇒ 共 3 次）。
* 重试用尽后查 `/v1/journal?reqId=…`，然后按结果给退出码：

| 退出码 | 含义 | 该怎么办 |
|---|---|---|
| 0 | 拿到 2xx | 正常 |
| 1 | 本地参数/配置错（没 token、Path 不以 `/v1/` 开头…） | 修参数，别重发 |
| 2 | 服务端**明确拒绝**（非 2xx 且不是 503） | 读 `error.code`，别重发 |
| 3 | 传输出错，**对账显示没执行** | 可以安全重来（同一个 reqId 也行） |
| 4 | 传输出错，**但对账显示已执行** | **别重发**，结果在 `replayedBody` 里 |
| 5 | 传输出错，**且查不到账** | **结果未知**，不要当成成功 |

### 2.5 纯 bash / curl 的等价写法（WorkBuddy 那种 shell）

```bash
REQ="wb-$(date +%Y%m%d%H%M%S)-$RANDOM"
curl -sS --max-time 8 --retry 2 --retry-delay 1 \
     -H "X-Oni-Token: $TOKEN" -H "X-Oni-Req-Id: $REQ" \
     -X POST -H 'Content-Type: application/json' \
     -d '{"type":"dig","x":111,"y":220,"dryRun":true}' \
     "http://127.0.0.1:8770/v1/job"
# 没拿到响应 ⇒ 先查账，再决定要不要重发：
curl -sS -H "X-Oni-Token: $TOKEN" "http://127.0.0.1:8770/v1/journal?reqId=$REQ"
```

`curl --retry` 只对连接错误 / 超时 / 5xx 生效，而且**它不会换 reqId** ⇒ 正好和我们这边的回放对齐。
⚠️ 拿到明确回执之后**不要再用同一个 id 发一次**（那等于故意让服务端回放）。

---

## 3. 边界（明确不做的，免得被当保证）

| 不做 | 原因 / 后果 |
|---|---|
| 不持久化账本 | 只在内存、只留最近 **128** 条、只存 **< 64 KB** 的响应体；mod 或存档重载即清空 ⇒ **`found:false` 不等于"一定没执行"** |
| 不做离线投递 / 队列 | mod 不会主动找 WorkBuddy —— 它没有常驻端口，反过来也做不到 |
| 不改 WorkBuddy | 它没有可编程入口，只能用 shell 调我们的脚本 / curl |
| 不换鉴权、不加加密 | 仍是 loopback + token；渠道可靠性与鉴权是两件事 |

---

## 4. 这份方案的验证状态

| 项 | 状态 |
|---|---|
| 编译 | 0 error（`dotnet build -c Release`，33 个既存警告） |
| `ReqJournal` 逻辑 | **独立单测 13/13 通过**（回放命中、`503` 不记账、超限 body、环形淘汰、列表升序、`since` 过滤、`Sanitize` 边界） |
| `tools/oni_client.ps1` | 连不上端口 ⇒ 3 次尝试后 `exit 5`（`reconciled:"unknown"`）；坏 Path / POST 空 body ⇒ `exit 1` |
| `/v1/journal` 端点 + 回放 | **未装机，无运行期样本** ⇒ `VERIFIED.md` 标 ⚠️ |
| 怎么验（装机后） | ① `GET /v1/health` 带 `X-Oni-Req-Id: t1`，看响应体 `reqId:"t1"`；② 同一个 id 再发一次 `POST /v1/job {...dryRun:true}`，看响应头 `X-Oni-Replayed: 1` 且**游戏侧没有第二次执行**（`/v1/marks` 回读）；③ `GET /v1/journal?reqId=t1` 能查到那条账；④ 拔掉游戏进程再发一次，看 `oni_client.ps1` 走 `exit 5` 而不是假装成功 |
