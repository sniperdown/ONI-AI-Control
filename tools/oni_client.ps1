#requires -Version 7.0
<#
oni_client.ps1 — /v1/* 的**可靠调用**封装（0.96.1 起配套 /v1/journal 对账）

为什么有这个脚本（不是"再包一层 curl"）：
  外部 agent（WorkBuddy 那类）的 shell 只活一轮，`curl` 没超时、没重试；一次
  `503 RETRY_LATER`（= 主线程忙、**动作尚未执行**）或一次连接超时，对它来说与
  "执行了但回执丢了"完全一样 —— 它既不敢重试（怕干两遍），也不敢说成功。
  这个脚本把三件事固定下来：
    1) **每个请求带 reqId**（`X-Oni-Req-Id`），服务端会把它回显在响应体的 `reqId` 字段里；
    2) **有界重试**：只重试"必定没执行"的情形（连不上 / 超时 / 503），最多 -MaxRetries 次；
       写操作重发是安全的 —— 同一个 reqId 再来一次，服务端**回放上次的响应，不再执行**；
    3) **失败可对账**：重试用尽后查 `GET /v1/journal?reqId=<id>`，明确告诉你
       "执行了（附结果）" / "没有记录（多半没执行）" / "查不到账（未知，别当成功）"。

用法：
  pwsh -File tools/oni_client.ps1 -Path '/v1/health'
  pwsh -File tools/oni_client.ps1 -Path '/v1/buildings?limit=2000&offset=0'
  pwsh -File tools/oni_client.ps1 -Method POST -Path '/v1/job' -Body '{"type":"dig","x":111,"y":220,"dryRun":true}'
  pwsh -File tools/oni_client.ps1 -Path '/v1/journal?reqId=wb-20261119-abc12345'

参数：
  -Method GET|POST|PUT   默认 GET
  -Path   以 /v1/ 开头（GET 时可直接带 query）
  -Body   POST/PUT 的 JSON 字符串（**写文件更安全**：-BodyFile <path>）
  -ReqId  不给就生成 `wb-<yyyyMMddHHmmss>-<8 hex>`；重试时**始终用同一个**
  -TimeoutSec 单次超时（默认 8）
  -MaxRetries 最多重试几次（默认 2 ⇒ 总共 3 次）
  -BaseUrl 默认 http://127.0.0.1:8770
  -Token   默认从 settings.json 读（与 smoke.ps1 同一个文件）
  -Log     追加一行结构化日志（默认不写；给路径才写）
  -Raw     只打印响应体（不给就把一行 JSON 结果 + 人读摘要都打出来）

退出码（给脚本/agent 判断用）：
  0 = 拿到 2xx
  1 = 本地参数/配置错误（没 token、Path 不以 /v1/ 开头 ……）
  2 = 服务端明确拒绝（非 2xx，且不是 503 重试耗尽）
  3 = 传输出错，且对账显示**没有执行**（可安全重来）
  4 = 传输出错，但对账显示**已执行**（结果已回放，别重来）
  5 = 传输出错，且**查不到账** ⇒ 结果未知（不要当成成功）
#>
param(
    [ValidateSet('GET', 'POST', 'PUT')][string]$Method = 'GET',
    [Parameter(Mandatory = $true)][string]$Path,
    [string]$Body = '',
    [string]$BodyFile = '',
    [string]$ReqId = '',
    [int]$TimeoutSec = 8,
    [int]$MaxRetries = 2,
    [string]$BaseUrl = 'http://127.0.0.1:8770',
    [string]$Token = '',
    [string]$Log = '',
    [switch]$Raw
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
try { [Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false) } catch { }

function Fail-Local {
    param([string]$Msg)
    Write-Host ("LOCAL_ERROR " + $Msg) -ForegroundColor Red
    exit 1
}

if ($Path -notlike '/v1/*') { Fail-Local "Path 必须以 /v1/ 开头（收到 '$Path'）" }
if (-not $ReqId) { $ReqId = 'wb-' + (Get-Date).ToString('yyyyMMddHHmmss') + '-' + ([guid]::NewGuid().ToString('N').Substring(0, 8)) }

if ($BodyFile) {
    if (-not (Test-Path $BodyFile)) { Fail-Local "BodyFile 不存在: $BodyFile" }
    $Body = [System.IO.File]::ReadAllText($BodyFile)
}
if (($Method -eq 'POST' -or $Method -eq 'PUT') -and [string]::IsNullOrWhiteSpace($Body)) {
    Fail-Local "$Method 需要 -Body 或 -BodyFile（空 body 会被服务端按 400 处理）"
}

if (-not $Token) {
    $settings = Join-Path $env:USERPROFILE 'Documents\Klei\OxygenNotIncluded\mods\config\OniAiBridge\settings.json'
    if (-not (Test-Path $settings)) { Fail-Local "找不到 settings.json: $settings" }
    try { $Token = [string](Get-Content -Raw $settings | ConvertFrom-Json).token }
    catch { Fail-Local "settings.json 解析失败（取 .token）" }
}
if ([string]::IsNullOrWhiteSpace($Token)) { Fail-Local 'token 为空' }

# ---------------- 单次请求 ----------------
function Invoke-Once {
    param([string]$Method, [string]$Path, [string]$Body, [string]$ReqId)

    # reqId 走 header（也能走 query，但 header 更干净）。注意：**每次重试都用同一个 reqId**。
    $headers = @{ 'X-Oni-Token' = $Token; 'X-Oni-Req-Id' = $ReqId }
    $p = @{
        Uri                = ($BaseUrl + $Path)
        Method             = $Method
        Headers            = $headers
        TimeoutSec         = $TimeoutSec
        SkipHttpErrorCheck = $true
    }
    if ($Method -ne 'GET') {
        $p['ContentType'] = 'application/json'
        $p['Body'] = $Body
    }
    try {
        $r = Invoke-WebRequest @p
        $json = $null
        try { $json = $r.Content | ConvertFrom-Json } catch { }
        return [pscustomobject]@{
            TransportOk = $true
            Status      = [int]$r.StatusCode
            Body        = [string]$r.Content
            Json        = $json
            Replayed    = [bool]($null -ne $r.Headers['X-Oni-Replayed'])
            Error       = ''
        }
    }
    catch {
        return [pscustomobject]@{
            TransportOk = $false
            Status      = 0
            Body        = ''
            Json        = $null
            Replayed    = $false
            Error       = $_.Exception.GetType().Name + ': ' + $_.Exception.Message
        }
    }
}

# ---------------- 有界重试 ----------------
$sw = [System.Diagnostics.Stopwatch]::StartNew()
$attempt = 0
$res = $null
$attempts = 0
$delays = @(400, 1200, 3000)
while ($true) {
    $attempt++
    $attempts = $attempt
    $res = Invoke-Once -Method $Method -Path $Path -Body $Body -ReqId $ReqId

    $retryable = $false
    if (-not $res.TransportOk) { $retryable = $true }          # 连不上 / 超时 ⇒ 请求没被处理完
    elseif ($res.Status -eq 503) { $retryable = $true }        # RETRY_LATER：服务端明确说"动作尚未执行"
    if (-not $retryable) { break }
    if ($attempt -gt $MaxRetries) { break }
    $d = $delays[[Math]::Min($attempt - 1, $delays.Count - 1)]
    Start-Sleep -Milliseconds $d
}
$sw.Stop()

# ---------------- 传输失败 ⇒ 对账 ----------------
$reconciled = 'not-needed'      # not-needed | executed | not-recorded | unknown
$replayBody = $null
if (-not $res.TransportOk) {
    $jr = Invoke-Once -Method GET -Path ('/v1/journal?reqId=' + [uri]::EscapeDataString($ReqId)) -Body '' -ReqId ($ReqId + '-chk')
    if ($jr.TransportOk -and $null -ne $jr.Json) {
        if ($jr.Json.data.found -eq $true) { $reconciled = 'executed'; $replayBody = $jr.Json.data.entry.body }
        else { $reconciled = 'not-recorded' }
    }
    else {
        $reconciled = 'unknown'      # 连 /v1/journal 都读不到 ⇒ 真不知道
    }
}

# ---------------- 结果 ----------------
$ok = ($res.TransportOk -and $res.Status -ge 200 -and $res.Status -lt 300)
$statusText = if ($res.TransportOk) { [string]$res.Status } else { 'TRANSPORT_ERROR' }

$out = [ordered]@{
    reqId      = $ReqId
    method     = $Method
    path       = $Path
    attempts   = $attempts
    status     = $statusText
    ok         = $ok
    replayed   = $res.Replayed
    elapsedMs  = [int]$sw.ElapsedMilliseconds
    reconciled = $reconciled
}
if (-not $res.TransportOk) { $out['transportError'] = $res.Error }
if ($res.Json -and $res.Json.ok -eq $false -and $null -ne $res.Json.error) { $out['code'] = [string]$res.Json.error.code }
if ($null -ne $replayBody) { $out['replayedBody'] = $replayBody }

$oneLine = ($out | ConvertTo-Json -Compress -Depth 5)

if ($Log) {
    $respForLog = if ($res.TransportOk) { ($res.Body -replace "`r", ' ' -replace "`n", ' ') } else { '' }
    $line = '[' + (Get-Date).ToString('yyyy-MM-ddTHH:mm:ssK') + '] CMD ' + $Method + ' ' + $Path
    $line += ' => HTTP ' + $statusText + ' | reqId=' + $ReqId + ' | attempts=' + $attempts + ' | reconciled=' + $reconciled
    if ($respForLog) { $line += ' | RESP ' + $respForLog }
    try { [System.IO.File]::AppendAllText($Log, $line + "`n", (New-Object System.Text.UTF8Encoding($false))) } catch { }
}

if ($Raw) {
    if ($res.TransportOk) { $res.Body } else { $oneLine }
}
else {
    $oneLine
}

# ---------------- 退出码 ----------------
if ($ok) { exit 0 }
if ($res.TransportOk) {
    Write-Host ('REJECTED http=' + $res.Status + ' reqId=' + $ReqId + ' —— 服务端明确拒绝，这不是"未知"') -ForegroundColor Red
    exit 2
}
switch ($reconciled) {
    'executed' {
        Write-Host ('TRANSPORT_FAILED but EXECUTED reqId=' + $ReqId + ' —— 已执行过，别再重发；结果见 replayedBody') -ForegroundColor Yellow
        exit 4
    }
    'not-recorded' {
        Write-Host ('TRANSPORT_FAILED and NOT RECORDED reqId=' + $ReqId + ' —— 服务端没有这次请求的账，可以安全重来') -ForegroundColor Yellow
        exit 3
    }
    default {
        Write-Host ('UNKNOWN reqId=' + $ReqId + ' —— 传输出错且查不到账（mod 可能没加载/账本被挤掉），不要当成成功') -ForegroundColor Red
        exit 5
    }
}
