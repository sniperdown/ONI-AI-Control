using System.Collections.Generic;
using System.Text;

namespace OniAiBridge
{
    /// <summary>
    /// 存档列表 ↔ UI 按钮 的**字符串桥**（0.102.0）。
    ///
    /// 起因：装载「首都」时靠截图认名字，把「首都」(cycle 4) 认成「首部」(cycle 34)。
    /// 新规矩：**名字只认字符串**（见 docs/SAVE-LOAD-RULES.md）。
    ///
    /// 做法：在 `LoadScreen` 下枚举 `Prefab_ColonyButton#i`，把每个按钮下的文本读出来，
    /// 与 `.sav` 文件路径里的殖民地目录名做**字符串比对**：
    ///   * 对上 ⇒ `uiIndex` 给出可点的 `#i`、`uiName` 给出按钮文本
    ///   * 对不上 ⇒ `uiIndex=null` + `uiMismatch:true`（上层必须停下，不许猜）
    ///
    /// 注意：装载界面没打开时枚举不到任何按钮，此时 `uiIndex` 一律 null 并注明原因。
    /// </summary>
    internal static class SaveUi
    {
        internal sealed class Entry
        {
            public int Index;
            public string Text;      // 按钮下的全部文本（拼起来，含档名/周期等）
        }

        /// <summary>从 `.sav` 相对路径取殖民地目录名：`<steamid>/<colony>/...` ⇒ colony。</summary>
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

        /// <summary>
        /// 枚举装载界面上的殖民地按钮（没打开就返回空表）。
        ///
        /// **0.104.0 修**：以前用 `GameObject.Find("LoadScreen")`，永远返回 null ——
        /// KScreen 实例名带 `(Clone)`（`LoadScreen(Clone)`），精确名找不到根，所以按钮数为 0。
        /// 现在**不依赖根对象**：直接 `Resources.FindObjectsOfTypeAll<Transform>()` 扫全部层级，
        /// 名字以 `Prefab_ColonyButton` 开头的就是目标按钮（这是实测存在的命名）。
        /// </summary>
        static List<Entry> ListOnMainThread()
        {
            var list = new List<Entry>();
            try
            {
                var all = UnityEngine.Resources.FindObjectsOfTypeAll<UnityEngine.Transform>();
                if (all == null) return list;
                for (int i = 0; i < all.Length; i++)
                {
                    var tr = all[i];
                    if (tr == null) continue;
                    string nm = null;
                    try { nm = tr.name; } catch { }
                    if (string.IsNullOrEmpty(nm) || !nm.StartsWith("Prefab_ColonyButton", System.StringComparison.Ordinal)) continue;

                    // 只认装载界面那批（排除场景里别的同名对象）：父链里有 SaveTemplate/ColonyList 之类
                    int idx = -1;
                    int hash = nm.LastIndexOf('#');
                    if (hash >= 0)
                    {
                        int j = hash + 1, e = j;
                        while (e < nm.Length && char.IsDigit(nm[e])) e++;
                        if (e > j) int.TryParse(nm.Substring(j, e - j), out idx);
                    }
                    if (idx < 0) continue;

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

                    // 去重（Resources 可能同一对象出现多次）
                    bool dup = false;
                    for (int k = 0; k < list.Count; k++) if (list[k].Index == idx) { dup = true; break; }
                    if (dup) continue;
                    list.Add(new Entry { Index = idx, Text = sb.ToString() });
                }
                list.Sort((a, b) => a.Index.CompareTo(b.Index));
            }
            catch { }
            return list;
        }
        static List<Entry> _cache;

        /// <summary>
        /// 在主线程枚举一次并缓存（Unity API 必须在主线程；在工作线程上调用**不报错、直接返回空**）。
        /// 返回 false 表示主线程泵没响应（例如主菜单态 Game.Update 不 tick）—— 调用方要如实报，别当成"没有按钮"。
        /// </summary>
        internal static bool Refresh(out string why)
        {
            why = null;
            string local = null;
            int okR; bool busyR;
            bool ran = MainThread.InvokeAdaptive(() =>
            {
                try { _cache = ListOnMainThread(); }
                catch (System.Exception e) { local = e.GetType().Name; }
            }, 3000, 8000, out okR, out busyR);
            if (!ran) { why = "主线程泵未响应（主菜单/装载界面可能没有 Game.Update tick）"; return false; }
            why = local;
            return true;
        }

        /// <summary>读缓存（没刷新过就是空表）。</summary>
        internal static List<Entry> Cached()
        {
            return _cache ?? new List<Entry>();
        }
        /// <summary>给某个殖民地找按钮：文本里**精确包含**殖民地名的第一个匹配；找不到返回 null。</summary>
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
    }
}
