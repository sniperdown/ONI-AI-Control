using System.Collections.Generic;
using System.Text;

namespace OniAiBridge
{
    /// <summary>
    /// `POST /v1/reach` —— **这个格子够不够得到**（下标记之前先问）。
    ///
    /// 为什么需要：盲测里 AI 给"够不到的格子"下挖掘标记 ⇒ 小人拿不到活、原地卡死
    /// （还有一次把小人传送进实心岩）。修法不是改 chore，而是**让它下标记前能问**。
    ///
    /// **判据全部照抄游戏自己的导航数据**（不自己发明规则）：
    ///   * `Grid.DupeImpassable[cell]` —— 游戏给复制人的"过不去"标志
    ///   * `Grid.Solid[cell]` —— 实心方块（要挖）
    ///   * `Grid.HasLadder[cell]` / `Grid.HasPole[cell]` —— 梯子/滑杆（可以垂直移动）
    ///   * `Grid.Liquid[cell]` + `Grid.LiquidDepth` —— 液体（会淹）
    /// 这些都是 `NavGrid` 构建时的同一批数据。
    ///
    /// 两条 BFS 分别回答"现在能走到"和"加了梯子能不能走到"，两者之差就是 **need_ladder**。
    ///
    /// 请求：
    ///   {"from":{"x":114,"y":217}, "cells":[[120,205],[121,205]], "maxCells":20000}
    ///   from 省略时用**总部所在格**。
    /// 响应每格：`reachable` / `dist` / `reason`
    ///   reason ∈ null | "need_ladder" | "need_dig" | "blocked_by_liquid" | "disconnected"
    /// </summary>
    internal static class Reach
    {
        const int MaxBatch = 400;

