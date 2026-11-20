using Klei.AI;
using System.Text;

namespace OniAiBridge
{
    /// <summary>
    /// `GET /v1/elements` —— **材料/元素性质**（只读、纯数据、不做任何推荐）。
    ///
    /// 数据来源：**运行时遍历 `ElementLoader.elements`（`List&lt;Element&gt;`，元数据实测）**，
    /// 不在代码里硬编码任何元素表，也不写死 wiki 数字 —— 返回的永远是**当前这局游戏**的真值。
    ///
    /// 用法：
    /// * `GET /v1/elements?tag=Cuprite` —— 单条全字段
    /// * `GET /v1/elements?tags=Cuprite,SandStone` —— 批量（建东西前对比材料用）
    /// * `GET /v1/elements` —— 轻量列表（tag/name/state/materialCategory/比热/导热），用于发现有哪些元素
    ///
    /// 单位口径：温度同时给 **K 与 °C**（`lowTempK`/`lowTempC`），比热 `DTU/(g·°C)`、导热 `DTU/(m·s·°C)`。
    /// `attributeModifiers` 也暴露（它是"事实"：这个材料给什么加成），但**不做"该选哪个"的判断**。
    /// </summary>
    internal static class Elements
    {
        const float KC = 273.15f;

        /// <summary>去掉 Klei 富文本标记：`<link="CUPRITE">铜矿</link>` → `铜矿`。</summary>
        static string StripLink(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            try
            {
                s = System.Text.RegularExpressions.Regex.Replace(s, "<link=\"[^\"]*\">", "");
                s = s.Replace("</link>", "");
                return s;
            }
            catch { return s; }
        }
        static string NameOfTag(Tag t)
        {
            try { return t.Name; } catch { return null; }
        }

        static string HashName(SimHashes h)
        {
            try
            {
                var e = ElementLoader.FindElementByHash(h);
                return e != null ? NameOfTag(e.tag) : null;
            }
            catch { return null; }
        }

        static string StateName(Element e)
        {
            try { return e.state.ToString(); } catch { return null; }
        }

        /// <summary>把一个 Element 的**常用字段**写成 JSON（约 500B）。</summary>
        internal static void AppendFull(StringBuilder sb, Element e)
        {
            sb.Append("{\"tag\":").Append(Snap.Str(NameOfTag(e.tag)));
            sb.Append(",\"name\":").Append(Snap.Str(StripLink(SafeName(e))));
            sb.Append(",\"state\":").Append(Snap.Str(StateName(e)));
            sb.Append(",\"dlcId\":").Append(Snap.Str(SafeDlc(e)));
            sb.Append(",\"disabled\":").Append(SafeDisabled(e) ? "true" : "false");
            sb.Append(",\"materialCategory\":").Append(Snap.Str(SafeCategory(e)));
            sb.Append(",\"oreTags\":[");
            { var tags = SafeOreTags(e); for (int i = 0; i < tags.Length; i++) { if (i > 0) sb.Append(','); sb.Append(Snap.Str(tags[i])); } }
            sb.Append(']');

            // ---- 热学 ----
            sb.Append(",\"specificHeatCapacity\":").Append(Snap.F(e.specificHeatCapacity)).Append(",\"specificHeatCapacityUnit\":\"DTU/(g*C)\"");
            sb.Append(",\"thermalConductivity\":").Append(Snap.F(e.thermalConductivity)).Append(",\"thermalConductivityUnit\":\"DTU/(m*s*C)\"");
            sb.Append(",\"molarMass\":").Append(Snap.F(e.molarMass)).Append(",\"molarMassUnit\":\"g/mol\"");

            // ---- 相变（K + °C 双给）----
            AppendTempPair(sb, "lowTemp", e.lowTemp);
            sb.Append(",\"lowTempTransitionTarget\":").Append(Snap.Str(HashName(e.lowTempTransitionTarget)));
            AppendTempPair(sb, "highTemp", e.highTemp);
            sb.Append(",\"highTempTransitionTarget\":").Append(Snap.Str(HashName(e.highTempTransitionTarget)));

            // ---- 质量/默认物理 ----
            sb.Append(",\"maxMass\":").Append(Snap.F(e.maxMass)).Append(",\"maxMassUnit\":\"kg/cell\"");
            sb.Append(",\"strength\":").Append(Snap.F(e.strength));
            sb.Append(",\"hardness\":").Append((int)e.hardness);
            try
            {
                var dv = e.defaultValues;
                sb.Append(",\"defaultMass\":").Append(Snap.F(dv.mass)).Append(",\"defaultMassUnit\":\"kg/cell\"");
                AppendTempPair(sb, "defaultTemperature", dv.temperature);
            }
            catch { }

            // ---- 光学/辐射 ----
            sb.Append(",\"lightAbsorptionFactor\":").Append(Snap.F(e.lightAbsorptionFactor));
            sb.Append(",\"radiationAbsorptionFactor\":").Append(Snap.F(e.radiationAbsorptionFactor));
            sb.Append(",\"radiationPer1000Mass\":").Append(Snap.F(e.radiationPer1000Mass));
            sb.Append(",\"toxicity\":").Append(Snap.F(e.toxicity));

            // ---- 加成（数据，不是推荐）----
            sb.Append(",\"attributeModifiers\":[");
            try
            {
                var mods = e.attributeModifiers;
                if (mods != null)
                {
                    int mi = 0;
                    for (int i = 0; i < mods.Count; i++)
                    {
                        var m = mods[i];
                        if (m == null) continue;
                        if (mi > 0) sb.Append(',');
                        mi++;
                        sb.Append("{\"attributeId\":").Append(Snap.Str(SafeAttrId(m)));
                        sb.Append(",\"value\":").Append(Snap.F(SafeAttrValue(m)));
                        sb.Append(",\"isMultiplier\":").Append(SafeAttrMul(m) ? "true" : "false");
                        sb.Append(",\"display\":").Append(Snap.Str(AttrDisplay(m))).Append('}');
                    }
                }
            }
            catch { }
            sb.Append("]}");
        }

