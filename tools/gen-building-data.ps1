$ErrorActionPreference = 'Stop'
$root = 'H:\deepseek-harness\workspace\oni-mod'
$dataDir = Join-Path $root 'docs\data'
New-Item -ItemType Directory -Force -Path $dataDir | Out-Null

$po = 'F:\SteamLibrary\steamapps\common\OxygenNotIncluded\OxygenNotIncluded_Data\StreamingAssets\strings\strings_preinstalled_zh_klei.po'
$lines = [System.IO.File]::ReadAllLines($po)

# ---------- 解析 .po：按空行切块 ----------
function Get-PoEntries([string[]]$all) {
    $entries = New-Object System.Collections.Generic.List[object]
    $ctx = $null; $id = $null; $str = $null
    foreach ($ln in $all) {
        if ($ln -eq '') {
            if ($ctx -ne $null) { $entries.Add([pscustomobject]@{ Ctx = $ctx; Id = $id; Str = $str }) }
            $ctx = $null; $id = $null; $str = $null
            continue
        }
        if ($ln -match '^msgctxt "(.*)"$')      { $ctx = $Matches[1] }
        elseif ($ln -match '^msgid "(.*)"$')    { $id  = $Matches[1] }
        elseif ($ln -match '^msgstr "(.*)"$')   { $str = $Matches[1] }
        elseif ($ln -match '^msgstr ""$')       { $str = '' }
    }
    if ($ctx -ne $null) { $entries.Add([pscustomobject]@{ Ctx = $ctx; Id = $id; Str = $str }) }
    return $entries
}

function Clean-Text([string]$s) {
    if ($null -eq $s) { return '' }
    $t = $s -replace '<link="[^"]*">', '' -replace '</link>', ''
    $t = $t -replace '<[^>]+>', ''
    $t = $t -replace '\\"', '"'
    return $t.Trim()
}

$entries = Get-PoEntries $lines
Write-Output ("PO 条目数: " + $entries.Count)

# ---------- 建筑：STRINGS.BUILDINGS.PREFABS.<ID>.NAME ----------
$buildings = New-Object System.Collections.Generic.List[object]
foreach ($e in $entries) {
    $m = [regex]::Match($e.Ctx, '^STRINGS\.BUILDINGS\.PREFABS\.([A-Z0-9_]+)\.NAME$')
    if ($m.Success) {
        $buildings.Add([pscustomobject]@{ PrefabID = $m.Groups[1].Value; Name = (Clean-Text $e.Str) })
    }
}
Write-Output ("建筑（有中文名）: " + $buildings.Count)

# ---------- 分类：STRINGS.UI.NEWBUILDCATEGORIES.<KEY>.NAME ----------
$cats = New-Object System.Collections.Generic.List[object]
foreach ($e in $entries) {
    $m = [regex]::Match($e.Ctx, '^STRINGS\.UI\.NEWBUILDCATEGORIES\.([A-Z0-9_]+)\.NAME$')
    if ($m.Success) {
        $raw = [string]$e.Str
        $isTop = ($raw -match 'BUILDCATEGORY')
        $cats.Add([pscustomobject]@{ Key = $m.Groups[1].Value; Name = (Clean-Text $raw); TopLevel = $isTop })
    }
}
Write-Output ("分类: " + $cats.Count + "（顶层 " + ($cats | Where-Object { $_.TopLevel }).Count + "）")

# ---------- buildings.csv（UTF-8 BOM，给 Excel 看）----------
$csv = New-Object System.Text.StringBuilder
[void]$csv.AppendLine('prefabID,中文名,defaultWidth,defaultHeight,分类key,分类中文名,备注')
foreach ($b in ($buildings | Sort-Object PrefabID)) {
    $line = '{0},"{1}",未确认,未确认,,,尺寸与分类都要运行时取（BuildingDef.WidthInCells/HeightInCells、PlanScreen.tagCategoryMap）' -f $b.PrefabID, ($b.Name -replace '"','""')
    [void]$csv.AppendLine($line)
}
$csvPath = Join-Path $dataDir 'buildings.csv'
[System.IO.File]::WriteAllText($csvPath, $csv.ToString(), (New-Object System.Text.UTF8Encoding($true)))
Write-Output ("写出 buildings.csv: " + $buildings.Count + " 行")

# ---------- categories.csv ----------
$csv2 = New-Object System.Text.StringBuilder
[void]$csv2.AppendLine('分类key,中文名,是否顶层页签,备注')
foreach ($c in ($cats | Sort-Object @{Expression={-not $_.TopLevel}}, Key)) {
    $note = if ($c.TopLevel) { '顶层页签（.po 里值带 <link="BUILDCATEGORY...">）' } else { '子分类；挂哪个顶层页签未确认' }
    [void]$csv2.AppendLine(('{0},"{1}",{2},{3}' -f $c.Key, ($c.Name -replace '"','""'), $(if ($c.TopLevel) { '是' } else { '否' }), $note))
}
$csv2Path = Join-Path $dataDir 'categories.csv'
[System.IO.File]::WriteAllText($csv2Path, $csv2.ToString(), (New-Object System.Text.UTF8Encoding($true)))
Write-Output ("写出 categories.csv: " + $cats.Count + " 行")

# ---------- wiki 对账 ----------
Push-Location 'H:\deepseek-harness\workspace\oni-wiki'
$wikiRaw = & node query.mjs search "建筑信息框" --limit 3000 2>&1
Pop-Location
$wikiTitles = @()
foreach ($ln in $wikiRaw) {
    $m = [regex]::Match($ln, '^— (.+)$')
    if ($m.Success) { $wikiTitles += $m.Groups[1].Value.Trim() }
}
Write-Output ("wiki 建筑页（含 建筑信息框 的搜索结果）: " + $wikiTitles.Count)

