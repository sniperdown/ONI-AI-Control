using System.Text;
using UnityEngine;

namespace OniAiBridge
{
    /// <summary>
    /// `POST /v1/command {"op":"pipeLink",...}` —— **精确单方向**地连接 / 切断一格管线（气/液/固）或电线。
    ///
    /// ## 两条实现路径（都是 2026-11-18 真机验过的，不是猜的）
    /// * **连**：`IUtilityNetworkMgr.AddConnection(UtilityConnections, int32 cell, bool isVirtual) : void`（public）。
    /// * **断**：反射调 **`DisconnectTool.DisconnectCellsAction`**
    ///   —— 元数据真值：`private` / **`instance`** / 4 参 / 返回 void；
    ///   参数 `(int32 cell, GameObject go, IHaveUtilityNetworkMgr mgr, UtilityConnections connections)`；
    ///   实例取自 **`public static` 字段 `DisconnectTool.Instance`**。
    ///
    /// ## 判据（重要）
    /// **主判据 = `IUtilityNetworkMgr.GetConnections(cell, false)`**（游戏认账的那条）；
    /// `physicalGrid[cell].connections` 只作**辅证**，两者不等 ⇒ `inconsistent:true`。
    ///
    /// 为什么不用 `IDisconnectable.IsDisconnected()`：实测它**恒为 false**，且与真实状态矛盾
    /// （`Connect()` 返回 true 而掩码不动就是它）。0.94.0 拿它当判据是错的。
    /// 为什么没有 `bidirectional`：实测**双向冗余不成立** —— `AddConnection` 只改本格，
    /// 邻居那格的反方向位不会跟着置位（(126,217) 往空白格连，邻居 getter 始终 0）。
    ///
    /// ## 校验（游戏侧不拦，必须我们自己做）
    /// `AddConnection` **不做任何校验** —— 实测往"右边没有线"的格子连，它照样接受（掩码 1→3），
    /// 造出正常玩法不可能出现的悬空连接。所以本 op 在动作前拦三条：
    /// ① 本格必须有该层建筑；② 邻格必须有**同层 + 同 prefabId** 的建筑；③ 跨层（气/液/固/电混连）拒绝。
    /// </summary>
    internal static class PipeLink
    {
        static readonly int[] CandidateLayers = {
            (int)ObjectLayer.GasConduit,
            (int)ObjectLayer.LiquidConduit,
            (int)ObjectLayer.SolidConduit,
            (int)ObjectLayer.Wire,
        };
        static readonly string[] CandidateNames = { "gasConduit", "liquidConduit", "solidConduit", "wire" };

        static string LayerKindOf(int layer)
        {
            for (int i = 0; i < CandidateLayers.Length; i++)
                if (CandidateLayers[i] == layer) return CandidateNames[i];
            return "layer" + layer;
        }

        static int LayerFromName(string s)
        {
            if (string.IsNullOrEmpty(s)) return -1;
            for (int i = 0; i < CandidateNames.Length; i++)
                if (string.Equals(CandidateNames[i], s, System.StringComparison.OrdinalIgnoreCase)) return CandidateLayers[i];
            return -1;
        }

        static bool TryDir(string s, out UtilityConnections dir, out string err)
        {
            dir = UtilityConnections.Left; err = null;
            if (string.IsNullOrEmpty(s)) { err = "缺 dir（left|right|up|down）"; return false; }
            switch (s.Trim().ToLowerInvariant())
            {
                case "left": dir = UtilityConnections.Left; return true;
                case "right": dir = UtilityConnections.Right; return true;
                case "up": dir = UtilityConnections.Up; return true;
                case "down": dir = UtilityConnections.Down; return true;
                default: err = "dir 只能是 left|right|up|down，收到 \"" + s + "\""; return false;
            }
        }

        static string Dirs(UtilityConnections c)
        {
            var parts = new System.Collections.Generic.List<string>(4);
            if ((c & UtilityConnections.Left) != 0) parts.Add("\"left\"");
            if ((c & UtilityConnections.Right) != 0) parts.Add("\"right\"");
            if ((c & UtilityConnections.Up) != 0) parts.Add("\"up\"");
            if ((c & UtilityConnections.Down) != 0) parts.Add("\"down\"");
            return "[" + string.Join(",", parts.ToArray()) + "]";
        }

        /// <summary>prefabId：优先 `Building.Def.PrefabID`，退回 `KPrefabID.PrefabTag.Name`（两套写法本仓库都在用）。</summary>
        static string PrefabOf(UnityEngine.GameObject go)
        {
            if (go == null) return null;
            try
            {
                var b = go.GetComponent<Building>();
                if (b != null && b.Def != null && !string.IsNullOrEmpty(b.Def.PrefabID)) return b.Def.PrefabID;
            }
            catch { }
            try
            {
                var kp = go.GetComponent<KPrefabID>();
                if (kp != null && kp.PrefabTag.Name != null) return kp.PrefabTag.Name;
            }
            catch { }
            return null;
        }

