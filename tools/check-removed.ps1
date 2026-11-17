# check-removed.ps1 —— "删除项残留检查"（build.ps1 末尾自动调用）
#
# 目的：**删掉的功能要能被证明真的没了**。只删代码不算证据，要在产物里查残留。
#
# 两张清单，判据严格分开（这是纪律，别混）：
#   A. 已删除的【界面文案】—— 残留必须为 0（用户不该再看到）
#   B. 有意保留的【代码路径/日志】—— 允许存在（回退用；删了反而退不回去）
#   C. 源码层面的【行创建调用】—— 不应再出现
#
# ⚠️ 为什么 A 类不放 "Debug 模式"：这个词在 `/v1/commands` 的 debug op 说明和
#    `Gate.UnavailableReason()` 的文案里是**合法存在**的；在 dll 里搜词必然误报。
#    判据应该是"设置页是否还在创建那一行" ⇒ 放到 C 类用源码模式匹配。
#
# 用法：
#   pwsh -File tools/check-removed.ps1                       # 检查 dist\OniAiBridge\OniAiBridge.dll
#   pwsh -File tools/check-removed.ps1 -Dll <path> -Src <dir>
# 退出码：0 = 全部通过；1 = 有 A 类残留（会阻断打包）
param(
    [string]$Dll = 'dist\OniAiBridge\OniAiBridge.dll',
    [string]$Src = 'src\OniAiBridge'
)

$ErrorActionPreference = 'Stop'

# ---------------- A. 已删除的界面文案：残留必须为 0 ----------------
$MustBeZero = @(
    @{ Text = '沙盒(存档)';       Why = 'M4.4 删除的只读行' },
    @{ Text = '沙盒(运行时)';     Why = 'M4.4 删除的只读行' },

    @{ Text = '作弊闸门';         Why = 'M4.4 删除的只读行' },
    @{ Text = '勾选立刻写盘';     Why = 'M4.4 删除的底部提示句' },
    @{ Text = '下一个字段';       Why = 'M4.4 下线的按钮' },
    @{ Text = '“当前字段：';      Why = 'M4.4 去掉的选中态文案（前缀）' }
)

# ---------------- B. 有意保留的代码路径：允许存在 ----------------
$AllowedKept = @(
    @{ Text = 'AdjustStep 已下线'; Why = 'M4.4 保留的加减按钮实现（未调用，便于回退）' },
    @{ Text = 'CycleField 已下线'; Why = 'M4.4 保留的切换字段实现（未调用，便于回退）' }
)

# ---------------- 源码层面的检查（dll 里单字符文案不好查） ----------------
$SourceForbidden = @(
    @{ Pattern = 'AddOption\("−';           Why = '[−] 按钮已下线，不应再创建' },
    @{ Pattern = 'AddOption\("\+';          Why = '[+] 按钮已下线，不应再创建' },
    @{ Pattern = 'AddOption\("下一个字段';   Why = '“下一个字段”按钮已下线' },
    # 下面几条用"源码里的行创建调用"判断，而不是在 dll 里搜词：
    # `Debug 模式` / `沙盒` 这类词在 API 说明与闸门文案里是**合法存在**的（/v1/commands、Gate.UnavailableReason），
    # 直接搜词会误报。判据应该是"设置页是否还在创建那一行"。
    @{ Pattern = 'L\("Debug 模式"';         Why = '设置页的 Debug 模式行应已删除' },
    @{ Pattern = 'L\("沙盒\(存档\)"';         Why = '设置页的沙盒(存档)行应已删除' },
    @{ Pattern = 'L\("沙盒\(运行时\)"';       Why = '设置页的沙盒(运行时)行应已删除' },
    @{ Pattern = 'L\("作弊闸门"';            Why = '设置页的作弊闸门行应已删除' },
    @{ Pattern = '勾选立刻写盘';             Why = '设置页底部提示句应已删除' }
)

if (-not (Test-Path $Dll)) { throw "找不到 dll：$Dll（先跑 build.ps1 的构建步骤）" }

# dll 里的字符串常量是 UTF-16，且奇偶两种对齐都可能出现 ⇒ 两种都解一遍再查
$bytes = [System.IO.File]::ReadAllBytes((Resolve-Path $Dll))
$u0 = [System.Text.Encoding]::Unicode.GetString($bytes)
$u1 = [System.Text.Encoding]::Unicode.GetString($bytes, 1, $bytes.Length - 1)

function Count-InDll([string]$needle) {
    return ([regex]::Matches($u0, [regex]::Escape($needle))).Count +
           ([regex]::Matches($u1, [regex]::Escape($needle))).Count
}

Write-Output ("=== 删除项残留检查: " + (Split-Path $Dll -Leaf) + " (" + $bytes.Length + " B) ===")

$fail = 0
Write-Output "--- A. 已删除的界面文案（残留必须为 0）---"
foreach ($item in $MustBeZero) {
    $n = Count-InDll $item.Text
    $state = if ($n -eq 0) { 'OK' } else { 'FAIL'; }
    if ($n -ne 0) { $fail++ }
    "{0,-6} {1,-16} 残留 {2,-3} {3}" -f $state, $item.Text, $n, $item.Why | Write-Output
}

Write-Output "--- B. 有意保留的代码路径（允许存在）---"
foreach ($item in $AllowedKept) {
    $n = Count-InDll $item.Text
    $state = if ($n -gt 0) { 'KEPT' } else { 'GONE' }
    "{0,-6} {1,-22} 出现 {2,-3} {3}" -f $state, $item.Text, $n, $item.Why | Write-Output
}

Write-Output "--- C. 源码层面（界面创建调用不应再出现）---"
$srcFiles = Get-ChildItem -Path $Src -Filter *.cs -File -ErrorAction SilentlyContinue
foreach ($item in $SourceForbidden) {
    $hit = @()
    foreach ($f in $srcFiles) {
        $text = [System.IO.File]::ReadAllText($f.FullName)
        if ($text -match $item.Pattern) { $hit += $f.Name }
    }
    if ($hit.Count -eq 0) { "OK     {0,-24} {1}" -f $item.Pattern, $item.Why | Write-Output }
    else { "FAIL   {0,-24} 出现在: {1}  ({2})" -f $item.Pattern, ($hit -join ', '), $item.Why | Write-Output; $fail++ }
}

if ($fail -gt 0) {
    Write-Output ("=== 失败：有 " + $fail + " 项删除残留（A/C 类）===")
    exit 1
}
Write-Output "=== 通过：界面文案零残留，保留项符合预期 ==="
exit 0
