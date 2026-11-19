#requires -Version 7.0
<#
bump-version.ps1 — 一条命令改完三处版本号（+ 可选 stage），并回读校验。

为什么有这个脚本：
  以前每版都要手工改三处（mod_info.yaml / csproj / Bridge.cs），改漏了就出现
  "yaml 是 0.93.6、Bridge 还停在 0.93.5"；stage 也脱节过两版（0.93.5 时才发现停在 M6.85）。
  build.ps1 只能"事后发现不一致"，这个脚本让"不一致"不会发生。

改哪三处（与 build.ps1 的校验一一对应）：
  mod\mod_info.yaml                  version: X
  src\OniAiBridge\OniAiBridge.csproj <Version>X</Version>
  src\OniAiBridge\Bridge.cs          public const string ModVersion = "X";
  （可选）-Stage M7.4 还会改 Bridge.cs 的 public const string Stage = "M7.4";

用法：
  pwsh -File tools/bump-version.ps1 -Version 0.96.0
  pwsh -File tools/bump-version.ps1 -Version 0.96.0 -Stage M7.4
  pwsh -File tools/bump-version.ps1 -Version 0.96.0 -DryRun     # 只看要改什么，不写盘
  pwsh -File tools/bump-version.ps1 -Version 0.95.1 -Force      # 允许改成"不比当前大"的版本

安全措施：
  * 每个模式必须**恰好命中一次**，否则立刻失败（不猜、不批量替换）；
  * 只替换捕获组 1，不重写整段匹配（否则会把 `version: ` 这类前缀一起吃掉）；
  * 替换用 MatchEvaluator（不把任意文本当 replacement，避免 $ 展开）；
  * 写盘后逐处回读，值不对就抛错，并把三个文件**整体回滚**；
  * 保留原文件的 BOM 状态与其余字节。

退出码：0 = 成功（或 DryRun 通过）；非 0 = 失败。
注意：本脚本**不碰文档**。docs/AI-GUIDE.md 头部、README 的阶段号仍需人工同步。
#>
param(
    [Parameter(Mandatory = $true)][string]$Version,
    [string]$Stage = '',
    [string]$Root = '',
    [switch]$Force,
    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'

if (-not $Root) { $Root = Split-Path $PSScriptRoot -Parent }
if (-not (Test-Path $Root)) { throw "root not found: $Root" }
$Root = (Resolve-Path $Root).Path

$yamlPath = Join-Path $Root 'mod\mod_info.yaml'
$projPath = Join-Path $Root 'src\OniAiBridge\OniAiBridge.csproj'
$codePath = Join-Path $Root 'src\OniAiBridge\Bridge.cs'
$allPaths = @($yamlPath, $projPath, $codePath)
foreach ($f in $allPaths) {
    if (-not (Test-Path $f)) { throw "missing file: $f" }
}

if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw "版本号格式应为 0.96.0（收到：$Version）" }
try { $vNew = [version]$Version } catch { throw "版本号无法解析：$Version" }

$yamlText = [System.IO.File]::ReadAllText($yamlPath)
$m = [regex]::Match($yamlText, '(?m)^version:\s*(\S+)')
if (-not $m.Success) { throw "读不到当前版本（$yamlPath）" }
$vCur = $m.Groups[1].Value
try { $vCurObj = [version]$vCur } catch { throw "当前版本无法解析：$vCur" }

if ($vNew -le $vCurObj -and -not $Force) {
    throw "新版本 $Version 不比当前 $vCur 大（要强制就加 -Force）"
}
if ($Stage -and $Stage -notmatch '^M\d+(\.\d+)?$') { throw "stage 格式应为 M7.4（收到：$Stage）" }

$suffix = if ($Stage) { "   stage -> $Stage" } else { '' }
Write-Host "bump: $vCur -> $Version$suffix"
if ($DryRun) { Write-Host 'DRY-RUN: nothing will be written' }

function Get-TextAndEncoding {
    param([string]$Path)
    $bytes = [System.IO.File]::ReadAllBytes($Path)
    $hasBom = ($bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF)
    return [pscustomobject]@{
        Text = [System.IO.File]::ReadAllText($Path)
        Enc  = New-Object System.Text.UTF8Encoding($hasBom)
    }
}

