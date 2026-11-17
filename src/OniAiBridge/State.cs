using System.Collections.Generic;
using Klei.AI;

// ⚠️ 不要 using System 之后直接用 Action/DateTime：Assembly-CSharp 里有同名全局类型会遮蔽它们。
namespace OniAiBridge
{
    /// <summary>
    /// 采集。**所有方法只能在主线程调用**（由 MainThread.Invoke / 泵里的 EventHub.Tick 保证）。
    ///
    /// 取值路径全部经实测确认：
    ///  * 属性：遍历 `Modifiers.amounts`（`Modifications&lt;,&gt;` 实现 IEnumerable&lt;AmountInstance&gt;），
    ///    取 `ai.modifier.Id` 与 `ai.value` —— 报的是游戏里真实存在的 id，不靠写死的字符串。
    ///  * 元素：`WorldInventory.GetAmount(element.tag, false)` 遍历 `ElementLoader.elements`
    ///    （M1 实测已核对：SandStone 35025.59 / Dirt 32872.61 / Water 10954.61 …）。
    ///  * 时钟/暂停/世界：GameClock / SpeedControlScreen / ClusterManager。
    /// </summary>
    internal static class State
    {
        public const float ElementMinKg = 1f;
        public const int ElementMaxEntries = 40;

        static readonly string[] CalorieHints = { "Calories", "Calorie" };
        static readonly string[] TemperatureHints = { "Temperature" };

        public static string Ts()
        {
            return System.DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ssK");
        }

        public static Snap CollectSnap(string elementsMode = null)
        {
            var swAll = System.Diagnostics.Stopwatch.StartNew();
            var s = new Snap();
            s.Ts = Ts();
            try { CollectColony(s); } catch (System.Exception e) { s.ColonyError = e.GetType().Name + ": " + e.Message; }
            try { CollectDupes(s); } catch (System.Exception e) { s.ColonyError = (s.ColonyError ?? "") + " dupes:" + e.GetType().Name; }
            try { CollectElements(s, elementsMode); } catch (System.Exception e) { s.ElementsError = e.GetType().Name + ": " + e.Message; }
            s.ElapsedMs = swAll.ElapsedMilliseconds;
            return s;
        }

        static void CollectColony(Snap s)
        {
            GameClock clock = GameClock.Instance;
            if (clock == null) { s.ColonyError = "GameClock.Instance == null（未载入殖民地）"; return; }
            s.CycleRaw = s.Cycle = clock.GetCycle();
            s.TimeSinceStartOfCycle = clock.GetTimeSinceStartOfCycle();
            s.TimePlayed = clock.GetTimePlayedInSeconds();
            s.Frame = clock.GetFrame();
            s.IsNight = clock.IsNighttime();

            SpeedControlScreen sc = SpeedControlScreen.Instance;
            if (sc != null) { s.IsPaused = sc.IsPaused; s.Speed = sc.GetSpeed(); }

            ClusterManager cm = ClusterManager.Instance;
            if (cm == null) return;
            s.WorldId = cm.activeWorldId;
            WorldContainer wc = cm.GetWorld(s.WorldId);
            if (wc == null) return;
            s.WorldName = wc.worldName;
            s.WorldTypeKey = wc.worldType;
            s.IsStartWorld = wc.IsStartWorld;
            var size = wc.WorldSize;      // Vector2I 的公开字段确实是 x/y（firstpass 实测）
            s.WorldSizeX = size.x;
            s.WorldSizeY = size.y;
        }