        public static string Query(string body, out int status, out string code, out string message)
        {
            status = 200; code = null; message = null;

            // ---- 起点 ----
            int sx = -1, sy = -1;
            int kx = body.IndexOf("\"from\"", System.StringComparison.Ordinal);
            if (kx >= 0)
            {
                sx = int.Parse(FieldNear(body, kx, "x", "0"));
                sy = int.Parse(FieldNear(body, kx, "y", "0"));
            }

            // ---- 目标格 ----
            var targets = new List<int>();
            var targetXY = new List<int[]>();
            int kc = body.IndexOf("\"cells\"", System.StringComparison.Ordinal);
            if (kc < 0) { status = 400; code = "BAD_REQUEST"; message = "需要 cells：[[x,y],...]"; return null; }
            int lb = body.IndexOf('[', kc);
            int depth = 0, end = -1;
            for (int i = lb; i < body.Length; i++)
            {
                if (body[i] == '[') depth++;
                else if (body[i] == ']') { depth--; if (depth == 0) { end = i; break; } }
            }
            if (end < 0) { status = 400; code = "BAD_REQUEST"; message = "cells 括号不匹配"; return null; }
            string arr = body.Substring(lb, end - lb + 1);
            int p = 1;   // ★ 跳过外层 '['，否则第一格会被吃掉（arr.IndexOf('[',0) 命中的是外层括号）
            while (p < arr.Length && targetXY.Count < MaxBatch)
            {
                int s = arr.IndexOf('[', p);
                if (s < 0) break;
                int e = arr.IndexOf(']', s);
                if (e < 0) break;
                var parts = arr.Substring(s + 1, e - s - 1).Split(',');
                if (parts.Length >= 2)
                {
                    int x, y;
                    if (int.TryParse(parts[0].Trim(), out x) && int.TryParse(parts[1].Trim(), out y))
                    { targetXY.Add(new int[] { x, y }); targets.Add(-1); }
                }
                p = e + 1;
            }
            if (targetXY.Count == 0) { status = 400; code = "BAD_REQUEST"; message = "cells 为空"; return null; }

            string data = null;
            string fatal = null;
            int okR; bool busyR;

            bool ran = MainThread.InvokeAdaptive(() =>
            {
                try
                {
                    // 起点缺省 = 总部
                    int start = -1;
                    if (sx >= 0 && sy >= 0) start = Grid.XYToCell(sx, sy);
                    if (start < 0 || !Grid.IsValidCell(start)) start = ResolveStart();
                    if (start < 0 || !Grid.IsValidCell(start))
                    {
                        fatal = "找不到起点（没给 from，也没找到 Headquarters）";
                        return;
                    }

                    sx = Grid.CellToXY(start).x; sy = Grid.CellToXY(start).y;

                    // 两条 BFS：只走地面 / 允许梯子
                    var distWalk = Bfs(start, false, 20000);
                    var distLadder = Bfs(start, true, 20000);

                    var sb = new StringBuilder(2048);
                    sb.Append("{\"from\":{\"x\":").Append(sx).Append(",\"y\":").Append(sy).Append(",\"cell\":").Append(start).Append('}');
                    sb.Append(",\"rule\":").Append(Snap.Str("BFS on Grid.DupeImpassable/Solid/HasLadder（与 NavGrid 同源）"));
                    sb.Append(",\"cells\":[");
                    for (int i = 0; i < targetXY.Count; i++)
                    {
                        int x = targetXY[i][0], y = targetXY[i][1];
                        int cell = Grid.XYToCell(x, y);
                        if (i > 0) sb.Append(',');
                        sb.Append("{\"x\":").Append(x).Append(",\"y\":").Append(y).Append(",\"cell\":").Append(cell);

                        if (!Grid.IsValidCell(cell))
                        {
                            sb.Append(",\"reachable\":false,\"dist\":null,\"reason\":\"bad_cell\"}");
                            continue;
                        }

                        int dw, dl;
                        bool hasW = distWalk.TryGetValue(cell, out dw);
                        bool hasL = distLadder.TryGetValue(cell, out dl);
                        bool solid = false; bool liquid = false; int depthKg = 0;
                        try { solid = Grid.Solid[cell]; } catch { }
                        try
                        {
                            var el = Grid.Element[cell];
                            if (el != null && el.state == Element.State.Liquid) { liquid = true; depthKg = (int)Grid.Mass[cell]; }
                        }
                        catch { }

                        string reason = null;
                        bool reachable = hasW;
                        if (!reachable)
                        {
                            if (hasL) reason = "need_ladder";
                            else if (solid) reason = "need_dig";
                            else if (liquid || depthKg > 0) reason = "blocked_by_liquid";
                            else
                            {
                                // 气体格、脚下没地板/梯子 ⇒ 站不住（不是"不连通"，是"没地方站"）
                                bool noFloor = false;
                                try
                                {
                                    int below = Grid.CellBelow(cell);
                                    bool belowSolid = false, belowLadder = false, belowPole = false;
                                    if (Grid.IsValidCell(below))
                                    {
                                        belowSolid = Grid.Solid[below];
                                        belowLadder = Grid.HasLadder[below];
                                        belowPole = Grid.HasPole[below];
                                    }
                                    bool ladderHere = Grid.HasLadder[cell] || Grid.HasPole[cell];
                                    noFloor = !belowSolid && !belowLadder && !belowPole && !ladderHere;
                                }
                                catch { }
                                reason = noFloor ? "no_floor" : "disconnected";
                            }
                        }
                        // ★ 0.113.0：**主判据换成游戏自己的 `Navigator.CanReach`**。
                        //   理由（实测）：我们的 BFS 只走四邻域 + 地板，漏掉一整类真实可走的情况 ——
                        //   * 对角台阶/斜通道（泥巴区实测：83 个空气格里 14 格游戏说能走而我们说不能）
                        //   * 跨 1 格间隙（游戏能跳）
                        //   * 爬 2 格陡坎
                        //   而且它还会**误判**：梯子顶被天花板压住时我们说能站，游戏说站不住（角色要占高度）。
                        //   没有 dupe 时才退回 BFS，并如实标注 judge=bfs-fallback（别让上层以为是游戏判据）。
                        bool? nav = null;
                        try { nav = Move.AnyCanReach(cell); } catch { }
                        bool finalReachable = nav.HasValue ? nav.Value : reachable;
                        string judge = nav.HasValue ? "Navigator.CanReach" : "bfs-fallback";
                        if (nav.HasValue && !nav.Value && reason == null) reason = "cant_reach";
                        if (nav.HasValue && nav.Value) reason = null;   // 游戏说能到 ⇒ 不需要补救原因
                        sb.Append(",\"reachable\":").Append(finalReachable ? "true" : "false");
                        sb.Append(",\"judge\":").Append(Snap.Str(judge));
                        sb.Append(",\"bfsReachable\":").Append(reachable ? "true" : "false");
                        sb.Append(",\"dist\":").Append(hasW ? dw.ToString() : (hasL ? dl.ToString() : "null"));
                        sb.Append(",\"reason\":").Append(reason == null ? "null" : Snap.Str(reason));
                        sb.Append(",\"solid\":").Append(solid ? "true" : "false");
                        sb.Append(",\"navCanReach\":").Append(nav.HasValue ? (nav.Value ? "true" : "false") : "null");
                        if (liquid || depthKg > 0) sb.Append(",\"liquidDepth\":").Append(depthKg);
                        sb.Append('}');
                    }
                    sb.Append(']');
                    sb.Append(",\"note\":").Append(Snap.Str(
                        "reachable 现在是**游戏自己的判据**（Navigator.CanReach，任一小人能到）；judge=bfs-fallback 表示当时没有复制人、用的是我们的四邻域 BFS（会漏对角/跳跃/爬坎，仅作参考）。bfsReachable 保留我们 BFS 的原始结论便于对比。reason 仅在我们判据下有意义：need_ladder/need_dig/blocked_by_liquid/no_floor/disconnected/cant_reach"));
                    sb.Append('}');
                    data = sb.ToString();
                }
                catch (System.Exception e) { fatal = e.GetType().Name + ": " + e.Message; }
            }, 5000, 30000, out okR, out busyR);

            if (!ran) { status = 503; code = "RETRY_LATER"; message = "main thread busy"; return null; }
            if (fatal != null) { status = 500; code = "REACH_FAILED"; message = fatal; return null; }
            JsonCheck.Validate(data, "reach");
            return data;
        }

