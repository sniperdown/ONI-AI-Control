$ErrorActionPreference = 'Stop'
Set-Location 'H:\deepseek-harness\workspace\oni-mod'

# ---------- CHANGELOG ----------
$entry = @'
## [0.28.0] - 2026-11-14 — M4.4 网页版配置页（GET / + GET/PUT /v1/settings）

**运行时未验证**（验收方式改为 curl + 浏览器，见下）。游戏内设置页**一行未动**，保留为"看一眼"的入口。

### 新增接口
| 方法 | 路径 | 说明 |
|---|---|---|
| GET | `/`、`/index.html` | 返回**内联**网页（`text/html; charset=utf-8`）；需 `?token=xxx` 放行（浏览器地址栏带不了 header） |
| GET | `/v1/settings` | 当前配置快照；**不含 token 内容**，只给 `tokenLength`；带 `notes.port = 改端口需重启` |
| PUT | `/v1/settings` | **逐字段**更新（只改请求里出现的字段，其余保持原值） |

### PUT 的校验规则
- **`token` 出现在请求里 ⇒ 400 `FIELD_READONLY`**（安全，不允许从网页改）。
- 数值字段 clamp 到合法范围（`alertStress 0–100`、`alertCaloriesKcal 100–10000`、`alertBladder 0–100`、
  `amountEpsilon 0–10`），越界会**夹紧**并把字段名放进响应 `clamped[]`；小数按字段位数取整。
- `port` 允许改（`1024–65535`），但响应 `notes.port` 明写"**改端口需要重启 mod**"（当前监听仍绑在旧端口）。
- 一个字都认不出来 ⇒ 400 `NO_FIELDS`（避免"看起来保存成功其实没写"）。
- 写盘走 `BridgeSettings.SaveAndVerify`（写盘后重读文件逐字段比对）；**失败则回滚内存值**并返回 500 `WRITE_FAILED`。
- 成功后返回 `applied`（真正生效的字段）、`clamped`、`notes`、以及最新 `settings` 快照。

### 页面
- **全内联**：无 CDN、无外链字体/脚本（源码里 `https?://` 与 `src=` 出现次数 **0**，已核）；
  HTML 属性与 JS 字符串统一用单引号，避免 C# 转义地狱；标签开闭已核对平衡（div/span/label/style/script/head/body/button/html）。
- 顶部状态行：`mod 0.28.0 · M4.4 · API 1.1 · 端口 8770`；字段：`allowCheats`（勾选框）、4 个数值（`step` 与范围提示）、
  `port`、`token`（只读，显示长度）；底部「保存」→ `PUT` → 显示 `已保存：<字段=值>` / 夹紧提示 / 失败原因。
- 页面用服务端注入的 token 在**请求头** `X-Oni-Token` 里鉴权（地址栏只用于首次放行首页）。

### 安全
- 仍只监听 `127.0.0.1`；页面不含任何外链；token 不出现在页面正文里（只在脚本变量中随页面下发，
  因为浏览器无法带自定义 header 打开首页 —— 这是该方案的既定代价，仅在本机回环地址上成立）。

'@
$c = Get-Content 'CHANGELOG.md' -Raw
[System.IO.File]::WriteAllText((Join-Path (Get-Location) 'CHANGELOG.md'), ($entry + $c), (New-Object System.Text.UTF8Encoding($false)))
Write-Output 'OK CHANGELOG 0.28.0'

# ---------- API.md ----------
$m = Get-Content 'docs/API.md' -Raw
$anchor = '| GET | `/v1/health` | 存活 + 泵状态 | ✅ M0 |'
if (-not $m.Contains($anchor)) { throw 'MISS API.md 端点表锚点' }
$add = @'
| GET | `/` · `/index.html` | **网页版配置页**（内联 HTML，`?token=` 放行首页） | ✅ M4.4 |
| GET | `/v1/settings` | 当前配置快照（不含 token 内容，只给长度） | ✅ M4.4 |
| PUT | `/v1/settings` | **逐字段**更新配置 → 写盘 → 回读校验；`token` 只读 | ✅ M4.4 |
'@
$m = $m.Replace($anchor, $add + $anchor)

$sec = @'

### 3.9 网页版配置页（M4.4）

浏览器打开 `http://127.0.0.1:8770/?token=<你的 token>` 即可（地址栏带不了自定义 header，所以首页用查询参数放行；
页面内的 API 调用走 `X-Oni-Token` 头）。

- 页面**全内联**：无 CDN/外链字体/外部脚本，离线可用；
- `PUT /v1/settings` **只更新请求里出现的字段**（整体覆盖会把没传的字段清空）；
- 数值越界会被**夹紧**并在响应 `clamped[]` 里列出；`token` 出现在请求里直接 `400 FIELD_READONLY`；
- 改 `port` 会被接受，但**需要重启 mod** 才生效（响应 `notes.port` 会说明）；
- 写盘失败会回滚并返回 `500 WRITE_FAILED`（不会出现"看起来成功"）。
'@
$m = $m.TrimEnd() + $sec + "`n"
[System.IO.File]::WriteAllText((Join-Path (Get-Location) 'docs\API.md'), $m, (New-Object System.Text.UTF8Encoding($false)))
Write-Output 'OK API.md 更新'
Remove-Item 'tools\tmp-build-028.ps1' -Force -ErrorAction SilentlyContinue
