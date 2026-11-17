using System.Text;
using Klei.AI;

// ⚠️ 不要 using System 之后直接用 Action/DateTime：Assembly-CSharp 里的全局 Action 枚举会遮蔽 System.Action。
namespace OniAiBridge
{
    /// <summary>
    /// M6.76/M6.77（0.92.0 → 0.92.1）：`/v1/job` 的**打标记**那批 ——
    /// `disinfect` / `empty_pipe` / `attack` / `capture` / `mop`。
    ///
    /// 形状：**按格（或矩形）解析出目标 → 逐个调组件的 Mark\* 方法**；
    /// `attack` / `mop` 是"一次工具调用完成全部"，**不逐目标调**（0.92.0 的 bug：两者同时计 marked 与 failed）。
    ///
    /// **dryRun 与真做走同一段解析代码**（`Resolve()`）—— 硬要求，否则 dryRun 是摆设。
    /// </summary>
    internal static class JobMarks
    {
        public const string T_DISINFECT = "disinfect";
        public const string T_EMPTY_PIPE = "empty_pipe";
        public const string T_ATTACK = "attack";
        public const string T_CAPTURE = "capture";
        public const string T_MOP = "mop";

        public static bool IsMarkType(string t)
        {
            return t == T_DISINFECT || t == T_EMPTY_PIPE || t == T_ATTACK || t == T_CAPTURE || t == T_MOP;
        }

        sealed class Target
        {
            public string Kind;
            public string PrefabId;
            public int Cell, X, Y, WorldId;
            public int InstanceId;
            public UnityEngine.GameObject Go;
            public UnityEngine.Component Comp;
            public bool AlreadyMarked;
            public string AlreadyReason;
            public string Extra;
        }