        /// <summary>
        /// BFS。`allowLadder=false`：只走有地面支撑的格子；`true`：额外允许梯子/滑杆/悬挂。
        /// 用游戏自己的 `Grid.*` 标志，不自己发明通行规则。
        /// </summary>
        static Dictionary<int, int> Bfs(int start, bool allowLadder, int maxCells)
        {
            var dist = new Dictionary<int, int>(4096);
            var q = new Queue<int>();
            dist[start] = 0;
            q.Enqueue(start);
            int guard = 0;
            while (q.Count > 0 && guard++ < maxCells)
            {
                int c = q.Dequeue();
                int d = dist[c];
                int cx, cy;
                var xy = Grid.CellToXY(c);
                cx = xy.x; cy = xy.y;

                for (int dir = 0; dir < 4; dir++)
                {
                    int nx = cx + (dir == 0 ? 1 : dir == 1 ? -1 : 0);
                    int ny = cy + (dir == 2 ? -1 : dir == 3 ? 1 : 0);
                    int n = Grid.XYToCell(nx, ny);
                    if (!Grid.IsValidCell(n)) continue;
                    if (dist.ContainsKey(n)) continue;
                    if (!Passable(n, allowLadder)) continue;
                    dist[n] = d + 1;
                    q.Enqueue(n);
                    if (dist.Count >= maxCells) break;
                }
            }
            return dist;
        }

        /// <summary>起点 = 总部所在格（找不到返回 -1）。</summary>
        internal static int ResolveStart()
        {
            try
            {
                var builds = Components.BuildingCompletes != null ? Components.BuildingCompletes.Items : null;
                if (builds == null) return -1;
                for (int i = 0; i < builds.Count; i++)
                {
                    var b = builds[i];
                    if (b == null) continue;
                    Building def = null;
                    try { def = b.GetComponent<Building>(); } catch { }
                    string pid = null;
                    try { pid = def != null && def.Def != null ? def.Def.PrefabID : null; } catch { }
                    if (pid == "Headquarters") return Grid.PosToCell(b.transform.position);
                }
            }
            catch { }
            return -1;
        }

