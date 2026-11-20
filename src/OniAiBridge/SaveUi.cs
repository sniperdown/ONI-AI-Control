using System.Collections.Generic;
using System.Text;

namespace OniAiBridge
{
    /// <summary>
    /// 存档界面 ↔ 字符串的两层桥（0.106.0）。
    ///
    /// **两层是独立的索引空间，别混**（混过一次，`#2` 一会是盲测档一会是第七次重启）：
    ///   * 第一层 `colonyButtons[i]`：对象名**精确等于** `Prefab_ColonyButton`（父 `Content`），
    ///     索引 = 同名对象的**出现序号** —— 与 `/v1/ui/click` 里 `Prefab_ColonyButton#i` 的解析口径一致。
    ///   * 第二层 `saveRows[k]`：对象名以 `SaveTemplate` 开头（每行存档），其 `LoadButton` 才是"加载"。
    ///
    /// ⚠️ **`Name#i` 里的 `#i` 是"同名对象序号"（UI 路径语法），不是 GameObject 名字**。
    /// 之前用 `name.LastIndexOf('#')` 解析索引，结果一个按钮都匹配不到（`uiButtonCount=0`，查了四轮）。
    /// </summary>
    internal static class SaveUi
    {
        internal sealed class Entry
        {
            public int Index;
            public string Text;
        }

        internal static string ColonyFromRel(string rel)
        {
            try
            {
                if (string.IsNullOrEmpty(rel)) return null;
                var parts = rel.Replace('\\', '/').Split('/');
                if (parts.Length >= 3) return parts[1];
            }
            catch { }
            return null;
        }

        static List<Entry> _colonies;
        static List<Entry> _rows;

        /// <summary>主线程枚举一次（Unity API 必须在主线程，见 AI-GUIDE 0-00）。</summary>
        internal static bool Refresh(out string why)
        {
            why = null;
            string local = null;
            int okR; bool busyR;
            bool ran = MainThread.InvokeAdaptive(() =>
            {
                try
                {
                    var c = new List<Entry>();
                    var r = new List<Entry>();
                    var all = UnityEngine.Resources.FindObjectsOfTypeAll<UnityEngine.Transform>();
                    if (all != null)
                    {
                        for (int i = 0; i < all.Length; i++)
                        {
                            var tr = all[i];
                            if (tr == null) continue;
                            string nm = null;
                            try { nm = tr.name; } catch { }
                            if (string.IsNullOrEmpty(nm)) continue;

                            if (nm == "Prefab_ColonyButton")
                            {
                                bool dup = false;
                                for (int k = 0; k < c.Count; k++) if (c[k].Text == ReadTexts(tr)) { dup = true; break; }
                                if (!dup) c.Add(new Entry { Index = c.Count, Text = ReadTexts(tr) });
                            }
                            else if (nm.StartsWith("SaveTemplate", System.StringComparison.Ordinal))
                            {
                                bool dup = false;
                                string tx = ReadTexts(tr);
                                for (int k = 0; k < r.Count; k++) if (r[k].Text == tx) { dup = true; break; }
                                if (!dup) r.Add(new Entry { Index = r.Count, Text = tx });
                            }
                        }
                    }
                    _colonies = c;
                    _rows = r;
                }
                catch (System.Exception e) { local = e.GetType().Name + ": " + e.Message; }
            }, 3000, 10000, out okR, out busyR);
            if (!ran) { why = "主线程泵未响应（主菜单态可能没有 Game.Update tick）"; return false; }
            why = local;
            return true;
        }

        internal static List<Entry> Cached() { return _colonies ?? new List<Entry>(); }   // 兼容旧调用
        internal static List<Entry> ColonyButtons() { return _colonies ?? new List<Entry>(); }
        internal static List<Entry> SaveRows() { return _rows ?? new List<Entry>(); }

        /// <summary>给某个殖民地找按钮：文本里含殖民地名的第一个。</summary>
        internal static Entry FindByColony(List<Entry> entries, string colony)
        {
            if (entries == null || string.IsNullOrEmpty(colony)) return null;
            for (int i = 0; i < entries.Count; i++)
            {
                var e = entries[i];
                if (e.Text != null && e.Text.IndexOf(colony, System.StringComparison.Ordinal) >= 0) return e;
            }
            return null;
        }

        static string ReadTexts(UnityEngine.Transform tr)
        {
            var sb = new StringBuilder();
            try
            {
                var texts = tr.GetComponentsInChildren<LocText>(true);
                for (int k = 0; k < texts.Length; k++)
                {
                    var lk = texts[k];
                    if (lk == null) continue;
                    string s = null;
                    try { s = lk.text; } catch { }
                    if (string.IsNullOrEmpty(s)) continue;
                    if (sb.Length > 0) sb.Append(" | ");
                    sb.Append(s);
                }
            }
            catch { }
            return sb.ToString();
        }
    }
}