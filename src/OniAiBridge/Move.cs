using System.Collections.Generic;
using System.Text;

namespace OniAiBridge
{
    /// <summary>
    /// 0.101.0 —— **挂游戏自己的导航**，不再自己反推规则。
    ///
    /// 真名（IL 与类型表核实）：
    ///   * 可达判定：`Navigator.CanReach(int cell)`（还有 1/2 参重载）—— 复制人自己算"我到不到得了"
    ///   * 距离/代价：`Navigator.GetNavigationCost(int cell)` / `GetNavigationCostIgnoreProberOffset`
    ///   * 区域查询：`Navigator.RunQuery(PathFinderQuery)`（游戏自己的 flood fill）
    ///   * 移动指令：`Navigator.MoveToLocation(...)`（玩家工具 `MoveToLocationTool.SetMoveToLocation` 里调的就是它）
    ///               另有 `Navigator.GoTo(...)`（2/3 参）被 IdleStates / BuzzStates 用
    ///   * 路径显示：`Navigator.DrawPath()` —— 由 `Game.LateUpdate` 每帧调（画面里那条线就是它画的）
    ///
    /// 之前我们用自己的 BFS（`Grid.DupeImpassable` 4 邻 + 地板），实测与游戏判据有偏差：
    /// 对角移动、陡坎、跳跃档我们全没有 —— 所以改成**优先问游戏**，BFS 只留作"为什么"的兜底解释。
    /// </summary>
    internal static class Move
    {
        sealed class DupeRef
        {
            public string Name;
            public int Cell;
            public Navigator Nav;
        }

        static List<DupeRef> Dupes()
        {
            var list = new List<DupeRef>();
            try
            {
                var ids = Components.LiveMinionIdentities != null ? Components.LiveMinionIdentities.Items : null;
                if (ids == null) return list;
                for (int i = 0; i < ids.Count; i++)
                {
                    var id = ids[i];
                    if (id == null) continue;
                    Navigator nav = null;
                    try { nav = id.GetComponent<Navigator>(); } catch { }
                    string nm = null;
                    try { nm = id.GetProperName(); } catch { }
                    int cell = -1;
                    try { cell = Grid.PosToCell(id.transform.position); } catch { }
                    list.Add(new DupeRef { Name = nm, Cell = cell, Nav = nav });
                }
            }
            catch { }
            return list;
        }

        /// <summary>游戏自己的可达判定（`Navigator.CanReach`）。失败返回 null 表示"这个方法没调通"。</summary>
        internal static bool? CanReach(Navigator nav, int cell)
        {
            if (nav == null) return null;
            try
            {
                foreach (var mi in nav.GetType().GetMethods())
                {
                    if (mi.Name != "CanReach") continue;
                    var ps = mi.GetParameters();
                    if (ps.Length != 1) continue;
                    if (ps[0].ParameterType != typeof(int)) continue;
                    var rv = mi.Invoke(nav, new object[] { cell });
                    if (rv is bool) return (bool)rv;
                }
            }
            catch { }
            return null;
        }

        internal static int? Cost(Navigator nav, int cell)
        {
            if (nav == null) return null;
            try
            {
                foreach (var mi in nav.GetType().GetMethods())
                {
                    if (mi.Name != "GetNavigationCost") continue;
                    var ps = mi.GetParameters();
                    if (ps.Length == 1 && ps[0].ParameterType == typeof(int))
                    {
                        var rv = mi.Invoke(nav, new object[] { cell });
                        if (rv is int) return (int)rv;
                    }
                }
            }
            catch { }
            return null;
        }