        static void CollectDupes(Snap s)
        {
            var cmp = Components.LiveMinionIdentities;
            if (cmp == null) return;
            s.DupeCount = cmp.Count;
            int idx = 0;
            foreach (MinionIdentity mi in cmp.Items)
            {
                if (mi == null) continue;
                var d = new Snap.DupeSnap();
                idx++;
                try { d.Name = mi.GetProperName(); } catch { }
                try { d.DbId = mi.nameStringKey; } catch { }
                if (string.IsNullOrEmpty(d.Name)) d.Name = d.DbId;
                try { d.Gender = mi.gender; } catch { }
                try { d.Cell = Grid.PosToCell(mi.gameObject); } catch { d.Cell = -1; }
                if (d.Cell >= 0)
                {
                    int dx, dy;
                    try { Grid.CellToXY(d.Cell, out dx, out dy); d.X = dx; d.Y = dy; } catch { }
                    try { d.WorldId = Grid.WorldIdx != null ? Grid.WorldIdx[d.Cell] : -1; } catch { }
                }
                d.Key = string.IsNullOrEmpty(d.DbId) ? ("dupe" + idx) : d.DbId;
                if (s.Dupes.Exists(x => x.Key == d.Key)) d.Key = d.Key + "#" + idx;   // 同 dbId 的罕见情况

                // 当前任务
                try
                {
                    var cd = mi.GetComponent<ChoreDriver>();
                    Chore chore = cd != null ? cd.GetCurrentChore() : null;
                    if (chore != null)
                    {
                        try { d.ChoreText = chore.GetReportName(); } catch { }
                        try { if (chore.choreType != null) d.ChoreId = chore.choreType.Id; } catch { }
                    }
                }
                catch { }

                // 属性
                try
                {
                    Modifiers mods = mi.modifiers;
                    var amounts = mods != null ? mods.amounts : null;
                    if (amounts == null) d.AmountsError = "Modifiers.amounts == null";
                    else
                    {
                        foreach (AmountInstance ai in amounts)
                        {
                            if (ai == null) continue;
                            Amount amt = ai.modifier;
                            string id = null;
                            try { if (amt != null) id = amt.Id; } catch { }
                            if (string.IsNullOrEmpty(id)) continue;
                            d.Amounts[id] = ai.value;
                        }
                    }
                }
                catch (System.Exception e) { d.AmountsError = e.GetType().Name + ": " + e.Message; }

                float cal = Lookup(d.Amounts, CalorieHints);
                float temp = Lookup(d.Amounts, TemperatureHints);
                float stress = Lookup(d.Amounts, new[] { "Stress" });
                if (!float.IsNaN(cal)) d.CaloriesKcal = cal / 1000f;
                if (!float.IsNaN(temp)) d.TemperatureC = temp - 273.15f;
                d.StressValue = stress;
                s.Dupes.Add(d);
            }
        }

