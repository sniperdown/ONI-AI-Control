using System.Text;

// ⚠️ 不要 using System 之后直接用 Action/DateTime：Assembly-CSharp 里的全局 Action 枚举会遮蔽 System.Action。
namespace OniAiBridge
{
    /// <summary>
    /// M6.24：`GET /v1/env` —— **环境读数**（气体成分 / 气压 / 温度 / 真空比例）。
    ///
    /// 为什么需要它：`/v1/state` 只有"人和库存"，看不到"环境"（氧气够不够、气压多少）。
    /// 本端点直接**扫格子**（`Grid.Mass / Grid.Element / Grid.Temperature / Grid.Visible`），
    /// 口径写在响应里，**不冒充游戏诊断面板的数字**。
    ///
    /// 参数：
    /// * `?x=&y=&r=` —— 以 (x,y) 为中心、半径 r（含边界）的方形区域；
    /// * 不带 x/y/r —— 扫**全图可见区域**（`Grid.Visible[cell] > 0`），并受 `maxCells` 限制；
    /// * `?maxCells=N` —— 扫描上限（默认 20000，上限 200000），超了 `truncated:true`。
    /// </summary>
    internal static class EnvQuery
    {
        public static string Sample(int x0, int y0, int x1, int y1, bool hasRegion, int maxCells,
                                    bool aroundDupes, int aroundR,
                                    out int status, out string code, out string message)
        {
            status = 200; code = null; message = null;

            if (maxCells <= 0) maxCells = 20000;
            if (maxCells > 200000) maxCells = 200000;

            if (!MainThread.Ready) { status = 503; code = "NOT_READY"; message = "main-thread pump is not running yet"; return null; }

            string data = null;
            string fail = null;
            int lStatus = 200; string lCode = null, lMessage = null;   // lambda 内不能写 out 参数（CS1628）
            int okT; bool busy;

            bool ran = MainThread.InvokeAdaptive(() =>
            {
                try
                {
                    int w = Grid.WidthInCells, h = Grid.HeightInCells;
                    if (w <= 0 || h <= 0) { fail = "Grid 尺寸不可用（w=" + w + ", h=" + h + "）"; return; }

                    int dupesIncluded = 0;
                    int bMinX = 0, bMinY = 0, bMaxX = 0, bMaxY = 0;
                    string mode = hasRegion ? "region" : "whole-visible";

                    if (aroundDupes)
                    {
                        // 取所有复制人的位置，算包围盒，再外扩 r
                        var cmp = Components.LiveMinionIdentities;
                        if (cmp == null) { fail = "拿不到 LiveMinionIdentities"; return; }
                        int mnX = int.MaxValue, mnY = int.MaxValue, mxX = int.MinValue, mxY = int.MinValue;
                        foreach (MinionIdentity mi in cmp.Items)
                        {
                            if (mi == null) continue;
                            int c = -1;
                            try { c = Grid.PosToCell(mi.gameObject); } catch { }
                            if (c < 0) continue;
                            int dx, dy;
                            try { Grid.CellToXY(c, out dx, out dy); } catch { continue; }
                            dupesIncluded++;
                            if (dx < mnX) mnX = dx; if (dx > mxX) mxX = dx;
                            if (dy < mnY) mnY = dy; if (dy > mxY) mxY = dy;
                        }
                        if (dupesIncluded == 0) { fail = "没有可用的复制人位置"; return; }
                        bMinX = mnX; bMinY = mnY; bMaxX = mxX; bMaxY = mxY;
                        if (aroundR <= 0) aroundR = 15;
                        x0 = mnX - aroundR; y0 = mnY - aroundR;
                        x1 = mxX + aroundR; y1 = mxY + aroundR;
                        hasRegion = true;
                        mode = "around-dupes";
                    }
                    else if (!hasRegion)
                    {
                        x0 = 0; y0 = 0; x1 = w - 1; y1 = h - 1;
                    }

                    // ★ 越界必须报错，不能"clamp 成全 0 然后 ok:true"
                    if (x1 < 0 || y1 < 0 || x0 > w - 1 || y0 > h - 1)
                    {
                        lStatus = 400; lCode = "REGION_OUT_OF_BOUNDS";
                        lMessage = "区域完全在世界之外：请求 x=" + x0 + ".." + x1 + ", y=" + y0 + ".." + y1
                                   + "；Grid 尺寸 " + w + "x" + h + "，合法范围 x=0.." + (w - 1) + "，y=0.." + (h - 1);
                        return;
                    }
                    if (x1 < x0) { int t = x0; x0 = x1; x1 = t; }
                    if (y1 < y0) { int t = y0; y0 = y1; y1 = t; }
                    if (x0 < 0) x0 = 0;
                    if (y0 < 0) y0 = 0;
                    if (x1 > w - 1) x1 = w - 1;
                    if (y1 > h - 1) y1 = h - 1;

                    var massByElem = new System.Collections.Generic.Dictionary<string, double>();
                    double totalMass = 0, tempSum = 0;
                    int cells = 0, visible = 0, vacuum = 0, scanned = 0, solidOrLiquid = 0;
                    bool truncated = false;
                    double tmin = double.MaxValue, tmax = double.MinValue;
                    byte fogOn = Vision.FogOn ? (byte)1 : (byte)0;
                    bool fogEnabled = Vision.FogOn;

                    for (int yy = y0; yy <= y1 && !truncated; yy++)
                    {
                        for (int xx = x0; xx <= x1; xx++)
                        {
                            int cell = Grid.XYToCell(xx, yy);
                            if (!Grid.IsValidCell(cell)) continue;
                            cells++;

                            // 视野门禁：AIEnableFog 开启时只算 Grid.Visible>0 的格子
                            if (fogEnabled)
                            {
                                byte v = 0;
                                try { v = Grid.Visible[cell]; } catch { }
                                if (v == 0) continue;
                            }
                            visible++;

                            if (scanned >= maxCells) { truncated = true; break; }
                            scanned++;

                            float m = 0f;
                            try { m = Grid.Mass[cell]; } catch { }
                            if (m <= 0f) { vacuum++; continue; }

                            // ★ 只统计**气体格**：固体/液体不算（固体也是非零 Mass，会把气压平均拉到几百 kg）
                            Element el = null;
                            try { el = Grid.Element[cell]; } catch { }
                            if (el == null) continue;
                            bool isGas = false;
                            try { isGas = el.IsGas; } catch { }
                            if (!isGas) { solidOrLiquid++; continue; }

                            totalMass += m;

                            string ename = null;
                            try { ename = el.id.ToString(); } catch { }
                            if (string.IsNullOrEmpty(ename)) ename = "Unknown";
                            double cur;
                            massByElem.TryGetValue(ename, out cur);
                            massByElem[ename] = cur + m;

                            float tk = 0f;
                            try { tk = Grid.Temperature[cell]; } catch { }
                            if (tk > 0f)
                            {
                                double tc = tk - 273.15;
                                tempSum += tc;
                                if (tc < tmin) tmin = tc;
                                if (tc > tmax) tmax = tc;
                            }
                        }
                    }

                    var sb = new StringBuilder(1024);
                    sb.Append("{\"region\":{\"mode\":").Append(Snap.Str(mode))
                      .Append(",\"dupesIncluded\":").Append(dupesIncluded)
                      .Append(",\"r\":").Append(aroundR)
                      .Append(",\"bbox\":{\"minX\":").Append(bMinX).Append(",\"minY\":").Append(bMinY)
                      .Append(",\"maxX\":").Append(bMaxX).Append(",\"maxY\":").Append(bMaxY).Append("}")
                      .Append(",\"x0\":").Append(x0).Append(",\"y0\":").Append(y0)
                      .Append(",\"x1\":").Append(x1).Append(",\"y1\":").Append(y1)
                      .Append(",\"hasRegion\":").Append(hasRegion ? "true" : "false").Append('}');
                    sb.Append(",\"cells\":{\"total\":").Append(cells)
                      .Append(",\"visible\":").Append(visible)
                      .Append(",\"scanned\":").Append(scanned)
                      .Append(",\"vacuum\":").Append(vacuum).Append('}');
                    sb.Append(",\"truncated\":").Append(truncated ? "true" : "false");
                    sb.Append(",\"maxCells\":").Append(maxCells);

                    // 气压（只在**气体格**上平均）
                    int gasCells = 0, nonVac = 0;
                    { nonVac = scanned - vacuum; }
                    gasCells = nonVac - solidOrLiquid;
                    if (gasCells < 0) gasCells = 0;
                    double avgP = gasCells > 0 ? totalMass / gasCells : 0;
                    sb.Append(",\"pressure\":{\"avgKg\":").Append(Fmt(avgP))
                      .Append(",\"totalKg\":").Append(Fmt(totalMass)).Append('}');

                    // 温度
                    int tN = 0;
                    for (int i = 0; i < scanned; i++) { }
                    double tAvg = 0;
                    // 用 sum/count 近似（count 用 scanned - vacuum 的非真空格，温度只在非真空累加）
                    if (gasCells > 0) tAvg = tempSum / gasCells;
                    sb.Append(",\"temperatureC\":{\"avg\":").Append(Fmt(tAvg))
                      .Append(",\"min\":").Append(tmin == double.MaxValue ? "null" : Fmt(tmin))
                      .Append(",\"max\":").Append(tmax == double.MinValue ? "null" : Fmt(tmax)).Append('}');

                    // 元素成分（按质量排序）
                    var list = new System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<string, double>>();
                    foreach (var kv in massByElem) list.Add(kv);
                    list.Sort((a, b) => b.Value.CompareTo(a.Value));
                    sb.Append(",\"elements\":[");
                    int em = 0;
                    for (int i = 0; i < list.Count; i++)
                    {
                        if (em > 0) sb.Append(',');
                        em++;
                        double pct = totalMass > 0 ? list[i].Value / totalMass * 100.0 : 0;
                        sb.Append("{\"id\":").Append(Snap.Str(list[i].Key));
                        sb.Append(",\"massKg\":").Append(Fmt(list[i].Value));
                        sb.Append(",\"pct\":").Append(Fmt(pct)).Append('}');
                    }
                    sb.Append(']');

                    // 真空%：真空格 / 可见格
                    double vacPct = visible > 0 ? (double)vacuum / visible * 100.0 : 0;
                    double solidPct = visible > 0 ? (double)solidOrLiquid / visible * 100.0 : 0;
                    sb.Append(",\"vacuumPct\":").Append(Fmt(vacPct));

                    // 可呼吸占比（氧气 + 污染氧），这是"人还能不能呼吸"的直接判据
                    double o2 = 0, co2 = 0, cl = 0;
                    for (int i = 0; i < list.Count; i++)
                    {
                        string id = list[i].Key;
                        if (string.Equals(id, "Oxygen", System.StringComparison.OrdinalIgnoreCase)
                            || string.Equals(id, "ContaminatedOxygen", System.StringComparison.OrdinalIgnoreCase)) o2 += list[i].Value;
                        else if (string.Equals(id, "CarbonDioxide", System.StringComparison.OrdinalIgnoreCase)) co2 += list[i].Value;
                        else if (string.Equals(id, "ChlorineGas", System.StringComparison.OrdinalIgnoreCase)
                              || string.Equals(id, "Chlorine", System.StringComparison.OrdinalIgnoreCase)) cl += list[i].Value;
                    }
                    double breathablePct = totalMass > 0 ? o2 / totalMass * 100.0 : 0;
                    double co2Pct = totalMass > 0 ? co2 / totalMass * 100.0 : 0;
                    double clPct = totalMass > 0 ? cl / totalMass * 100.0 : 0;
                    sb.Append(",\"breathablePct\":").Append(Fmt(breathablePct));
                    sb.Append(",\"co2Pct\":").Append(Fmt(co2Pct));
                    sb.Append(",\"chlorinePct\":").Append(Fmt(clPct));
                    if (gasCells == 0)
                        sb.Append(",\"reason\":").Append(Snap.Str("no-gas-cells-in-region"));
                    string warn = null;
                    if (gasCells > 0)
                    {
                        warn = "可呼吸 " + Fmt(breathablePct) + "%";
                        if (co2Pct >= 1) warn += "，二氧化碳 " + Fmt(co2Pct) + "%";
                        if (clPct >= 0.5) warn += "，氯气 " + Fmt(clPct) + "%";
                    }
                    sb.Append(",\"warning\":").Append(Snap.Str(warn));
                    sb.Append(",\"solidPct\":").Append(Fmt(solidPct));
                    sb.Append(",\"gasCells\":").Append(gasCells);
                    sb.Append(",\"solidOrLiquidCells\":").Append(solidOrLiquid);
                    sb.Append(",\"fogOn\":").Append(fogEnabled ? "true" : "false");
                    sb.Append(",\"source\":").Append(Snap.Str("grid-scan(Grid.Mass/Element/Temperature/Visible)"));
                    sb.Append(",\"note\":").Append(Snap.Str(
                        "口径：只统计可见格（AIEnableFog 开启时 Grid.Visible>0）；**元素/气压/温度只算气体格**（Element.IsGas）；"
                        + "成分按质量百分比；**不是游戏诊断面板那张表的数字**（面板口径另算）"));
                    sb.Append('}');
                    data = sb.ToString();
                    Log.Info("[env] cells=" + cells + " visible=" + visible + " scanned=" + scanned
                             + " vacuum=" + vacuum + " truncated=" + truncated + " elements=" + massByElem.Count);
                }
                catch (System.Exception e) { fail = e.GetType().Name + ": " + e.Message; }
            }, 5000, 30000, out okT, out busy);

            if (!ran) { status = 503; code = "RETRY_LATER"; message = "main thread busy；请 1 秒后重试"; return null; }
            if (lStatus != 200) { status = lStatus; code = lCode; message = lMessage; Log.Info("[env] " + status + " " + lCode + ": " + lMessage); return null; }
            if (fail != null) { status = 500; code = "ENV_UNAVAILABLE"; message = fail; Log.Info("[env] 失败: " + fail); return null; }
            if (data == null) { status = 500; code = "INTERNAL"; message = "env 采集返回空"; return null; }
            return data;
        }

        static string Fmt(double v)
        {
            if (double.IsNaN(v) || double.IsInfinity(v)) return "null";
            return v.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
        }
    }
}
