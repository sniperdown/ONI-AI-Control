using System.Text;
using Klei.AI;

// ⚠️ 不要 using System 之后直接用 Action/DateTime：Assembly-CSharp 里的全局 Action 枚举会遮蔽 System.Action。
namespace OniAiBridge
{
    /// <summary>
    /// M6.70（**只读**）：`GET /v1/dupes` —— 复制人清单（比 `/v1/state` 多寻路与"疑似卡住"探针，并区分身体类型）。
    ///
    /// 取数（**成员名已核对**）：
    /// * `Components.LiveMinionIdentities : Cmps<MinionIdentity>`（public 静态字段）
    /// * `MinionIdentity.GetComponent<Navigator>()` → **`Navigator.path : Path`（public 字段）**
    /// * `Path.nodes : List<Node>`（public 字段）；`Node.cell : int32`（public 字段）—— **只取首尾两格**
    /// * `ChoreDriver.GetCurrentChore()` → `chore.choreType.Id` / `chore.GetReportName()`
    /// * `modifiers.amounts`（`IEnumerable<AmountInstance>`）→ `AmountInstance.value` / `.GetMin()` / `.GetMax()` / `.amount.units`
    ///
    /// ⚠️ **身体类型不同，属性集合不同**（实测 0.89.2，Cycle 6990「外世」档）：
    /// * 普通小人（organic）：有 `Calories/CaloriesKcal/Bladder/Stamina`
    /// * 仿生人（bionic）：**没有** `Calories/Bladder/Stamina`，多 `BionicOil/BionicGunk/BionicInternalBattery/BionicOxygenTank`
    /// ⇒ 本端点 `amounts` **只输出实际存在的键**（不补 0！）；`kind` 判据 = **有没有 `BionicOil`**。
    /// </summary>
    internal static class DupesApi
    {
        sealed class StuckState
        {
            public int Cell;
            public int PathLen;
            public long SinceUtcMs;
        }

        static readonly System.Collections.Generic.Dictionary<string, StuckState> Stuck =
            new System.Collections.Generic.Dictionary<string, StuckState>();

        public const string BionicProbeAmountId = "BionicOil";

