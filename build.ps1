# build.ps1 — 构建 OniAiBridge 并打成"可以直接放进 mods\Local 的文件夹"。
#
# ⚠️ 本脚本【不会】往游戏目录装任何东西。它只产出 dist\OniAiBridge\，
#    安装需要在用户同意后手动复制（见脚本最后打印的命令）。
#
# 用法：
#   pwsh -File build.ps1 -GameDir "<ONI_INSTALL_DIR>"
#   $env:ONI_GAME_DIR = "<ONI_INSTALL_DIR>"; pwsh -File build.ps1
param(
    [string]$GameDir = $env:ONI_GAME_DIR,
    [string]$Configuration = 'Release',
    [string]$Out = ''
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot                                   # oni-mod\
if (-not $GameDir) { throw "缺少 -GameDir（或环境变量 ONI_GAME_DIR）。示例：-GameDir '<ONI_INSTALL_DIR>'（游戏根目录，里面有 OxygenNotIncluded_Data）" }
$managed = Join-Path $GameDir 'OxygenNotIncluded_Data\Managed'
if (-not (Test-Path (Join-Path $managed 'Assembly-CSharp.dll'))) { throw "找不到 $managed\Assembly-CSharp.dll —— GameDir 指错了" }

if (-not $Out) { $Out = Join-Path $root 'dist' }
$pkg = Join-Path $Out 'OniAiBridge'
if (Test-Path $pkg) { Remove-Item $pkg -Recurse -Force }
New-Item -ItemType Directory -Force -Path $pkg | Out-Null

Write-Output "=== 1/3 build ($Configuration) ==="
& dotnet build (Join-Path $root 'src\OniAiBridge\OniAiBridge.csproj') `
    -c $Configuration -o (Join-Path $pkg '.lib') --nologo "-p:OniGameDir=$GameDir"
if ($LASTEXITCODE -ne 0) { throw "dotnet build 失败" }

Write-Output "=== 2/3 组装 mod 文件夹 ==="
Copy-Item (Join-Path $pkg '.lib\OniAiBridge.dll') (Join-Path $pkg 'OniAiBridge.dll') -Force
if (Test-Path (Join-Path $pkg '.lib\OniAiBridge.pdb')) { Copy-Item (Join-Path $pkg '.lib\OniAiBridge.pdb') $pkg -Force }
Remove-Item (Join-Path $pkg '.lib') -Recurse -Force

# mod.yaml / mod_info.yaml 必须以 UTF-8【无 BOM】写盘（实测：带 BOM 会让 KMod 解析报
# "Found a property '<BOM>le' on a type 'KMod.KModHeader'"）——所以这里重写一遍，不用默认编码。
$noBom = New-Object System.Text.UTF8Encoding($false)
foreach ($f in @('mod.yaml', 'mod_info.yaml')) {
    $txt = Get-Content (Join-Path $root "mod\$f") -Raw
    [System.IO.File]::WriteAllText((Join-Path $pkg $f), $txt, $noBom)
}

Write-Output "=== 3/3 校验 ==="
$files = Get-ChildItem $pkg -File | Select-Object Name, Length
$files | Format-Table -AutoSize | Out-String | Write-Output
foreach ($f in @('mod.yaml', 'mod_info.yaml')) {
    $b = [System.IO.File]::ReadAllBytes((Join-Path $pkg $f))
    if ($b.Length -ge 3 -and $b[0] -eq 0xEF -and $b[1] -eq 0xBB -and $b[2] -eq 0xBF) { throw "$f 带 BOM，游戏会解析失败" }
}
# ---- 删除项残留检查（两张清单：界面文案必须为 0 / 保留的代码路径允许存在）----
Write-Output "=== 4/4 删除项残留检查（tools/check-removed.ps1）==="
& pwsh -File (Join-Path $root 'tools\check-removed.ps1') -Dll (Join-Path $pkg 'OniAiBridge.dll') -Src (Join-Path $root 'src\OniAiBridge')
if ($LASTEXITCODE -ne 0) { throw "删除项残留检查失败（界面文案还有残留，见上面 FAIL 行）" }
Write-Output "OK: $pkg （UTF-8 无 BOM 已校验）"

$local = Join-Path $env:USERPROFILE 'Documents\Klei\OxygenNotIncluded\mods\Local'
Write-Output ""
Write-Output "安装顺序（血泪教训，别改）："
Write-Output "  1) 先确认游戏已退出：Get-Process -Name OxygenNotIncluded   # 必须为空"
Write-Output "  2) **先拷 OniAiBridge.dll**（最可能被 Mono 内存映射锁住）。它失败就立刻停，别去动 pdb/yaml，"
Write-Output "     否则会出现 dll 旧、yaml 新的不一致状态（真发生过一次）。"
Write-Output "  3) 再拷 pdb 与两个 yaml。"
Write-Output "  4) 判断能否覆盖【直接试写】，不要用可以打开文件当判据："
Write-Output "     ERROR_USER_MAPPED_FILE(1224) = 文件正被游戏内存映射 ⇒ 必须让用户退出游戏。"
Write-Output ""
Write-Output "安装前自检（务必先确认游戏已退出，否则文件被占用会覆盖失败）："
Write-Output "  Get-Process -Name OxygenNotIncluded -ErrorAction SilentlyContinue   # 必须为空"
Write-Output ""
Write-Output "下一步（需要用户同意后再执行，脚本不代劳）："
Write-Output "  New-Item -ItemType Directory -Force -Path '$local' | Out-Null"
Write-Output "  Copy-Item '$pkg' '$local' -Recurse -Force"
Write-Output "  然后进游戏：Mods 界面勾选 'ONI AI Bridge' → 重载/重启游戏"
Write-Output "  验证：Player.log 里 grep '[OniAiBridge]'（应有 OnLoad / listening 两行）"

# ========== 文档随包分发（工坊用户要能读到 AI-GUIDE / API / README）==========
foreach ($doc in @('AI-GUIDE.md','API.md','README.md')) {
    $src = Join-Path $PSScriptRoot ('docs\' + $doc)
    if (-not (Test-Path $src)) { $src = Join-Path $PSScriptRoot $doc }   # README 在仓库根
    if (Test-Path $src) { Copy-Item $src (Join-Path $pkg $doc) -Force; Write-Host ("  打包文档: " + $doc) }
    else { Write-Host ("  跳过（不存在）: " + $doc) -ForegroundColor Yellow }
}
# ========== 版本号一致性校验（mod_info.yaml / csproj / Bridge.cs 必须相同）==========
$vYaml = (Select-String -Path (Join-Path $PSScriptRoot 'mod\mod_info.yaml') -Pattern '^version:\s*(\S+)').Matches[0].Groups[1].Value
$vProj = (Select-String -Path (Join-Path $PSScriptRoot 'src\OniAiBridge\OniAiBridge.csproj') -Pattern '<Version>([^<]+)</Version>').Matches[0].Groups[1].Value
$vCode = (Select-String -Path (Join-Path $PSScriptRoot 'src\OniAiBridge\Bridge.cs') -Pattern 'ModVersion = "([^"]+)"').Matches[0].Groups[1].Value
if ($vYaml -ne $vProj -or $vProj -ne $vCode) {
    Write-Host ("FAIL: 三处版本号不一致 yaml=" + $vYaml + " csproj=" + $vProj + " Bridge=" + $vCode) -ForegroundColor Red
    exit 1
}
Write-Host ("OK: 三处版本号一致 = " + $vYaml)