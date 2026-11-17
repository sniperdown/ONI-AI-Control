using System.Text;
using Klei.AI;

// ⚠️ 不要 using System 之后直接用 Action/DateTime：Assembly-CSharp 里的全局 Action 枚举会遮蔽 System.Action。
namespace OniAiBridge
{
    /// <summary>
    /// M6.70：**告警判据的唯一出处**（阈值 / 移动类 chore 白名单 / 卡住窗口都在这里；`docs/API.md §3.35` 逐条对应）。
    ///
    /// ⚠️ **两套阈值**：实测（0.89.2「外世」档）小人有两种身体：
    /// * `organic`：有 `Calories/CaloriesKcal/Bladder/Stamina` ⇒ 看 呼吸 / 卡路里 / 压力 / 膀胱 / 疾病
    /// * `bionic`：**没有** 上面三个，改为 `BionicInternalBattery/BionicOil/BionicOxygenTank/BionicGunk`
    ///   ⇒ 看 呼吸 / **电池** / **油** / **氧气罐** / 疾病。
    ///
    /// **仿生人那三项的单位不确定**（实测电池 566,566~624,104，量级不像百分比）⇒
    /// 判据一律用 **`value / max` 的比例**（`AmountInstance.GetMax()`，运行时读），
    /// 这样与单位无关；响应里同时给 `value`、`threshold`（= 比例×max）、`unit`（`Amount.units` 枚举名）。
    /// 若 `max` 读不到（0/NaN）⇒ 该条不给阈值，标 `thresholdUnavailable:true`，**不拿拍脑袋的数字报警**。
    /// </summary>
    internal static class AlertRules
    {
        // ---- 卡住探针 ----
        public const double StuckWindowSeconds = 5.0;
        public static readonly string[] MovementChorePrefixes = new string[] { "MoveTo" };
        public const string IdleChoreId = "Idle";

        // ---- organic（普通小人）阈值 ----
        public const float BreathWarn = 30f;             // 呼吸 < 30 ⇒ warning
        public const float BreathCritical = 10f;         // 呼吸 < 10 ⇒ critical
        public const float CaloriesWarnKcal = 1500f;     // 卡路里 < 1500 kcal ⇒ warning
        public const float CaloriesCriticalKcal = 500f;  // < 500 ⇒ critical
        public const float StressWarn = 80f;             // 压力 > 80% ⇒ warning
        public const float StressCritical = 95f;         // > 95% ⇒ critical
        public const float BladderWarn = 95f;            // 膀胱 > 95% ⇒ warning

        // ---- bionic（仿生人）阈值：**比例**（value/max）----
        public const float BionicReserveWarnRatio = 0.20f;      // 电池/油/氧气罐 < 20% ⇒ warning
        public const float BionicReserveCriticalRatio = 0.10f;  // < 10% ⇒ critical

        /// <summary>仿生人要看的三项储量（比例判据）。</summary>
        public static readonly string[] BionicReserveIds = new string[]
        { "BionicInternalBattery", "BionicOil", "BionicOxygenTank" };

        /// <summary>疾病类 amount 的 id（任一 &gt; 0 就报警，两套身体通用）。</summary>
        public static readonly string[] DiseaseIds = new string[]
        { "FoodPoisoning", "SlimeLung", "PollenGerms", "ZombieSpores", "RadiationSickness" };