        /// <summary>
        /// 给一批格子做可达性标注（给 dig/build 响应"挂在必经之路上"用）。
        /// **只算一次 BFS**（两条），逐格分类，成本与格数线性；超过 cap 就截断并报数。
        /// 返回 JSON 数组；失败返回 null（调用方就不加这个字段，别让主流程失败）。
        /// </summary>
        internal static string Annotate(System.Collections.Generic.List<int> cells, int cap, out int omitted, out int unreachableCount)
        {
            omitted = 0; unreachableCount = 0;
            if (cells == null || cells.Count == 0) return null;
            try
            {
                int start = ResolveStart();
                if (start < 0) return null;
                var walk = Bfs(start, false, 20000);
                var ladder = Bfs(start, true, 20000);

                var sb = new StringBuilder(1024);
                sb.Append('[');
                int n = 0;
                for (int i = 0; i < cells.Count; i++)
                {
                    int cell = cells[i];
                    if (!Grid.IsValidCell(cell)) continue;
                    if (n >= cap) { omitted = cells.Count - i; break; }

                    int dw, dl;
                    bool hasW = walk.TryGetValue(cell, out dw);
                    bool hasL = ladder.TryGetValue(cell, out dl);
                    bool solid = false, liquid = false;
                    int depthKg = 0;
                    // ★ 0.100.1：挖掘/建造的**对象格本身站不上去**（实心/有建筑），
                    //   真正的判据是"**能不能到它旁边干活**" ⇒ 自身或 4 邻格有一个能走到即可。
                    {
                        int bestW = int.MaxValue, bestL = int.MaxValue;
                        for (int d2 = 0; d2 < 4; d2++)
                        {
                            var cxy = Grid.CellToXY(cell);
                            int nc = Grid.XYToCell(cxy.x + (d2 == 0 ? 1 : d2 == 1 ? -1 : 0), cxy.y + (d2 == 2 ? 1 : d2 == 3 ? -1 : 0));
                            if (!Grid.IsValidCell(nc)) continue;
                            int v;
                            if (walk.TryGetValue(nc, out v) && v + 1 < bestW) bestW = v + 1;
                            if (ladder.TryGetValue(nc, out v) && v + 1 < bestL) bestL = v + 1;
                        }
                        if (!hasW && bestW != int.MaxValue) { hasW = true; dw = bestW; }
                        if (!hasL && bestL != int.MaxValue) { hasL = true; dl = bestL; }
                    }
                    try { solid = Grid.Solid[cell]; } catch { }
                    try { var el = Grid.Element[cell]; if (el != null && el.state == Element.State.Liquid) { liquid = true; depthKg = (int)Grid.Mass[cell]; } } catch { }

                    string reason = null;
                    if (!hasW)
                    {
                        if (hasL) reason = "need_ladder";
                        else if (solid) reason = "need_dig";
                        else if (liquid || depthKg > 0) reason = "blocked_by_liquid";
                        else reason = "no_floor";
                    }
                    // ★ 0.113.0：dig/build 里的 reach 标注同样换成**游戏判据**（有 dupe 时）。
                    //   否则 AI 下标记时看到的仍是"我们的 BFS"，而它会把斜通道/跳跃判成死路。
                    bool? nav = null;
                    try { nav = Move.AnyCanReach(cell); } catch { }
                    bool finalReachable = nav.HasValue ? nav.Value : hasW;
                    if (nav.HasValue)
                    {
                        reason = nav.Value ? null : (reason ?? "cant_reach");
                    }
                    if (!finalReachable) unreachableCount++;
                    // ★ 0.113.1：这个"活"到底有没有人能来干 ——
                    //   实测（2026-11-21）：标记挂上就 `marked=true`，但若目标**没有任何可站立的邻格**，
                    //   小人取到 chore 后会立刻放弃（转 IdleChore），**标记永久留着、永不消失**。
                    //   例：(122,211)/(122,212) 正下方 (122,213) 是砖 ⇒ 一年也不动；把那格砖清掉后立刻被拆。
                    //   所以这里只报**能确定的事实**（有无可站立且可达的邻格），不推测更大的作业矩形。
                    int wsn = 0; string wsnExample = null;
                    try
                    {
                        var wxy = Grid.CellToXY(cell);
                        int[] nx = { wxy.x + 1, wxy.x - 1, wxy.x, wxy.x };
                        int[] ny = { wxy.y, wxy.y, wxy.y + 1, wxy.y - 1 };
                        for (int d2 = 0; d2 < 4; d2++)
                        {
                            int nc = Grid.XYToCell(nx[d2], ny[d2]);
                            if (!Grid.IsValidCell(nc)) continue;
                            if (!Passable(nc, true)) continue;
                            bool? nv = null;
                            try { nv = Move.AnyCanReach(nc); } catch { }
                            if (nv == false) continue;            // 游戏说站不到
                            wsn++;
                            if (wsnExample == null) wsnExample = nx[d2] + "," + ny[d2];
                        }
                    }
                    catch { }
                    var xy = Grid.CellToXY(cell);
                    if (n > 0) sb.Append(',');
                    sb.Append("{\"x\":").Append(xy.x).Append(",\"y\":").Append(xy.y)
                      .Append(",\"reachable\":").Append(finalReachable ? "true" : "false")
                      .Append(",\"judge\":").Append(Snap.Str(nav.HasValue ? "Navigator.CanReach" : "bfs-fallback"))
                      .Append(",\"bfsReachable\":").Append(hasW ? "true" : "false")
                      .Append(",\"dist\":").Append(hasW ? dw.ToString() : (hasL ? dl.ToString() : "null"))
                      .Append(",\"workPos\":{\"standableNeighbors\":").Append(wsn)
                      .Append(",\"example\":").Append(wsnExample == null ? "null" : Snap.Str(wsnExample))
                      .Append(",\"verdict\":").Append(Snap.Str(wsn > 0 ? "will_do" : "no_standable_neighbor"))
                      .Append('}')
                      .Append(",\"reason\":").Append(reason == null ? "null" : Snap.Str(reason))
                      .Append('}');
                    n++;
                }
                sb.Append(']');
                return sb.ToString();
            }
            catch { return null; }
        }