        static void AppendTempPair(StringBuilder sb, string baseName, float kelvin)
        {
            bool has = kelvin > 0.01f;   // 0/负值 ⇒ 该方向没有相变，给 null
            sb.Append(",\"").Append(baseName).Append("K\":").Append(has ? Snap.F(kelvin) : "null");
            sb.Append(",\"").Append(baseName).Append("C\":").Append(has ? Snap.F(kelvin - KC) : "null");
        }

        static string SafeName(Element e) { try { return e.name; } catch { return null; } }
        static string SafeDlc(Element e) { try { return e.dlcId; } catch { return null; } }
        static bool SafeDisabled(Element e) { try { return e.disabled; } catch { return false; } }
        static string SafeCategory(Element e) { try { return NameOfTag(e.materialCategory); } catch { return null; } }
        static string[] SafeOreTags(Element e)
        {
            try
            {
                var arr = e.oreTags;
                if (arr == null) return new string[0];
                var outArr = new string[arr.Length];
                for (int i = 0; i < arr.Length; i++) outArr[i] = NameOfTag(arr[i]);
                return outArr;
            }
            catch { return new string[0]; }
        }
        static string SafeAttrId(AttributeModifier m) { try { return m.AttributeId; } catch { return null; } }
        static float SafeAttrValue(AttributeModifier m) { try { return m.Value; } catch { return 0f; } }
        static bool SafeAttrMul(AttributeModifier m) { try { return m.IsMultiplier; } catch { return false; } }
        static string AttrDisplay(AttributeModifier m)
        {
            try
            {
                string id = m.AttributeId ?? "?";
                float v = m.Value;
                if (m.IsMultiplier) return id + " x" + (1f + v).ToString("0.###");
                return id + " " + (v >= 0 ? "+" : "") + v.ToString("0.###");
            }
            catch { return null; }
        }
        static string SafeAttrDesc(AttributeModifier m) { try { return m.Description; } catch { return null; } }

        /// <summary>轻量列表项（发现用）：只给 tag/name/state/category/两个热学值。</summary>
        static void AppendLight(StringBuilder sb, Element e)
        {
            sb.Append("{\"tag\":").Append(Snap.Str(NameOfTag(e.tag)));
            sb.Append(",\"name\":").Append(Snap.Str(StripLink(SafeName(e))));
            sb.Append(",\"state\":").Append(Snap.Str(StateName(e)));
            sb.Append(",\"materialCategory\":").Append(Snap.Str(SafeCategory(e)));
            sb.Append(",\"specificHeatCapacity\":").Append(Snap.F(e.specificHeatCapacity));
            sb.Append(",\"thermalConductivity\":").Append(Snap.F(e.thermalConductivity)).Append('}');
        }

