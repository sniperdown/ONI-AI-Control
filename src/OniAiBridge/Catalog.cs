using System.Collections.Generic;
using System.Reflection;
using System.Text;

// ⚠️ 不要 using System 之后直接用 Action/DateTime：Assembly-CSharp 里的全局 Action 枚举会遮蔽 System.Action。
namespace OniAiBridge
{
    /// <summary>
    /// M5.2：只读建筑目录 `GET /v1/catalog`。
    ///
    /// 目的：把离线阶段两个"未确认"（建筑尺寸、建筑分类）在运行时确认掉。
    /// * 尺寸：`BuildingDef.WidthInCells / HeightInCells`（public 字段，基类 `Def` 提供 `PrefabID`/`Tag`）
    /// * 分类：`PlanScreen.tagCategoryMap : Dictionary&lt;Tag, HashedString&gt;`（**nonpub ⇒ 反射**），
    ///   键是 `BuildingDef.Tag`（IL 实证：`PlanScreen` 里 `tagCategoryMap[def.Tag]`）
    /// * 顶层分类：`BuildMenuCategoriesScreen.categorizedCategoryMap`（**nonpub**，顶层→子分类列表），
    ///   取到一个实例才能反查；取不到就 `topCategory: null` + `topCategoryUnavailable` 说明原因
    ///
    /// 三条硬要求：只读（不写任何游戏状态）、不落盘（内存缓存一次）、反射失败降级不抛 500。
    /// </summary>
    internal static class Catalog
    {
        static string _cachedJson;

        /// <summary>
        /// 6 个顶层建造页签（来源：游戏 `strings_preinstalled_zh_klei.po` 里值带
        /// `&lt;link="BUILDCATEGORY..."&gt;` 的键）。它是**游戏静态数据**，编进 dll 才能在
        /// "顶层归属反射失败"时依然能把顶层条目报出来（用户要求 76 条 ≠ 只报拿得到的）。
        /// </summary>
        static readonly string[] TopKeys = new[] { "BASE", "FOODANDAGRICULTURE", "HEALTHANDHAPPINESS", "INDUSTRIAL", "INFRASTRUCTURE", "LOGISTICS" };

        /// <summary>70 个子分类键（同上来源）。与运行时 `tagCategoryMap` 的值取并集，避免游戏加分类后我们漏报。</summary>
        static readonly string[] KnownSubKeys = "ADVANCED,ARCHAEOLOGY,AUTOMATED,BATTERIES,BEDS,BUILDMENUPORTS,CARGO,CONDUITSENSORS,CONVEYANCE,CONVEYANCESTRUCTURES,COOKING,DECOR,DINING,DISTRIBUTORS,DOORS,ELECTROBANKBUILDINGS,ENGINES,EQUIPMENT,FARMING,FITTINGS,FURNITURE,GENERATORS,HYGIENE,INDUSTRIALSTATION,LADDERS,LIGHTS,LOGICAUDIO,LOGICGATES,LOGICMANAGER,MANUFACTURING,MATERIALS,MEDICAL,METEORDEFENSE,MISSILES,MODULE,OIL,OPERATIONS,ORGANIC,OXYGEN,PIPES,PLUMBINGSTRUCTURES,PORTS,POWERCONTROL,PRINTINGPODS,PRODUCERS,PUMPS,RANCHING,RECREATION,REFINING,RESEARCH,ROCKETNAV,ROCKETRY,ROCKETSTRUCTURES,SANITATION,SCRUBBERS,SENSORS,STORAGE,SWITCHES,TANKS,TELESCOPES,TEMPERATURE,TILES,TRANSMISSIONS,TRANSPORT,UTILITIES,VALVES,VENTILATIONSTRUCTURES,WASHROOM,WELLNESS,WIRES".Split(',');

