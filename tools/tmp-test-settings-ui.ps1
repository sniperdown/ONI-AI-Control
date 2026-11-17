$ErrorActionPreference = 'Stop'
Set-Location 'H:\deepseek-harness\workspace\oni-mod'

# 准备：把真实 settings.json 复制到临时位置做测试（绝不碰真文件）
$real = Join-Path $env:USERPROFILE 'Documents\Klei\OxygenNotIncluded\mods\config\OniAiBridge\settings.json'
$tmpDir = 'H:\deepseek-harness\workspace\shared\tmp\oni-settings-test'
New-Item -ItemType Directory -Force -Path $tmpDir | Out-Null
$testCfg = Join-Path $tmpDir 'settings.json'
Get-ChildItem $tmpDir -Filter 'settings.json*' -ErrorAction SilentlyContinue | Remove-Item -Force
if (Test-Path $real) { Copy-Item $real $testCfg -Force } else { throw "真实配置不存在: $real" }
Write-Output ("测试配置: " + $testCfg)
Write-Output ("初始内容:"); Get-Content $testCfg | ForEach-Object { "  " + $_ }

Write-Output "`n=== node 版本 ==="
node --version

Write-Output "`n=== 启动服务（端口 8779，配置指向临时副本）==="
$job = Start-Job -ScriptBlock {
    param($dir, $cfg, $port)
    Set-Location $dir
    node tools\settings-ui\server.mjs --config $cfg --port $port 2>&1
} -ArgumentList (Get-Location).Path, $testCfg, 8779
Start-Sleep -Seconds 2
Receive-Job $job | ForEach-Object { "  " + $_ }

Write-Output "`n=== GET /api/settings ==="
$r1 = Invoke-WebRequest -Uri 'http://127.0.0.1:8779/api/settings' -UseBasicParsing
$r1.Content | Out-String -Width 120

Write-Output "`n=== PUT 修改 alertStress=42, amountEpsilon=0.25 ==="
$body = '{"alertStress":42,"amountEpsilon":0.25}'
$r2 = Invoke-WebRequest -Uri 'http://127.0.0.1:8779/api/settings' -Method Put -Body $body -ContentType 'application/json' -UseBasicParsing
$r2.Content | Out-String -Width 120

Write-Output "`n=== 写后文件内容（应只有这两个字段变）==="
Get-Content $testCfg | ForEach-Object { "  " + $_ }
Write-Output ("备份文件: " + ((Get-ChildItem $tmpDir -Filter 'settings.json.bak-*' | Select-Object -First 1).Name))

Write-Output "`n=== PUT token（应 400 FIELD_READONLY）==="
try { Invoke-WebRequest -Uri 'http://127.0.0.1:8779/api/settings' -Method Put -Body '{"token":"hack"}' -ContentType 'application/json' -UseBasicParsing | Out-Null } catch {
    $resp = $_.Exception.Response
    Write-Output ("  HTTP " + [int]$resp.StatusCode)
    $sr = New-Object System.IO.StreamReader($resp.GetResponseStream()); Write-Output ("  " + $sr.ReadToEnd())
}

Write-Output "`n=== PUT port=99999（应被夹到 65535 并给 notes）==="
$r3 = Invoke-WebRequest -Uri 'http://127.0.0.1:8779/api/settings' -Method Put -Body '{"port":99999}' -ContentType 'application/json' -UseBasicParsing
$r3.Content | Out-String -Width 120

Write-Output "`n=== GET / （HTML）==="
$r4 = Invoke-WebRequest -Uri 'http://127.0.0.1:8779/' -UseBasicParsing
Write-Output ("  status=" + $r4.StatusCode + " contentType=" + $r4.Headers['Content-Type'] + " bytes=" + $r4.Content.Length)
Write-Output ("  含标题: " + ($r4.Content -match 'ONI AI Bridge · 配置'))
Write-Output ("  含重启提示: " + ($r4.Content -match '改完需要重启游戏'))
Write-Output ("  外链数量: " + ([regex]::Matches($r4.Content, 'https?://')).Count)

Stop-Job $job -ErrorAction SilentlyContinue
Remove-Job $job -Force -ErrorAction SilentlyContinue
Write-Output "`n=== 已停止服务 ==="
