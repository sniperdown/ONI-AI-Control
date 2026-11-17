$ErrorActionPreference = 'Stop'
Set-Location 'H:\deepseek-harness\workspace\oni-mod'

# ---------- 1) Bridge.HandleBuildings：解析 prefabId / 区域 ----------
$bf = 'src\OniAiBridge\Bridge.cs'
$bt = [System.IO.File]::ReadAllText((Join-Path (Get-Location) $bf))
$old = @'
            int offset = 0, limit = BuildingsQuery.DefaultLimit;
'@
$new = @'
            int offset = 0, limit = BuildingsQuery.DefaultLimit;
            string fPrefab = null;
            int fx0 = 0, fy0 = 0, fx1 = 0, fy1 = 0; bool hasRegion = false;
'@
if (-not $bt.Contains($old)) { throw 'MISS offset 声明' }
$bt = $bt.Replace($old, $new)

# 在已有的 query 解析循环里加分支：找到 buildings 的解析块
$oldParse = @'
                foreach (string kv in query.Split('&'))
                {
                    int e = kv.IndexOf('=');
                    if (e <= 0) continue;
'@
if (-not $bt.Contains($oldParse)) { throw 'MISS 解析块' }
# 找到 HandleBuildings 里那段解析（用 buildings 的独特上下文定位）
$idx = $bt.IndexOf('static void HandleBuildings')
if ($idx -lt 0) { throw 'MISS HandleBuildings' }
$seg = $bt.Substring($idx, 1600)
$newParse = @'
                foreach (string kv in query.Split('&'))
                {
                    int e = kv.IndexOf('=');
                    if (e <= 0) continue;
                    string key = kv.Substring(0, e), val = kv.Substring(e + 1);
                    if (key == "prefabId") { fPrefab = val; continue; }
                    int n2;
                    if (!int.TryParse(val, out n2)) continue;
                    if (key == "x0") { fx0 = n2; hasRegion = true; }
                    else if (key == "y0") { fy0 = n2; hasRegion = true; }
                    else if (key == "x1") { fx1 = n2; hasRegion = true; }
                    else if (key == "y1") { fy1 = n2; hasRegion = true; }
                    else if (key == "offset") { offset = n2; }
                    else if (key == "limit") { limit = n2; }
                    continue;
'@
# 用正则替换 HandleBuildings 内的第一处解析循环（保留其后的 int.TryParse 分支）
$pattern = [regex]::Escape($oldParse)
$count = ([regex]::Matches($seg, $pattern)).Count
if ($count -lt 1) { throw 'MISS buildings 解析循环' }
# 只替换该段内第一处
$segNew = [regex]::Replace($seg, $pattern, $newParse.Replace('$','$$'), 1)
$bt = $bt.Substring(0, $idx) + $segNew + $bt.Substring($idx + 1600)

# 调用处传参
$oldCall = 'bool ok = MainThread.Invoke(() => { try { data = BuildingsQuery.Query(offset, limit); } catch { data = null; } }, 5000);'
$newCall = 'bool ok = MainThread.Invoke(() => { try { data = BuildingsQuery.Query(offset, limit, fPrefab, hasRegion, fx0, fy0, fx1, fy1); } catch { data = null; } }, 5000);'
if (-not $bt.Contains($oldCall)) { throw 'MISS Query 调用' }
$bt = $bt.Replace($oldCall, $newCall)

# 版本 0.39.0 / M6.8
$bt = $bt.Replace('public const string ModVersion = "0.38.0";','public const string ModVersion = "0.39.0";')
$bt = $bt.Replace('public const string Stage = "M6.7";','public const string Stage = "M6.8";')
[System.IO.File]::WriteAllText((Join-Path (Get-Location) $bf), $bt, (New-Object System.Text.UTF8Encoding($false)))
Write-Output 'OK Bridge：prefabId / 区域解析 + 传参 + 版本 0.39.0'

# ---------- 2) Job.cs：候选加 instanceId（deconstruct + harvest） ----------
$jf = 'src\OniAiBridge\Job.cs'
$jt = [System.IO.File]::ReadAllText((Join-Path (Get-Location) $jf))
$oldD = @'
                sb.Append(",\"deconstructable\":").Append(c.Deconstructable ? "true" : "false");
'@
$newD = @'
                int iid1 = -1; try { iid1 = c.Go != null ? c.Go.GetInstanceID() : -1; } catch { }
                sb.Append(",\"instanceId\":").Append(iid1);
                sb.Append(",\"deconstructable\":").Append(c.Deconstructable ? "true" : "false");
'@
if (-not $jt.Contains($oldD)) { throw 'MISS deconstruct 候选字段' }
$jt = $jt.Replace($oldD, $newD)

$oldH = @'
                sb.Append(",\"filterLayerName\":").Append(Snap.Str(FilterLayerZh(c.FilterLayer)));
                sb.Append(",\"markedForHarvest\":")
'@
$newH = @'
                sb.Append(",\"filterLayerName\":").Append(Snap.Str(FilterLayerZh(c.FilterLayer)));
                int iid2 = -1; try { iid2 = c.Go != null ? c.Go.GetInstanceID() : -1; } catch { }
                sb.Append(",\"instanceId\":").Append(iid2);
                sb.Append(",\"markedForHarvest\":")
'@
if (-not $jt.Contains($oldH)) { throw 'MISS harvest 候选字段' }
$jt = $jt.Replace($oldH, $newH)
[System.IO.File]::WriteAllText((Join-Path (Get-Location) $jf), $jt, (New-Object System.Text.UTF8Encoding($false)))
Write-Output 'OK Job.cs：候选加 instanceId（两处）'

$j = [System.IO.File]::ReadAllText((Join-Path (Get-Location) 'src\OniAiBridge\OniAiBridge.csproj')).Replace('<Version>0.38.0</Version>','<Version>0.39.0</Version>')
[System.IO.File]::WriteAllText((Join-Path (Get-Location) 'src\OniAiBridge\OniAiBridge.csproj'), $j, (New-Object System.Text.UTF8Encoding($false)))
$mi = [System.IO.File]::ReadAllText((Join-Path (Get-Location) 'mod\mod_info.yaml')).Replace('version: 0.38.0','version: 0.39.0')
[System.IO.File]::WriteAllText((Join-Path (Get-Location) 'mod\mod_info.yaml'), $mi, (New-Object System.Text.UTF8Encoding($false)))
Write-Output 'OK 版本文件 0.39.0'
$env:ONI_GAME_DIR='F:\SteamLibrary\steamapps\common\OxygenNotIncluded'
& dotnet build .\src\OniAiBridge\OniAiBridge.csproj -c Release -o .\dist\_probe --nologo "-p:OniGameDir=$env:ONI_GAME_DIR" 2>&1 | Select-String -Pattern 'error CS|已成功' | Select-Object -First 8 | Out-String -Width 170
Remove-Item 'dist\_probe' -Recurse -Force -ErrorAction SilentlyContinue
