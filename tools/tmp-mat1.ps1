$ErrorActionPreference = 'Stop'
Set-Location 'H:\deepseek-harness\workspace\oni-mod'
$f = 'src\OniAiBridge\Job.cs'
$lines = [System.Collections.Generic.List[string]]([System.IO.File]::ReadAllLines((Join-Path (Get-Location) $f)))
$report = @()

# 1) 声明材料统计变量
$i = -1
for ($k = 0; $k -lt $lines.Count; $k++) { if ($lines[$k].Contains('bool toolRestored = false; string restoreErr = null;')) { $i = $k; break } }
if ($i -lt 0) { throw 'MISS toolRestored 声明' }
$lines.Insert($i + 1, '                double needKg = -1, haveKg = -1;   // 材料需求/持有（-1 = 读不到）')

# 2) 在回读失败判定之前，计算 needKg / haveKg（仍在主线程 true/false 窗口外，可读）
$j = -1
for ($k = 0; $k -lt $lines.Count; $k++) { if ($lines[$k].Contains('// 3) 回读：该格是否出现')) { $j = $k; break } }
if ($j -lt 0) { throw 'MISS 回读注释' }
$calc = @(
'',
'                        // ★ 材料需求/持有（用于把"材料不足"和"别的原因"分开）',
'                        try',
'                        {',
'                            BuildingDef defM = null;',
'                            try { defM = Assets.GetBuildingDef(prefabId); } catch { }',
'                            if (defM != null)',
'                            {',
'                                try { if (defM.Mass != null && defM.Mass.Length > 0) needKg = defM.Mass[0]; } catch { }',
'                                double sum = 0; double best = -1; string bestTag = null;',
'                                var buildsM = Components.BuildingCompletes != null ? Components.BuildingCompletes.Items : null;',
'                                if (buildsM != null && matsSource2 != null)',
'                                {',
'                                    for (int bi = 0; bi < buildsM.Count; bi++)',
'                                    {',
'                                        var bb = buildsM[bi];',
'                                        if (bb == null) continue;',
'                                        UnityEngine.Component[] cs = null;',
'                                        try { cs = bb.GetComponentsInChildren<Storage>(); } catch { }',
'                                        if (cs == null) continue;',
'                                        for (int ci = 0; ci < cs.Length; ci++)',
'                                        {',
'                                            var st2 = cs[ci] as Storage;',
'                                            if (st2 == null) continue;',
'                                            for (int ti = 0; ti < matTagNames.Count; ti++)',
'                                            {',
'                                                float amt = 0f;',
'                                                try { amt = st2.GetAmountAvailable(TagManager.Create(matTagNames[ti])); } catch { }',
'                                                sum += amt;',
'                                                if (amt > best) { best = amt; bestTag = matTagNames[ti]; }',
'                                            }',
'                                        }',
'                                    }',
'                                }',
'                                haveKg = sum;',
'                                matHaveTag = bestTag;',
'                                matHaveOneTagKg = best;',
'                            }',
'                        }',
'                        catch { }'
)
$lines.InsertRange($j, [string[]]$calc)

# 3) 声明 matHaveTag / matHaveOneTagKg / matsSource2 / matTagNames
$k2 = -1
for ($k = 0; $k -lt $lines.Count; $k++) { if ($lines[$k].Contains('double needKg = -1, haveKg = -1;')) { $k2 = $k; break } }
if ($k2 -lt 0) { throw 'MISS needKg 声明' }
$lines.Insert($k2 + 1, '                string matHaveTag = null, matsSource2 = null; double matHaveOneTagKg = -1;')

[System.IO.File]::WriteAllLines((Join-Path (Get-Location) $f), $lines, (New-Object System.Text.UTF8Encoding($false)))
$report += '材料 need/have 统计已插入'
$report | ForEach-Object { 'OK   ' + $_ }