        public static string Execute(string type, string body, out int status, out string code, out string message, out string details)
        {
            status = 200; code = null; message = null; details = null;

            // ---------- 1) 参数 ----------
            int x = 0, y = 0;
            int x0 = 0, y0 = 0, x1 = 0, y1 = 0; bool hasRect = false;
            if (JobQuery.HasKey(body, "x0"))
            {
                if (!JobQuery.TryInt(body, "x0", out x0) || !JobQuery.TryInt(body, "y0", out y0)
                    || !JobQuery.TryInt(body, "x1", out x1) || !JobQuery.TryInt(body, "y1", out y1))
                { status = 400; code = "BAD_REQUEST"; message = "矩形需要 x0/y0/x1/y1（整数）"; return null; }
                if (x1 < x0) { int t = x0; x0 = x1; x1 = t; }
                if (y1 < y0) { int t = y0; y0 = y1; y1 = t; }
                hasRect = true;
            }
            else
            {
                if (!JobQuery.TryInt(body, "x", out x) || !JobQuery.TryInt(body, "y", out y))
                { status = 400; code = "BAD_REQUEST"; message = "单格需要 x/y（整数），或矩形 x0/y0/x1/y1"; return null; }
                x0 = x; y0 = y; x1 = x; y1 = y;
            }

            int worldId = -1;
            JobQuery.TryInt(body, "worldId", out worldId);

            int priority = 0;
            if (JobQuery.HasKey(body, "priority"))
            {
                if (!JobQuery.TryInt(body, "priority", out priority))
                { status = 400; code = "BAD_REQUEST"; message = "priority 必须是 1..9 的整数"; return null; }
                if (priority < 1 || priority > 9)
                { status = 400; code = "BAD_REQUEST"; message = "priority 只允许 1..9，收到 " + priority; return null; }
            }

            bool dryRun = false, confirm = false;
            JobQuery.MatchBool(body, "dryRun", out dryRun);
            JobQuery.MatchBool(body, "confirm", out confirm);
            bool includeOwnColony = false; JobQuery.MatchBool(body, "includeOwnColony", out includeOwnColony);

            bool rectType = (type == T_ATTACK || type == T_CAPTURE);
            if (hasRect && !rectType && (x0 != x1 || y0 != y1))
            {
                status = 400; code = "RECT_NOT_SUPPORTED";
                message = type + " 只支持单格（x/y）；\"拉范围\"由调用方循环调用";
                return null;
            }


            if (!dryRun && !confirm)
            {
                status = 400; code = "CONFIRM_REQUIRED";
                message = "打标记会改存档（" + type + "），必须显式 \"confirm\": true；只想看会标到谁请用 \"dryRun\": true";
                return null;
            }

            if (!MainThread.Ready) { status = 503; code = "NOT_READY"; message = "main-thread pump is not running yet"; return null; }

            string data = null, fail = null;
            int okT; bool busy;
            bool ran = MainThread.InvokeAdaptive(() =>
            {
                try
                {
                    mopLiquidCells = 0; mopTooMuch = 0; mopNotOnFloor = 0; ownColonyExcluded = 0;
                    var targets = new System.Collections.Generic.List<Target>();
                    string resolveNote = null;
                    if (!Resolve(type, x0, y0, x1, y1, worldId, includeOwnColony, targets, ref resolveNote))
                    { fail = resolveNote; return; }

                    int marked = 0, already = 0, failed = 0, prioApplied = 0;

                    // ★ 0.93.7 修：`alreadyMarked` 的**顶层聚合**必须对所有类型都算。
                    //   attack / mop 是"一次工具调用完成全部"，不进下面 `if (!dryRun)` 里的逐目标循环
                    //   ⇒ 原来只有 disinfect/empty_pipe/capture 会在那个循环里 `already++`，
                    //   工具型两条路径的 `alreadyMarked` **恒为 0**（mop 的 `wouldMark` 因此恒等于 targets）。
                    //   逐目标的 `alreadyMarked` 是 Resolve 填的，这里只做汇总。
                    //   非工具类型不能在这里算（否则与下面的循环重复计数），所以按类型分流。
                    if (type == T_ATTACK || type == T_MOP)
                    {
                        for (int i3 = 0; i3 < targets.Count; i3++)
                            if (targets[i3].AlreadyMarked) already++;
                    }

                    string attackBBox = null;
                    int attackMarkedCount = -1;   // 仅 attack：回读到的已标记数
                    int choreObservedImmediately = -1;   // attack：同帧回读到的 chore 数（可能为 0，不代表失败）
                    // ★ M6.90（0.93.6）：capture 的回读。`Capturable.MarkForCapture` 的 IL 第一个判断就是
                    //   `IsCapturable()`，为假时**静默什么都不做** ⇒ 只数"尝试了几个"会把没标上的也算成功。
                    //   与 attack 不同：`IsMarkedForCapture` 就是 `return markedForCapture;` ⇒ **同帧可靠**，可当判据。
                    int captureMarkedCount = -1;   // 仅 capture：回读到的已标记数（含此前就带的）
                    int captureReadbackOk = 0;     // capture：真做时逐目标回读成功的计数
                    string captureReadbackUnavailable = null;   // capture：回读抛异常时的降级字段（不拿 null 冒充 0）
                    // ★ M6.79：attackBBox / attackMarkedCount 在 dryRun 时也要给（0.92.2 里 dryRun 分别给 null / -1）
                    if (type == T_ATTACK)
                    {
                        attackBBox = BuildAttackBBox(x0, y0, x1, y1);
                        int preMarked = 0;
                        for (int i2 = 0; i2 < targets.Count; i2++)
                            if (targets[i2].AlreadyMarked) preMarked++;
                        attackMarkedCount = preMarked;   // dryRun：解析时的回读值；真做后会被调用后的回读覆盖
                    }
                    if (type == T_CAPTURE)
                    {
                        int preMarked = 0;
                        for (int i2 = 0; i2 < targets.Count; i2++)
                            if (targets[i2].AlreadyMarked) preMarked++;
                        captureMarkedCount = preMarked;   // dryRun：解析时的回读值；真做后改由下面覆盖
                    }
                    var failedSb = new StringBuilder();
                    string toolNote = null;

                    if (!dryRun)
                    {
                        bool toolType = (type == T_ATTACK || type == T_MOP);

                        if (toolType)
                        {
                            // ★ M6.77：这两类**一次工具调用完成全部**，绝不进逐目标循环
                            //   （0.92.0 的 bug：循环里每个目标都抛"不应逐目标调用" ⇒ marked 与 failed 同时为 1）
                            string terr;
                            int r;
                            if (type == T_ATTACK)
                            {
                                r = ApplyAttack(targets, x0, y0, x1, y1, ref attackBBox, out terr);
                                if (r >= 0)
                                {
                                // ★ M6.81：**不要用同一帧的回读判失败**。实测：MarkForAttack 确实挂了 chore，
                                //   但 chore 要到下一帧/下一次 chore 更新才出现在 choreWorldMap ⇒ 同帧回读看不到。
                                //   所以：工具调用没抛异常 ⇒ marked = targets 数；同帧回读只作观察字段（不代表失败）。
                                marked = targets.Count;
                                int observedNow = 0;
                                for (int i = 0; i < targets.Count; i++)
                                    if (targets[i].Go != null && HasAttackChore(targets[i].Go, targets[i].WorldId)) observedNow++;
                                choreObservedImmediately = observedNow;
                                attackMarkedCount = observedNow;   // 仅"当下已带 chore"的数量（含此前就带的）
                                }
                                else { failed = 1; failedSb.Append("{\"error\":").Append(Snap.Str(terr ?? "attack 调用失败")).Append('}') ;}
                            }
                            else
                            {
                                r = ApplyMop(targets, priority, ref prioApplied, out terr);
                                if (r < 0) { failed = 1; failedSb.Append("{\"error\":").Append(Snap.Str(terr ?? "mop 失败")).Append('}') ;}
                                else { marked = r; }
                            }
                            toolNote = (type == T_ATTACK)
                                ? "attack 用一次范围调用；marked = 调用后**回读**到的 Attack chore 数；没标上的逐条进 failedDetails"
                                : "mop 走预制体路径（不经工具）；marked = MopPlacer 真的落层并被回读确认的格数";
                        }
                        else
                        {
                            for (int i = 0; i < targets.Count; i++)
                            {
                                var tg = targets[i];
                                if (tg.AlreadyMarked) { already++; continue; }
                                try
                                {
                                    if (type == T_DISINFECT)
                                    {
                                        var d = tg.Comp as Disinfectable;
                                        if (d == null) throw new System.InvalidOperationException("Disinfectable 为空");
                                        d.MarkForDisinfect(true);
                                    }
                                    else if (type == T_EMPTY_PIPE)
                                    {
                                        var w = tg.Comp as EmptyConduitWorkable;
                                        if (w != null) w.MarkForEmptying();
                                        else
                                        {
                                            var iw = tg.Comp as IEmptyConduitWorkable;
                                            if (iw == null) throw new System.InvalidOperationException("IEmptyConduitWorkable 为空");
                                            iw.MarkForEmptying();
                                        }
                                    }
                                    else if (type == T_CAPTURE)
                                    {
                                        var c = tg.Comp as Capturable;
                                        if (c == null) throw new System.InvalidOperationException("Capturable 为空");
                                        c.MarkForCapture(true);
                                        // ★ M6.90：同帧回读标记位（`IsMarkedForCapture` = `return markedForCapture;`）。
                                        //   标记位没置起来 ⇒ 游戏侧其实没生效（多半是 `IsCapturable()` 为假），
                                        //   所以这个计数是 `marked`（尝试数）之外**独立的**证据，不改变 `marked` 语义。
                                        try { if (c.IsMarkedForCapture) captureReadbackOk++; }
                                        catch (System.Exception e2) { captureReadbackUnavailable = e2.GetType().Name + ": " + e2.Message; }
                                    }

                                    if (priority > 0 && tg.Go != null) ApplyPriority(tg.Go, priority, ref prioApplied);
                                    marked++;
                                }
                                catch (System.Exception e)
                                {
                                    failed++;
                                    if (failed < 20) failedSb.Append((failed > 1 ? "," : "")).Append("{\"cell\":").Append(tg.Cell)
                                        .Append(",\"prefabId\":").Append(Snap.Str(tg.PrefabId))
                                        .Append(",\"error\":").Append(Snap.Str(e.GetType().Name + ": " + e.Message)).Append('}');
                                }
                            }
                        }
                    }

                    // ★ M6.90：capture 的回读总数 = 真做时回读成功的 + 解析时就已带标记的（`already` 在逐目标循环里累计）。
                    //   回读抛过异常 ⇒ 取不到，按本项目的约定给 -1 + `captureMarkedUnavailable`，**不拿 0 / null 冒充**。
                    if (type == T_CAPTURE && !dryRun)
                        captureMarkedCount = (captureReadbackUnavailable != null) ? -1 : (captureReadbackOk + already);

                    // ---------- 响应 ----------
                    var sb = new StringBuilder(2048);
                    sb.Append("{\"type\":").Append(Snap.Str(type));
                    sb.Append(",\"dryRun\":").Append(dryRun ? "true" : "false");
                    sb.Append(",\"rect\":{\"x0\":").Append(x0).Append(",\"y0\":").Append(y0)
                      .Append(",\"x1\":").Append(x1).Append(",\"y1\":").Append(y1).Append('}');
                    sb.Append(",\"worldId\":").Append(worldId);
                    if (!string.IsNullOrEmpty(attackBBox)) sb.Append(",\"attackBBox\":").Append(attackBBox);
                    sb.Append(",\"priorityRequested\":").Append(priority > 0 ? priority.ToString() : "null");
                    sb.Append(",\"targets\":").Append(targets.Count);
                    if (ownColonyExcluded > 0) sb.Append(",\"ownColonyExcluded\":").Append(ownColonyExcluded);
                    if (includeOwnColony && type == T_ATTACK) sb.Append(",\"hazard\":").Append(Snap.Str("包含己方殖民者（includeOwnColony=true）"));
                    sb.Append(",\"alreadyMarked\":").Append(already);
                    if (type == T_MOP) sb.Append(",\"mopGating\":{\"maxMopAmt\":").Append(F(MopMaxAmt()))
                        .Append(",\"liquidCells\":").Append(mopLiquidCells)
                        .Append(",\"skippedTooMuch\":").Append(mopTooMuch)
                        .Append(",\"skippedNotOnFloor\":").Append(mopNotOnFloor)
                        // ★ 0.93.7：判据用的**真实** choreType（取自 `Db.Get().ChoreTypes.Mop`），
                        //   回显它的 `Id` 供外部核对；取不到就是 null（不再用"含 Mop"的推断串）。
                        .Append(",\"choreTypeId\":").Append(Snap.Str(MopChoreTypeId())).Append('}');
                    if (type == T_ATTACK) sb.Append(",\"attackMarkedCount\":").Append(attackMarkedCount);
                    if (type == T_ATTACK) sb.Append(",\"choreObservedImmediately\":").Append(choreObservedImmediately);
                    if (type == T_CAPTURE) sb.Append(",\"captureMarkedCount\":").Append(captureMarkedCount);
                    if (type == T_CAPTURE && captureReadbackUnavailable != null)
                        sb.Append(",\"captureMarkedUnavailable\":").Append(Snap.Str(captureReadbackUnavailable));
                    if (!dryRun)
                    {
                        sb.Append(",\"marked\":").Append(marked);
                        sb.Append(",\"failed\":").Append(failed);
                        if (priority > 0) sb.Append(",\"priorityApplied\":").Append(prioApplied);
                        if (toolNote != null) sb.Append(",\"toolNote\":").Append(Snap.Str(toolNote));
                        sb.Append(",\"jobState\":").Append(Snap.Str(marked > 0 ? "marked" : (already > 0 ? "already-marked" : "nothing-marked")));
                        if (failedSb.Length > 0) sb.Append(",\"failedDetails\":[").Append(failedSb).Append(']');
                    }
                    else
                    {
                        sb.Append(",\"wouldMark\":").Append(targets.Count - already);
                        sb.Append(",\"jobState\":").Append(Snap.Str("dry-run"));
                    }

                    sb.Append(",\"list\":[");
                    int lim = targets.Count < 200 ? targets.Count : 200;
                    for (int i = 0; i < lim; i++)
                    {
                        var tg = targets[i];
                        if (i > 0) sb.Append(',');
                        sb.Append("{\"kind\":").Append(Snap.Str(tg.Kind));
                        sb.Append(",\"prefabId\":").Append(Snap.Str(tg.PrefabId));
                        sb.Append(",\"cell\":").Append(tg.Cell).Append(",\"x\":").Append(tg.X).Append(",\"y\":").Append(tg.Y);
                        sb.Append(",\"worldId\":").Append(tg.WorldId);
                        sb.Append(",\"alreadyMarked\":").Append(tg.AlreadyMarked ? "true" : "false");
                        if (!string.IsNullOrEmpty(tg.AlreadyReason)) sb.Append(",\"alreadyReason\":").Append(Snap.Str(tg.AlreadyReason));
                        if (!string.IsNullOrEmpty(tg.Extra)) sb.Append(",\"extra\":").Append(Snap.Str(tg.Extra));
                        // ★ M6.77：attack 的**回读**（0.92.0 没有，无法判断是否标上）
                        if (type == T_ATTACK && tg.Go != null)
                            sb.Append(",\"attackMarked\":").Append(HasAttackChore(tg.Go, tg.WorldId) ? "true" : "false");
                        // ★ M6.90：capture 的**回读**（同帧可靠，见上）；取不到就给降级字段，不给 false
                        if (type == T_CAPTURE && tg.Comp is Capturable)
                        {
                            try { sb.Append(",\"captureMarked\":").Append(((Capturable)tg.Comp).IsMarkedForCapture ? "true" : "false"); }
                            catch { sb.Append(",\"captureMarkedUnavailable\":true"); }
                        }
                        // ★ M6.77：mop 的**回读**（MopPlacer 层）
                        if (type == T_MOP)
                            sb.Append(",\"mopChoreFound\":").Append(HasMopChore(tg.Cell, tg.WorldId) ? "true" : "false");   // 仅"格上物体"快照，**不代表成败**
                            sb.Append(",\"mopMinionChores\":").Append(CountMopChoresOnMinions());   // 接活小人身上 Mop chore 的数量（真正的执行信号）
                            sb.Append(",\"mopPlacerPresent\":").Append(MopPlacerPresent(tg.Cell) ? "true" : "false");   // ★ marked 的判据
                        sb.Append('}');
                    }
                    sb.Append(']');
                    if (targets.Count > lim) sb.Append(",\"listTruncated\":true");
                    sb.Append(",\"note\":").Append(Snap.Str(
                        "打标记批次（0.93.7）。`dryRun` 与真做共用同一段解析（Resolve）。"
                        + "`attack`/`mop` 是**一次工具调用完成全部**，不进逐目标循环 ⇒ `marked` 只有 0 或 1，`failed` 只在工具调用真抛异常时 +1。"
                        + "`attackMarked` = 该对象身上是否已有 Attack chore（回读判据）。"
                        + "**`mop` 的幂等判据（0.93.7 修正）**：`alreadyMarked` = 该格已有 Mop chore **或** 已有 `ObjectLayer.MopPlacer` 标记物"
                        + "——两者是同一件事的两个阶段（标记物先落层，小人接活后才出现 chore），任一存在即算已标记 ⇒ 第二次 `dryRun` 给"
                        + "`alreadyMarked:1 / wouldMark:0`。`mopGating.choreTypeId` 是判据用的真实 choreType Id（取自 `Db.Get().ChoreTypes.Mop`）。"
                        + "`capture` 走 `Capturable.MarkForCapture(bool)`，逐目标调；`marked` 是**尝试数**，"
                        + "`captureMarkedCount` 才是**游戏侧回读**到的已标记数（`IsMarkedForCapture`，同帧可靠）—— 两者不等说明游戏拒了（多半 `IsCapturable()` 为假）。"
                        + "attack 会**真的伤害小动物**，请先 dryRun 看 list。"));
                    sb.Append('}');
                    data = sb.ToString();
                    Log.Info("[job] type=" + type + " dryRun=" + dryRun + " targets=" + targets.Count + " marked=" + marked
                             + " already=" + already + " failed=" + failed + " priority=" + priority);
                }
                catch (System.Exception e) { fail = e.GetType().Name + ": " + e.Message; }
            }, 8000, 40000, out okT, out busy);

            if (!ran) { status = 503; code = "RETRY_LATER"; message = "main thread busy；请 1 秒后重试"; return null; }
            if (fail != null) { status = 500; code = "MARKS_UNAVAILABLE"; message = fail; return null; }
            if (data == null) { status = 500; code = "INTERNAL"; message = "marks 采集返回空"; return null; }
            JsonCheck.Validate(data, "job-marks");
            return data;
        }