        public static string Query(string query, out int status, out string code, out string message)
        {
            status = 200; code = null; message = null;

            string tag = QueryValue(query, "tag");
            string tags = QueryValue(query, "tags");
            bool all = QueryValue(query, "all") == "1";

            var wanted = new System.Collections.Generic.List<string>();
            if (!string.IsNullOrEmpty(tag)) wanted.Add(tag);
            if (!string.IsNullOrEmpty(tags))
                foreach (var s in tags.Split(','))
                    if (!string.IsNullOrWhiteSpace(s)) wanted.Add(s.Trim());
            bool listMode = wanted.Count == 0;

            string data = null;
            string fail = null;
            long ms = 0;
            var sw = System.Diagnostics.Stopwatch.StartNew();

            int okT; bool busy;
            bool ran = MainThread.InvokeAdaptive(() =>
            {
                try
                {
                    var allEls = ElementLoader.elements;
                    if (allEls == null) { fail = "ElementLoader.elements 为 null（元素表还没加载）"; return; }

                    var sb = new StringBuilder(listMode ? 16384 : 2048);
                    if (listMode)
                    {
                        sb.Append("{\"count\":").Append(allEls.Count).Append(",\"mode\":\"list\",\"elements\":[");
                        int n = 0;
                        for (int i = 0; i < allEls.Count; i++)
                        {
                            var e = allEls[i];
                            if (e == null) continue;
                            if (n > 0) sb.Append(',');
                            n++;
                            AppendLight(sb, e);
                        }
                        sb.Append("],\"counts\":{\"total\":").Append(allEls.Count).Append(",\"emitted\":").Append(n).Append('}');
                    }
                    else
                    {
                        sb.Append("{\"mode\":\"full\",\"counts\":{\"requested\":").Append(wanted.Count).Append(",\"found\":0},\"elements\":[");
                        int found = 0;
                        for (int i = 0; i < allEls.Count; i++)
                        {
                            var e = allEls[i];
                            if (e == null) continue;
                            string nm = NameOfTag(e.tag);
                            if (nm == null) continue;
                            bool hit = false;
                            for (int k = 0; k < wanted.Count; k++)
                                if (string.Equals(wanted[k], nm, System.StringComparison.OrdinalIgnoreCase)) { hit = true; break; }
                            if (!hit) continue;
                            if (found > 0) sb.Append(',');
                            found++;
                            if (all) AppendFull(sb, e); else AppendFull(sb, e);
                        }
                        sb.Append("],\"missing\":[");
                        { int mi = 0; for (int k = 0; k < wanted.Count; k++)
                            {
                                bool got = false;
                                for (int i = 0; i < allEls.Count; i++)
                                {
                                    var e = allEls[i]; if (e == null) continue;
                                    if (string.Equals(wanted[k], NameOfTag(e.tag), System.StringComparison.OrdinalIgnoreCase)) { got = true; break; }
                                }
                                if (!got) { if (mi > 0) sb.Append(','); mi++; sb.Append(Snap.Str(wanted[k])); }
                            } }
                        sb.Append(']');
                        sb.Replace("\"found\":0", "\"found\":" + found);
                    }
                    sb.Append(",\"source\":").Append(Snap.Str("ElementLoader.elements（运行时遍历，未硬编码）"));
                    sb.Append(",\"units\":{\"temperature\":\"K and C (dual)\",\"specificHeatCapacity\":\"DTU/(g*C)\",\"thermalConductivity\":\"DTU/(m*s*C)\",\"mass\":\"kg/cell\"}");
                    sb.Append('}');
                    data = sb.ToString();
                }
                catch (System.Exception e) { fail = e.GetType().Name + ": " + e.Message; }
            }, 3000, 15000, out okT, out busy);
            sw.Stop(); ms = sw.ElapsedMilliseconds;

            if (!ran) { status = 503; code = "RETRY_LATER"; message = "main thread busy"; return null; }
            if (fail != null) { status = 500; code = "ELEMENTS_UNAVAILABLE"; message = fail; return null; }
            if (data == null) { status = 500; code = "INTERNAL"; message = "elements 采集返回空"; return null; }

            JsonCheck.Validate(data, "elements");
            Log.Info("[elements] mode=" + (listMode ? "list" : "full") + " bytes=" + data.Length + " ms=" + ms);
            return data;
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