        static System.Reflection.MethodInfo _disconnectAction;
        static System.Reflection.MethodInfo DisconnectAction()
        {
            if (_disconnectAction == null)
            {
                _disconnectAction = typeof(DisconnectTool).GetMethod("DisconnectCellsAction",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            }
            return _disconnectAction;
        }

        public static string Exec(string body, out int status, out string code, out string msg)
        {
            status = 200; code = null; msg = null;

            int x, y;
            if (!TryInt(body, "x", out x) || !TryInt(body, "y", out y))
            { status = 400; code = "BAD_REQUEST"; msg = "需要 x / y（整数格坐标）"; return null; }

            UtilityConnections dir;
            string derr;
            if (!TryDir(Str(body, "dir"), out dir, out derr))
            { status = 400; code = "BAD_REQUEST"; msg = derr; return null; }

            bool connect = true;
            if (HasBool(body, "connect"))
            {
                if (!TryBool(body, "connect", out connect))
                { status = 400; code = "BAD_REQUEST"; msg = "connect 必须是 true / false"; return null; }
            }
            bool dryRun = false;
            if (HasBool(body, "dryRun")) TryBool(body, "dryRun", out dryRun);

            int cell = Grid.XYToCell(x, y);
            if (!Grid.IsValidCell(cell))
            { status = 400; code = "BAD_REQUEST"; msg = "格坐标越界：(" + x + "," + y + ")"; return null; }

            int width = Grid.WidthInCells;
            int nb = cell;
            if (dir == UtilityConnections.Left) nb = cell - 1;
            else if (dir == UtilityConnections.Right) nb = cell + 1;
            else if (dir == UtilityConnections.Up) nb = cell + width;
            else if (dir == UtilityConnections.Down) nb = cell - width;
            if (!Grid.IsValidCell(nb))
            { status = 400; code = "BAD_REQUEST"; msg = "目标方向没有相邻格"; return null; }
            {
                int nbx, nby;
                Grid.CellToXY(nb, out nbx, out nby);
                if (Mathf.Abs(nbx - x) + Mathf.Abs(nby - y) != 1)
                { status = 400; code = "BAD_REQUEST"; msg = "跨世界/越界：邻格算到 (" + nbx + "," + nby + ")，不是四邻之一"; return null; }
            }

            // ---- 选层：显式 layer 优先；否则在本格四个候选层里挑唯一的非空 ----
            int layer = LayerFromName(Str(body, "layer"));
            if (layer < 0)
            {
                int found = 0;
                for (int i = 0; i < CandidateLayers.Length; i++)
                {
                    var o = Grid.Objects[cell, CandidateLayers[i]];
                    if (o != null) { found++; layer = CandidateLayers[i]; }
                }
                if (found == 0)
                { status = 404; code = "NOT_FOUND_TARGET"; msg = "该格 (" + x + "," + y + ") 上没有气/液/固管线或电线"; return null; }
                if (found > 1)
                { status = 400; code = "AMBIGUOUS_LAYER"; msg = "该格有多种可连层，请显式给 layer=gasConduit|liquidConduit|solidConduit|wire"; return null; }
            }

            // ---- 校验①：本格必须有该层建筑 ----
            var objA = Grid.Objects[cell, layer];
            if (objA == null)
            {
                status = 404; code = "NOT_FOUND_TARGET";
                msg = "该格在层 " + LayerKindOf(layer) + " 上没有对象";
                return null;
            }

            // ---- 校验②③：邻格必须同层 + 同 prefabId（跨层/不同线种都在这里被拒）----
            var objB = Grid.Objects[nb, layer];
            if (objB == null)
            {
                status = 400; code = "NEIGHBOUR_NOT_CONDUIT";
                msg = "目标方向那格在层 " + LayerKindOf(layer) + " 上没有同类管线/电线（气/液/固/电不能混连）";
                return null;
            }
            string prefabA = PrefabOf(objA), prefabB = PrefabOf(objB);
            if (!string.IsNullOrEmpty(prefabA) && !string.IsNullOrEmpty(prefabB)
                && !string.Equals(prefabA, prefabB, System.StringComparison.Ordinal))
            {
                status = 400; code = "PREFAB_MISMATCH";
                msg = "两格不是同一种线/管：" + prefabA + " ≠ " + prefabB + "（游戏里混用会标红拒绝）";
                return null;
            }

            var ih = objA.GetComponent<IHaveUtilityNetworkMgr>();
            var mgr = ih != null ? ih.GetNetworkManager() : null;
            if (mgr == null)
            { status = 503; code = "NOT_READY"; msg = "拿不到该格的 IUtilityNetworkMgr"; return null; }

            var grid = GridOf(mgr);
            UtilityConnections beforeGetter = mgr.GetConnections(cell, false);
            int beforeArray = (grid != null && cell < grid.Length) ? (int)grid[cell].connections : -1;
            int beforeNetId = -1;
            try { var n0 = mgr.GetNetworkForCell(cell); beforeNetId = n0 != null ? n0.id : -1; } catch { }

            UtilityConnections expected = connect ? (beforeGetter | dir) : (beforeGetter & ~dir);
            bool alreadyInState = connect ? ((beforeGetter & dir) != 0) : ((beforeGetter & dir) == 0);

            string via = null, failReason = null;
            if (dryRun)
            {
                via = "dryRun（只读：未改动任何状态）";
            }
            else if (alreadyInState)
            {
                via = "noop（该方向已经是请求的状态，未改动任何状态）";
            }
            else if (connect)
            {
                mgr.AddConnection(dir, cell, false);
                via = "IUtilityNetworkMgr.AddConnection(" + dir + ", " + cell + ", false)";
            }
            else
            {
                var inst = DisconnectTool.Instance;
                var mi = DisconnectAction();
                if (inst == null) { status = 503; code = "TOOL_UNAVAILABLE"; msg = "DisconnectTool.Instance 为 null（工具未实例化）"; return null; }
                if (mi == null) { status = 500; code = "INTERNAL"; msg = "反射找不到 DisconnectTool.DisconnectCellsAction"; return null; }
                try
                {
                    // ⚠️ 第 3 参是 IHaveUtilityNetworkMgr（组件本体），不是 IUtilityNetworkMgr（管理器）。
                    mi.Invoke(inst, new object[] { cell, objA, ih, dir });
                    via = "DisconnectTool.DisconnectCellsAction(cell, go, IHaveUtilityNetworkMgr, " + dir + ") [reflection]";
                }
                catch (System.Reflection.TargetInvocationException tie)
                {
                    failReason = tie.InnerException != null ? tie.InnerException.GetType().Name + ": " + tie.InnerException.Message : tie.Message;
                }
                catch (System.Exception e) { failReason = e.GetType().Name + ": " + e.Message; }
            }

            // ---- 回读：**只读一次，就在同一个主线程调用里** ----
            //   ⚠️ 这里绝对不能 Sleep 再读：本方法整体跑在 MainThread.InvokeAdaptive 的那一次调用里，
            //   在里面睡 1s/3s 会把整个游戏主线程堵住（0.94.6 首测实测：心跳停了 33 秒，后续请求全 RETRY_LATER）。
            //   ⇒ 只给"动作后立刻"的读数；判"3 秒后有没有被重建改掉"由调用方隔 1s/3s 再发一次 `dryRun:true`（纯读）。
            int afterGetter = (int)mgr.GetConnections(cell, false);
            int afterArray = (grid != null) ? (int)grid[cell].connections : -1;
            int afterNetId = beforeNetId;
            try { var n1 = mgr.GetNetworkForCell(cell); afterNetId = n1 != null ? n1.id : -1; } catch { }

            bool applied = !dryRun && (failReason == null) && (afterGetter == (int)expected);
            bool inconsistent = (afterArray != afterGetter);

            var sb = new StringBuilder(768);
            sb.Append("{\"op\":\"pipeLink\"");
            sb.Append(",\"cell\":").Append(cell).Append(",\"x\":").Append(x).Append(",\"y\":").Append(y);
            sb.Append(",\"neighbourCell\":").Append(nb);
            sb.Append(",\"layer\":").Append(Snap.Str(LayerKindOf(layer)));
            sb.Append(",\"prefabId\":").Append(Snap.Str(prefabA));
            sb.Append(",\"dir\":").Append(Snap.Str(dir.ToString()));
            sb.Append(",\"connectRequested\":").Append(connect ? "true" : "false");
            sb.Append(",\"alreadyInRequestedState\":").Append(alreadyInState ? "true" : "false");
            sb.Append(",\"via\":").Append(Snap.Str(via));
            if (failReason != null) sb.Append(",\"failReason\":").Append(Snap.Str(failReason));
            sb.Append(",\"dryRun\":").Append(dryRun ? "true" : "false");
            sb.Append(",\"before\":{\"getter\":").Append((int)beforeGetter).Append(",\"array\":").Append(beforeArray)
              .Append(",\"networkId\":").Append(beforeNetId).Append('}');
            sb.Append(",\"expectedGetter\":").Append((int)expected);
            sb.Append(",\"after\":{\"getter\":").Append(afterGetter).Append(",\"array\":").Append(afterArray)
              .Append(",\"networkId\":").Append(afterNetId).Append('}');
            sb.Append(",\"applied\":").Append(applied ? "true" : "false");
            sb.Append(",\"inconsistent\":").Append(inconsistent ? "true" : "false");
            sb.Append(",\"note\":").Append(Snap.Str(
                "方向语义：Left/Right = x-1/x+1，Up/Down = y+1/y-1（ONI 的 y 向上）。"
                + "**主判据是 after.getter == expectedGetter**（IUtilityNetworkMgr.GetConnections，游戏认账那条）；"
                + "after.array 是 physicalGrid 里存的那份，只作辅证，和 getter 不等时 inconsistent:true。"
                + "连走 AddConnection，断走反射 DisconnectTool.DisconnectCellsAction（private instance）；两者都只动**单方向**。"
                + "校验：目标格必须有该层建筑、邻格必须同层且同 prefabId（游戏自身的 AddConnection 不校验，会造出悬空连接）。"
                + "after 是**动作后立刻**的读数（本方法跑在主线程里，不能在里面 Sleep）；要确认'游戏重建后有没有被改掉'，"
                + "请隔 1s/3s 再发一次 {\"dryRun\":true} 纯读本格。"
                + "不含 isDisconnected（实测恒 false 且与真实状态矛盾）与 bidirectional（双向冗余已证不成立）。"));
            sb.Append('}');
            string json = sb.ToString();
            JsonCheck.Validate(json, "pipeLink");
            Log.Info("[pipeLink] cell=" + cell + " " + dir + " connect=" + connect + " dryRun=" + dryRun
                     + " getter " + (int)beforeGetter + "->" + afterGetter + " applied=" + applied + " err=" + (failReason ?? "-"));
            return json;
        }

        static IUtilityNetworkMgr ManagerOf(IHaveUtilityNetworkMgr ih)
        {
            try { return ih != null ? ih.GetNetworkManager() : null; } catch { return null; }
        }

        /// <summary>`physicalGrid` 是 protected/private ⇒ 反射拿数组引用（元数据：`GetGrid(bool)` 为 protected）。</summary>
        static UtilityNetworkGridNode[] GridOf(IUtilityNetworkMgr mgr)
        {
            if (mgr == null) return null;
            var bf = System.Reflection.BindingFlags.Instance
                   | System.Reflection.BindingFlags.NonPublic
                   | System.Reflection.BindingFlags.Public;
            var t = mgr.GetType();
            while (t != null)
            {
                try
                {
                    var mi = t.GetMethod("GetGrid", bf);
                    if (mi != null)
                    {
                        var arr = mi.Invoke(mgr, new object[] { false }) as UtilityNetworkGridNode[];
                        if (arr != null) return arr;
                    }
                }
                catch { }
                try
                {
                    var fi = t.GetField("physicalGrid", bf);
                    if (fi != null)
                    {
                        var arr = fi.GetValue(mgr) as UtilityNetworkGridNode[];
                        if (arr != null) return arr;
                    }
                }
                catch { }
                t = t.BaseType;
            }
            return null;
        }

        // ---- 极简 JSON 取值 ----
        static string Str(string body, string key)
        {
            if (string.IsNullOrEmpty(body)) return null;
            var m = System.Text.RegularExpressions.Regex.Match(body, "\"" + key + "\"\\s*:\\s*\"([^\"]*)\"");
            return m.Success ? m.Groups[1].Value : null;
        }

        static bool TryInt(string body, string key, out int val)
        {
            val = 0;
            if (string.IsNullOrEmpty(body)) return false;
            var m = System.Text.RegularExpressions.Regex.Match(body, "\"" + key + "\"\\s*:\\s*(-?\\d+)");
            if (!m.Success) return false;
            return int.TryParse(m.Groups[1].Value, out val);
        }

        static bool HasBool(string body, string key)
        {
            if (string.IsNullOrEmpty(body)) return false;
            return System.Text.RegularExpressions.Regex.IsMatch(body, "\"" + key + "\"\\s*:\\s*(true|false|\"true\"|\"false\")",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        }

        static bool TryBool(string body, string key, out bool val)
        {
            val = false;
            if (string.IsNullOrEmpty(body)) return false;
            var m = System.Text.RegularExpressions.Regex.Match(body, "\"" + key + "\"\\s*:\\s*(true|false|\"true\"|\"false\")",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (!m.Success) return false;
            val = m.Groups[1].Value.Trim('"').Equals("true", System.StringComparison.OrdinalIgnoreCase);
            return true;
        }
    }
}
