using System.Text;
using Klei.AI;

namespace OniAiBridge
{
    /// <summary>
    /// `GET /v1/build-materials?prefabId=Electrolyzer` —— **这个建筑能用哪些材料 + 本档实况**。
    ///
    /// 为什么要这个端点：`build` 不传 `materials` 时走 `def.DefaultElements()`（该类别里的默认一项），
    /// 那个默认项**不保证库存里有** —— 盲测时 Tile 的默认项是 `Isoresin`（本档 0kg），
    /// 结果 API 返回 `placed=true` 但蓝图永远建不完。根因是**调用方不知道还有别的材料可选**。
    ///
    /// 硬性要求（主人定）：**穷举**——每种合法材料都列，**哪怕本档 0kg 也要列**，
    /// 并给 `available`（inStorage / onGround / total）与 `enough`。
    /// 只列够的会让 AI 连"还能换材料"这个念头都没有，那个信息差比"材料不够"更致命。
    ///
    /// **只给数据，不给推荐**：没有 `recommended` 字段，选哪个由调用方按温度/导热/库存自己判。
    ///
    /// 排序：`enough=true` 的在前，组内按 tag 名；**排序不影响"全部列出"**。
    /// </summary>
    internal static class BuildMaterials
    {
        // ===================== 供 build 响应复用的轻量候选清单 =====================

        /// <summary>
        /// 轻量候选清单（只给 tag / available / enough / discovered，**不带完整 props**）。
        /// `build` 在「没传 materials」或「传了但不够」时把这段塞进响应，
        /// 让调用方**第一次失败就能看到全部备选**，不必先查 /v1/build-materials。
        /// 排序与 /v1/build-materials 一致：够的在前。
        /// </summary>
        internal static string LightList(BuildingDef def, double needKg, out int total, out int enoughCount)
        {
            total = 0; enoughCount = 0;
            try
            {
                if (def == null) return null;
                string[] cats = null;
                try { cats = def.MaterialCategory; } catch { }
                if (cats == null || cats.Length == 0) return null;

                var allEls = ElementLoader.elements;
                if (allEls == null) return null;

                // 候选：UI 同一套
                var validSet = new System.Collections.Generic.HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
                try
                {
                    for (int cIdx = 0; cIdx < cats.Length; cIdx++)
                    {
                        Tag catTag = default(Tag);
                        try { catTag = TagManager.Create(cats[cIdx]); } catch { }
                        if (catTag == default(Tag)) continue;
                        var valid = MaterialSelector.GetValidMaterials(catTag, false);
                        if (valid == null) continue;
                        for (int vi = 0; vi < valid.Count; vi++)
                        {
                            string vn = null;
                            try { vn = valid[vi].Name; } catch { }
                            if (!string.IsNullOrEmpty(vn)) validSet.Add(vn);
                        }
                    }
                }
                catch { }

                System.Collections.Generic.Dictionary<string, double> inStore, onGround;
                ScanAvailability(out inStore, out onGround);

                var enough = new System.Collections.Generic.List<string>(32);
                var shortOnes = new System.Collections.Generic.List<string>(32);
                for (int i = 0; i < allEls.Count; i++)
                {
                    var e = allEls[i];
                    if (e == null) continue;
                    string tagName = null;
                    try { tagName = e.tag.Name; } catch { }
                    if (string.IsNullOrEmpty(tagName)) continue;
                    if (validSet.Count > 0 && !validSet.Contains(tagName)) continue;
                    if (validSet.Count == 0) continue;

                    double inS, onG;
                    inStore.TryGetValue(tagName, out inS);
                    onGround.TryGetValue(tagName, out onG);
                    double tot = inS + onG;
                    bool ok = needKg > 0 ? (tot + 0.0001 >= needKg) : (tot > 0);
                    bool discovered = true;
                    try { var dr = DiscoveredResources.Instance; if (dr != null) discovered = dr.IsDiscovered(e.tag); } catch { }

                    var sb = new StringBuilder(160);
                    sb.Append("{\"tag\":").Append(Snap.Str(tagName));
                    sb.Append(",\"available\":{\"inStorage\":").Append(Snap.D(inS))
                      .Append(",\"onGround\":").Append(Snap.D(onG))
                      .Append(",\"total\":").Append(Snap.D(tot)).Append('}');
                    sb.Append(",\"enough\":").Append(ok ? "true" : "false");
                    sb.Append(",\"discovered\":").Append(discovered ? "true" : "false").Append('}');

                    total++;
                    if (ok) { enoughCount++; enough.Add(sb.ToString()); } else shortOnes.Add(sb.ToString());
                }

                var outSb = new StringBuilder(2048);
                outSb.Append('[');
                bool first = true;
                for (int i = 0; i < enough.Count; i++) { if (!first) outSb.Append(','); first = false; outSb.Append(enough[i]); }
                for (int i = 0; i < shortOnes.Count; i++) { if (!first) outSb.Append(','); first = false; outSb.Append(shortOnes[i]); }
                outSb.Append(']');
                return outSb.ToString();
            }
            catch { return null; }
        }

