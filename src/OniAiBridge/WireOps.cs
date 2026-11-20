using System.Text;

namespace OniAiBridge
{
    /// <summary>
    /// 电力/管线"连线"操作 —— **直接照抄游戏自己的配方**（IL 已核）。
    ///
    /// 出处：`UtilityBuildTool.ApplyPathToConduitSystem()`（基类同名方法是空实现，
    /// 方法体就一句 `DebugUtil.Assert("I don't think this function ever runs")`）。
    /// 它对 `path` 里每一对相邻节点做：
    ///
    ///     dir = UtilityConnectionsExtensions.DirectionFromToCell(cellA, cellB)
    ///     inv = UtilityConnectionsExtensions.InverseDirection(dir)
    ///     if (mgr.CanAddConnection(dir, cellA, false, ref x) || mgr.CanAddConnection(inv, cellB, false, ref x))
    ///     {
    ///         mgr.AddConnection(dir, cellA, false);
    ///         mgr.AddConnection(inv, cellB, false);
    ///     }
    ///
    /// **注意**：这条路径里游戏**不调** `SetConnections` / `AddToNetworks` /
    /// `ForceRebuildNetworks` —— 我们 0.98.2 那套绕远了，实测反而把节点弄丢。
    ///
    /// 单格（path.Count == 1）时循环一次都不进 ⇒ **一根连接都不设**（与实测到的
    /// "单格建线是 mask=0 孤岛"一致）。
    /// </summary>
    internal static class WireOps
    {
        /// <summary>cellA→cellB 的方向（游戏原函数，坐标算方向，不查掩码）。</summary>
        internal static UtilityConnections? DirFromTo(int cellA, int cellB)
        {
            try { return UtilityConnectionsExtensions.DirectionFromToCell(cellA, cellB); }
            catch { return null; }
        }

        internal static UtilityConnections? Inverse(UtilityConnections d)
        {
            try { return UtilityConnectionsExtensions.InverseDirection(d); }
            catch { return null; }
        }

        static System.Reflection.MethodInfo _canAdd;
        static System.Reflection.ParameterInfo[] _canAddParams;

        /// <summary>
        /// `IUtilityNetworkMgr.CanAddConnection(dir, cell, isVirtual, ref ?)` —— 第 4 参是 by-ref。
        /// 用反射统一处理（by-ref 参数传 ElementType 的默认值，调用后由 CLR 回填）。
        /// </summary>
        internal static bool CanAdd(IUtilityNetworkMgr mgr, UtilityConnections dir, int cell, out string err)
        {
            err = null;
            if (mgr == null) { err = "mgr=null"; return false; }
            try
            {
                if (_canAdd == null)
                {
                    foreach (var t in new System.Type[] { typeof(IUtilityNetworkMgr), mgr.GetType() })
                    {
                        foreach (var mi in t.GetMethods())
                            if (mi.Name == "CanAddConnection") { _canAdd = mi; break; }
                        if (_canAdd != null) break;
                    }
                    if (_canAdd != null) _canAddParams = _canAdd.GetParameters();
                }
                if (_canAdd == null) { err = "找不到 CanAddConnection"; return true; }   // 退路：不拦

                var args = new object[_canAddParams.Length];
                args[0] = dir;
                args[1] = cell;
                if (args.Length > 2) args[2] = false;
                for (int i = 3; i < args.Length; i++)
                {
                    var pt = _canAddParams[i].ParameterType;
                    if (pt.IsByRef) { var et = pt.GetElementType(); args[i] = et.IsValueType ? System.Activator.CreateInstance(et) : null; }
                    else args[i] = pt.IsValueType ? System.Activator.CreateInstance(pt) : null;
                }
                var rv = _canAdd.Invoke(mgr, args);
                return rv is bool && (bool)rv;
            }
            catch (System.Exception e) { err = e.GetType().Name; return true; }   // 退路：守卫失效不阻断
        }

        internal static bool Add(IUtilityNetworkMgr mgr, UtilityConnections dir, int cell, out string err)
        {
            err = null;
            try { mgr.AddConnection(dir, cell, false); return true; }
            catch (System.Exception e) { err = e.GetType().Name; return false; }
        }

        /// <summary>取该格（Wire 层）的网络管理器。</summary>
        internal static IUtilityNetworkMgr ManagerAt(int cell)
        {
            // 0.99.1：**先找 Wire 层，再找 Building 层**。
            //   实测：刚放下的电线是**蓝图**（在建），它不在 Wire 层而在 Building 层，
            //   只查 Wire 层会全部 NO_MGR（这就是 wirePath 连线全失败的原因）。
            try
            {
                // ★ 0.99.2：管理器是**全局**的 `Game.Instance.electricalConduitSystem`
                //   （`Wire.OnSpawn` 里就是这么拿的），**不是**从格子上的对象取组件——
                //   刚放下的电线是蓝图、身上没有网络管理器，所以之前全 NO_MGR。
                try
                {
                    var gm = Game.Instance;
                    if (gm != null)
                    {
                        var sys = gm.electricalConduitSystem;
                        if (sys != null)
                        {
                            var asMgr = sys as IUtilityNetworkMgr;
                            if (asMgr != null) return asMgr;
                        }
                    }
                }
                catch { }
                var layers = new int[] { (int)ObjectLayer.Wire, (int)ObjectLayer.Building };
                for (int i = 0; i < layers.Length; i++)
                {
                    var go = Grid.Objects[cell, layers[i]];
                    if (go == null) continue;
                    var h = go.GetComponent<IHaveUtilityNetworkMgr>();
                    if (h != null)
                    {
                        var m = h.GetNetworkManager();
                        if (m != null) return m;
                    }
                }
                return null;
            }
            catch { return null; }
        }

        /// <summary>
        /// 把相邻两格连起来 —— **完全照游戏配方**：先两侧守卫，任一通过就两侧都 AddConnection。
        /// 返回人话的诊断（给 JSON 用）。
        /// </summary>
        internal static string ConnectPair(int cellA, int cellB)
        {
            var dir = DirFromTo(cellA, cellB);
            if (dir == null) return "NO_DIRECTION";
            var inv = Inverse(dir.Value);
            if (inv == null) return "NO_INVERSE";

            var mgrA = ManagerAt(cellA);
            var mgrB = ManagerAt(cellB);
            if (mgrA == null || mgrB == null) return "NO_MGR";

            string e1, e2;
            bool canA = CanAdd(mgrA, dir.Value, cellA, out e1);
            bool canB = CanAdd(mgrB, inv.Value, cellB, out e2);

            if (!canA && !canB) return "CANNOT_ADD(guard)";

            string e3, e4;
            bool okA = Add(mgrA, dir.Value, cellA, out e3);
            bool okB = Add(mgrB, inv.Value, cellB, out e4);
            if (!okA || !okB) return "ADD_FAILED(" + (e3 ?? e4 ?? "?") + ")";
            return "CONNECTED(" + dir.Value + "/" + inv.Value + ")";
        }

        /// <summary>把一条格子序列当 path：相邻两格依次 ConnectPair。返回每对的诊断。</summary>
        internal static string ConnectPath(System.Collections.Generic.List<int> cells)
        {
            if (cells == null || cells.Count < 2) return "PATH_TOO_SHORT(单格不连线，与游戏一致)";
            var parts = new System.Collections.Generic.List<string>(cells.Count);
            for (int i = 0; i + 1 < cells.Count; i++)
                parts.Add(i + ":" + ConnectPair(cells[i], cells[i + 1]));
            return string.Join("; ", parts);
        }
    }
}
