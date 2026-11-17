using System.Text;

// ⚠️ 不要 using System 之后直接用 Action/DateTime：Assembly-CSharp 里的全局 Action 枚举会遮蔽 System.Action。
namespace OniAiBridge
{
    /// <summary>
    /// M6.25：
    /// * `GET  /v1/build-menu`      —— 暴露建造 UI 结构（RootMenu 分类 + ToolMenu 建筑图标），**只读**
    /// * `POST /v1/building/select` —— 选中某格上的建筑，弹出它的菜单（之后用 `/v1/ui/click` 点菜单）
    ///
    /// 为什么走 UI：用户给的关键提示 —— **建造和建筑操作本来就是"点按钮/点建筑"**，
    /// 不必去挖底层放置函数；而 UI 点击这条路今天已经实测可用（`加载游戏` / `外世` 都点成功）。
    /// </summary>
    internal static class BuildUi
    {
        /// <summary>把某类 screen 上的按钮列出来（index / path / 文字 / 状态）。</summary>
        public static string Menu(string screenFilter, int limit,
                                  out int status, out string code, out string message)
        {
            status = 200; code = null; message = null;
            if (limit <= 0) limit = 900;
            if (limit > 2000) limit = 2000;
            if (!MainThread.Ready) { status = 503; code = "NOT_READY"; message = "main-thread pump is not running yet"; return null; }

            string data = null; string fail = null; int okT; bool busy;
            bool ran = MainThread.InvokeAdaptive(() =>
            {
                try
                {
                    var mgr = KScreenManager.Instance;
                    if (mgr == null) { fail = "KScreenManager.Instance 为 null（UI 还没建）"; return; }

                    KScreen[] all = null;
                    try { all = UnityEngine.Object.FindObjectsOfType<KScreen>(); }
                    catch (System.Exception e) { fail = "FindObjectsOfType<KScreen> 失败：" + e.GetType().Name + ": " + e.Message; return; }
                    if (all == null) { fail = "没有找到任何 KScreen"; return; }

                    var sb = new StringBuilder(4096);
                    sb.Append("{\"menus\":[");
                    int menus = 0, totalButtons = 0;

                    for (int i = 0; i < all.Length; i++)
                    {
                        var s = all[i];
                        if (s == null) continue;
                        bool active = false; try { active = s.IsActive(); } catch { }
                        if (!active) continue;

                        string sname = null;
                        try { sname = s.screenName; } catch { }
                        if (string.IsNullOrEmpty(sname)) { try { sname = s.displayName; } catch { } }
                        if (string.IsNullOrEmpty(sname)) continue;
                        if (!string.IsNullOrEmpty(screenFilter) && !string.Equals(sname, screenFilter, System.StringComparison.OrdinalIgnoreCase)) continue;

                        KButton[] btns = null;
                        try { btns = s.GetComponentsInChildren<KButton>(true); } catch { }
                        if (btns == null || btns.Length == 0) continue;

                        if (menus > 0) sb.Append(',');
                        menus++;
                        sb.Append("{\"screen\":").Append(Snap.Str(sname));
                        sb.Append(",\"buttonCount\":").Append(btns.Length);
                        sb.Append(",\"buttons\":[");

                        int em = 0;
                        for (int k = 0; k < btns.Length; k++)
                        {
                            var b = btns[k];
                            if (b == null) continue;
                            if (em >= limit) break;

                            string txt = null; bool hasLoc = false;
                            try
                            {
                                var lt = b.GetComponentInChildren<LocText>(true);
                                if (lt != null) { txt = lt.text; hasLoc = true; }
                            }
                            catch { }
                            bool vis = false; try { vis = b.gameObject != null && b.gameObject.activeInHierarchy; } catch { }
                            bool inter = false; try { inter = b.isInteractable; } catch { }
                            string p = null; try { p = PathOf(b.transform, s.transform); } catch { }

                            if (em > 0) sb.Append(',');
                            sb.Append("{\"index\":").Append(em);
                            em++; totalButtons++;
                            sb.Append(",\"text\":").Append(Snap.Str(txt));
                            sb.Append(",\"hasLocText\":").Append(hasLoc ? "true" : "false");
                            sb.Append(",\"interactable\":").Append(inter ? "true" : "false");
                            sb.Append(",\"activeInHierarchy\":").Append(vis ? "true" : "false");
                            sb.Append(",\"path\":").Append(Snap.Str(p));
                            sb.Append('}');
                        }
                        sb.Append("],\"truncated\":").Append((btns.Length > em) ? "true" : "false").Append('}');
                    }

                    sb.Append("],\"menuCount\":").Append(menus);
                    sb.Append(",\"buttonCount\":").Append(totalButtons);
                    sb.Append(",\"scope\":").Append(Snap.Str("active-screens-only"));
                    sb.Append(",\"note\":").Append(Snap.Str(
                        "index 是**该 screen 内**的顺序号（用于 /v1/ui 之外的人工核对）；"
                        + "path 供定位；RootMenu 的分类按钮通常没有 LocText（hasLocText:false）⇒ 只能按 index/path 选"));
                    sb.Append('}');
                    data = sb.ToString();
                }
                catch (System.Exception e) { fail = e.GetType().Name + ": " + e.Message; }
            }, 3000, 30000, out okT, out busy);

            if (!ran) { status = 503; code = "RETRY_LATER"; message = "main thread busy；请 1 秒后重试"; return null; }
            if (fail != null) { status = 500; code = "UI_UNAVAILABLE"; message = fail; return null; }
            if (data == null) { status = 500; code = "INTERNAL"; message = "build-menu 采集返回空"; return null; }
            return data;
        }