        public static bool IsMovementChore(string choreId)
        {
            if (string.IsNullOrEmpty(choreId)) return false;
            for (int i = 0; i < MovementChorePrefixes.Length; i++)
                if (choreId.StartsWith(MovementChorePrefixes[i], System.StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        public static bool IsIdleChore(string choreId)
        {
            return !string.IsNullOrEmpty(choreId)
                && string.Equals(choreId, IdleChoreId, System.StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// M6.70（**只读**）：`GET /v1/alerts` —— 按严重度排序的告警清单。
    ///
    /// 判据全部来自 `AlertRules`（单点定义）。建筑类**只做两项**（`building_broken` / `building_stopped`），
    /// 数据源 `Building.GetComponent<Operational>()` 的 public 属性 `IsFunctional/IsOperational/IsActive`；
    /// **`building_no_material` 本版不做**（缺料要走建造 chore + 配方比对，没有可靠单一判据 ⇒ 宁可省略，不编）。
    /// </summary>
    internal static class AlertsApi
    {
        /// <summary>建筑告警的聚合单元：同一 (kind, prefabId, worldId) 只出一条。</summary>
        sealed class Group
        {
            public string Kind;
            public string PrefabId;
            public int WorldId;
            public int Count;
            public System.Collections.Generic.List<int[]> Sample;   // 最多 3 个坐标
            public int? HpMin;   // 仅 building_broken：最小心血量（用于核实判据）
            public int? HpMax;
        }

        sealed class Alert
        {
            public string Severity;
            public string Kind;
            public string SubjectKind;   // "dupe" | "building"
            public string Name;
            public string PrefabId;
            public int X, Y, WorldId;
            public double? Value;
            public double? Threshold;
            public string Unit;
            public string Hint;
            public bool ThresholdUnavailable;
        }

        public static string List(bool detail, out int status, out string code, out string message)
        {
            status = 200; code = null; message = null;
            if (!MainThread.Ready) { status = 503; code = "NOT_READY"; message = "main-thread pump is not running yet"; return null; }

            string data = null, fail = null;
            int okT; bool busy;
            bool ran = MainThread.InvokeAdaptive(() =>
            {
                try
                {
                    var alerts = new System.Collections.Generic.List<Alert>();
                    int dupesScanned = 0, buildingsScanned = 0, bionicCount = 0;
                    var bldGroups = new System.Collections.Generic.Dictionary<string, Group>();

                    // ---------- 复制人 ----------
                    var cmp = Components.LiveMinionIdentities;
                    if (cmp != null)
                    {
                        foreach (MinionIdentity mi in cmp.Items)
                        {
                            if (mi == null) continue;
                            dupesScanned++;

                            string name = null;
                            try { name = mi.GetProperName(); } catch { }
                            int cell = -1;
                            try { cell = Grid.PosToCell(mi.gameObject); } catch { }
                            int x = -1, y = -1, wid = -1;
                            if (cell >= 0)
                            {
                                try { Grid.CellToXY(cell, out x, out y); } catch { }
                                try { wid = Grid.WorldIdx != null ? Grid.WorldIdx[cell] : -1; } catch { }
                            }

                            var vals = new System.Collections.Generic.Dictionary<string, float>();
                            var maxes = new System.Collections.Generic.Dictionary<string, float>();
                            var mins = new System.Collections.Generic.Dictionary<string, float>();
                            var units = new System.Collections.Generic.Dictionary<string, string>();
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
                                        try { vals[id] = ai.value; } catch { }
                                        try { mins[id] = ai.GetMin(); } catch { }
                                        try { maxes[id] = ai.GetMax(); } catch { }
                                        try { if (amt != null) units[id] = amt.units.ToString(); } catch { }
                                    }
                                }
                            }
                            catch { }

                            bool bionic = vals.ContainsKey(DupesApi.BionicProbeAmountId);
                            if (bionic) bionicCount++;
                            string kind = bionic ? "bionic" : "organic";

                            // 呼吸（两套通用）
                            float v;
                            if (vals.TryGetValue("Breath", out v))
                            {
                                if (v < AlertRules.BreathCritical)
                                    Add(alerts, "critical", "dupe_breath", "dupe", name, null, x, y, wid, v, AlertRules.BreathCritical, "%",
                                        "马上安排呼吸/供氧，或把人撤出缺氧区", kind);
                                else if (v < AlertRules.BreathWarn)
                                    Add(alerts, "warning", "dupe_breath", "dupe", name, null, x, y, wid, v, AlertRules.BreathWarn, "%",
                                        "检查这片区域的氧气浓度与供氧", kind);
                            }

                            // 压力（两套通用）
                            if (vals.TryGetValue("Stress", out v))
                            {
                                if (v > AlertRules.StressCritical)
                                    Add(alerts, "critical", "dupe_stress", "dupe", name, null, x, y, wid, v, AlertRules.StressCritical, "%",
                                        "立刻降压力：休息/装饰/洗澡，并查高压源", kind);
                                else if (v > AlertRules.StressWarn)
                                    Add(alerts, "warning", "dupe_stress", "dupe", name, null, x, y, wid, v, AlertRules.StressWarn, "%",
                                        "安排休息或改善装饰", kind);
                            }

                            if (!bionic)
                            {
                                // 卡路里（organic 专有；bionic 没有这个 amount ⇒ 不会走到这里）
                                if (vals.TryGetValue("Calories", out v))
                                {
                                    float kcal = v / 1000f;
                                    if (kcal < AlertRules.CaloriesCriticalKcal)
                                        Add(alerts, "critical", "dupe_calories", "dupe", name, null, x, y, wid, kcal, AlertRules.CaloriesCriticalKcal, "kcal",
                                            "立刻派人吃/补卡路里，否则会饿晕", kind);
                                    else if (kcal < AlertRules.CaloriesWarnKcal)
                                        Add(alerts, "warning", "dupe_calories", "dupe", name, null, x, y, wid, kcal, AlertRules.CaloriesWarnKcal, "kcal",
                                            "保证食物供给与用餐时间", kind);
                                }
                                // 膀胱（organic 专有）
                                if (vals.TryGetValue("Bladder", out v) && v > AlertRules.BladderWarn)
                                    Add(alerts, "warning", "dupe_bladder", "dupe", name, null, x, y, wid, v, AlertRules.BladderWarn, "%",
                                        "确认厕所有人可达、数量够", kind);
                            }
                            else
                            {
                                // 仿生人：电池 / 油 / 氧气罐（**比例判据**，与单位无关）
                                for (int i = 0; i < AlertRules.BionicReserveIds.Length; i++)
                                {
                                    string id = AlertRules.BionicReserveIds[i];
                                    float cur, mx;
                                    if (!vals.TryGetValue(id, out cur)) continue;   // 没有就不报（不补 0）
                                    if (!maxes.TryGetValue(id, out mx) || mx <= 0f || float.IsNaN(mx))
                                    {
                                        var a0 = Add(alerts, "info", "dupe_bionic_reserve", "dupe", name, null, x, y, wid,
                                            cur, null, UnitOf(units, id), "该 amount 的 max 读不到，无法给阈值（只报当前值）", kind);
                                        a0.ThresholdUnavailable = true;
                                        continue;
                                    }
                                    double ratio = cur / mx;
                                    double thrWarn = AlertRules.BionicReserveWarnRatio * mx;
                                    double thrCrit = AlertRules.BionicReserveCriticalRatio * mx;
                                    string unit = UnitOf(units, id);
                                    if (ratio < AlertRules.BionicReserveCriticalRatio)
                                        Add(alerts, "critical", "dupe_bionic_reserve", "dupe", name, null, x, y, wid, cur, thrCrit, unit,
                                            "给仿生人补 " + id + "（电池去充电座 / 油与氧气罐补货）", kind);
                                    else if (ratio < AlertRules.BionicReserveWarnRatio)
                                        Add(alerts, "warning", "dupe_bionic_reserve", "dupe", name, null, x, y, wid, cur, thrWarn, unit,
                                            "提前安排补给 " + id, kind);
                                }
                            }

                            // 疾病（两套通用）
                            for (int i = 0; i < AlertRules.DiseaseIds.Length; i++)
                            {
                                string id = AlertRules.DiseaseIds[i];
                                float dv;
                                if (!vals.TryGetValue(id, out dv)) continue;
                                if (dv > 0f)
                                    Add(alerts, "warning", "dupe_disease", "dupe", name, null, x, y, wid, dv, 0, "%",
                                        "安排治疗/隔离，检查污染源", kind);
                            }
                        }
                    }

                    // ---------- 建筑（只做 broken / stopped）----------
                    var blds = Components.BuildingCompletes;
                    if (blds != null)
                    {
                        foreach (Building b in blds.Items)
                        {
                            if (b == null) continue;
                            buildingsScanned++;
                            Operational op = null;
                            try { op = b.GetComponent<Operational>(); } catch { }
                            if (op == null) continue;

                            bool functional = true, operational = true;
                            try { functional = op.IsFunctional; } catch { }
                            try { operational = op.IsOperational; } catch { }

                            // ★ M6.73：**三态**（0.90.2 把"没在运行"当"坏"，14 组全是误报）
                            //   ① building_broken  = 血量归零（BuildingHP.HitPoints <= 0）—— 需修复；
                            //   ② building_disabled = 玩家用建筑开关关掉了（BuildingEnabledButton.IsEnabled == false）⇒ 配置，不是故障；
                            //   ③ building_stopped  = 其余"开着但没在运行"（缺料/缺工/没通电等）⇒ info。
                            BuildingEnabledButton enBtn = null;
                            bool enabled = true;
                            try { enBtn = b.GetComponent<BuildingEnabledButton>(); if (enBtn != null) enabled = enBtn.IsEnabled; } catch { }

                            BuildingHP hp = null; bool hpOk = false; int hpCur = -1, hpMaxV = -1;
                            try { hp = b.GetComponent<BuildingHP>(); } catch { }
                            if (hp != null)
                            {
                                try { hpCur = hp.HitPoints; hpMaxV = hp.MaxHitPoints; hpOk = true; } catch { }
                            }
                            bool broken = hpOk && hpCur <= 0;    // 血量归零 = 已损坏待修（HP 组件不存在 ⇒ 不会判坏）

                            // 只有"开着、有血、在运行"才完全跳过
                            if (functional && operational && enabled && !broken) continue;
                            if (functional && operational) continue;

                            int bc = -1;
                            try { bc = Grid.PosToCell(b.gameObject); } catch { }
                            int bx = -1, by = -1, bw = -1;
                            if (bc >= 0)
                            {
                                try { Grid.CellToXY(bc, out bx, out by); } catch { }
                                try { bw = Grid.WorldIdx != null ? Grid.WorldIdx[bc] : -1; } catch { }
                            }
                            string pid = null;
                            try { pid = b.Def != null ? b.Def.PrefabID : null; } catch { }

                            // ★ M6.71/6.73：建筑类**聚合**（按 kind + prefabId + worldId），kind 取三态之一
                            string bkind = broken ? "building_broken" : (!enabled ? "building_disabled" : "building_stopped");
                            string gkey = bkind + "|" + (pid ?? "<null>") + "|" + bw;
                            Group g;
                            if (!bldGroups.TryGetValue(gkey, out g))
                            {
                                g = new Group
                                {
                                    Kind = bkind,
                                    PrefabId = pid,
                                    WorldId = bw,
                                    Sample = new System.Collections.Generic.List<int[]>()
                                };
                                bldGroups[gkey] = g;
                            }
                            g.Count++;
                            if (g.Sample.Count < 3) g.Sample.Add(new int[] { bx, by });
                            if (broken && hpOk)
                            {
                                if (!g.HpMin.HasValue || hpCur < g.HpMin.Value) g.HpMin = hpCur;
                                if (!g.HpMax.HasValue || hpMaxV > g.HpMax.Value) g.HpMax = hpMaxV;
                            }
                            if (detail)
                                Add(alerts, bkind == "building_broken" ? "warning" : "info", bkind, "building", null, pid, bx, by, bw, null, null, null,
                                    bkind == "building_broken" ? "血量归零，需派复制人修理"
                                    : (bkind == "building_disabled" ? "玩家手动关闭（配置，不是故障）；需要它工作就去开"
                                       : "检查供电/输入/条件；停工不一定是故障"), null);
                        }        // foreach (Building b …)
                    }            // if (blds != null)

                    // ---------- 排序（dupe 逐条）----------
                    alerts.Sort((a, b) => SevRank(a.Severity).CompareTo(SevRank(b.Severity)));

                    // 汇总：聚合后的条数 vs 聚合前的原始条数（两个数都给，别只给一个）
                    int occDupe = alerts.Count;
                    int occBld = 0;
                    foreach (var kv in bldGroups) occBld += kv.Value.Count;
                    int occTotal = occDupe + occBld;
                    int aggTotal = occDupe + bldGroups.Count;

                    // ---------- 组装 ----------
                    var sb = new StringBuilder(4096);
                    sb.Append("{\"dupesScanned\":").Append(dupesScanned);
                    sb.Append(",\"buildingsScanned\":").Append(buildingsScanned);
                    sb.Append(",\"bionicDupes\":").Append(bionicCount);
                    sb.Append(",\"detail\":").Append(detail ? "true" : "false");
                    sb.Append(",\"aggregation\":{\"dupeAlerts\":\"per-dupe（不聚合）\",\"buildingAlerts\":\"by(kind,prefabId,worldId)\"}");

                    var body = new StringBuilder(4096);
                    int emitted = 0, crit = 0, warn = 0, info = 0;

                    for (int i = 0; i < alerts.Count; i++)
                    {
                        var a = alerts[i];
                        if (a.Severity == "critical") crit++;
                        else if (a.Severity == "warning") warn++;
                        else info++;
                        if (emitted > 0) body.Append(',');
                        emitted++;
                        body.Append("{\"severity\":").Append(Snap.Str(a.Severity));
                        body.Append(",\"kind\":").Append(Snap.Str(a.Kind));
                        body.Append(",\"subjectKind\":").Append(Snap.Str(a.SubjectKind));
                        body.Append(",\"subject\":{\"dupeName\":").Append(Snap.Str(a.Name));
                        body.Append(",\"prefabId\":").Append(Snap.Str(a.PrefabId));
                        body.Append(",\"x\":").Append(a.X).Append(",\"y\":").Append(a.Y);
                        body.Append(",\"worldId\":").Append(a.WorldId).Append('}');
                        body.Append(",\"count\":1");
                        body.Append(",\"value\":").Append(a.Value.HasValue ? Fmt(a.Value.Value) : "null");
                        body.Append(",\"threshold\":").Append(a.Threshold.HasValue ? Fmt(a.Threshold.Value) : "null");
                        body.Append(",\"unit\":").Append(Snap.Str(a.Unit));
                        if (a.ThresholdUnavailable) body.Append(",\"thresholdUnavailable\":true");
                        body.Append(",\"hint\":").Append(Snap.Str(a.Hint));
                        body.Append('}');
                    }

                    // 建筑聚合条目（默认模式；detail=1 时明细已在 alerts 里逐条，仍附一份聚合摘要便于对账）
                    var glist = new System.Collections.Generic.List<Group>(bldGroups.Values);
                    glist.Sort((x, y) => y.Count.CompareTo(x.Count));
                    for (int i = 0; i < glist.Count; i++)
                    {
                        var g = glist[i];
                        if (detail) continue;   // detail 模式只出逐条明细（其严重度已在 alerts 里计过），不重复出聚合条目/不重复计数
                        string sev = g.Kind == "building_broken" ? "warning" : "info";   // broken ⇒ warning；disabled/stopped ⇒ info（配置/空闲不是故障）
                        if (sev == "warning") warn++; else info++;
                        if (emitted > 0) body.Append(',');
                        emitted++;
                        body.Append("{\"severity\":").Append(Snap.Str(sev));
                        body.Append(",\"kind\":").Append(Snap.Str(g.Kind));
                        body.Append(",\"subjectKind\":\"building\"");
                        body.Append(",\"subject\":{\"dupeName\":null,\"prefabId\":").Append(Snap.Str(g.PrefabId))
                            .Append(",\"worldId\":").Append(g.WorldId).Append('}');
                        body.Append(",\"prefabId\":").Append(Snap.Str(g.PrefabId));
                        body.Append(",\"worldId\":").Append(g.WorldId);
                        body.Append(",\"count\":").Append(g.Count);
                        body.Append(",\"sample\":[");
                        for (int j = 0; j < g.Sample.Count; j++)
                        {
                            if (j > 0) body.Append(',');
                            body.Append("{\"x\":").Append(g.Sample[j][0]).Append(",\"y\":").Append(g.Sample[j][1]).Append('}');
                        }
                        body.Append(']');
                        if (g.HpMin.HasValue) body.Append(",\"hp\":").Append(g.HpMin.Value).Append(",\"maxHp\":").Append(g.HpMax.HasValue ? g.HpMax.Value : 0);
                        body.Append(",\"value\":null,\"threshold\":null,\"unit\":null");
                        body.Append(",\"hint\":").Append(Snap.Str(g.Kind == "building_broken"
                            ? "血量归零，需派复制人修理"
                            : (g.Kind == "building_disabled" ? "玩家手动关闭（配置，不是故障）" : "检查供电/输入/条件；停工不一定是故障")));
                        body.Append('}')
                        ;
                    }
                    sb.Append(",\"counts\":{\"alerts\":").Append(aggTotal)
                      .Append(",\"occurrences\":").Append(occTotal)
                      .Append(",\"dupeAlerts\":").Append(occDupe)
                      .Append(",\"buildingGroups\":").Append(bldGroups.Count)
                      .Append(",\"buildingOccurrences\":").Append(occBld).Append('}');
                    sb.Append(",\"critical\":").Append(crit).Append(",\"warning\":").Append(warn).Append(",\"info\":").Append(info);

                    // ---------- 响应体硬上限 200 KB ----------
                    const int MaxBytes = 200 * 1024;
                    var full = new StringBuilder(sb.Length + body.Length + 4096);
                    full.Append(sb).Append(",\"alerts\":[").Append(body).Append(']');
                    AppendTail(full);
                    string text = full.ToString();
                    if (text.Length > MaxBytes)
                    {
                        // 只返回计数、不返回明细数组；**明确说明被截断**，不静默
                        var lean = new StringBuilder(2048);
                        lean.Append(sb).Append(",\"alerts\":null,\"truncated\":true");
                        lean.Append(",\"truncatedReason\":").Append(Snap.Str(
                            "聚合后响应体仍超过 " + (MaxBytes / 1024) + "KB 上限（实际 " + text.Length + " 字节）⇒ 本次只返回计数，不返回明细数组；用 ?detail=1 取明细（同样受上限约束）"));
                        AppendTail(lean);
                        text = lean.ToString();
                    }
                    data = text;
                    Log.Info("[alerts] dupesScanned=" + dupesScanned + " buildingsScanned=" + buildingsScanned + " bionic=" + bionicCount
                             + " detail=" + detail + " alerts=" + aggTotal + " occurrences=" + occTotal
                             + " (dupe=" + occDupe + ", bldOcc=" + occBld + ", bldGroups=" + bldGroups.Count + ")"
                             + " critical=" + crit + " warning=" + warn + " info=" + info + " bytes=" + text.Length);
                }
                catch (System.Exception e)
                {
                    // ★ M6.72：**把异常打全**（0.90.1 那次只留了"JSON 非法"，看不到真因，靠猜了一轮）
                    string full = null;
                    try { full = e.ToString(); } catch { }
                    if (full != null && full.Length > 500) full = full.Substring(0, 500) + "…";
                    Log.Warn("[alerts] 采集异常（完整栈前 500 字）: " + (full ?? (e.GetType().Name + ": " + e.Message)));
                    fail = e.GetType().Name + ": " + e.Message;
                }
            }, 8000, 40000, out okT, out busy);

            if (!ran) { status = 503; code = "RETRY_LATER"; message = "main thread busy；请 1 秒后重试"; return null; }
            if (fail != null) { status = 500; code = "ALERTS_UNAVAILABLE"; message = fail; return null; }
            if (data == null) { status = 500; code = "INTERNAL"; message = "alerts 采集返回空"; return null; }
            if (data.Length < 2 || data[0] != '{' || data[data.Length - 1] != '}') { status = 500; code = "INTERNAL"; message = "alerts 生成了不完整的 JSON（首尾不是 {}，长度 " + data.Length + "）"; return null; }   // ★ 廉价护栏：0.90.1 的半截 JSON 就是这类
            JsonCheck.Validate(data, "alerts");
            return data;
        }

        static Alert Add(System.Collections.Generic.List<Alert> list, string sev, string kind, string subjectKind,
                         string name, string prefabId, int x, int y, int wid,
                         double? value, double? threshold, string unit, string hint, string dupeKind)
        {
            var a = new Alert
            {
                Severity = sev,
                Kind = kind,
                SubjectKind = subjectKind,
                Name = name,
                PrefabId = prefabId,
                X = x,
                Y = y,
                WorldId = wid,
                Value = value,
                Threshold = threshold,
                Unit = unit,
                Hint = hint
            };
            list.Add(a);
            return a;
        }

        static string UnitOf(System.Collections.Generic.Dictionary<string, string> units, string id)
        {
            string u;
            return units.TryGetValue(id, out u) ? u : null;
        }

        static int SevRank(string s)
        {
            if (s == "critical") return 0;
            if (s == "warning") return 1;
            return 2;
        }

        /// <summary>尾部固定字段（阈值表 / source / note）—— 全量与截断两条路径共用，避免两处各写一套。</summary>
        static void AppendTail(StringBuilder sb)
        {
            sb.Append(",\"thresholds\":{");
            sb.Append("\"organic\":{\"breathWarn\":").Append(AlertRules.BreathWarn)
              .Append(",\"breathCritical\":").Append(AlertRules.BreathCritical)
              .Append(",\"caloriesWarnKcal\":").Append(AlertRules.CaloriesWarnKcal)
              .Append(",\"caloriesCriticalKcal\":").Append(AlertRules.CaloriesCriticalKcal)
              .Append(",\"stressWarn\":").Append(AlertRules.StressWarn)
              .Append(",\"stressCritical\":").Append(AlertRules.StressCritical)
              .Append(",\"bladderWarn\":").Append(AlertRules.BladderWarn).Append('}')
              .Append(",\"bionic\":{\"reserveWarnRatio\":").Append(AlertRules.BionicReserveWarnRatio)
              .Append(",\"reserveCriticalRatio\":").Append(AlertRules.BionicReserveCriticalRatio)
              .Append(",\"reserveIds\":[").Append(string.Join(",", System.Array.ConvertAll(AlertRules.BionicReserveIds, x => "\"" + x + "\""))).Append("]}");
            sb.Append(",\"diseaseAnyPositive\":true,\"stuckWindowSeconds\":").Append(AlertRules.StuckWindowSeconds).Append('}')
              .Append(",\"source\":").Append(Snap.Str("LiveMinionIdentities.modifiers.amounts + BuildingCompletes.GetComponent<Operational>()"))
              .Append(",\"note\":").Append(Snap.Str(
                  "只读。**两套阈值**：organic 看 呼吸/卡路里/压力/膀胱/疾病；bionic 看 呼吸/**电池·油·氧气罐（value/max 比例）**/压力/疾病。"
                  + "`subjectKind` 指出用的哪套；仿生人三项单位不确定 ⇒ 判据用比例，`max` 读不到时 `thresholdUnavailable:true`。"
                  + "**建筑类告警已聚合**（按 kind+prefabId+worldId，一栋一条的明细改成 `count` + `sample`≤3 个坐标）；"
                  + "`counts.alerts` 是聚合后条数、`counts.occurrences` 是聚合前原始条数 —— 两个数都给。"
                  + "**建筑三态**：`building_broken` = **血量归零**（`BuildingHP.HitPoints <= 0`，聚合条目附带 `hp`/`maxHp`）；`building_disabled` = 玩家用建筑开关关了（`BuildingEnabledButton.IsEnabled == false`，配置不是故障）；`building_stopped` = 其余开着但没在运行（缺料/缺工/没通电）。0.90.2 把三态混成一态 ⇒ 14 组全是误报。"
                  + "默认不返回逐条明细；需要时用 `?detail=1`（同样受 200KB 上限约束，超了就只给计数并标 `truncated:true`）。"
                  + "**`building_no_material` 本版不提供**（缺料需要建造 chore + 配方比对，没有可靠单一判据）；"
                  + "`dupe_stuck_candidate` 也不进本端点（在 /v1/dupes 的 stuckProbe 里，可靠性低）。"));
            sb.Append('}')
            ;
        }

        static string Fmt(double v)
        {
            if (double.IsNaN(v) || double.IsInfinity(v)) return "null";
            return v.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
        }
    }
}
