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

        /// <summary>枚举当前装载界面上的殖民地按钮（没打开就返回空表）。</summary>
        internal static List<Entry> List()
        {
            var list = new List<Entry>();
            try
            {
                UnityEngine.GameObject root = null;
                try { root = UnityEngine.GameObject.Find("LoadScreen"); } catch { }
                if (root == null) { try { root = UnityEngine.GameObject.Find("MainMenu")?.transform?.Find("LoadScreen")?.gameObject; } catch { } }
                if (root == null) return list;

                var trs = root.GetComponentsInChildren<UnityEngine.Transform>(true);
                for (int i = 0; i < trs.Length; i++)
                {
                    var tr = trs[i];
                    if (tr == null) continue;
                    string nm = null;
                    try { nm = tr.name; } catch { }
                    if (string.IsNullOrEmpty(nm) || !nm.StartsWith("Prefab_ColonyButton", System.StringComparison.Ordinal)) continue;

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
                    list.Add(new Entry { Index = idx, Text = sb.ToString() });
                }
            }
            catch { }
            return list;
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
