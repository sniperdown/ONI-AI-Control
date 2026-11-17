$ErrorActionPreference = 'Stop'
Set-Location 'H:\deepseek-harness\workspace\oni-mod'

# ---------- 1) Bridge：先取字符串参数，再做数字解析 ----------
$f = 'src\OniAiBridge\Bridge.cs'
$lines = [System.Collections.Generic.List[string]]([System.IO.File]::ReadAllLines((Join-Path (Get-Location) $f)))

$i = -1
for ($k = 0; $k -lt $lines.Count; $k++) { if ($lines[$k].Contains('int n;') -and $lines[$k + 1].Contains('if (!int.TryParse(v, out n)) continue;')) { $i = $k; break } }
if ($i -lt 0) { throw 'MISS 数字解析两行' }
# 在 TryParse 之前插入字符串参数分支
$lines.Insert($i + 2, '                    if (k == "prefabId") { fPrefab = v; continue; }')
[System.IO.File]::WriteAllLines((Join-Path (Get-Location) $f), $lines, (New-Object System.Text.UTF8Encoding($false)))
Write-Output ('OK Bridge：prefabId 在 TryParse 之后取（L' + ($i + 3) + '）—— 注意顺序见下方说明')

# 复核
$l2 = [System.IO.File]::ReadAllLines((Join-Path (Get-Location) $f))
for ($k = 258; $k -le 272; $k++) { 'L' + ($k + 1) + ': [' + $l2[$k] + ']' }
