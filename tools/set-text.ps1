# set-text.ps1 — 安全地做"文本替换"：替换后立刻回读校验，MISS 直接 fail fast。
#
# 为什么有这个脚本（一次真实事故）：
#   给 CommandsJson() 插入作弊 op 列表时，锚点没匹配上，PowerShell 的 .Replace()
#   在"找不到"时**静默返回原串**，脚本继续跑、编译照样通过，于是"6 条命令都在"这个
#   结论被报了出去，而实际一条都没加进去。
#   ⇒ 规矩：凡是用文本替换/正则改代码的地方，替换后必须校验目标内容存在；
#      不存在就立刻非零退出，不允许继续往下走。
#
# 用法：
#   pwsh -File set-text.ps1 -Path <file> -Old <旧文本> -New <新文本>            # 替换 + 校验
#   pwsh -File set-text.ps1 -Path <file> -Old <旧文本> -New <新文本> -Expect <应出现的新片段>
#   多个文件/多处替换请在一个脚本里调用本函数（见 -AsFunction 用法），或逐个调用本脚本。
param(
    [Parameter(Mandatory = $true)][string]$Path,
    [Parameter(Mandatory = $true)][string]$Old,
    [Parameter(Mandatory = $true)][string]$New,
    [string]$Expect = '',
    [switch]$AllowMultiple
)

$ErrorActionPreference = 'Stop'
if (-not (Test-Path $Path)) { throw "MISS: 文件不存在 $Path" }
$full = (Resolve-Path $Path).Path
$text = [System.IO.File]::ReadAllText($full)

$count = ([regex]::Matches($text, [regex]::Escape($Old))).Count
if ($count -eq 0) { throw "MISS: 锚点未找到（未做任何修改）：$Path`n--- 锚点前 200 字符 ---`n" + $Old.Substring(0, [Math]::Min(200, $Old.Length)) }
if ($count -gt 1 -and -not $AllowMultiple) { throw "MISS: 锚点命中 $count 次（要求唯一）：$Path" }

$newText = $text.Replace($Old, $New)
[System.IO.File]::WriteAllText($full, $newText, (New-Object System.Text.UTF8Encoding($false)))

# 回读校验：新内容必须真的落盘
$check = [System.IO.File]::ReadAllText($full)
$probe = if ($Expect) { $Expect } else { $New }
if (-not $check.Contains($probe)) { throw "MISS: 替换后回读校验失败（$Path）" }
if ($check.Contains($Old) -and -not $New.Contains($Old)) { throw "MISS: 旧内容仍在（$Path）" }

Write-Output "OK   $([System.IO.Path]::GetFileName($Path))  命中 $count 处，已回读校验"