        public static string List(bool idleOnly, out int status, out string code, out string message)
        {
            status = 200; code = null; message = null;
            if (!MainThread.Ready) { status = 503; code = "NOT_READY"; message = "main-thread pump is not running yet"; return null; }

            string data = null, fail = null;
            int okT; bool busy;
            bool ran = MainThread.InvokeAdaptive(() =>
            {
                try
                {
                    var cmp = Components.LiveMinionIdentities;
                    if (cmp == null) { fail = "Components.LiveMinionIdentities 为 null"; return; }

                    long nowMs = System.DateTime.UtcNow.Ticks / System.TimeSpan.TicksPerMillisecond;
                    var alive = new System.Collections.Generic.HashSet<string>();
                    int emitted = 0, idle = 0, busy2 = 0, stuckCandidates = 0, totalScanned = 0, organic = 0, bionic = 0;

                    var body = new StringBuilder(8192);

                    foreach (MinionIdentity mi in cmp.Items)
                    {
                        if (mi == null) continue;
                        totalScanned++;

                        string name = null, dbId = null, gender = null;
                        try { name = mi.GetProperName(); } catch { }
                        try { dbId = mi.nameStringKey; } catch { }
                        if (string.IsNullOrEmpty(name)) name = dbId;
                        try { gender = mi.gender.ToString(); } catch { }

                        int cell = -1;
                        try { cell = Grid.PosToCell(mi.gameObject); } catch { }
                        int x = -1, y = -1, worldId = -1;
                        if (cell >= 0)
                        {
                            try { Grid.CellToXY(cell, out x, out y); } catch { }
                            try { worldId = Grid.WorldIdx != null ? Grid.WorldIdx[cell] : -1; } catch { }
                        }

                        string choreId = null, choreText = null;
                        UnityEngine.GameObject choreTarget = null;
                        try
                        {
                            var cd = mi.GetComponent<ChoreDriver>();
                            var chore = cd != null ? cd.GetCurrentChore() : null;
                            if (chore != null)
                            {
                                try { choreText = chore.GetReportName(); } catch { }
                                try { if (chore.choreType != null) choreId = chore.choreType.Id; } catch { }
                                // ★ 0.95.0：**作业目标**（不是导航终点 —— `path.targetX/Y` 是走到哪儿，不是拆什么）。
                                //   `Chore.target` 是属性、类型 `IStateMachineTarget`（元数据实测）⇒ 转 KMonoBehaviour 取 GameObject。
                                try
                                {
                                    var tgt = chore.target as KMonoBehaviour;
                                    if (tgt != null) choreTarget = tgt.gameObject;
                                }
                                catch { }
                            }
                        }
                        catch { }

                        bool isIdle = AlertRules.IsIdleChore(choreId);
                        if (isIdle) idle++; else busy2++;
                        if (idleOnly && !isIdle) continue;

                        // ---- 属性：只输出**实际存在**的键（不补 0）----
                        var amtVals = new StringBuilder(256);
                        var amtMeta = new StringBuilder(512);
                        int amtN = 0, metaN = 0;
                        bool hasBionicOil = false;
                        try
                        {
                            var mods = mi.modifiers;
                            var amts = mods != null ? mods.amounts : null;
                            if (amts != null)
                            {
                                foreach (AmountInstance ai in amts)
                                {
                                    if (ai == null) continue;
                                    var amt = ai.modifier;
                                    string id = null;
                                    try { if (amt != null) id = amt.Id; } catch { }
                                    if (string.IsNullOrEmpty(id)) continue;
                                    if (string.Equals(id, BionicProbeAmountId, System.StringComparison.OrdinalIgnoreCase)) hasBionicOil = true;

                                    float v = 0f;
                                    try { v = ai.value; } catch { }
                                    if (amtN > 0) amtVals.Append(',');
                                    amtVals.Append(Snap.Str(id)).Append(':').Append(Num(v));
                                    amtN++;

                                    // 元信息：min / max / units（units 是 `Amount.units` 的枚举名）
                                    float mn = float.NaN, mx = float.NaN;
                                    try { mn = ai.GetMin(); } catch { }
                                    try { mx = ai.GetMax(); } catch { }
                                    string units = null;
                                    try { if (amt != null) units = amt.units.ToString(); } catch { }
                                    if (metaN > 0) amtMeta.Append(',');
                                    amtMeta.Append(Snap.Str(id)).Append(":{\"min\":").Append(Num(mn))
                                           .Append(",\"max\":").Append(Num(mx))
                                           .Append(",\"units\":").Append(Snap.Str(units)).Append('}');
                                    metaN++;
                                }
                            }
                        }
                        catch { }

                        string kind = hasBionicOil ? "bionic" : "organic";
                        if (hasBionicOil) bionic++; else organic++;

                        // ---- 寻路（Navigator.path → Path.nodes，只取首尾）----
                        bool hasPath = false;
                        int pathLen = 0, firstCell = -1, targetCell = -1;
                        try
                        {
                            var nav = mi.GetComponent<Navigator>();
                            if (nav != null)
                            {
                                // ⚠️ 实测：`Navigator.path` 的类型是 **`PathFinder.Path`（struct，不是 class）**
                                //   ⇒ 不能与 null 比较；空路径表现为 `nodes == null` 或 `Count == 0`。
                                var p = nav.path;
                                if (p.nodes != null && p.nodes.Count > 0)
                                {
                                    hasPath = true;
                                    pathLen = p.nodes.Count;
                                    try { firstCell = p.nodes[0].cell; } catch { }
                                    try { targetCell = p.nodes[p.nodes.Count - 1].cell; } catch { }
                                }
                            }
                        }
                        catch { }

                        int tx = -1, ty = -1, tw = -1;
                        string targetPrefab = null;
                        if (targetCell >= 0)
                        {
                            try { Grid.CellToXY(targetCell, out tx, out ty); } catch { }
                            try { tw = Grid.WorldIdx != null ? Grid.WorldIdx[targetCell] : -1; } catch { }
                            try
                            {
                                var gob = Grid.Objects[targetCell, (int)ObjectLayer.Building];
                                if (gob != null)
                                {
                                    var b = gob.GetComponent<Building>();
                                    if (b != null && b.Def != null) targetPrefab = b.Def.PrefabID;
                                }
                            }
                            catch { }
                        }

                        // ---- 疑似卡住（判据见 AlertRules；possible:true **不是结论**）----
                        string key = string.IsNullOrEmpty(dbId) ? ("dupe" + totalScanned) : dbId;
                        alive.Add(key);
                        double stillSeconds = 0;
                        bool possible = false;
                        StuckState st;
                        if (!Stuck.TryGetValue(key, out st) || st == null)
                        {
                            Stuck[key] = new StuckState { Cell = cell, PathLen = pathLen, SinceUtcMs = nowMs };
                        }
                        else if (st.Cell != cell || st.PathLen != pathLen)
                        {
                            st.Cell = cell; st.PathLen = pathLen; st.SinceUtcMs = nowMs;
                        }
                        else
                        {
                            stillSeconds = (nowMs - st.SinceUtcMs) / 1000.0;
                            possible = AlertRules.IsMovementChore(choreId) && hasPath && stillSeconds >= AlertRules.StuckWindowSeconds;
                        }
                        if (possible) stuckCandidates++;

                        if (emitted > 0) body.Append(',');
                        emitted++;
                        body.Append("{\"name\":").Append(Snap.Str(name));
                        body.Append(",\"dbId\":").Append(Snap.Str(dbId));
                        body.Append(",\"kind\":").Append(Snap.Str(kind));
                        body.Append(",\"gender\":").Append(Snap.Str(gender));
                        body.Append(",\"cell\":").Append(cell);
                        body.Append(",\"x\":").Append(x).Append(",\"y\":").Append(y);
                        body.Append(",\"worldId\":").Append(worldId);
                        body.Append(",\"chore\":{\"text\":").Append(Snap.Str(choreText))
                            .Append(",\"id\":").Append(Snap.Str(choreId)).Append('}');
                        // ★ 0.95.0：**作业目标**（prefabId + 坐标）。没有目标（如 IdleChore）⇒ null，不编造。
                        if (choreTarget == null)
                        {
                            body.Append(",\"choreTarget\":null");
                        }
                        else
                        {
                            string ctPrefab = null;
                            int ctCell = -1, ctX = -1, ctY = -1, ctWorld = -1;
                            try
                            {
                                var cb = choreTarget.GetComponent<Building>();
                                if (cb != null && cb.Def != null) ctPrefab = cb.Def.PrefabID;
                                if (string.IsNullOrEmpty(ctPrefab))
                                {
                                    var kp2 = choreTarget.GetComponent<KPrefabID>();
                                    if (kp2 != null) ctPrefab = kp2.PrefabTag.Name;
                                }
                            }
                            catch { }
                            try { ctCell = Grid.PosToCell(choreTarget); } catch { }
                            if (ctCell >= 0)
                            {
                                try { Grid.CellToXY(ctCell, out ctX, out ctY); } catch { }
                                try { ctWorld = Grid.WorldIdx != null ? Grid.WorldIdx[ctCell] : -1; } catch { }
                            }
                            body.Append(",\"choreTarget\":{\"prefabId\":").Append(Snap.Str(ctPrefab))
                                .Append(",\"cell\":").Append(ctCell)
                                .Append(",\"x\":").Append(ctX).Append(",\"y\":").Append(ctY)
                                .Append(",\"worldId\":").Append(ctWorld)
                                .Append(",\"name\":").Append(Snap.Str(choreTarget.name))
                                .Append('}');
                        }
                        body.Append(",\"amounts\":{").Append(amtVals).Append('}');
                        body.Append(",\"amountMeta\":{").Append(amtMeta).Append('}');

                        body.Append(",\"path\":{\"hasPath\":").Append(hasPath ? "true" : "false");
                        body.Append(",\"pathLen\":").Append(pathLen);
                        body.Append(",\"firstCell\":").Append(firstCell);
                        body.Append(",\"targetCell\":").Append(targetCell);
                        body.Append(",\"targetX\":").Append(tx).Append(",\"targetY\":").Append(ty);
                        body.Append(",\"targetWorldId\":").Append(tw);
                        body.Append(",\"targetPrefabId\":").Append(Snap.Str(targetPrefab));
                        body.Append('}');

                        body.Append(",\"stuckProbe\":{\"possible\":").Append(possible ? "true" : "false");
                        body.Append(",\"stillSeconds\":").Append(stillSeconds.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture));
                        body.Append(",\"pathLen\":").Append(pathLen);
                        body.Append(",\"choreId\":").Append(Snap.Str(choreId));
                        body.Append(",\"choreIsMovement\":").Append(AlertRules.IsMovementChore(choreId) ? "true" : "false");
                        body.Append(",\"windowSeconds\":").Append(AlertRules.StuckWindowSeconds.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture));
                        string reason = possible
                            ? "移动类 chore + 有路径 + " + AlertRules.StuckWindowSeconds.ToString("0") + "s 内路径未缩短且格子没变"
                            : (!AlertRules.IsMovementChore(choreId) ? "chore 不是移动类（白名单前缀 " + string.Join("/", AlertRules.MovementChorePrefixes) + "）"
                               : (!hasPath ? "没有路径" : "观测窗口不足 " + AlertRules.StuckWindowSeconds.ToString("0") + "s"));
                        body.Append(",\"reason\":").Append(Snap.Str(reason)).Append("}}");
                    }

                    var dead = new System.Collections.Generic.List<string>();
                    foreach (var k in Stuck.Keys) if (!alive.Contains(k)) dead.Add(k);
                    for (int i = 0; i < dead.Count; i++) Stuck.Remove(dead[i]);

                    var sb = new StringBuilder(8192);
                    sb.Append("{\"count\":").Append(emitted);
                    sb.Append(",\"totalScanned\":").Append(totalScanned);
                    sb.Append(",\"idle\":").Append(idle);
                    sb.Append(",\"busy\":").Append(busy2);
                    sb.Append(",\"stuckCandidates\":").Append(stuckCandidates);
                    sb.Append(",\"organic\":").Append(organic);
                    sb.Append(",\"bionic\":").Append(bionic);
                    sb.Append(",\"filter\":{\"idleOnly\":").Append(idleOnly ? "true" : "false").Append('}');
                    sb.Append(",\"dupes\":[").Append(body).Append(']');
                    sb.Append(",\"note\":").Append(Snap.Str(
                        "只读。`kind` 判据 = **有没有 `BionicOil` 这个 amount**（有 ⇒ bionic，无 ⇒ organic）。"
                        + "`amounts` **只包含该小人实际存在的 amount**（不存在的键不出现、也**不补 0**）—— 实测普通小人有 Calories/CaloriesKcal/Bladder/Stamina，"
                        + "仿生人没有这三个、改为 BionicOil/BionicGunk/BionicInternalBattery/BionicOxygenTank。"
                        + "`amountMeta[id]` 给 min/max/units（`units` 是 `Amount.units` 枚举名），用于把绝对值和百分比互算。"
                        + "`path` 只给首尾两格（不返回整条路径）。`stuckProbe.possible:true` **不是结论**：等开门、排队、就地工作、被传送都可能命中；"
                        + "判据 = 移动类 chore（前缀 " + string.Join("/", AlertRules.MovementChorePrefixes) + "） + hasPath + 连续 "
                        + AlertRules.StuckWindowSeconds.ToString("0") + "s pathLen 未减少且 cell 未变；第一次查询只建立基线，第二次起才可能 true。"));
                    sb.Append('}');
                    data = sb.ToString();
                    Log.Info("[dupes] count=" + emitted + " idle=" + idle + " busy=" + busy2 + " organic=" + organic + " bionic=" + bionic
                             + " stuckCandidates=" + stuckCandidates + " idleOnly=" + idleOnly);
                }
                catch (System.Exception e) { fail = e.GetType().Name + ": " + e.Message; }
            }, 5000, 30000, out okT, out busy);

            if (!ran) { status = 503; code = "RETRY_LATER"; message = "main thread busy；请 1 秒后重试"; return null; }
            if (fail != null) { status = 500; code = "DUPES_UNAVAILABLE"; message = fail; return null; }
            if (data == null) { status = 500; code = "INTERNAL"; message = "dupes 采集返回空"; return null; }
            JsonCheck.Validate(data, "dupes");
            return data;
        }

        static string Num(float v)
        {
            if (float.IsNaN(v) || float.IsInfinity(v)) return "null";
            return v.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
        }
    }
}
