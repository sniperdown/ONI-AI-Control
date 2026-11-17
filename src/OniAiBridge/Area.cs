using System.Text;

// ⚠️ 不要 using System 之后直接用 Action/DateTime：Assembly-CSharp 里的全局 Action 枚举会遮蔽 System.Action。
namespace OniAiBridge
{
    /// <summary>
    /// M6.67（**只读**）：`GET /v1/area` —— 区域**数字档**（纯统计，不返回逐格数组）。
    ///
    /// 两种寻址：
    /// * 块寻址：`?worldId=0&cx=3&cy=5`（块大小/原点见 `Chunks` 与 `docs/API.md §3.31`）
    /// * 框寻址：`?worldId=0&x0=..&y0=..&x1=..&y1=..`（全局网格坐标）
    ///
    /// 统计口径（**与 `/v1/env` 不同，别混**）：
    /// * 本端点 **逐格**统计（固/液/气/真空都算），`elements[].massKg` 是这些格子的质量之和；
    /// * `/v1/env` 只统计**气体格**、且受 `Grid.Visible`（迷雾）门禁 —— 两者数字本来就不该相等。
    ///
    /// 字段：
    /// * `cells.{total,scanned,mineable,unmineable,vacuum,gas,liquid,solid}`
    /// * `elements[]` 按元素聚合（数量降序，前 10 个 + `other`），每项 `id` / `count` / `massKg`
    /// * `buildings[]` 按 `prefabId` 聚合（按**实例**去重：多格建筑只算一次，用 instanceId 去重）
    /// * `bbox` 实际覆盖范围；`chunk` 用块寻址时回显 `cx/cy` 与真实 `w/h`
    /// </summary>
    internal static class Area
    {
        public static string Query(int worldId, bool useChunk, int cx, int cy,
                                   int x0, int y0, int x1, int y1,
                                   int maxCells,
                                   out int status, out string code, out string message)
        {
            status = 200; code = null; message = null;
            if (maxCells <= 0) maxCells = 4096;
            if (maxCells > 65536) maxCells = 65536;
            if (!MainThread.Ready) { status = 503; code = "NOT_READY"; message = "main-thread pump is not running yet"; return null; }

            string data = null, fail = null, failCode = null, failMsg = null;
            int okT; bool busy;
            bool ran = MainThread.InvokeAdaptive(() =>
            {
                try
                {
                    int bx0, by0, bx1, by1;
                    if (useChunk)
                    {
                        string err;
                        if (!Chunks.TryChunkBBox(worldId, cx, cy, out bx0, out by0, out bx1, out by1, out err))
                        { failCode = "BAD_CHUNK"; failMsg = err; return; }
                    }
                    else
                    {
                        bx0 = x0; by0 = y0; bx1 = x1; by1 = y1;
                        if (bx1 < bx0) { int t = bx0; bx0 = bx1; bx1 = t; }
                        if (by1 < by0) { int t = by0; by0 = by1; by1 = t; }
                    }

                    long total = (long)(bx1 - bx0 + 1) * (long)(by1 - by0 + 1);
                    if (total > maxCells)
                    {
                        failCode = "AREA_TOO_LARGE";
                        failMsg = "区域太大：本请求 " + total + " 格，上限 " + maxCells + "（用块寻址 cx/cy 或缩小范围）";
                        return;
                    }

                    int scanned = 0, mineable = 0, unmineable = 0, vacuum = 0, gas = 0, liquid = 0, solid = 0, alreadyDigMarked = 0, neutronium = 0;
                    var elemCount = new System.Collections.Generic.Dictionary<string, int>();
                    var elemMass = new System.Collections.Generic.Dictionary<string, double>();
                    var bldByPrefab = new System.Collections.Generic.Dictionary<string, int>();
                    var seenInstance = new System.Collections.Generic.HashSet<int>();

                    int layers = (int)ObjectLayer.NumLayers;

                    for (int yy = by0; yy <= by1; yy++)
                    {
                        for (int xx = bx0; xx <= bx1; xx++)
                        {
                            int cell = Grid.XYToCell(xx, yy);
                            if (!Grid.IsValidCell(cell)) continue;
                            scanned++;

                            Element el = null;
                            try { el = Grid.Element[cell]; } catch { }

                            bool isSolid = false, isLiquid = false, isGas = false;
                            try { if (el != null) { isSolid = el.IsSolid; isLiquid = el.IsLiquid; isGas = el.IsGas; } } catch { }

                            float mass = 0f;
                            try { mass = Grid.Mass[cell]; } catch { }

                            bool solidFlag = false;
                            try { solidFlag = Grid.IsSolidCell(cell); } catch { }

                            if (mass <= 0f) vacuum++;
                            else if (isGas) gas++;
                            else if (isLiquid) liquid++;
                            else if (isSolid || solidFlag) solid++;

                            // 元素 id（用于聚合）
                            string eid = null;
                            try { eid = el != null ? el.id.ToString() : null; } catch { }
                            if (string.IsNullOrEmpty(eid)) eid = "Unknown";

                            // ★ M6.68：可挖判据**只允许一个出处** —— CellRules.IsDiggable（dig 分支也调它）
                            //   以前这里自己写了一套更严的（把 Unobtanium 也算不可挖），
                            //   导致同一块 area.mineable=144 而 dig.marked=419 自相矛盾。
                            if (solidFlag)
                            {
                                if (CellRules.IsNeutronium(cell)) neutronium++;   // 仅信息：游戏边界层
                                CellRules.DigVerdict dv;
                                if (CellRules.IsDiggable(cell, out dv)) mineable++;
                                else if (dv == CellRules.DigVerdict.AlreadyMarked) alreadyDigMarked++;
                                else unmineable++;                                  // 目前只会是 HasBuilding
                            }

                            if (mass > 0f)
                            {
                                int c;
                                elemCount.TryGetValue(eid, out c);
                                elemCount[eid] = c + 1;
                                double m0;
                                elemMass.TryGetValue(eid, out m0);
                                elemMass[eid] = m0 + mass;
                            }

                            // 建筑：按实例去重（多格建筑在多个格子上都会被看到）
                            for (int ly = 0; ly < layers; ly++)
                            {
                                UnityEngine.GameObject go = null;
                                try { go = Grid.Objects[cell, ly]; } catch { continue; }
                                if (go == null) continue;
                                try
                                {
                                    var b = go.GetComponent<Building>();
                                    if (b == null) continue;
                                    int inst = go.GetInstanceID();
                                    if (!seenInstance.Add(inst)) continue;
                                    string pid = null;
                                    try { pid = b.Def != null ? b.Def.PrefabID : null; } catch { }
                                    if (string.IsNullOrEmpty(pid)) pid = "Unknown";
                                    int c2;
                                    bldByPrefab.TryGetValue(pid, out c2);
                                    bldByPrefab[pid] = c2 + 1;
                                }
                                catch { }
                            }
                        }
                    }

                    // elements 排序（按 count 降序），前 10 + other
                    var elList = new System.Collections.Generic.List<string>(elemCount.Keys);
                    elList.Sort((a, b) => elemCount[b].CompareTo(elemCount[a]));

                    var sb = new StringBuilder(2048);
                    sb.Append("{\"worldId\":").Append(worldId);
                    sb.Append(",\"bbox\":{\"x0\":").Append(bx0).Append(",\"y0\":").Append(by0)
                      .Append(",\"x1\":").Append(bx1).Append(",\"y1\":").Append(by1)
                      .Append(",\"w\":").Append(bx1 - bx0 + 1).Append(",\"h\":").Append(by1 - by0 + 1).Append('}');
                    if (useChunk)
                    {
                        sb.Append(",\"chunk\":{\"cx\":").Append(cx).Append(",\"cy\":").Append(cy)
                          .Append(",\"w\":").Append(bx1 - bx0 + 1).Append(",\"h\":").Append(by1 - by0 + 1).Append('}');
                    }
                    else sb.Append(",\"chunk\":null");

                    sb.Append(",\"cells\":{\"total\":").Append(total)
                      .Append(",\"scanned\":").Append(scanned)
                      .Append(",\"mineable\":").Append(mineable)
                      .Append(",\"unmineable\":").Append(unmineable)
                      .Append(",\"vacuum\":").Append(vacuum)
                      .Append(",\"gas\":").Append(gas)
                      .Append(",\"liquid\":").Append(liquid)
                      .Append(",\"solid\":").Append(solid)
                      .Append(",\"alreadyDigMarked\":").Append(alreadyDigMarked)
                      .Append(",\"neutronium\":").Append(neutronium).Append('}');

                    sb.Append(",\"elements\":[");
                    int em = 0;
                    long otherCount = 0; double otherMass = 0;
                    for (int i = 0; i < elList.Count; i++)
                    {
                        string id = elList[i];
                        if (i >= 10) { otherCount += elemCount[id]; otherMass += elemMass[id]; continue; }
                        if (em > 0) sb.Append(',');
                        em++;
                        sb.Append("{\"id\":").Append(Snap.Str(id));
                        sb.Append(",\"count\":").Append(elemCount[id]);
                        sb.Append(",\"massKg\":").Append(Num(elemMass[id])).Append('}');
                    }
                    if (otherCount > 0)
                    {
                        if (em > 0) sb.Append(',');
                        sb.Append("{\"id\":\"other\",\"count\":").Append(otherCount)
                          .Append(",\"massKg\":").Append(Num(otherMass)).Append('}');
                    }
                    sb.Append(']');
                    sb.Append(",\"elementKinds\":").Append(elList.Count);

                    sb.Append(",\"buildings\":[");
                    int bm = 0;
                    var bList = new System.Collections.Generic.List<string>(bldByPrefab.Keys);
                    bList.Sort((a, b) => bldByPrefab[b].CompareTo(bldByPrefab[a]));
                    for (int i = 0; i < bList.Count && i < 20; i++)
                    {
                        if (bm > 0) sb.Append(',');
                        bm++;
                        sb.Append("{\"prefabId\":").Append(Snap.Str(bList[i]));
                        sb.Append(",\"count\":").Append(bldByPrefab[bList[i]]).Append('}');
                    }
                    sb.Append(']');
                    sb.Append(",\"buildingKinds\":").Append(bldByPrefab.Count);

                    sb.Append(",\"source\":").Append(Snap.Str("Grid.Element/Mass/IsSolidCell + Grid.Objects 全层（建筑按 instanceId 去重）"));
                    sb.Append(",\"note\":").Append(Snap.Str(
                        "**逐格**统计（固/液/气/真空都算），与 /v1/env 口径不同（后者只算气体格且受迷雾门禁）⇒ 两边的数字本来就不该相等。"
                        + "`mineable`/`unmineable`/`alreadyDigMarked` 由 CellRules.IsDiggable 判定（**与 dig 分支同源**，见 docs/API.md §3.34）："
                        + "实心、该格无建筑、且尚无挖标记。`unmineable` 目前只表示\"实心但有建筑挡着\"；"
                        + "`neutronium` 是实心里的中子质格数（**仅信息**，不参与可挖判定 —— 能不能挖最终由游戏 PlaceDig 裁定）。"
                        + "⇒ `mineable` 是**上界**：PlaceDig 还可能再拒绝若干格（dig 会记进 rejected.byReason.NOT_DIGGABLE）。"
                        + "`elements[].massKg` 是该元素在这些格子里的质量之和；元素最多列 10 个，其余合并为 `other`；建筑最多列 20 种。"));
                    sb.Append('}');
                    data = sb.ToString();
                    Log.Info("[area] worldId=" + worldId + " bbox=" + bx0 + "," + by0 + ".." + bx1 + "," + by1
                             + " scanned=" + scanned + " mineable=" + mineable + " elements=" + elList.Count
                             + " buildings=" + bldByPrefab.Count);
                }
                catch (System.Exception e) { fail = e.GetType().Name + ": " + e.Message; }
            }, 5000, 30000, out okT, out busy);

            if (!ran) { status = 503; code = "RETRY_LATER"; message = "main thread busy；请 1 秒后重试"; return null; }
            if (failCode != null) { status = 400; code = failCode; message = failMsg; return null; }
            if (fail != null) { status = 500; code = "AREA_UNAVAILABLE"; message = fail; return null; }
            if (data == null) { status = 500; code = "INTERNAL"; message = "area 采集返回空"; return null; }
            JsonCheck.Validate(data, "area");
            return data;
        }

        static string Num(double v)
        {
            if (double.IsNaN(v) || double.IsInfinity(v)) return "null";
            return v.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
        }
    }
}
