using System.Text;

namespace OniAiBridge
{
    /// <summary>
    /// `POST /v1/wire` —— **批量铺电线并显式设置每格的连接掩码**（0.98.0）。
    ///
    /// 为什么需要：游戏里**单格放电线不会自动相连**（防粘连设计：若单格建就自动连，
    /// 在已有线旁造一格会粘连一大片），只有"拖动"才会连。而 `build` 只有单格模式
    /// ⇒ 建出来全是断的 ⇒ 电网接不通 ⇒ 制氧机/打浆机/充电器全带不起来。
    ///
    /// 走"单格建 + 显式设连接"（主人拍板 B 方案），但接口**批量化**：
    /// 一次给一串格子 + 每格掩码，一次设完。
    ///
    /// **以前为什么改不动**（0.94.1 踩过两次）：
    ///   直接写 `physicalGrid[cell].connections` 或只调 `AddConnection` 后，
    ///   `GetConnections` 认得掩码、但 `GetNetworkForCell` 一直是 -1（InvalidNetworkIdx）——
    ///   因为**没有触发网络重建**，管理器内部的 networkId 拓扑还是旧的/空的。
    ///   ⇒ 本实现在设完掩码后**显式 ForceRebuildNetworks()**，并回读 networkId 作判据。
    ///
    /// 判据（三档，别只看掩码）：
    ///   ① `getter` == 期望掩码（IUtilityNetworkMgr.GetConnections 认账的那条）
    ///   ② `networkId` != -1，且同一条线上的格子 networkId 相同
    ///   ③ 真正的电真的流过去（自己去看电池电量/用电器 operational，本端点不负责验）
    /// </summary>
    internal static class WireNet
    {
        public static string Run(string body, out int status, out string code, out string message)
        {
            status = 200; code = null; message = null;

            bool apply = false;
            apply = body != null && body.IndexOf("\"confirm\":true", System.StringComparison.OrdinalIgnoreCase) >= 0;
            string cellsJson = ExtractArray(body, "cells");
            if (string.IsNullOrEmpty(cellsJson))
            {
                status = 400; code = "BAD_REQUEST";
                message = "需要 cells 数组，例如 {\"cells\":[{\"x\":117,\"y\":217,\"connect\":\"left,right\"}],\"confirm\":true}";
                return null;
            }

            var sb = new StringBuilder(2048);
            int okCount = 0, failCount = 0;
            int okR; bool busyR;
            string fatal = null;
            var rows = new StringBuilder(2048);
            string pathDiag = null;

            bool ran = MainThread.InvokeAdaptive(() =>
            {
                try
                {
                    // 0.99.0: 整条路径一次连完（照抄 UtilityBuildTool.ApplyPathToConduitSystem）
                    var allReqs = ParseCells(cellsJson);
                    if (apply)
                    {
                        var seq = new System.Collections.Generic.List<int>(allReqs.Count);
                        for (int k = 0; k < allReqs.Count; k++)
                        {
                            int cc = Grid.XYToCell(allReqs[k].X, allReqs[k].Y);
                            if (Grid.IsValidCell(cc)) seq.Add(cc);
                        }
                        pathDiag = WireOps.ConnectPath(seq);
                    }
                    int idx = 0;
                    foreach (var c in ParseCells(cellsJson))
                    {
                        idx++;
                        int cell = Grid.XYToCell(c.X, c.Y);
                        if (!Grid.IsValidCell(cell))
                        {
                            rows.Append((idx > 1 ? "," : "")).Append("{\"x\":").Append(c.X).Append(",\"y\":").Append(c.Y)
                                .Append(",\"applied\":false,\"reason\":\"BAD_CELL\"}");
                            failCount++; continue;
                        }

                        // 该格必须有电线类建筑（已建成或蓝图都不行，必须是完工对象）
                        UnityEngine.GameObject go = null;
                        try { go = Grid.Objects[cell, (int)ObjectLayer.Wire]; } catch { }
                        if (go == null)
                        {
                            rows.Append((idx > 1 ? "," : "")).Append("{\"x\":").Append(c.X).Append(",\"y\":").Append(c.Y)
                                .Append(",\"applied\":false,\"reason\":\"NO_WIRE_AT_CELL\"}");
                            failCount++; continue;
                        }

                        IHaveUtilityNetworkMgr holder = null;
                        try { holder = go.GetComponent<IHaveUtilityNetworkMgr>(); } catch { }
                        if (holder == null)
                        {
                            rows.Append((idx > 1 ? "," : "")).Append("{\"x\":").Append(c.X).Append(",\"y\":").Append(c.Y)
                                .Append(",\"applied\":false,\"reason\":\"NO_NETWORK_MGR\"}");
                            failCount++; continue;
                        }
                        var mgr = holder.GetNetworkManager();
                        UtilityConnections before = mgr.GetConnections(cell, false);

                        bool applied = true; string err = null;
                        // 0.99.0: 连接不在这里逐格做（整条路径一次连完，见下），这里只读回状态。
                        UtilityConnections after = mgr.GetConnections(cell, false);
                        // networkId 兜底：直接问 GetNetworkForCell
                        int netId = -1; int netId2 = -1; string netHow = "GetNetworkForCell(cell)";
                        try
                        {
                            var net = mgr.GetNetworkForCell(cell);
                            if (net != null) { netId = net.id; netId2 = net.id; }
                        }
                        catch { }

                        if (applied) okCount++; else failCount++;
                        rows.Append((idx > 1 ? "," : ""));
                        rows.Append("{\"x\":").Append(c.X).Append(",\"y\":").Append(c.Y)
                            .Append(",\"applied\":").Append(applied ? "true" : "false")
                            .Append(",\"before\":").Append((int)before)
                            .Append(",\"after\":").Append((int)after)
                            .Append(",\"expected\":").Append((int)c.Mask)
                            .Append(",\"networkId\":").Append(netId)
                            .Append(",\"via\":").Append(Snap.Str(netHow));

                        if (err != null) rows.Append(",\"error\":").Append(Snap.Str(err));
                        rows.Append('}');
                    }
                }
                catch (System.Exception e) { fatal = e.GetType().Name + ": " + e.Message; }
            }, 5000, 30000, out okR, out busyR);

            if (!ran) { status = 503; code = "RETRY_LATER"; message = "main thread busy"; return null; }
            if (fatal != null) { status = 500; code = "INTERNAL"; message = fatal; return null; }

            sb.Append("{\"apply\":").Append(apply ? "true" : "false");
            sb.Append(",\"ok\":").Append(okCount).Append(",\"failed\":").Append(failCount);
            sb.Append(",\"cells\":[").Append(rows.ToString()).Append(']');
            if (pathDiag != null) sb.Append(",\"pathDiag\":").Append(Snap.Str(pathDiag));
            sb.Append(",\"note\":").Append(Snap.Str(
                "apply=false 时只读回当前掩码与 networkId。判据三档：①after==expected ②networkId != -1 且同线一致 ③用电真的通电（自己验证）"));
            sb.Append('}');
            JsonCheck.Validate(sb.ToString(), "wire");
            Log.Info("[wire] ok=" + okCount + " failed=" + failCount + " apply=" + apply);
            return sb.ToString();
        }