        public static string Build()
        {
            if (_cachedJson != null) return _cachedJson;

            var sb = new StringBuilder(262144);
            var notes = new StringBuilder();

            // ---- 1) 分类映射（nonpub 反射）----
            Dictionary<Tag, HashedString> tagCategory = null;
            string catErr = null;
            try
            {
                PlanScreen ps = PlanScreen.Instance;
                if (ps == null) catErr = "PlanScreen.Instance 为 null（不在游戏内？）";
                else
                {
                    FieldInfo fi = typeof(PlanScreen).GetField("tagCategoryMap",
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (fi == null) catErr = "PlanScreen.tagCategoryMap 字段不存在（游戏更新？）";
                    else tagCategory = fi.GetValue(ps) as Dictionary<Tag, HashedString>;
                    if (tagCategory == null && catErr == null) catErr = "tagCategoryMap 取值为 null";
                }
            }
            catch (System.Exception e) { catErr = e.GetType().Name + ": " + e.Message; }

            // ---- 2) 顶层分类映射（nonpub 反射，需要屏幕实例）----
            Dictionary<string, string> subToTop = null;
            string topErr = null;
            try
            {
                var bmcs = UnityEngine.Object.FindObjectOfType<BuildMenuCategoriesScreen>();
                if (bmcs == null) topErr = "找不到 BuildMenuCategoriesScreen 实例（建造菜单未打开过）";
                else
                {
                    FieldInfo fi = typeof(BuildMenuCategoriesScreen).GetField("categorizedCategoryMap",
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (fi == null) topErr = "categorizedCategoryMap 字段不存在";
                    else
                    {
                        var map = fi.GetValue(bmcs) as Dictionary<HashedString, List<HashedString>>;
                        if (map == null) topErr = "categorizedCategoryMap 为 null";
                        else
                        {
                            subToTop = new Dictionary<string, string>();
                            foreach (var kv in map)
                            {
                                string top = kv.Key.ToString();
                                if (kv.Value == null) continue;
                                foreach (var sub in kv.Value)
                                {
                                    string sk = sub.ToString();
                                    if (!subToTop.ContainsKey(sk)) subToTop[sk] = top;
                                }
                            }
                        }
                    }
                }
            }
            catch (System.Exception e) { topErr = e.GetType().Name + ": " + e.Message; }

            // ---- 3) 建筑列表 ----
            List<BuildingDef> defs = Assets.BuildingDefs;
            int count = defs == null ? 0 : defs.Count;
            sb.Append("{\"buildings\":[");
            int emittedBuildings = 0;      // 真正写进 JSON 的条数（响应里的 count 必须与它一致）
            for (int i = 0; i < count; i++)
            {
                BuildingDef d = defs[i];
                if (d == null) continue;
                if (emittedBuildings > 0) sb.Append(',');
                sb.Append('{');            // ★ 每个对象的开括号（之前漏了 ⇒ 整个 JSON 非法）
                emittedBuildings++;
                if (emittedBuildings <= 3)
                {
                    Log.Info("[catalog] 前" + emittedBuildings + "条: "
                             + (d.PrefabID ?? "<null>") + " | " + (d.Name ?? "<null>")
                             + " | " + d.WidthInCells + "x" + d.HeightInCells);
                }
                sb.Append("\"prefabId\":").Append(Snap.Str(d.PrefabID));
                sb.Append(",\"name\":").Append(Snap.Str(PlainText(d.Name)));
                sb.Append(",\"width\":").Append(d.WidthInCells);
                sb.Append(",\"height\":").Append(d.HeightInCells);
                sb.Append(",\"objectLayer\":").Append(Snap.Str(d.ObjectLayer.ToString()));

                string cat = null;
                if (tagCategory != null)
                {
                    try
                    {
                        HashedString hs;
                        if (tagCategory.TryGetValue(d.Tag, out hs) && hs.IsValid) cat = hs.ToString();
                    }
                    catch { cat = null; }
                }
                sb.Append(",\"categories\":");
                if (cat == null) sb.Append("[]");
                else sb.Append('[').Append(Snap.Str(cat)).Append(']');

                string top = null;
                if (cat != null && subToTop != null) subToTop.TryGetValue(cat, out top);
                sb.Append(",\"topCategory\":").Append(Snap.Str(top));
                sb.Append('}');
            }
            sb.Append("],\"count\":").Append(emittedBuildings);   // 与日志里的 emittedBuildings 一致
            sb.Append(",\"defsCount\":").Append(count);          // Assets.BuildingDefs 总数（含 null 项）

            // ---- 4) 分类表：**76 条完整清单**（6 顶层 isTop:true + 70 子分类 isTop:false）----
            sb.Append(",\"categories\":[");
            bool firstCat = true;
            var emitted = new HashSet<string>();

            // 4.1 六个顶层页签（即使运行时归属拿不到也要报出来）
            for (int i = 0; i < TopKeys.Length; i++)
            {
                string k = TopKeys[i];
                if (!emitted.Add(k)) continue;
                if (!firstCat) sb.Append(',');
                firstCat = false;
                sb.Append("{\"key\":").Append(Snap.Str(k));
                sb.Append(",\"name\":").Append(Snap.Str(PlainText(LocalizedName(k))));
                sb.Append(",\"isTop\":true,\"top\":").Append(Snap.Str(k)).Append('}');
            }

            // 4.2 子分类：编译进的 70 个 + 运行时 tagCategoryMap 里新出现的（并集，不漏报）
            var subKeys = new List<string>();
            for (int i = 0; i < KnownSubKeys.Length; i++)
            {
                string k = KnownSubKeys[i].Trim();
                if (k.Length > 0 && !emitted.Contains(k)) { subKeys.Add(k); emitted.Add(k); }
            }
            if (tagCategory != null)
            {
                foreach (var kv in tagCategory)
                {
                    string k = kv.Value.ToString();
                    if (!string.IsNullOrEmpty(k) && !emitted.Contains(k)) { subKeys.Add(k); emitted.Add(k); }
                }
            }
            for (int i = 0; i < subKeys.Count; i++)
            {
                string k = subKeys[i];
                string top = null;
                if (subToTop != null) subToTop.TryGetValue(k, out top);
                if (!firstCat) sb.Append(',');
                firstCat = false;
                sb.Append("{\"key\":").Append(Snap.Str(k));
                sb.Append(",\"name\":").Append(Snap.Str(PlainText(LocalizedName(k))));
                sb.Append(",\"isTop\":false,\"top\":").Append(Snap.Str(top)).Append('}');
            }
            sb.Append(']');
            if (catErr != null) sb.Append(",\"categoriesUnavailable\":").Append(Snap.Str(catErr));
            if (topErr != null) sb.Append(",\"topCategoryUnavailable\":").Append(Snap.Str(topErr));
            if (notes.Length > 0) sb.Append(",\"notes\":").Append(Snap.Str(notes.ToString()));
            sb.Append('}');

            string __json = sb.ToString();
            JsonCheck.Validate(__json, "catalog");   // 手写 JSON ⇒ 出门前必须能解析
            _cachedJson = __json;
            // 判据（第 6 次同型）：日志里的数字必须与响应里的数字同口径
            Log.Info("[catalog] 已生成建筑目录：buildings=" + emittedBuildings + "/" + count + "（写出/总数）"
                     + " bytes=" + __json.Length
                     + " categoriesErr=" + (catErr ?? "<none>") + " topErr=" + (topErr ?? "<none>"));
            if (emittedBuildings != count) Log.Warn("[catalog] 条数不一致：写出 " + emittedBuildings + " 条，defs 总数 " + count);

            // 降级原因必须与响应同进同退：有原因 ⇒ 字段在且非空；无原因 ⇒ 字段不出现
            CheckDegradeField(__json, "categoriesUnavailable", catErr);
            CheckDegradeField(__json, "topCategoryUnavailable", topErr);
            return _cachedJson;
        }

        static void AppendCategory(StringBuilder sb, string key, Dictionary<string, string> subToTop)
        {
            string top = null;
            if (subToTop != null) subToTop.TryGetValue(key, out top);
            sb.Append("{\"key\":").Append(Snap.Str(key));
            sb.Append(",\"name\":").Append(Snap.Str(PlainText(LocalizedName(key))));
            sb.Append(",\"isTop\":").Append(top == null ? "false" : "false");   // 子分类列表里的 key 本身都不是顶层页签
            sb.Append(",\"top\":").Append(Snap.Str(top));
            sb.Append('}');
        }

        /// <summary>
        /// 剥掉 Unity 富文本标记（`&lt;link="X"&gt;`、`&lt;/link&gt;`、`&lt;i&gt;`、`&lt;b&gt;` 等所有 `&lt;...&gt;`）。
        /// **API 返回的是数据，不是要渲染的富文本** —— 使用方想渲染，自己加。
        /// 起因：`"name": "&lt;link=\"ADVANCEDAPOTHECARY\"&gt;核能配药桌&lt;/link&gt;"`，人类会以为数据坏了。
        /// </summary>
        internal static string PlainText(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            if (s.IndexOf('<') < 0) return s;
            var sb = new StringBuilder(s.Length);
            bool inTag = false;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '<') { inTag = true; continue; }
                if (c == '>') { inTag = false; continue; }
                if (!inTag) sb.Append(c);
            }
            return sb.ToString().Trim();
        }

        /// <summary>校验降级字段"同进同退"：有原因 ⇒ 字段存在；没原因 ⇒ 字段不出现。</summary>
        static void CheckDegradeField(string json, string field, string reason)
        {
            bool present = json.IndexOf("\"" + field + "\"", System.StringComparison.Ordinal) >= 0;
            bool should = reason != null;
            if (present != should)
            {
                Log.Warn("[catalog] 降级字段不一致：" + field + " should=" + should + " present=" + present
                         + " reason=" + (reason ?? "<null>"));
            }
        }
        /// <summary>取本地化分类名：`STRINGS.UI.NEWBUILDCATEGORIES.&lt;KEY&gt;.NAME`（取不到返回 key 本身）。</summary>
        static string LocalizedName(string key)
        {
            try
            {
                string full = "STRINGS.UI.NEWBUILDCATEGORIES." + key + ".NAME";
                StringEntry se = Strings.Get(full);      // 取不到时可能返回 null / 抛异常，都兜住
                if (se != null) return se.String;
            }
            catch { }
            return key;
        }
    }
}