        // mop 解析过程中的统计（主线程单次请求内使用）
        static int mopLiquidCells, mopTooMuch, mopNotOnFloor;
        static int ownColonyExcluded;   // 因"己方殖民者"被排除的目标数（attack/capture）
        // ================= 解析（dryRun 与真做的唯一入口）=================
        static bool Resolve(string type, int x0, int y0, int x1, int y1, int worldId, bool includeOwnColony,
                            System.Collections.Generic.List<Target> outList, ref string err)
        {
            int layers = (int)ObjectLayer.NumLayers;
            var seen = new System.Collections.Generic.HashSet<int>();

            for (int yy = y0; yy <= y1; yy++)
            {
                for (int xx = x0; xx <= x1; xx++)
                {
                    int cell = Grid.XYToCell(xx, yy);
                    if (!Grid.IsValidCell(cell)) continue;

                    int w = -1;
                    try { w = Grid.WorldIdx != null ? Grid.WorldIdx[cell] : -1; } catch { }
                    if (worldId >= 0 && w != worldId) continue;   // ★ 不跨世界误伤

                    if (type == T_MOP)
                    {
                        // ★ M6.80：**不再用"是液体"当可拖判据** —— 实测一片 486kg 的水池不可拖。
                        //   游戏的真正判据（`MopTool.OnDragTool` 的 IL）：
                        //     ① 该格不是固体；② `Element.IsLiquid`；③ **下方一格是固体**（必须在"地面"上）；
                        //     ④ `Grid.Mass[cell] <= MopTool.maxMopAmt`（cctor: 0x43160000 = **150.0 kg**），
                        //        超了游戏会弹 `MOP.TOO_MUCH_LIQUID`；不在地面则弹 `MOP.NOT_ON_FLOOR`。
                        //   ⇒ 解析按这四条过滤，并统计被每种条件挡掉的格数（mopGating）。
                        Element el = null;
                        try { el = Grid.Element[cell]; } catch { }
                        bool isLiquid = false;
                        try { isLiquid = el != null && el.IsLiquid; } catch { }
                        if (!isLiquid) continue;
                        float m = 0f;
                        try { m = Grid.Mass[cell]; } catch { }
                        if (m <= 0f) continue;
                        mopLiquidCells++;

                        float maxAmt = MopMaxAmt();
                        if (m > maxAmt) { mopTooMuch++; continue; }

                        bool onFloor = false;
                        try { onFloor = Grid.Solid[Grid.CellBelow(cell)]; } catch { }
                        if (!onFloor) { mopNotOnFloor++; continue; }

                        bool hasChore = HasMopChore(cell, w);
                        bool placerNow = MopPlacerPresent(cell);
                        // ★ 0.93.7 修：`alreadyMarked` 必须把 **MopPlacer 标记物**也算进来。
                        //   0.93.6 只认 chore ⇒ 真标记之后（marked=1、mopPlacerPresent 已 true）再 dryRun，
                        //   仍报 alreadyMarked=0 / wouldMark=1，调用方按它做幂等会**重复下发**同一个拖地任务
                        //   （实测 cell=152161）。标记物与 chore 是"同一件事的两个阶段"：标记物先落层，
                        //   小人接活后才出现 chore ⇒ 两者任一存在都表示"这格已经被标了"。
                        //   注：下方 `HasMopChore` 的旧注释说"MopPlacer 松手即被消费"，那是**拖拽工具**路径的
                        //   临时预览物；本插件走的是直接实例化预制体写入 ObjectLayer.MopPlacer，实测会留到被拖掉
                        //   （22:07 标上 → 22:09 复测仍在），所以这里可以当判据。
                        bool already = hasChore || placerNow;
                        outList.Add(new Target
                        {
                            Kind = "cell",
                            PrefabId = null,
                            Cell = cell, X = xx, Y = yy, WorldId = w,
                            InstanceId = cell,
                            AlreadyMarked = already,
                            AlreadyReason = (hasChore && placerNow) ? "该格已有 Mop chore + MopPlacer 标记"
                                          : (hasChore ? "该格已有 Mop chore"
                                                      : (placerNow ? "该格已有 MopPlacer 标记" : null)),
                            Extra = "liquidMassKg=" + m.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)
                                    + "; onFloor=1; maxMopAmt=" + maxAmt.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)
                                    + "; mopChoreFound=" + (hasChore ? "1" : "0")
                                    + "; mopPlacerPresent=" + (placerNow ? "1" : "0")
                        });
                        continue;
                    }

                    for (int ly = 0; ly < layers; ly++)
                    {
                        UnityEngine.GameObject go = null;
                        try { go = Grid.Objects[cell, ly]; } catch { continue; }
                        if (go == null) continue;

                        int inst = 0;
                        try { inst = go.GetInstanceID(); } catch { }
                        if (inst != 0 && !seen.Add(inst)) continue;

                        bool taken = false;
                        if (type == T_DISINFECT)
                        {
                            Disinfectable d = null;
                            try { d = go.GetComponent<Disinfectable>(); } catch { }
                            if (d != null)
                            {
                                int disease = 0;
                                try { var pe = go.GetComponent<PrimaryElement>(); if (pe != null) disease = pe.DiseaseCount; } catch { }
                                bool mk = IsDisinfectMarked(d);
                                outList.Add(new Target
                                {
                                    Kind = "Disinfectable",
                                    PrefabId = PrefabOf(go),
                                    Cell = cell, X = xx, Y = yy, WorldId = w,
                                    InstanceId = inst, Go = go, Comp = d,
                                    AlreadyMarked = mk,
                                    AlreadyReason = mk ? "isMarkedForDisinfect == true" : null,
                                    Extra = "diseaseCount=" + disease
                                });
                                taken = true;
                            }
                        }
                        else if (type == T_EMPTY_PIPE)
                        {
                            EmptyConduitWorkable w2 = null;
                            try { w2 = go.GetComponent<EmptyConduitWorkable>(); } catch { }
                            if (w2 != null)
                            {
                                outList.Add(new Target
                                {
                                    Kind = "EmptyConduitWorkable",
                                    PrefabId = PrefabOf(go),
                                    Cell = cell, X = xx, Y = yy, WorldId = w,
                                    InstanceId = inst, Go = go, Comp = w2,
                                    AlreadyMarked = false,
                                    AlreadyReason = null,
                                    Extra = "alreadyMarkedUnavailable=1"
                                });
                                taken = true;
                            }
                        }
                        else if (type == T_CAPTURE)
                        {
                            // ★ M6.81：捕捉同样排除己方殖民者
                            if (!includeOwnColony && IsOwnColony(go)) { ownColonyExcluded++; if (inst != 0) seen.Remove(inst); continue; }
                            Capturable c = null;
                            try { c = go.GetComponent<Capturable>(); } catch { }
                            if (c != null)
                            {
                                bool mk = false;
                                try { mk = c.IsMarkedForCapture; } catch { }
                                outList.Add(new Target
                                {
                                    Kind = "Capturable",
                                    PrefabId = PrefabOf(go),
                                    Cell = cell, X = xx, Y = yy, WorldId = w,
                                    InstanceId = inst, Go = go, Comp = c,
                                    AlreadyMarked = mk,
                                    AlreadyReason = mk ? "IsMarkedForCapture == true" : null
                                });
                                taken = true;
                            }
                        }
                        else if (type == T_ATTACK)
                        {
                            // ★ M6.81 安全：默认**排除己方殖民者**（否则一次范围攻击会把自家殖民者挂上攻击标记）
                            if (!includeOwnColony && IsOwnColony(go)) { ownColonyExcluded++; if (inst != 0) seen.Remove(inst); continue; }
                            var fa = go.GetComponent<FactionAlignment>();
                            var atk = go.GetComponent<AttackableBase>();
                            if (fa != null && atk != null)
                            {
                                bool mk = HasAttackChore(go, w);
                                int hp = -1;
                                try { var h = go.GetComponent<Health>(); if (h != null) hp = (int)h.hitPoints; } catch { }
                                outList.Add(new Target
                                {
                                    Kind = "AttackableBase",
                                    PrefabId = PrefabOf(go),
                                    Cell = cell, X = xx, Y = yy, WorldId = w,
                                    InstanceId = inst, Go = go,
                                    AlreadyMarked = mk,
                                    AlreadyReason = mk ? "已有 Attack chore" : null,
                                    Extra = "hp=" + hp + "; attackMarked=" + (mk ? "1" : "0")
                                });
                                taken = true;
                            }
                        }

                        if (!taken && inst != 0) seen.Remove(inst);
                    }
                }
            }