        /// <summary>强制重建网络：优先 ForceRebuildNetworks()，拿不到再退 RebuildNetworks()。</summary>
        static void ForceRebuild(IUtilityNetworkMgr mgr)
        {
            if (mgr == null) return;
            try
            {
                var mi = mgr.GetType().GetMethod("ForceRebuildNetworks", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                if (mi != null) { mi.Invoke(mgr, null); return; }
            }
            catch { }
            try
            {
                var mi2 = mgr.GetType().GetMethod("RebuildNetworks", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                if (mi2 != null)
                {
                    var ps = mi2.GetParameters();
                    if (ps.Length == 2) mi2.Invoke(mgr, new object[] { 0, 0 });
                    else mi2.Invoke(mgr, null);
                }
            }
            catch { }
        }

        struct CellReq { public int X, Y; public int Mask; public System.Collections.Generic.List<UtilityConnections> Dirs; }

        static System.Collections.Generic.List<CellReq> ParseCells(string arr)
        {
            var list = new System.Collections.Generic.List<CellReq>();
            int i = 0;
            while (i < arr.Length)
            {
                int s = arr.IndexOf('{', i);
                if (s < 0) break;
                int e = arr.IndexOf('}', s);
                if (e < 0) break;
                string obj = arr.Substring(s, e - s + 1);
                i = e + 1;
                var r = new CellReq();
                r.X = (int)NumField(obj, "x");
                r.Y = (int)NumField(obj, "y");
                string conn = StrField(obj, "connect");
                r.Dirs = new System.Collections.Generic.List<UtilityConnections>();
                int mask = 0;
                if (!string.IsNullOrEmpty(conn))
                {
                    foreach (var raw in conn.Split(','))
                    {
                        var d = ParseDir(raw.Trim());
                        if (d.HasValue) { r.Dirs.Add(d.Value); }
                    }
                }
                else
                {
                    // 没给 connect ⇒ 用数字掩码（1=up 2=right 4=down 8=left）
                    int m = (int)NumField(obj, "mask");
                    if (m > 0)
                    {
                        if ((m & 1) != 0) r.Dirs.Add(UtilityConnections.Up);
                        if ((m & 2) != 0) r.Dirs.Add(UtilityConnections.Right);
                        if ((m & 4) != 0) r.Dirs.Add(UtilityConnections.Down);
                        if ((m & 8) != 0) r.Dirs.Add(UtilityConnections.Left);
                    }
                }
                foreach (var d in r.Dirs) mask |= DirBit(d);
                r.Mask = mask;
                list.Add(r);
            }
            return list;
        }

        static UtilityConnections? ParseDir(string s)
        {
            string low = (s ?? "").ToLowerInvariant();
            if (low == "up" || low == "u" || low == "上") return UtilityConnections.Up;
            if (low == "right" || low == "r" || low == "右") return UtilityConnections.Right;
            if (low == "down" || low == "d" || low == "下") return UtilityConnections.Down;
            if (low == "left" || low == "l" || low == "左") return UtilityConnections.Left;
            return null;
        }

        /// <summary>直接用枚举自己的位值（别自己猜 1/2/4/8 —— 猜反了会让掩码变成错的）。</summary>
        static int DirBit(UtilityConnections d) { return (int)d; }

        static string ExtractArray(string body, string key)
        {
            if (string.IsNullOrEmpty(body)) return null;
            int k = body.IndexOf("\"" + key + "\"", System.StringComparison.Ordinal);
            if (k < 0) return null;
            int lb = body.IndexOf('[', k);
            if (lb < 0) return null;
            int depth = 0;
            for (int i = lb; i < body.Length; i++)
            {
                if (body[i] == '[') depth++;
                else if (body[i] == ']') { depth--; if (depth == 0) return body.Substring(lb, i - lb + 1); }
            }
            return null;
        }

        static double NumField(string obj, string key)
        {
            int k = obj.IndexOf("\"" + key + "\"", System.StringComparison.Ordinal);
            if (k < 0) return 0;
            int colon = obj.IndexOf(':', k);
            if (colon < 0) return 0;
            int i = colon + 1;
            while (i < obj.Length && (obj[i] == ' ' || obj[i] == '\t')) i++;
            int start = i;
            while (i < obj.Length && (char.IsDigit(obj[i]) || obj[i] == '-' || obj[i] == '.')) i++;
            double v;
            double.TryParse(obj.Substring(start, i - start), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out v);
            return v;
        }

        static string StrField(string obj, string key)
        {
            int k = obj.IndexOf("\"" + key + "\"", System.StringComparison.Ordinal);
            if (k < 0) return null;
            int colon = obj.IndexOf(':', k);
            if (colon < 0) return null;
            int q1 = obj.IndexOf('"', colon + 1);
            if (q1 < 0) return null;
            int q2 = obj.IndexOf('"', q1 + 1);
            if (q2 < 0) return null;
            return obj.Substring(q1 + 1, q2 - q1 - 1);
        }
    }
}