        static bool Passable(int cell, bool allowLadder)
        {
            bool impassable = false;
            try { impassable = Grid.DupeImpassable[cell]; } catch { }
            if (impassable) return false;
            bool solid = false;
            try { solid = Grid.Solid[cell]; } catch { }
            if (solid) return false;

            bool ladder = false, pole = false;
            try { ladder = Grid.HasLadder[cell]; } catch { }
            try { pole = Grid.HasPole[cell]; } catch { }
            // 梯子/滑杆格本身可通行（垂直移动；挂着时对头顶不敏感，所以放在净空检查之前）
            if (ladder || pole) return true;
            // ★ 0.113.1：复制人**占 2 格高**，平地站位要求头顶那格非实心。
            //   实测（2026-11-21）：(113,220) 挖空后头顶 (113,221) 仍是 Dirt ⇒ 游戏 CanReach 判 F；
            //   把头顶那格也挖掉后立刻变 T。梯子顶被天花板压住同理（(116,212) 案例）。
            {
                bool aboveSolid = true;
                try
                {
                    int above = Grid.CellAbove(cell);
                    if (Grid.IsValidCell(above)) aboveSolid = Grid.Solid[above];
                }
                catch { }
                if (aboveSolid) return false;
            }

            // 需要有地面支撑（下一格实心）—— 与游戏"站在地板上"一致
            int below = Grid.CellBelow(cell);
            if (Grid.IsValidCell(below))
            {
                bool belowSolid = false;
                try { belowSolid = Grid.Solid[below]; } catch { }
                if (belowSolid) return true;
                // 站在梯子/滑杆**顶上**也是正常行走（实测：小人就站在梯子顶格上，
                // 早先只在 allowLadder 时才算 ⇒ 把他们自己的格子判成"要梯子"）
                {
                    bool belowLadder = false, belowPole = false;
                    try { belowLadder = Grid.HasLadder[below]; } catch { }
                    try { belowPole = Grid.HasPole[below]; } catch { }
                    if (belowLadder || belowPole) return true;
                }
            }
            // 悬挂（墙挂）在 allowLadder 时也允许
            if (allowLadder)
            {
                bool l = false, r = false;
                try { l = Grid.HasLadder[Grid.CellLeft(cell)]; } catch { }
                try { r = Grid.HasLadder[Grid.CellRight(cell)]; } catch { }
                if (l || r) return true;
            }
            return false;
        }

        static string FieldNear(string body, int from, string key, string dflt)
        {
            int k = body.IndexOf("\"" + key + "\"", from, System.StringComparison.Ordinal);
            if (k < 0) return dflt;
            int colon = body.IndexOf(':', k);
            if (colon < 0) return dflt;
            int i = colon + 1;
            while (i < body.Length && (body[i] == ' ' || body[i] == '\t')) i++;
            int s = i;
            while (i < body.Length && (char.IsDigit(body[i]) || body[i] == '-' || body[i] == '.')) i++;
            string v = body.Substring(s, i - s);
            return string.IsNullOrEmpty(v) ? dflt : v;
        }
    }
}
