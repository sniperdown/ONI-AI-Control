# 只读轮询：扫农场格找成熟植物，遇到"熟且未标"就立刻收一次（拿干净样本）
$ErrorActionPreference = 'Continue'
$h = @{'X-Oni-Token' = '7eabe0d8bce9447da6d8aef7aaa018f1'}
$base = 'http://127.0.0.1:8770'
$log = 'H:\deepseek-harness\workspace\oni-mod\logs\ripe-watch.log'
$dir = Split-Path $log -Parent
if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }

function L($m) {
  $line = "[{0}] {1}" -f (Get-Date -Format 'HH:mm:ss'), $m
  Add-Content -Path $log -Value $line -Encoding UTF8
}

# 候选格：农场类建筑上方一格（从导出文件读）
$all = Import-Clixml 'H:\deepseek-harness\workspace\oni-mod\buildings-big.xml'
$farms = $all | Where-Object { $_.prefabId -in @('HydroponicFarm', 'FarmTile', 'PlanterBox') }
$cells = New-Object System.Collections.ArrayList
$seen = @{}
foreach ($f in $farms) {
  $x = [int]$f.x; $y = [int]$f.y + 1
  $k = "$x,$y"
  if ($seen.ContainsKey($k)) { continue }
  $seen[$k] = 1
  [void]$cells.Add(@($x, $y))
}
L "启动：候选格 $($cells.Count) 个，每轮只读扫一遍"

$round = 0
while ($true) {
  $round++
  # 游戏没跑就等
  try { $st = (Invoke-RestMethod -Uri "$base/v1/state" -Headers $h -TimeoutSec 20).data }
  catch { L "第 $round 轮：state 取不到，睡 60s"; Start-Sleep -Seconds 60; continue }
  if (-not $st.colony) { L "第 $round 轮：还没进档，睡 60s"; Start-Sleep -Seconds 60; continue }

  $ripeUnmarked = New-Object System.Collections.ArrayList
  $ripeMarked = 0
  $err = 0
  foreach ($c in $cells) {
    $x = $c[0]; $y = $c[1]
    try {
      $m = (Invoke-RestMethod -Uri "$base/v1/marks?worldId=0&x=$x&y=$y" -Headers $h -TimeoutSec 10).data
      $hv = $m.harvest
      if ($null -eq $hv) { continue }
      if ($hv.readyToHarvest -eq $true) {
        if ($hv.marked -eq $true) { $ripeMarked++ }
        else { [void]$ripeUnmarked.Add([pscustomobject]@{ x = $x; y = $y; name = $hv.objectName; count = $hv.count }) }
      }
    }
    catch { $err++ }
  }

  L "第 $round 轮 cycle=$($st.colony.cycle) paused=$($st.colony.isPaused) | 熟且已标=$ripeMarked | 熟且未标=$($ripeUnmarked.Count) | 出错=$err"

  # 找到"熟且未标"的 => 立刻收一次，这是干净样本
  foreach ($p in $ripeUnmarked) {
    $body = @{ type = 'harvest'; worldId = 0; x = $p.x; y = $p.y } | ConvertTo-Json -Compress
    $tmp = [System.IO.Path]::GetTempFileName()
    [System.IO.File]::WriteAllText($tmp, $body, [System.Text.UTF8Encoding]::new($false))
    try {
      $r = & curl.exe -s -m 30 -X POST "$base/v1/job" -H "X-Oni-Token: $($h['X-Oni-Token'])" -H 'Content-Type: application/json' --data-binary "@$tmp" | ConvertFrom-Json
      if ($r.ok) {
        L ("★ 干净样本收成 ($($p.x),$($p.y)) $($p.name)  alreadyMarkedBefore=$($r.data.alreadyMarkedBefore) choreCreated=$($r.data.choreCreated) hasHarvestable=$($r.data.hasHarvestable) shortCircuited=$($r.data.shortCircuited)")
        if ($r.data.alreadyMarkedBefore -eq $false -and $r.data.choreCreated -eq $true) {
          L "★★★ 拿到单次观察级硬证据：alreadyMarkedBefore=false 且 choreCreated=true"
        }
      }
      else { L "($($p.x),$($p.y)) $($r.error.code) $($r.error.message)" }
    }
    catch { L "($($p.x),$($p.y)) 请求失败" }
    Remove-Item $tmp -Force -ErrorAction SilentlyContinue
  }

  Start-Sleep -Seconds 90
}