        /// <summary>选中某格上的建筑，让它的菜单弹出来（之后用 /v1/ui/click 点菜单）。</summary>
        public static string Select(string body, out int status, out string code, out string message)
        {
            status = 200; code = null; message = null;

            int x, y;
            if (!TryInt(body, "x", out x) || !TryInt(body, "y", out y))
            { status = 400; code = "BAD_REQUEST"; message = "需要 x / y"; return null; }
            int waitMs = 300; { int n; if (TryInt(body, "waitMs", out n)) waitMs = n; }
            if (waitMs < 0) waitMs = 0; if (waitMs > 5000) waitMs = 5000;

            if (!MainThread.Ready) { status = 503; code = "NOT_READY"; message = "main-thread pump is not running yet"; return null; }

            string fail = null, prefabId = null;
            bool selected = false;
            int okT; bool busy;

            bool ran = MainThread.InvokeAdaptive(() =>
            {
                try
                {
                    int cell = Grid.XYToCell(x, y);
                    if (!Grid.IsValidCell(cell)) { fail = "格子无效：" + x + "," + y; return; }

                    KSelectable sel = null;
                    int layers = (int)ObjectLayer.NumLayers;
                    for (int layer = 0; layer < layers; layer++)
                    {
                        UnityEngine.GameObject go = null;
                        try { go = Grid.Objects[cell, layer]; } catch { continue; }
                        if (go == null) continue;
                        var ks = go.GetComponent<KSelectable>();
                        if (ks == null) continue;
                        sel = ks;
                        try { var b = go.GetComponent<Building>(); if (b != null && b.Def != null) prefabId = b.Def.PrefabID; } catch { }
                        break;
                    }
                    if (sel == null) { fail = "该格上没有可选中的对象（没有 KSelectable）"; return; }

                    var tool = SelectTool.Instance;
                    if (tool == null) { fail = "SelectTool.Instance 为 null"; return; }
                    var pos = new UnityEngine.Vector3(x + 0.5f, y + 0.5f, 0f);
                    tool.SelectAndFocus(pos, sel);
                    selected = true;
                }
                catch (System.Exception e) { fail = e.GetType().Name + ": " + e.Message; }
            }, 3000, 30000, out okT, out busy);

            if (!ran) { status = 503; code = "RETRY_LATER"; message = "main thread busy；请 1 秒后重试"; return null; }
            if (fail != null) { status = 400; code = "SELECT_FAILED"; message = fail; return null; }
            if (!selected) { status = 500; code = "SELECT_FAILED"; message = "未发生选中"; return null; }

            if (waitMs > 0) { try { System.Threading.Thread.Sleep(waitMs); } catch { } }

            var sb = new StringBuilder(384);
            sb.Append("{\"selected\":true");
            sb.Append(",\"x\":").Append(x).Append(",\"y\":").Append(y);
            sb.Append(",\"prefabId\":").Append(Snap.Str(prefabId));
            sb.Append(",\"waitedMs\":").Append(waitMs);
            sb.Append(",\"method\":").Append(Snap.Str("SelectTool.SelectAndFocus(Vector3, KSelectable)"));
            sb.Append(",\"note\":").Append(Snap.Str("已选中；菜单是否弹出请 GET /v1/ui 确认，然后用 /v1/ui/click 点菜单项"));
            sb.Append('}');
            Log.Info("[building/select] x=" + x + " y=" + y + " prefab=" + (prefabId ?? "<null>") + " waitedMs=" + waitMs);
            return sb.ToString();
        }

        static string PathOf(UnityEngine.Transform t, UnityEngine.Transform root)
        {
            try
            {
                var parts = new System.Collections.Generic.List<string>();
                var cur = t; int guard = 0;
                while (cur != null && guard++ < 32)
                {
                    parts.Add(cur.name);
                    if (root != null && cur == root) break;
                    cur = cur.parent;
                }
                parts.Reverse();
                return string.Join("/", parts.ToArray());
            }
            catch { return null; }
        }

        static bool TryInt(string body, string key, out int v)
        {
            v = 0;
            if (string.IsNullOrEmpty(body)) return false;
            var m = System.Text.RegularExpressions.Regex.Match(body, "\"" + key + "\"\\s*:\\s*(-?[0-9]+)");
            if (!m.Success) return false;
            return int.TryParse(m.Groups[1].Value, System.Globalization.NumberStyles.Integer,
                                System.Globalization.CultureInfo.InvariantCulture, out v);
        }
    }
}
