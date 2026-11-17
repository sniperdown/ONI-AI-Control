$ErrorActionPreference = 'Stop'
Set-Location 'H:\deepseek-harness\workspace\oni-mod'

$b = Get-Content 'src\OniAiBridge\Bridge.cs' -Raw
$b = $b.Replace('public const string ModVersion = "0.27.0";', 'public const string ModVersion = "0.28.0";')
$b = $b.Replace('public const string Stage = "M4.3c2";', 'public const string Stage = "M4.4";')
[System.IO.File]::WriteAllText((Join-Path (Get-Location) 'src\OniAiBridge\Bridge.cs'), $b, (New-Object System.Text.UTF8Encoding($false)))

$j = Get-Content 'src\OniAiBridge\OniAiBridge.csproj' -Raw
$j = $j.Replace('<Version>0.27.0</Version>', '<Version>0.28.0</Version>')
[System.IO.File]::WriteAllText((Join-Path (Get-Location) 'src\OniAiBridge\OniAiBridge.csproj'), $j, (New-Object System.Text.UTF8Encoding($false)))

$mi = Get-Content 'mod\mod_info.yaml' -Raw
$mi = $mi.Replace('version: 0.27.0', 'version: 0.28.0')
[System.IO.File]::WriteAllText((Join-Path (Get-Location) 'mod\mod_info.yaml'), $mi, (New-Object System.Text.UTF8Encoding($false)))

$env:ONI_GAME_DIR = 'F:\SteamLibrary\steamapps\common\OxygenNotIncluded'
& dotnet build .\src\OniAiBridge\OniAiBridge.csproj -c Release -o .\dist\_probe --nologo "-p:OniGameDir=$env:ONI_GAME_DIR" 2>&1 |
    Select-String -Pattern 'error CS|已成功' | Select-Object -First 12 | Out-String -Width 200 | Write-Output
$code = $LASTEXITCODE
Remove-Item 'dist\_probe' -Recurse -Force -ErrorAction SilentlyContinue
if ($code -ne 0) { throw 'build failed' }

& pwsh -File .\build.ps1 2>&1 | Select-String -Pattern 'error|OK:' | Out-String -Width 150 | Write-Output

$p = 'dist\OniAiBridge'
Get-ChildItem $p -File | ForEach-Object { "{0,-18} {1,7}" -f $_.Name, $_.Length } | Write-Output
(Select-String -Path "$p\mod_info.yaml" -Pattern 'version:').Line | Write-Output
$dll = Join-Path (Resolve-Path $p) 'OniAiBridge.dll'
(Get-FileHash $dll -Algorithm SHA256).Hash | Write-Output

$x = [System.IO.File]::ReadAllBytes($dll)
$u0 = [System.Text.Encoding]::Unicode.GetString($x)
$u1 = [System.Text.Encoding]::Unicode.GetString($x, 1, $x.Length - 1)
foreach ($k in @('ONI AI Bridge · 配置', 'FIELD_READONLY', 'NO_FIELDS', '[settings-api] PUT 已应用', 'M4.4')) {
    $n = ([regex]::Matches($u0, [regex]::Escape($k))).Count + ([regex]::Matches($u1, [regex]::Escape($k))).Count
    "{0,-30} {1} 次" -f $k, $n | Write-Output
}