function Set-Site {
    param(
        [string]$Path,
        [string]$Pattern,      # 必须有一个捕获组，且组 1 是"要被换掉的值"
        [string]$NewValue,
        [string]$Label
    )
    $text = [System.IO.File]::ReadAllText($Path)
    $hits = [regex]::Matches($text, $Pattern)
    if ($hits.Count -ne 1) {
        throw "[$Label] 模式必须恰好命中一次，实际 $($hits.Count) 次（$Path）"
    }
    $oldValue = $hits[0].Groups[1].Value
    if ($oldValue -eq $NewValue) {
        Write-Host "  SKIP  $Label  (already $NewValue)"
        return
    }

    # 只替换捕获组 1，保留匹配里的前后缀（例如 `version: `、`<Version>`、`ModVersion = "`）
    $newText = [regex]::Replace($text, $Pattern, {
            param($mm)
            $g = $mm.Groups[1]
            $prefixLen = $g.Index - $mm.Index
            $mm.Value.Substring(0, $prefixLen) + $NewValue + $mm.Value.Substring($prefixLen + $g.Length)
        }.GetNewClosure())

    if ($DryRun) {
        Write-Host "  [dry] $Label : '$oldValue' -> '$NewValue'"
        return
    }

    $enc = (Get-TextAndEncoding -Path $Path).Enc
    [System.IO.File]::WriteAllText($Path, $newText, $enc)

    # 回读校验：同一模式必须仍然恰好命中一次，且组 1 等于新值
    $check = [regex]::Matches([System.IO.File]::ReadAllText($Path), $Pattern)
    if ($check.Count -ne 1 -or $check[0].Groups[1].Value -ne $NewValue) {
        throw "[$Label] 回读校验失败（$Path）"
    }
    Write-Host "  OK    $Label : '$oldValue' -> '$NewValue'"
}

# 先快照，任何一步失败就整体回滚（不留半改状态）
$snapshot = @{}
if (-not $DryRun) {
    foreach ($p in $allPaths) { $snapshot[$p] = Get-TextAndEncoding -Path $p }
}

try {
    Set-Site -Path $yamlPath -Pattern '(?m)^version:\s*(\S+)' -NewValue $Version -Label 'mod_info.yaml'
    Set-Site -Path $projPath -Pattern '<Version>([^<]+)</Version>' -NewValue $Version -Label 'OniAiBridge.csproj'
    Set-Site -Path $codePath -Pattern 'ModVersion = "([^"]+)"' -NewValue $Version -Label 'Bridge.cs ModVersion'
    if ($Stage) {
        Set-Site -Path $codePath -Pattern 'Stage = "([^"]+)"' -NewValue $Stage -Label 'Bridge.cs Stage'
    }
}
catch {
    if (-not $DryRun) {
        foreach ($p in $allPaths) {
            [System.IO.File]::WriteAllText($p, $snapshot[$p].Text, $snapshot[$p].Enc)
        }
        Write-Host 'ROLLBACK: three files restored to the pre-bump content' -ForegroundColor Yellow
    }
    throw
}

if ($DryRun) {
    Write-Host 'DRY-RUN complete (no files changed)'
    exit 0
}

# 三处一致性自检（与 build.ps1 同口径）
$vYaml = ([regex]::Match([System.IO.File]::ReadAllText($yamlPath), '(?m)^version:\s*(\S+)')).Groups[1].Value
$vProj = ([regex]::Match([System.IO.File]::ReadAllText($projPath), '<Version>([^<]+)</Version>')).Groups[1].Value
$vCode = ([regex]::Match([System.IO.File]::ReadAllText($codePath), 'ModVersion = "([^"]+)"')).Groups[1].Value
if ($vYaml -ne $vProj -or $vYaml -ne $vCode) {
    throw "三处版本号不一致 yaml=$vYaml csproj=$vProj Bridge=$vCode"
}
Write-Host "OK: 三处版本号一致 = $vYaml"
Write-Host 'NOTE: docs/AI-GUIDE.md 头部与 README 里的版本/阶段号仍需人工同步（本脚本不碰文档）'
exit 0
