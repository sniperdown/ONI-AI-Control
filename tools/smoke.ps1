#requires -Version 7.0
<#
smoke.ps1 — OniAiBridge 只读冒烟自检（每版装机后跑一次，红了就别当验收）

做什么：只发 GET 和"必定被拒"的 POST，不改变游戏状态。
检查什么：
  1) 连通性与主线程泵     2) 装的是不是这份构建（版本对不上 = 装错了 dll）
  3) 三个"对数等式"        4) 鉴权门 / 通道闸门 / 未知 type 的拒绝语义

用法：
  pwsh -File tools/smoke.ps1
  pwsh -File tools/smoke.ps1 -BaseUrl http://127.0.0.1:8770
  pwsh -File tools/smoke.ps1 -Token <32位十六进制>          # 不读 settings.json
  pwsh -File tools/smoke.ps1 -SettingsPath <settings.json>
  pwsh -File tools/smoke.ps1 -NoVersionCheck                 # 跳过"装机版本 vs 仓库版本"

退出码：
  0 = 全过    1 = 有失败    2 = 连不上（游戏没开 / mod 没加载 / 泵没起）

约定：输出只用 ASCII（Windows 控制台是 GBK，中文/emoji 会乱码）。
#>
param(
    [string]$BaseUrl = 'http://127.0.0.1:8770',
    [string]$Token = '',
    [string]$SettingsPath = '',
    [int]$TimeoutSec = 20,
    [switch]$NoVersionCheck
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

# ---------------- token ----------------
if (-not $Token) {
    if (-not $SettingsPath) {
        $SettingsPath = Join-Path $env:USERPROFILE 'Documents\Klei\OxygenNotIncluded\mods\config\OniAiBridge\settings.json'
    }
    if (-not (Test-Path $SettingsPath)) {
        Write-Host "FAIL  setup: settings.json not found: $SettingsPath"
        exit 1
    }
    try {
        $Token = [string](Get-Content -Raw $SettingsPath | ConvertFrom-Json).token
    }
    catch {
        Write-Host "FAIL  setup: cannot parse settings.json: $($_.Exception.GetType().Name)"
        exit 1
    }
}
if ([string]::IsNullOrWhiteSpace($Token) -or $Token.Length -lt 8) {
    Write-Host "FAIL  setup: token missing or too short"
    exit 1
}
$script:Token = $Token

Write-Host ''
Write-Host "OniAiBridge smoke  base=$BaseUrl  token=$($Token.Length) chars  timeout=${TimeoutSec}s"
Write-Host ''

# ---------------- helpers ----------------
$script:Pass = 0
$script:Fail = 0

function Check {
    param([string]$Name, [bool]$Ok, [string]$Detail = '')
    if ($Ok) {
        $script:Pass++
        Write-Host ("PASS  {0,-38} {1}" -f $Name, $Detail)
    }
    else {
        $script:Fail++
        Write-Host ("FAIL  {0,-38} {1}" -f $Name, $Detail) -ForegroundColor Red
    }
}

function Info {
    param([string]$Text)
    Write-Host ("      " + $Text) -ForegroundColor DarkGray
}

function Invoke-Oni {
    param(
        [string]$Path,
        [string]$Method = 'GET',
        $Body = $null,
        [switch]$NoAuth
    )
    $headers = @{}
    if (-not $NoAuth) { $headers['X-Oni-Token'] = $script:Token }
    $request = @{
        Uri               = ($BaseUrl + $Path)
        Method            = $Method
        Headers           = $headers
        TimeoutSec        = $TimeoutSec
        SkipHttpErrorCheck = $true
    }
    if ($null -ne $Body) {
        $request['ContentType'] = 'application/json'
        $request['Body'] = ($Body | ConvertTo-Json -Compress -Depth 8)
    }
    $resp = Invoke-WebRequest @request
    $json = $null
    try { $json = $resp.Content | ConvertFrom-Json } catch { }
    return [pscustomobject]@{ Status = [int]$resp.StatusCode; Json = $json }
}

# ---------------- 连通性（失败 = 2） ----------------
try {
    $health = Invoke-Oni -Path '/v1/health'
}
catch {
    $kind = $_.Exception.GetType().Name
    Write-Host "NOT_RUNNING  cannot reach $BaseUrl  ($kind)" -ForegroundColor Yellow
    Write-Host '              start the game and load a save first (the mod only listens while the game runs)'
    exit 2
}

Check 'health: HTTP 200 + ok:true' ($health.Status -eq 200 -and $health.Json.ok -eq $true) "http=$($health.Status)"
Check 'health: data.alive' ($health.Json.data.alive -eq $true) "pumpReady=$($health.Json.data.pumpReady) stage=$($health.Json.data.stage)"
if ($health.Json.data.pumpReady -ne $true) {
    Info 'pumpReady=false -> most endpoints will answer 503 NOT_READY (still loading?)'
}

# ---------------- meta + 版本 ----------------
$meta = Invoke-Oni -Path '/v1/meta'
Check 'meta: HTTP 200 + ok:true' ($meta.Status -eq 200 -and $meta.Json.ok -eq $true) "http=$($meta.Status)"
Check 'meta: apiVersion present' ($meta.Json.ok -eq $true -and -not [string]::IsNullOrWhiteSpace([string]$meta.Json.apiVersion)) "apiVersion=$($meta.Json.apiVersion)"
$vInstalled = [string]$meta.Json.data.mod.version
Check 'meta: mod.version present' (-not [string]::IsNullOrWhiteSpace($vInstalled)) "version=$vInstalled stage=$($meta.Json.data.mod.stage) colonyLoaded=$($meta.Json.data.colonyLoaded)"

if (-not $NoVersionCheck) {
    $yaml = Join-Path (Split-Path $PSScriptRoot -Parent) 'mod\mod_info.yaml'
    if (Test-Path $yaml) {
        $m = Select-String -Path $yaml -Pattern '^version:\s*(\S+)'
        $vRepo = if ($m) { $m.Matches[0].Groups[1].Value } else { '' }
        Check 'installed build == repo version' ($vRepo -ne '' -and $vInstalled -eq $vRepo) "installed=$vInstalled repo=$vRepo"
    }
    else {
        Info "version check skipped (no $yaml)"
    }
}

# ---------------- state ----------------
$st = Invoke-Oni -Path '/v1/state'
$stItems = @($st.Json.data.dupes.items)
Check 'state: colony + dupes.items' ($st.Json.ok -eq $true -and $null -ne $st.Json.data.colony -and $stItems.Count -ge 1) "dupes=$($stItems.Count) cycle=$($st.Json.data.colony.cycle) paused=$($st.Json.data.colony.isPaused)"

# ---------------- catalog: 三处相等 ----------------
$cat = Invoke-Oni -Path '/v1/catalog'
$catLen = @($cat.Json.data.buildings).Count
$catCount = $cat.Json.data.count
$catDefs = $cat.Json.data.defsCount
Check 'catalog: count == buildings.length' ($cat.Json.ok -eq $true -and $catCount -eq $catLen) "count=$catCount len=$catLen defsCount=$catDefs"
if ($catDefs -ne $catCount) { Info "NOTE: defsCount != count (null defs?) -- not a failure" }

# ---------------- buildings: 硬等式 + 分页（0.96.0 起对命中集切片） ----------------
$bld = Invoke-Oni -Path '/v1/buildings?limit=50'
$b = $bld.Json.data
$bSum = [int]$b.count + [int]$b.skipped + [int]$b.notEmittedDueToLimit + [int]$b.filteredByFog + [int]$b.filteredInvalid + [int]$b.filteredByBox + [int]$b.filteredByPrefab
Check 'buildings: scanned == count+skip+over+fog+invalid+box+prefab' ($bld.Json.ok -eq $true -and [int]$b.scanned -eq $bSum) "scanned=$($b.scanned) sum=$bSum (count=$($b.count) skip=$($b.skipped) over=$($b.notEmittedDueToLimit) fog=$($b.filteredByFog) invalid=$($b.filteredInvalid) box=$($b.filteredByBox) prefab=$($b.filteredByPrefab))"
$bTotal = [int]$b.total
$bTotalSum = [int]$b.count + [int]$b.skipped + [int]$b.notEmittedDueToLimit
Check 'buildings: total == count+skip+over' ($bld.Json.ok -eq $true -and $bTotal -eq $bTotalSum) "total=$bTotal sum=$bTotalSum totalItems=$($b.totalItems)"
Check 'buildings: nextOffset/hasMore consistent' ($bld.Json.ok -eq $true -and (($b.hasMore -eq $true -and [int]$b.nextOffset -eq [int]$b.offset + [int]$b.count) -or ($b.hasMore -eq $false -and [int]$b.nextOffset -eq -1))) "hasMore=$($b.hasMore) nextOffset=$($b.nextOffset) offset=$($b.offset) count=$($b.count)"
$b2 = (Invoke-Oni -Path '/v1/buildings?limit=50&offset=50').Json.data
Check 'buildings: page2 same total, disjoint window' ($null -ne $b2 -and [int]$b2.total -eq $bTotal -and [int]$b2.offset -eq 50 -and [int]$b2.skipped -eq [Math]::Min(50, $bTotal)) "page2 total=$($b2.total) count=$($b2.count) offset=$($b2.offset) skipped=$($b2.skipped)"
Check 'buildings: names opt-in (default off)' ($bld.Json.ok -eq $true -and $bld.Json.data.namesIncluded -eq $false -and $null -eq @($b.buildings)[0].name) "namesIncluded=$($bld.Json.data.namesIncluded) firstHasName=$($null -ne @($b.buildings)[0].name)"

# ---------------- worlds ----------------
$w = Invoke-Oni -Path '/v1/worlds'
$wList = @($w.Json.data.worlds)
Check 'worlds: count == worlds.length' ($w.Json.ok -eq $true -and [int]$w.Json.data.count -eq $wList.Count -and $wList.Count -ge 1) "count=$($w.Json.data.count) len=$($wList.Count) activeWorldId=$($w.Json.data.activeWorldId)"

# ---------------- area: 硬等式 ----------------
$worldId = 0
if ($wList.Count -ge 1 -and $null -ne $wList[0].worldId) { $worldId = [int]$wList[0].worldId }
$area = Invoke-Oni -Path "/v1/area?worldId=$worldId&cx=0&cy=0"
$a = $area.Json.data
if ($area.Json.ok -eq $true -and $null -ne $a.cells) {
    $aSum = [int]$a.cells.mineable + [int]$a.cells.unmineable + [int]$a.cells.alreadyDigMarked
    Check 'area: mineable+unmineable+marked == solid' ($aSum -eq [int]$a.cells.solid) "worldId=$worldId cells=$($a.cells.total) solid=$($a.cells.solid) sum=$aSum (mineable=$($a.cells.mineable) unmineable=$($a.cells.unmineable) marked=$($a.cells.alreadyDigMarked))"
}
else {
    Check 'area: mineable+unmineable+marked == solid' $false "area failed: http=$($area.Status) code=$($area.Json.error.code)"
}

# ---------------- dupes ----------------
$dup = Invoke-Oni -Path '/v1/dupes'
$dList = @($dup.Json.data.dupes)
$badKind = @($dList | Where-Object { $_.kind -ne 'organic' -and $_.kind -ne 'bionic' }).Count
Check 'dupes: count == dupes.length + kind' ($dup.Json.ok -eq $true -and [int]$dup.Json.data.count -eq $dList.Count -and $badKind -eq 0) "count=$($dup.Json.data.count) len=$($dList.Count) organic=$($dup.Json.data.organic) bionic=$($dup.Json.data.bionic) badKind=$badKind"

# ---------------- ui ----------------
# 0.96.0 起这一条**收紧**：不传 `clickables=` 时模式必须是 compact（VERIFIED.md §2.1 ③ 缺的就是这条证据）。
$ui = Invoke-Oni -Path '/v1/ui?clickablesLimit=5'
$mode = [string]$ui.Json.data.clickablesMode
$uiScr = @($ui.Json.data.screens)
Check 'ui: screens + default clickablesMode=compact' ($ui.Json.ok -eq $true -and $uiScr.Count -ge 1 -and $mode -eq 'compact') "screens=$($uiScr.Count) mode=$mode"
$uiFirst = $null
foreach ($s in $uiScr) { $c = @($s.clickables); if ($c.Count -ge 1) { $uiFirst = $c[0]; break } }
$uiKeys = @()
if ($null -ne $uiFirst) { $uiKeys = @($uiFirst.PSObject.Properties.Name) }
Check 'ui: compact clickable == 4 fields incl path' ($null -ne $uiFirst -and $uiKeys.Count -eq 4 -and -not [string]::IsNullOrEmpty($uiFirst.path)) "fields=[$($uiKeys -join ',')] path=$($uiFirst.path)"

# ---------------- alerts ----------------
$al = Invoke-Oni -Path '/v1/alerts'
$alOk = ($al.Status -eq 200 -and $al.Json.ok -eq $true)
$alTrunc = ($al.Json.data.truncated -eq $true)
$alCount = $al.Json.data.counts.alerts
Check 'alerts: 200 + counts.alerts' ($alOk -and ($alTrunc -or $null -ne $alCount)) "http=$($al.Status) alerts=$alCount occurrences=$($al.Json.data.counts.occurrences) truncated=$alTrunc"

# ---------------- 拒绝语义（都不改状态） ----------------
$job = Invoke-Oni -Path '/v1/job' -Method 'POST' -Body @{ type = 'smoke_no_such_type' }
Check 'job: unknown type -> 400 NOT_IMPLEMENTED' ($job.Status -eq 400 -and $job.Json.ok -eq $false -and $job.Json.error.code -eq 'NOT_IMPLEMENTED') "http=$($job.Status) code=$($job.Json.error.code)"

$noAuth = Invoke-Oni -Path '/v1/state' -NoAuth
Check 'auth: no token -> 401' ($noAuth.Status -eq 401 -and $noAuth.Json.ok -eq $false) "http=$($noAuth.Status) code=$($noAuth.Json.error.code)"

$map = Invoke-Oni -Path '/v1/map'
Check 'map: missing bbox -> 400/403' (($map.Status -eq 400 -or $map.Status -eq 403) -and $map.Json.ok -eq $false) "http=$($map.Status) code=$($map.Json.error.code) (403 = allowMap off, 400 = on but params missing)"

# ---------------- 汇总 ----------------
Write-Host ''
if ($script:Fail -eq 0) {
    Write-Host "OK   $($script:Pass) checks passed, 0 failed" -ForegroundColor Green
    exit 0
}
else {
    Write-Host "FAIL $($script:Pass) passed, $($script:Fail) FAILED" -ForegroundColor Red
    Write-Host '     do not call this build verified until the red lines are explained' -ForegroundColor Red
    exit 1
}