        static void CollectElements(Snap s, string mode)
        {
            // M6.17：**Storage 聚合成为默认主路**（两个档上 WorldInventory 都给不出数据）；
            // WorldInventory 降级为可选：?elementsMode=worldinventory（只看它）或 auto（先它、再回退）
            string m = mode == null ? "" : mode.Trim().ToLowerInvariant();
            bool useWorldInv = (m == "worldinventory" || m == "auto");
            bool forceStorage = !useWorldInv || m == "storage";
            ClusterManager cm = ClusterManager.Instance;
            if (cm == null) { s.ElementsError = "ClusterManager 不可用"; return; }
            var worlds = cm.WorldContainers;   // ClusterManager.WorldContainers（public 属性，已核）
            if (worlds == null || worlds.Count == 0) { s.ElementsError = "没有世界"; return; }

            // M6.12 修正：**跨世界累加**。之前只查 activeWorld 的 WorldInventory；
            // 若当时看的是另一个小行星/无资源的太空世界，就会得到 count:0 —— 那是"查错了范围"，不是"没有资源"。
            s.ElementScope = "all-worlds-sum";
            s.ElementWorldsScanned = worlds.Count;

            var list = new List<KeyValuePair<string, float>>();
            var els = ElementLoader.elements;
            if (!useWorldInv) { els = null; s.ElementTagsScanned = 0; }   // 默认不走 WorldInventory
            s.ElementTagsScanned = els != null ? els.Count : 0;
            if (els != null)
            {
                for (int i = 0; i < els.Count; i++)
                {
                    Element el = els[i];
                    if (el == null) continue;
                    string id = null;
                    try { id = el.tag.Name; } catch { }
                    if (string.IsNullOrEmpty(id)) continue;
                    float total = 0f;      // GetAmount(tag, false)
                    float totalRel = 0f;   // GetAmount(tag, true) —— 用来验证第二参到底影响什么
                    for (int w = 0; w < worlds.Count; w++)
                    {
                        var wc = worlds[w];
                        if (wc == null) continue;
                        var inv = wc.worldInventory;
                        if (inv == null) continue;
                        try { total += inv.GetAmount(el.tag, false); } catch { }
                        try { totalRel += inv.GetAmount(el.tag, true); } catch { }
                    }
                    if (!float.IsNaN(totalRel) && totalRel >= ElementMinKg) s.ElementPositiveTagsRelated++;
                    if (float.IsNaN(total) || total < ElementMinKg) continue;
                    s.ElementPositiveTags++;
                    list.Add(new KeyValuePair<string, float>(id, total));
                }
            }
            // ★ M6.16：WorldInventory 两条路都读不到东西（实测 positiveTags=0 且 positiveTagsRelated=0）
            //   ⇒ 换方案：**遍历所有建筑的 Storage，按 GetAmountAvailable(tag) 累加**（慢，但能读到实际存在的物品）
            if (list.Count == 0 || forceStorage)
            {
                if (forceStorage) list.Clear();          // 强制模式：忽略 WorldInventory 的结果
                var totals = new System.Collections.Generic.Dictionary<string, float>();
                int storages = 0; int unnamed = 0;
                try
                {
                    var builds = Components.BuildingCompletes != null ? Components.BuildingCompletes.Items : null;
                    if (builds != null)
                    {
                        for (int i = 0; i < builds.Count; i++)
                        {
                            var b = builds[i];
                            if (b == null) continue;
                            UnityEngine.Component[] comps = null;
                            try { comps = b.GetComponentsInChildren<Storage>(); } catch { }
                            if (comps == null) continue;
                            for (int k = 0; k < comps.Length; k++)
                            {
                                var st = comps[k] as Storage;
                                if (st == null) continue;
                                storages++;
                                System.Collections.Generic.HashSet<Tag> ids = null;
                                try { ids = st.GetAllIDsInStorage(); } catch { }
                                if (ids == null) continue;
                                foreach (var id in ids)
                                {
                                    if (unnamed < 3) { try { Log.Info("[state] storage tag: Name=[" + (id.Name ?? "<null>") + "] ToString=[" + id.ToString() + "]"); } catch { } }
                                    float v = 0f;
                                    try { v = st.GetAmountAvailable(id); } catch { continue; }
                                    if (float.IsNaN(v) || v < ElementMinKg) continue;
                                    string nm = null;
                                    try { nm = id.Name; } catch { }
                                    if (string.IsNullOrEmpty(nm)) { try { nm = id.ToString(); } catch { } }   // 回退到枚举名
                                    if (string.IsNullOrEmpty(nm)) { unnamed++; continue; }
                                    float cur;
                                    totals.TryGetValue(nm, out cur);
                                    totals[nm] = cur + v;
                                }
                            }
                        }
                    }
                }
                catch (System.Exception e2) { s.ElementsError = "storage 聚合失败: " + e2.GetType().Name; }

                foreach (var kv in totals) list.Add(new KeyValuePair<string, float>(kv.Key, kv.Value));
                s.ElementScope = forceStorage
                    ? (m == "storage" ? "storages-sum(forced by elementsMode=storage)"
                                      : "storages-sum(default 主路：遍历建筑 Storage 累加)")
                    : "storages-sum（WorldInventory 返回 0 后的回退）";
                s.ElementStoragesScanned = storages;
                s.ElementTagsFound = totals.Count;          // 真正有数的 tag 数
                s.ElementTagsUnnamed = unnamed;
                if (s.ElementTagsScanned == 0) s.ElementTagsScanned = totals.Count;   // 默认路：扫到的 tag 数
                s.ElementPositiveTagsRelated = 0;           // 默认路不跑 GetAmount(tag,true)
                s.ElementTagsScanned = totals.Count;
                s.ElementPositiveTags = totals.Count;
                s.ElementWorldsScanned = 0;
                try { Log.Info("[state] elements 改用 Storage 聚合：storages=" + storages + " tags=" + totals.Count); } catch { }
            }

            list.Sort((a, b) => b.Value.CompareTo(a.Value));
            s.ElementCount = list.Count;
            int n = System.Math.Min(list.Count, ElementMaxEntries);
            for (int i = 0; i < n; i++)
            {
                s.Elements[list[i].Key] = list[i].Value;
                if (i < 10) { try { Log.Info("[state] emit tag=[" + (list[i].Key ?? "<null>") + "] amount=[" + list[i].Value + "]"); } catch { } }
            }
            try { Log.Info("[state] elements 写入 " + n + " 条；ElementCount=" + s.ElementCount + "；字典条数=" + s.Elements.Count); } catch { }
        }

        /// <summary>大小写不敏感、允许子串的语义查找（容忍 id 拼写差异）。</summary>
        public static float Lookup(Dictionary<string, float> vals, string[] hints)
        {
            for (int h = 0; h < hints.Length; h++)
                foreach (var kv in vals)
                    if (string.Equals(kv.Key, hints[h], System.StringComparison.OrdinalIgnoreCase)) return kv.Value;
            for (int h = 0; h < hints.Length; h++)
                foreach (var kv in vals)
                    if (kv.Key.IndexOf(hints[h], System.StringComparison.OrdinalIgnoreCase) >= 0) return kv.Value;
            return float.NaN;
        }
    }
}