            if (outList.Count == 0)
            {
                err = "NO_TARGET：该范围内没有可标记的对象（" + type + "）";
                return false;
            }
            return true;
        }

        // ================= 攻击（范围 API + chore 回读）=================
        /// <summary>
        /// 攻击：用**世界坐标**的矩形调游戏静态范围入口。
        /// ⚠️ 0.92.1 的 bug：`v0/v1` 直接用了格子序号（`x0/y0`），而 `AttackTool.MarkForAttack` 吃的是**世界坐标**
        /// ⇒ 一次 1×1 的调用覆盖了一大片（实测 21 只动物被标，而只真打过 1 只）。
        /// 现在用 `Grid.CellToPos(cell)` 换算，并把**实际传给游戏的包围盒**回填给调用方（`attackBBox`）便于对账。
        /// </summary>
        static int ApplyAttack(System.Collections.Generic.List<Target> targets, int rx0, int ry0, int rx1, int ry1,
                               ref string bboxReport, out string err)
        {
            err = null;
            try
            {
                int c0 = Grid.XYToCell(rx0, ry0);
                int c1 = Grid.XYToCell(rx1, ry1);
                if (!Grid.IsValidCell(c0) || !Grid.IsValidCell(c1))
                { err = "矩形角点无效（cell0=" + c0 + ", cell1=" + c1 + "）"; return -1; }

                UnityEngine.Vector3 p0 = Grid.CellToPos(c0);
                UnityEngine.Vector3 p1 = Grid.CellToPos(c1);
                UnityEngine.Vector2 v0 = new UnityEngine.Vector2(p0.x, p0.y);
                UnityEngine.Vector2 v1 = new UnityEngine.Vector2(p1.x, p1.y);

                bboxReport = BuildAttackBBox(rx0, ry0, rx1, ry1);

                AttackTool.MarkForAttack(v0, v1, false);

                int ok = 0;
                for (int i = 0; i < targets.Count; i++)
                    if (targets[i].Go != null && HasAttackChore(targets[i].Go, targets[i].WorldId)) ok++;
                return ok;
            }
            catch (System.Exception e) { err = e.GetType().Name + ": " + e.Message; return -1; }
        }

        /// <summary>attack 的"实际传给游戏的包围盒"描述串（dryRun 与真做共用，保证两边一致）。</summary>
        static string BuildAttackBBox(int rx0, int ry0, int rx1, int ry1)
        {
            try
            {
                int c0 = Grid.XYToCell(rx0, ry0);
                int c1 = Grid.XYToCell(rx1, ry1);
                UnityEngine.Vector3 p0 = Grid.CellToPos(c0);
                UnityEngine.Vector3 p1 = Grid.CellToPos(c1);
                return "{\"worldXY\":{\"x0\":" + F(p0.x) + ",\"y0\":" + F(p0.y) + ",\"x1\":" + F(p1.x) + ",\"y1\":" + F(p1.y) + "}"
                     + ",\"cells\":{\"x0\":" + rx0 + ",\"y0\":" + ry0 + ",\"x1\":" + rx1 + ",\"y1\":" + ry1 + "}"
                     + ",\"cell0\":" + c0 + ",\"cell1\":" + c1 + "}";
            }
            catch (System.Exception e) { return "{\"error\":" + Snap.Str(e.GetType().Name + ": " + e.Message) + "}"; }
        }

        /// <summary>`MopTool.maxMopAmt`（public static float，cctor 里是 150.0f）—— 可拖的最大液体质量。</summary>
        static float MopMaxAmt()
        {
            try { return MopTool.maxMopAmt; } catch { return 150f; }
        }

        static string F(float v) { return v.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture); }


        // ================= 拖地（Activate → OnDragTool → finally 复原 + MopPlacer 回读）=================
        /// <summary>
        /// 拖地：**直接实例化 `MopPlacer` 预制体**写进 `Grid.Objects[cell, ObjectLayer.MopPlacer]`
        /// （与 `MopTool.OnDragTool` 的 IL 同一条路径）。
        ///
        /// ⚠️ 0.92.0/0.92.1 的两条失败路线都弃用：
        /// * 0.92.0 直接反射调 `OnDragTool`（未激活 ⇒ 不落地）；
        /// * 0.92.1 `ActivateTool` + `OnDragTool` + `DeactivateTool`（实测仍不落地）。
        /// ⇒ 现在**完全不依赖工具状态**。
        /// </summary>
        static int ApplyMop(System.Collections.Generic.List<Target> targets, int priority, ref int prioApplied, out string err)
        {
            err = null;
            var prefab = GetMopPlacerPrefab();
            if (prefab == null) { err = "拿不到 MopPlacer 预制体（MopTool.Placer 字段 与 Assets.GetPrefab(MopPlacerConfig.ID) 都失败）"; return -1; }

            int done = 0;
            for (int i = 0; i < targets.Count; i++)
            {
                var t = targets[i];
                if (t.AlreadyMarked) continue;
                try
                {
                    var go = Util.KInstantiate(prefab, Grid.CellToPos(t.Cell));
                    if (go == null) { if (err == null) err = "cell " + t.Cell + " KInstantiate 返回 null"; continue; }
                    try { Grid.Objects[t.Cell, (int)ObjectLayer.MopPlacer] = go; } catch { }
                    try { go.SetActive(true); } catch { }
                    if (priority > 0) ApplyPriority(go, priority, ref prioApplied);

                    // ★ M6.83：**判据换成 placer 是否留住**。实测（Cycle 5, (111,217) 0.037kg）：
                    //   真做后 `MopPlacerPresent` 由 0→1 并**留住**，20 秒后小人接了 `Mop` chore 把水渍拖掉。
                    //   （`HasMopChore(cell)` 查的是"格上物体"，而拖地 chore 挂在**接活的小人**身上 ⇒ 那个判据永远 false）
                    if (MopPlacerPresent(t.Cell)) done++;
                    else if (err == null) err = "cell " + t.Cell + " 实例化后 MopPlacer 不在该层（未生效）";
                }
                catch (System.Exception e)
                {
                    if (err == null) err = "cell " + t.Cell + " mop 失败: " + e.GetType().Name + ": " + e.Message;
                }
            }
            return done;
        }

        /// <summary>
        /// 取 `MopPlacer` 预制体：① 反射读 `MopTool.Instance.Placer`（nonpub 实例字段，玩家用的同一个）；
        /// ② 退回 `Assets.GetPrefab(new Tag(MopPlacerConfig.ID))`。
        /// </summary>
        static UnityEngine.GameObject GetMopPlacerPrefab()
        {
            try
            {
                var tool = MopTool.Instance;
                if (tool != null)
                {
                    var pf = typeof(MopTool).GetField("Placer",
                        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                    if (pf != null)
                    {
                        var go = pf.GetValue(tool) as UnityEngine.GameObject;
                        if (go != null) return go;
                    }
                }
            }
            catch { }

            try
            {
                string id = null;
                var fld = typeof(MopPlacerConfig).GetField("ID",
                    System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                if (fld != null) id = fld.GetValue(null) as string;
                if (!string.IsNullOrEmpty(id))
                {
                    var go = Assets.GetPrefab(new Tag(id));
                    if (go != null) return go;
                }
            }
            catch { }

            return null;
        }


        static bool MopPlacerPresent(int cell)
        {
            try { return Grid.Objects[cell, (int)ObjectLayer.MopPlacer] != null; } catch { return false; }
        }

        /// <summary>
        /// `attack` 的回读：该对象身上是否已挂着 `Attack` chore。
        /// 依据：`ChoreProvider.choreWorldMap : Dictionary&lt;int, List&lt;Chore&gt;&gt;` 是 **public 字段**，
        /// `Chore.choreType.Id` / `Chore.gameObject` 都是 public。
        /// </summary>
        /// <summary>
        /// `mop` 判据用的**真实** `ChoreType` —— 直接取游戏自己的表 `Db.Get().ChoreTypes.Mop`。
        /// ★ 0.93.7：原来靠 `choreType.Id` 里含 "Mop" 这个**推断串**判断（从实测文本 `阿玛里=Mop` 倒推的），
        ///   现在改成引用游戏表里那个单例；字符串只剩 `Id` 属性的回显（响应里的 `mopGating.choreTypeId`），
        ///   不再参与判定。取不到时返回 null，调用方**如实报 null，不许退回去猜**。
        /// </summary>
        static ChoreType MopChoreType()
        {
            try
            {
                var db = Db.Get();
                if (db == null) return null;
                return db.ChoreTypes != null ? db.ChoreTypes.Mop : null;
            }
            catch { return null; }
        }

        /// <summary>Mop choreType 的真实 `Id`（给响应回显用）；取不到就是 null。</summary>
        static string MopChoreTypeId()
        {
            try { var t = MopChoreType(); return t != null ? t.Id : null; } catch { return null; }
        }

        /// <summary>某个 choreType 是不是 Mop（引用比较优先，Id 相等兜底）。</summary>
        static bool IsMopChoreType(ChoreType ct)
        {
            if (ct == null) return false;
            var want = MopChoreType();
            if (want == null) return false;
            if (object.ReferenceEquals(ct, want)) return true;
            try { return string.Equals(ct.Id, want.Id, System.StringComparison.Ordinal); } catch { return false; }
        }

        /// <summary>
        /// 统计**小人身上**的 Mop chore 数量（拖地 chore 挂在接活的小人身上，不在那格的物体上）。
        /// 依据：`Components.LiveMinionIdentities` → `ChoreDriver.GetCurrentChore()` → `chore.choreType`。
        /// </summary>
        static int CountMopChoresOnMinions()
        {
            int n = 0;
            try
            {
                var cmp = Components.LiveMinionIdentities;
                if (cmp == null) return 0;
                foreach (MinionIdentity mi in cmp.Items)
                {
                    if (mi == null) continue;
                    var cd = mi.GetComponent<ChoreDriver>();
                    var chore = cd != null ? cd.GetCurrentChore() : null;
                    if (chore == null) continue;
                    if (IsMopChoreType(chore.choreType)) n++;
                }
            }
            catch { }
            return n;
        }

        static bool HasMopChore(int cell, int worldId)
        {
            try
            {
                int layers = (int)ObjectLayer.NumLayers;
                for (int ly = 0; ly < layers; ly++)
                {
                    UnityEngine.GameObject go = null;
                    try { go = Grid.Objects[cell, ly]; } catch { continue; }
                    if (go == null) continue;
                    var cp = go.GetComponent<ChoreProvider>();
                    if (cp == null || cp.choreWorldMap == null) continue;
                    System.Collections.Generic.List<Chore> list;
                    if (!cp.choreWorldMap.TryGetValue(worldId, out list) || list == null) continue;
                    for (int i = 0; i < list.Count; i++)
                    {
                        var c = list[i];
                        if (c == null) continue;
                        if (IsMopChoreType(c.choreType)) return true;
                    }
                }
            }
            catch { }
            return false;
        }

        static bool HasAttackChore(UnityEngine.GameObject go, int worldId)
        {
            try
            {
                // chore 是挂在**对象自己**身上的（`ChoreProvider` 是组件，`choreWorldMap` 是 public 字段）
                var cp = go.GetComponent<ChoreProvider>();
                if (cp == null || cp.choreWorldMap == null) return false;
                System.Collections.Generic.List<Chore> list;
                if (!cp.choreWorldMap.TryGetValue(worldId, out list) || list == null) return false;
                for (int i = 0; i < list.Count; i++)
                {
                    var c = list[i];
                    if (c == null || c.choreType == null) continue;
                    string id = null;
                    try { id = c.choreType.Id; } catch { }
                    if (id == null || id.IndexOf("Attack", System.StringComparison.OrdinalIgnoreCase) < 0) continue;
                    try { if (c.gameObject == go) return true; } catch { return true; }   // 同 provider 上的 Attack chore 即算
                }
            }
            catch { }
            return false;
        }

        static void ApplyPriority(UnityEngine.GameObject go, int priority, ref int applied)
        {
            try
            {
                var pr = go.GetComponent<Prioritizable>();
                if (pr == null) return;
                PrioritySetting ps;
                if (TryMakePriority(priority, out ps)) { pr.SetMasterPriority(ps); applied++; }
            }
            catch { }
        }

        static bool TryMakePriority(int value, out PrioritySetting ps)
        {
            ps = default(PrioritySetting);
            try
            {
                var asm = typeof(PrioritySetting).Assembly;
                System.Type tpc = null;
                var types = asm.GetTypes();
                for (int i = 0; i < types.Length; i++)
                    if (types[i] != null && types[i].Name == "PriorityClass") { tpc = types[i]; break; }
                if (tpc == null) return false;
                var basicF = tpc.GetField("basic", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
                var ctor = typeof(PrioritySetting).GetConstructor(new System.Type[] { tpc, typeof(int) });
                if (basicF == null || ctor == null) return false;
                ps = (PrioritySetting)ctor.Invoke(new object[] { basicF.GetValue(null), value });
                return true;
            }
            catch { return false; }
        }

        static bool IsDisinfectMarked(Disinfectable d)
        {
            try
            {
                var f = typeof(Disinfectable).GetField("isMarkedForDisinfect",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                if (f != null) return (bool)f.GetValue(d);
            }
            catch { }
            return false;
        }

        /// <summary>是否己方殖民者（`Minion` / `BionicMinion`，或身上有 `MinionIdentity`）。</summary>
        static bool IsOwnColony(UnityEngine.GameObject go)
        {
            try
            {
                if (go.GetComponent<MinionIdentity>() != null) return true;
                string pid = PrefabOf(go);
                if (!string.IsNullOrEmpty(pid))
                {
                    if (pid == "Minion" || pid == "BionicMinion") return true;
                    if (pid.IndexOf("Minion", System.StringComparison.OrdinalIgnoreCase) >= 0) return true;
                }
            }
            catch { }
            return false;
        }

        static string PrefabOf(UnityEngine.GameObject go)
        {
            try
            {
                var kp = go.GetComponent<KPrefabID>();
                if (kp != null && kp.PrefabTag.Name != null) return kp.PrefabTag.Name;
            }
            catch { }
            try { return go.name; } catch { return null; }
        }
    }
}
