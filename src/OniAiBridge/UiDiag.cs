using System.Collections.Generic;
using System.Text;

namespace OniAiBridge
{
    /// <summary>
    /// `GET /v1/ui/diag` —— 一轮判掉两件事：
    ///   A. 装载界面**是否真的开着**（按类/前缀扫 screen）
    ///   B. 按钮对象**到底叫什么**（列出名字含关键字的所有 Transform）
    ///
    /// 为什么需要：`SaveUi.List()` 用 `Prefab_ColonyButton*` 前缀扫到 0 个，
    /// 而 `uiRefreshOk=true` 证明主线程没问题 ⇒ 只剩"界面没开"或"名字不对"两种可能。
    /// 全部在主线程执行并缓存，避免工作线程调 Unity API 返回空（AI-GUIDE 0-00）。
    /// </summary>
    internal static class UiDiag
    {
        static readonly string[] Keys = { "Colony", "Button", "Load", "Save", "Slot", "Entry" };

        public static string Run(out int status, out string code, out string message)
        {
            status = 200; code = null; message = null;
            string data = null; string fatal = null;
            int okR; bool busyR;
            bool ran = MainThread.InvokeAdaptive(() =>
            {
                try
                {
                    var all = UnityEngine.Resources.FindObjectsOfTypeAll<UnityEngine.Transform>();
                    int total = all != null ? all.Length : 0;

                    var hit = new List<string>();
                    var seen = new HashSet<string>();
                    if (all != null)
                    {
                        for (int i = 0; i < all.Length && hit.Count < 40; i++)
                        {
                            var tr = all[i];
                            if (tr == null) continue;
                            string nm = null;
                            try { nm = tr.name; } catch { }
                            if (string.IsNullOrEmpty(nm)) continue;
                            bool match = false;
                            for (int k = 0; k < Keys.Length; k++)
                                if (nm.IndexOf(Keys[k], System.StringComparison.OrdinalIgnoreCase) >= 0) { match = true; break; }
                            if (!match) continue;
                            string key = nm + "@" + (tr.parent != null ? tr.parent.name : "<root>");
                            if (seen.Contains(key)) continue;
                            seen.Add(key); hit.Add(key);
                        }
                    }

                    // A：按前缀/类扫 screen
                    var screens = new List<string>();
                    try
                    {
                        var mgr = GameScreenManager.Instance;
                        if (mgr != null)
                        {
                            var f = mgr.GetType().GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                            for (int i = 0; i < f.Length; i++)
                            {
                                var v = f[i].GetValue(mgr);
                                if (v == null) continue;
                                var t = v.GetType();
                                if (t.Name.IndexOf("Screen", System.StringComparison.OrdinalIgnoreCase) >= 0
                                    || t.Name.IndexOf("Stack", System.StringComparison.OrdinalIgnoreCase) >= 0)
                                {
                                    string desc = t.Name;
                                    try { var c = (v as System.Collections.ICollection); if (c != null) desc += "(" + c.Count + "):"; } catch { }
                                    try
                                    {
                                        var en = (v as System.Collections.IEnumerable);
                                        if (en != null)
                                        {
                                            int n = 0;
                                            foreach (var it in en)
                                            {
                                                if (n++ >= 6) break;
                                                if (it == null) continue;
                                                desc += it.GetType().Name + ",";
                                            }
                                        }
                                    }
                                    catch { }
                                    screens.Add(desc);
                                }
                            }
                        }
                    }
                    catch { }

                    int foundLoadScreen = 0;
                    try
                    {
                        var arr = UnityEngine.Resources.FindObjectsOfTypeAll<UnityEngine.Transform>();
                        for (int i = 0; i < arr.Length; i++)
                        {
                            var tr = arr[i];
                            if (tr == null) continue;
                            string nm = tr.name;
                            if (!string.IsNullOrEmpty(nm) && nm.StartsWith("LoadScreen", System.StringComparison.Ordinal)) foundLoadScreen++;
                        }
                    }
                    catch { }

                    var sb = new StringBuilder(2048);
                    sb.Append("{\"transformTotal\":").Append(total);
                    sb.Append(",\"loadScreenNamed\":").Append(foundLoadScreen);
                    sb.Append(",\"screenFields\":[");
                    for (int i = 0; i < screens.Count; i++) { if (i > 0) sb.Append(','); sb.Append(Snap.Str(screens[i])); }
                    sb.Append("],\"nameHits\":[");
                    for (int i = 0; i < hit.Count; i++) { if (i > 0) sb.Append(','); sb.Append(Snap.Str(hit[i])); }
                    sb.Append(']');
                    sb.Append(",\"note\":").Append(Snap.Str("loadScreenNamed>0 表示界面开着；nameHits 里看真正的按钮对象叫什么"));
                    sb.Append('}');
                    data = sb.ToString();
                }
                catch (System.Exception e) { fatal = e.GetType().Name + ": " + e.Message; }
            }, 3000, 15000, out okR, out busyR);

            if (!ran) { status = 503; code = "MAIN_THREAD_BUSY"; message = "主线程泵未响应（主菜单态可能没有 Game.Update tick）"; return null; }
            if (fatal != null) { status = 500; code = "DIAG_FAILED"; message = fatal; return null; }
            JsonCheck.Validate(data, "ui-diag");
            return data;
        }
    }
}