        static void ScanAvailability(out System.Collections.Generic.Dictionary<string, double> inStore,
                                     out System.Collections.Generic.Dictionary<string, double> onGround)
        {
            inStore = new System.Collections.Generic.Dictionary<string, double>(System.StringComparer.OrdinalIgnoreCase);
            onGround = new System.Collections.Generic.Dictionary<string, double>(System.StringComparer.OrdinalIgnoreCase);
            try
            {
                var builds = Components.BuildingCompletes != null ? Components.BuildingCompletes.Items : null;
                if (builds != null)
                {
                    for (int bi = 0; bi < builds.Count; bi++)
                    {
                        var bb = builds[bi];
                        if (bb == null) continue;
                        Storage[] cs = null;
                        try { cs = bb.GetComponentsInChildren<Storage>(); } catch { }
                        if (cs == null) continue;
                        for (int ci = 0; ci < cs.Length; ci++)
                        {
                            var st = cs[ci] as Storage;
                            if (st == null) continue;
                            System.Collections.Generic.HashSet<Tag> ids = null;
                            try { ids = st.GetAllIDsInStorage(); } catch { }
                            if (ids == null) continue;
                            foreach (Tag idTag in ids)
                            {
                                string nm = null;
                                try { nm = idTag.Name; } catch { }
                                if (string.IsNullOrEmpty(nm)) continue;
                                float amt = 0f;
                                try { amt = st.GetAmountAvailable(idTag); } catch { }
                                if (amt <= 0f) continue;
                                double cur;
                                inStore.TryGetValue(nm, out cur);
                                inStore[nm] = cur + amt;
                            }
                        }
                    }
                }
            }
            catch { }
            try
            {
                var picks = Components.Pickupables != null ? Components.Pickupables.Items : null;
                if (picks != null)
                {
                    for (int pi = 0; pi < picks.Count; pi++)
                    {
                        var pp = picks[pi];
                        if (pp == null) continue;
                        bool stored = false;
                        try { stored = pp.storage != null; } catch { }
                        if (stored) continue;
                        UnityEngine.GameObject pgo = null;
                        try { pgo = pp.gameObject; } catch { }
                        if (pgo == null) continue;
                        PrimaryElement ppe = null;
                        try { ppe = pgo.GetComponent<PrimaryElement>(); } catch { }
                        if (ppe == null) continue;
                        string nm = null;
                        try { nm = ppe.ElementID.ToString(); } catch { }
                        if (string.IsNullOrEmpty(nm)) continue;
                        float m = 0f;
                        try { m = ppe.Mass; } catch { }
                        if (m <= 0f) continue;
                        double cur;
                        onGround.TryGetValue(nm, out cur);
                        onGround[nm] = cur + m;
                    }
                }
            }
            catch { }
        }
        /// <summary>`discovered` 的自解释文案（每个响应都带一份，读到的人不用翻文档）。</summary>
        internal const string DiscoveredMeaning =
            "discovered=该资源是否已进入本档的『资源发现』账本 —— 即这局游戏里**真正获得过**（被收进世界库存 / 被建筑消耗 / 世界模板发放）。" +
            "false = 本档还没获得过这种资源，**不是「没研究解锁」**；研究状态看 /v1/research。";