        /// <summary>
        /// `POST /v1/move` {"dupe":"卡塔莉娜","x":120,"y":205}
        /// 先问 `Navigator.CanReach`；到不了就不下指令（并给出原因）。
        /// 到得了就按候选顺序尝试游戏自己的移动入口，回报实际用的是哪个（`via`）——**不猜，试通哪个报哪个**。
        /// </summary>
        public static string Execute(string body, out int status, out string code, out string message)
        {
            status = 200; code = null; message = null;

            string want = MatchStr(body, "dupe");
            int tx = -1, ty = -1;
            int kx = body.IndexOf("\"x\"", System.StringComparison.Ordinal);
            if (kx >= 0) int.TryParse(TrimNum(body, kx), out tx);
            int ky = body.IndexOf("\"y\"", System.StringComparison.Ordinal);
            if (ky >= 0) int.TryParse(TrimNum(body, ky), out ty);
            if (tx < 0 || ty < 0) { status = 400; code = "BAD_REQUEST"; message = "需要 x / y"; return null; }

            string data = null; string fatal = null;
            int okR; bool busyR;
            bool ran = MainThread.InvokeAdaptive(() =>
            {
                try
                {
                    var dupes = Dupes();
                    if (dupes.Count == 0) { fatal = "找不到复制人"; return; }
                    var d = dupes[0];
                    if (!string.IsNullOrEmpty(want))
                    {
                        for (int i = 0; i < dupes.Count; i++)
                        {
                            var q = dupes[i];
                            if (q.Name != null && (q.Name == want || q.Name.StartsWith(want, System.StringComparison.Ordinal)))
                            { d = q; break; }
                        }
                    }
                    int target = Grid.XYToCell(tx, ty);
                    if (!Grid.IsValidCell(target)) { fatal = "目标格非法"; return; }

                    bool? can = CanReach(d.Nav, target);
                    int? cost = Cost(d.Nav, target);

                    var sb = new StringBuilder(512);
                    sb.Append("{\"dupe\":").Append(Snap.Str(d.Name));
                    sb.Append(",\"from\":{\"cell\":").Append(d.Cell).Append("}");
                    sb.Append(",\"target\":{\"x\":").Append(tx).Append(",\"y\":").Append(ty).Append(",\"cell\":").Append(target).Append('}');
                    sb.Append(",\"canReach\":").Append(can.HasValue ? (can.Value ? "true" : "false") : "null");
                    sb.Append(",\"cost\":").Append(cost.HasValue ? cost.Value.ToString() : "null");
                    sb.Append(",\"judge\":").Append(Snap.Str("Navigator.CanReach"));

                    if (can.HasValue && !can.Value)
                    {
                        sb.Append(",\"moved\":false,\"reason\":").Append(Snap.Str("游戏判据说这个复制人到不了该格（换成全队任一人再试）"));
                        sb.Append('}');
                        data = sb.ToString();
                        return;
                    }

                    string via = null;
                    // 候选顺序：游戏工具用的 MoveToLocation → IdleStates 用的 GoTo
                    via = TryInvoke(d.Nav, "MoveToLocation", target);
                    if (via == null) via = TryInvoke(d.Nav, "GoTo", target);

                    sb.Append(",\"moved\":").Append(via != null ? "true" : "false");
                    sb.Append(",\"via\":").Append(via == null ? "null" : Snap.Str(via));
                    if (via == null) sb.Append(",\"reason\":").Append(Snap.Str("所有候选移动入口都没调通"));
                    sb.Append(",\"note\":").Append(Snap.Str("已下移动指令；位置变化请稍后读 /v1/state（小人是按路径走的，不是瞬移）"));
                    sb.Append('}');
                    data = sb.ToString();
                }
                catch (System.Exception e) { fatal = e.GetType().Name + ": " + e.Message; }
            }, 5000, 20000, out okR, out busyR);

            if (!ran) { status = 503; code = "RETRY_LATER"; message = "main thread busy"; return null; }
            if (fatal != null) { status = 500; code = "MOVE_FAILED"; message = fatal; return null; }
            JsonCheck.Validate(data, "move");
            return data;
        }

        /// <summary>在 Navigator 上按名字找可用的移动入口，逐个试；返回实际成功的签名描述。</summary>
        static string TryInvoke(Navigator nav, string name, int cell)
        {
            if (nav == null) return null;
            try
            {
                foreach (var mi in nav.GetType().GetMethods())
                {
                    if (mi.Name != name) continue;
                    var ps = mi.GetParameters();
                    object[] args;
                    if (ps.Length == 1 && ps[0].ParameterType == typeof(int)) args = new object[] { cell };
                    else if (ps.Length == 2 && ps[0].ParameterType == typeof(int)) args = new object[] { cell, null };
                    else if (ps.Length == 3 && ps[0].ParameterType == typeof(int)) args = new object[] { cell, null, null };
                    else continue;
                    try
                    {
                        mi.Invoke(nav, args);
                        return name + "/" + ps.Length + "params";
                    }
                    catch { }
                }
            }
            catch { }
            return null;
        }

        static string TrimNum(string body, int keyIdx)
        {
            int colon = body.IndexOf(':', keyIdx);
            if (colon < 0) return null;
            int i = colon + 1;
            while (i < body.Length && (body[i] == ' ' || body[i] == '"')) i++;
            int s = i;
            while (i < body.Length && (char.IsDigit(body[i]) || body[i] == '-')) i++;
            return body.Substring(s, i - s);
        }

        static string MatchStr(string body, string key)
        {
            int k = body.IndexOf("\"" + key + "\"", System.StringComparison.Ordinal);
            if (k < 0) return null;
            int colon = body.IndexOf(':', k);
            if (colon < 0) return null;
            int q1 = body.IndexOf('"', colon);
            if (q1 < 0) return null;
            int q2 = body.IndexOf('"', q1 + 1);
            if (q2 < 0) return null;
            return body.Substring(q1 + 1, q2 - q1 - 1);
        }
    }
}
