# enable-mod.ps1 — 把本 mod 在 ONI 的 mods.json 里设为 enabled=true（带备份与回读校验）
#
# 为什么要这个脚本：ONI 在"上次会话里出过错的 mod"名单里记账，下次退出时会把该 mod 的
# `enabled` 写回 false（M4.2k 起因是我们误用了 Debug.LogError，见 DESIGN §10.6h）。
# 每次装完新包都应核一眼，false 就用这个脚本改回 true，否则游戏里根本不会加载本 mod。
#
# ⚠️ 定位锚点必须是 `"staticID": "OniAiBridge"`（在外层对象里）；
#    `"id": "OniAiBridge"` 在 `label` 子对象里，用它会把层级认错（踩过）。
param(
    [string]$StaticId = 'OniAiBridge'
)

$ErrorActionPreference = 'Stop'
$dir = Join-Path $env:USERPROFILE 'Documents\Klei\OxygenNotIncluded\mods'
$j = Join-Path $dir 'mods.json'
if (-not (Test-Path $j)) { throw "找不到 $j" }

# ⚠️ 游戏运行中改这个文件是**无效**的：ONI 会把 mods.json 在内存里维护，退出时整份重写，
#    我们写进去的 true 会被它内存里的 false 覆盖（实测：18:21 改成 true，游戏退出后又变回 false）。
$proc = Get-Process -Name OxygenNotIncluded -ErrorAction SilentlyContinue
if ($proc) {
    Write-Output ("REFUSED: 游戏正在运行（PID " + $proc.Id + "），改 mods.json 会被退出时覆盖。请先退出游戏。")
    exit 2
}

$raw = [System.IO.File]::ReadAllText($j)

function Get-EnclosingObject([string]$text, [string]$marker) {
    $idx = $text.IndexOf($marker)
    if ($idx -lt 0) { throw "找不到 $marker" }
    $depth = 0; $open = -1
    for ($i = $idx - 1; $i -ge 0; $i--) {
        $c = $text[$i]
        if ($c -eq '}') { $depth++ }
        elseif ($c -eq '{') { if ($depth -eq 0) { $open = $i; break } else { $depth-- } }
    }
    if ($open -lt 0) { throw '找不到外层对象起点' }
    $depth = 0; $close = -1
    for ($i = $open; $i -lt $text.Length; $i++) {
        $c = $text[$i]
        if ($c -eq '{') { $depth++ } elseif ($c -eq '}') { $depth--; if ($depth -eq 0) { $close = $i; break } }
    }
    if ($close -lt 0) { throw '找不到外层对象终点' }
    return @{ Start = $open; End = $close; Text = $text.Substring($open, $close - $open + 1) }
}

$marker = '"staticID": "' + $StaticId + '"'
$obj = Get-EnclosingObject $raw $marker
$enabled = [regex]::Match($obj.Text, '"enabled":\s*(true|false)').Groups[1].Value
$crash = [regex]::Match($obj.Text, '"crash_count":\s*(\d+)').Groups[1].Value
Write-Output ("改前: enabled=" + $enabled + " crash_count=" + $crash)

if ($enabled -eq 'true') {
    Write-Output '已经是 true，不写'
} else {
    $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    $bak = Join-Path $dir ("mods.json.bak-" + $stamp)
    [System.IO.File]::WriteAllText($bak, $raw, (New-Object System.Text.UTF8Encoding($false)))
    Write-Output ("备份: " + (Split-Path $bak -Leaf))

    $newText = [regex]::Replace($obj.Text, '"enabled":\s*false', '"enabled": true')
    $out = $raw.Substring(0, $obj.Start) + $newText + $raw.Substring($obj.End + 1)
    [System.IO.File]::WriteAllText($j, $out, (New-Object System.Text.UTF8Encoding($false)))
    Write-Output '已写入 enabled: true'
}

# 回读校验
$check = [System.IO.File]::ReadAllText($j)
$obj2 = Get-EnclosingObject $check $marker
$en2 = [regex]::Match($obj2.Text, '"enabled":\s*(true|false)').Groups[1].Value
$cr2 = [regex]::Match($obj2.Text, '"crash_count":\s*(\d+)').Groups[1].Value
Write-Output ("改后: enabled=" + $en2 + " crash_count=" + $cr2)
$ok = 'OK'
try { $null = $check | ConvertFrom-Json } catch { $ok = '解析失败: ' + $_.Exception.Message }
Write-Output ("JSON 合法性: " + $ok)