        public static string Query(string query, out int status, out string code, out string message)
        {
            status = 200; code = null; message = null;

            string prefabId = QueryValue(query, "prefabId");
            if (string.IsNullOrEmpty(prefabId)) prefabId = QueryValue(query, "prefab");
            if (string.IsNullOrEmpty(prefabId))
            {
                status = 400; code = "BAD_REQUEST"; message = "需要 prefabId（如 ?prefabId=Electrolyzer）";
                return null;
            }

            string data = null;
            string fail = null;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            int okT; bool busy;

            bool ran = MainThread.InvokeAdaptive(() =>
            {
                try
                {
                    BuildingDef def = null;
                    try { def = Assets.GetBuildingDef(prefabId); } catch { }
                    if (def == null) { fail = "找不到建筑 prefabId=" + prefabId; return; }

                    // ---- 需求量：与 build 的 INSUFFICIENT_MATERIAL 同口径（def.Mass[0]）----
                    double needKg = -1;
                    try { if (def.Mass != null && def.Mass.Length > 0) needKg = def.Mass[0]; } catch { }

                    // ---- 这套建筑允许的材料类别 ----
                    string[] cats = null;
                    try { cats = def.MaterialCategory; } catch { }
                    if (cats == null || cats.Length == 0)
                    {
                        fail = "该建筑没有 MaterialCategory（不可能靠材料选，prefabId=" + prefabId + "）";
                        return;
                    }

                    // ---- 一次扫描：把所有储物箱里每种 tag 的数量收集起来 ----
                    var inStore = new System.Collections.Generic.Dictionary<string, double>(System.StringComparer.OrdinalIgnoreCase);
                    try
                    {
                        var builds = Components.BuildingCompletes != null ? Components.BuildingCompletes.Items : null;
                        if (builds != null)
                        {
                            for (int bi = 0; bi < builds.Count; bi++)
                            {
                                var bb = builds[bi];
                                if (bb == null) continue;
                                Storage[] cs = null;
                                try { cs = bb.GetComponentsInChildren<Storage>(); } catch { }
                                if (cs == null) continue;
                                for (int ci = 0; ci < cs.Length; ci++)
                                {
                                    var st = cs[ci] as Storage;
                                    if (st == null) continue;
                                    System.Collections.Generic.HashSet<Tag> ids = null;
                                    try { ids = st.GetAllIDsInStorage(); } catch { }
                                    if (ids == null) continue;
                                    foreach (Tag idTag in ids)
                                    {
                                        string nm = null;
                                        try { nm = idTag.Name; } catch { }
                                        if (string.IsNullOrEmpty(nm)) continue;
                                        float amt = 0f;
                                        try { amt = st.GetAmountAvailable(idTag); } catch { }
                                        if (amt <= 0f) continue;
                                        double cur;
                                        inStore.TryGetValue(nm, out cur);
                                        inStore[nm] = cur + amt;
                                    }
                                }
                            }
                        }
                    }
                    catch { }

                    // ---- 一次扫描：地上散落（小人能捡起来用）----
                    var onGround = new System.Collections.Generic.Dictionary<string, double>(System.StringComparer.OrdinalIgnoreCase);
                    try
                    {
                        var picks = Components.Pickupables != null ? Components.Pickupables.Items : null;
                        if (picks != null)
                        {
                            for (int pi = 0; pi < picks.Count; pi++)
                            {
                                var pp = picks[pi];
                                if (pp == null) continue;
                                bool inStorage = false;
                                try { inStorage = pp.storage != null; } catch { }
                                if (inStorage) continue;
                                UnityEngine.GameObject pgo = null;
                                try { pgo = pp.gameObject; } catch { }
                                if (pgo == null) continue;
                                PrimaryElement ppe = null;
                                try { ppe = pgo.GetComponent<PrimaryElement>(); } catch { }
                                if (ppe == null) continue;
                                string nm = null;
                                try { nm = ppe.ElementID.ToString(); } catch { }
                                if (string.IsNullOrEmpty(nm)) continue;
                                float m = 0f;
                                try { m = ppe.Mass; } catch { }
                                if (m <= 0f) continue;
                                double cur;
                                onGround.TryGetValue(nm, out cur);
                                onGround[nm] = cur + m;
                            }
                        }
                    }
                    catch { }

                    // ---- 候选集：优先用**游戏 UI 同一个方法** MaterialSelector.GetValidMaterials ----
                    // （它内部就是"按 def.MaterialCategory 从 ElementLoader 里筛"，但我们不自己复刻，
                    //   直接调它 ⇒ 返回的候选与玩家在建造菜单里看到的完全一致）
                    var validSet = new System.Collections.Generic.HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
                    string candidateSource = "def.MaterialCategory × ElementLoader（自筛）";
                    try
                    {
                        for (int cIdx = 0; cIdx < cats.Length; cIdx++)
                        {
                            Tag catTag = default(Tag);
                            try { catTag = TagManager.Create(cats[cIdx]); } catch { }
                            if (catTag == default(Tag)) continue;
                            var valid = MaterialSelector.GetValidMaterials(catTag, false);
                            if (valid == null) continue;
                            for (int vi = 0; vi < valid.Count; vi++)
                            {
                                string vn = null;
                                try { vn = valid[vi].Name; } catch { }
                                if (!string.IsNullOrEmpty(vn)) validSet.Add(vn);
                            }
                            if (valid.Count > 0) candidateSource = "MaterialSelector.GetValidMaterials（与游戏 UI 同一套）";
                        }
                    }
                    catch { }

                    // ---- 穷举：遍历 ElementLoader，取所有合法候选（不跨类别）----
                    var allEls = ElementLoader.elements;
                    if (allEls == null) { fail = "ElementLoader.elements 为 null"; return; }

                    var rows = new System.Collections.Generic.List<string>(64);
                    var enoughRows = new System.Collections.Generic.List<string>(64);
                    int total = 0, enoughCount = 0, discoveredCount = 0;

                    for (int i = 0; i < allEls.Count; i++)
                    {
                        var e = allEls[i];
                        if (e == null) continue;
                        string cat = null;
                        try { cat = e.materialCategory.Name; } catch { }
                        if (string.IsNullOrEmpty(cat)) continue;

                        string tagName = null;
                        try { tagName = e.tag.Name; } catch { }
                        if (string.IsNullOrEmpty(tagName)) continue;

                        bool hitCat;
                        if (validSet.Count > 0)
                        {
                            hitCat = validSet.Contains(tagName);           // UI 权威清单
                        }
                        else
                        {
                            hitCat = false;
                            for (int c = 0; c < cats.Length; c++)
                                if (string.Equals(cats[c], cat, System.StringComparison.OrdinalIgnoreCase)) { hitCat = true; break; }
                        }
                        if (!hitCat) continue;

                        // 解锁状态：未发现的资源列出来但标 false（"存在、但这档还没发现"本身就是决策信息）
                        bool discovered = true;
                        try
                        {
                            var dr = DiscoveredResources.Instance;
                            if (dr != null) discovered = dr.IsDiscovered(e.tag);
                        }
                        catch { }

                        double inS, onG;
                        inStore.TryGetValue(tagName, out inS);
                        onGround.TryGetValue(tagName, out onG);
                        double tot = inS + onG;
                        bool enough = needKg > 0 ? (tot + 0.0001 >= needKg) : (tot > 0);

                        var sb = new StringBuilder(512);
                        sb.Append("{\"tag\":").Append(Snap.Str(tagName));
                        sb.Append(",\"available\":{\"inStorage\":").Append(Snap.D(inS))
                          .Append(",\"onGround\":").Append(Snap.D(onG))
                          .Append(",\"total\":").Append(Snap.D(tot)).Append('}');
                        sb.Append(",\"need\":").Append(needKg >= 0 ? Snap.D(needKg) : "null");
                        sb.Append(",\"enough\":").Append(enough ? "true" : "false");
                        sb.Append(",\"discovered\":").Append(discovered ? "true" : "false");
                        sb.Append(",\"props\":");
                        Elements.AppendFull(sb, e);   // 复用 /v1/elements 的字段口径（含 attributeModifiers）
                        sb.Append('}');

                        total++;
                        if (discovered) discoveredCount++;
                        if (enough) { enoughCount++; enoughRows.Add(sb.ToString()); }
                        else rows.Add(sb.ToString());
                    }

                    if (total == 0)
                    {
                        fail = "类别 [" + string.Join(",", cats) + "] 下没有任何元素（异常）";
                        return;
                    }

                    var outSb = new StringBuilder(8192);
                    outSb.Append("{\"prefabId\":").Append(Snap.Str(prefabId));
                    outSb.Append(",\"name\":").Append(Snap.Str(SafeDefName(def)));
                    outSb.Append(",\"needKg\":").Append(needKg >= 0 ? Snap.D(needKg) : "null");
                    outSb.Append(",\"materialCategories\":[");
                    { for (int c = 0; c < cats.Length; c++) { if (c > 0) outSb.Append(','); outSb.Append(Snap.Str(cats[c])); } }
                    outSb.Append("]");
                    outSb.Append(",\"counts\":{\"total\":").Append(total).Append(",\"enough\":").Append(enoughCount)
                         .Append(",\"short\":").Append(total - enoughCount).Append(",\"discovered\":").Append(discoveredCount).Append('}');
                    outSb.Append(",\"note\":").Append(Snap.Str("类别内穷举：该建筑合法材料类别下的全部候选都列（含本档 0kg、含未解锁的）；只给数据不给推荐"));
                    outSb.Append(",\"materials\":[");
                    bool first = true;
                    for (int i = 0; i < enoughRows.Count; i++) { if (!first) outSb.Append(','); first = false; outSb.Append(enoughRows[i]); }
                    for (int i = 0; i < rows.Count; i++) { if (!first) outSb.Append(','); first = false; outSb.Append(rows[i]); }
                    outSb.Append(']');
                    outSb.Append(",\"discoveredMeaning\":").Append(Snap.Str(DiscoveredMeaning))
                   .Append(",\"candidateSource\":").Append(Snap.Str(candidateSource));
                    outSb.Append('}');
                    data = outSb.ToString();
                }
                catch (System.Exception ex) { fail = ex.GetType().Name + ": " + ex.Message; }
            }, 3000, 20000, out okT, out busy);
            sw.Stop();

            if (!ran) { status = 503; code = "RETRY_LATER"; message = "main thread busy"; return null; }
            if (fail != null) { status = 404; code = "PREFAB_NOT_FOUND"; message = fail; return null; }
            if (data == null) { status = 500; code = "INTERNAL"; message = "build-materials 采集返回空"; return null; }

            JsonCheck.Validate(data, "build-materials");
            Log.Info("[build-materials] prefabId=" + prefabId + " bytes=" + data.Length + " ms=" + sw.ElapsedMilliseconds);
            return data;
        }

        static string SafeDefName(BuildingDef def)
        {
            try { return def.Name; } catch { return null; }
        }

        static string QueryValue(string query, string key)
        {
            if (string.IsNullOrEmpty(query)) return null;
            foreach (var kv in query.Split('&'))
            {
                int eq = kv.IndexOf('=');
                if (eq <= 0) continue;
                if (kv.Substring(0, eq) == key) return kv.Substring(eq + 1);
            }
            return null;
        }
    }
}