$gameNames = @{}
foreach ($b in $buildings) { if ($b.Name) { $gameNames[$b.Name] = $b.PrefabID } }

$missing = @()   # 游戏有名字、wiki 无同名页面
foreach ($b in $buildings) {
    if (-not $b.Name) { continue }
    if ($wikiTitles -notcontains $b.Name) { $missing += $b }
}
$onlyWiki = @()  # wiki 有页面、游戏里没有同名建筑
foreach ($t in $wikiTitles) { if (-not $gameNames.ContainsKey($t)) { $onlyWiki += $t } }

$md = New-Object System.Text.StringBuilder
[void]$md.AppendLine('# 建筑对照：游戏本体 ↔ 中文 wiki')
[void]$md.AppendLine('')
[void]$md.AppendLine('数据来源：')
[void]$md.AppendLine('- 游戏：`strings_preinstalled_zh_klei.po` 里 `STRINGS.BUILDINGS.PREFABS.<ID>.NAME`（共 ' + $buildings.Count + ' 条有中文名）')
[void]$md.AppendLine('- wiki：`oni-wiki.db` 中包含 `建筑信息框` 模板的页面（共 ' + $wikiTitles.Count + ' 页）')
[void]$md.AppendLine('')
[void]$md.AppendLine('> ⚠️ 对账只按**中文名完全相同**匹配，所以「稍有不同的别名」「wiki 重定向」「同一建筑多形态」都会被算成差异。')
[void]$md.AppendLine('')
[void]$md.AppendLine('## 1. 游戏有、wiki 没有同名页面（' + $missing.Count + ' 条）')
[void]$md.AppendLine('')
[void]$md.AppendLine('| prefabID | 中文名 |')
[void]$md.AppendLine('|---|---|')
foreach ($b in ($missing | Sort-Object PrefabID | Select-Object -First 200)) {
    [void]$md.AppendLine('| ' + $b.PrefabID + ' | ' + $b.Name + ' |')
}
if ($missing.Count -gt 200) { [void]$md.AppendLine('| … | 其余 ' + ($missing.Count - 200) + ' 条省略 |') }
[void]$md.AppendLine('')
[void]$md.AppendLine('## 2. wiki 有、游戏里没有同名 prefabID（' + $onlyWiki.Count + ' 条，前 200）')
[void]$md.AppendLine('')
foreach ($t in ($onlyWiki | Sort-Object | Select-Object -First 200)) { [void]$md.AppendLine('- ' + $t) }
[void]$md.AppendLine('')
[void]$md.AppendLine('## 3. 说明（怎么读这份差异）')
[void]$md.AppendLine('')
[void]$md.AppendLine('- **第 1 类是"wiki 覆盖不足"**：游戏里 593 个建筑有中文名，wiki 只覆盖一部分；不等于这些建筑不能操作。')
[void]$md.AppendLine('- **第 2 类多半是"别名/多形态/教程页"**：wiki 用玩家俗称或页面拆成多个形态（如"丰碑底基/中段/顶部"），游戏里是一个 prefabID 或多个变体。')
[void]$md.AppendLine('- **结论**：给 AI 用的名字表**必须以游戏 prefabID 为主键**，wiki 名只作为"人类别名"补充。')

$diffPath = Join-Path $dataDir 'buildings-wiki-diff.md'
[System.IO.File]::WriteAllText($diffPath, $md.ToString(), (New-Object System.Text.UTF8Encoding($false)))
Write-Output ("写出 buildings-wiki-diff.md：游戏有/wik 无 " + $missing.Count + " 条；wiki 有/游戏无 " + $onlyWiki.Count + " 条")

# ---------- 可操作 Tag 白名单（从类 IL 里找 GameTags::get_* 引用）----------
$tool = 'H:\deepseek-harness\workspace\shared\tmp\oni-mem\tools\layout\bin\Release\net6.0\layout.exe'
$managed = 'F:\SteamLibrary\steamapps\common\OxygenNotIncluded\OxygenNotIncluded_Data\Managed'
$types = @('BuildingDef','Door','ManualDoor','ExternalDoor','ComplexFabricator','ComplexFabricatorWorkable',
           'CraftingStation','Operational','BuildingEnabledButton','Deconstructable','Chore','ChoreType','Workable',
           'Prioritizable','Storage','Generator','EnergyConsumer','LogicOperationalController','Toggleable',
           'BuildingComplete','Building')
$tagUses = @{}
foreach ($t in $types) {
    $out = & $tool $managed il $t 2>&1
    foreach ($ln in $out) {
        $m = [regex]::Match($ln, 'GameTags::([A-Za-z0-9_]+)')
        if ($m.Success) {
            $name = $m.Groups[1].Value
            if (-not $tagUses.ContainsKey($name)) { $tagUses[$name] = New-Object System.Collections.Generic.List[string] }
            if (-not $tagUses[$name].Contains($t)) { $tagUses[$name].Add($t) }
        }
    }
}
Write-Output ("可操作候补 Tag: " + $tagUses.Count)
$csv3 = New-Object System.Text.StringBuilder
[void]$csv3.AppendLine('标签名,被哪些类引用,判据')
foreach ($k in ($tagUses.Keys | Sort-Object)) {
    [void]$csv3.AppendLine(('{0},"{1}",IL 里对 GameTags.{0} 的引用（这些类与"操作"直接相关）' -f $k, ($tagUses[$k] -join '; ')))
}
$csv3Path = Join-Path $dataDir 'operable-tags.csv'
[System.IO.File]::WriteAllText($csv3Path, $csv3.ToString(), (New-Object System.Text.UTF8Encoding($true)))
Write-Output ("写出 operable-tags.csv: " + $tagUses.Count + " 行")
