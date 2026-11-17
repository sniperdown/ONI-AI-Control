using System.Text;

// ⚠️ 不要 using System 之后直接用 Action/DateTime：Assembly-CSharp 里的全局 Action 枚举会遮蔽 System.Action。
namespace OniAiBridge
{
    /// <summary>
    /// M6.42（第 1 步，**只读**）：`GET /v1/ui/widgets` —— 列出激活界面里**所有能接收鼠标点击的组件**。
    ///
    /// 为什么不再只认 `KButton`：实测 `ResearchScreen` 打开时只有 5 个 `KButton`（关闭/缩放/清空搜索），
    /// **科技节点不是 `KButton`**。而 `Clickable` / `TechWidget` 这两个名字**在程序集里根本不存在**
    /// （`layout.exe types` 查过两个 Assembly-CSharp，含 Click 的类型只有 `OnClick`、`ClickCallback` …）。
    ///
    /// ⇒ 改用**接口判定**，不依赖具体类型名：扫该屏幕下所有 `MonoBehaviour`，
    /// 凡是实现了 `UnityEngine.EventSystems.IPointerClickHandler`（或 `ISubmitHandler`）的都算"可点击控件"。
    /// 这条能覆盖 `KButton` / `UnityEngine.UI.Button` / 各种自定义控件，且**不需要知道类名**。
    ///
    /// 本版**不点击**（点击在下一步单独做，避免"列出"和"点击"混在一起不好验证）。
    /// </summary>
    internal static class UiWidgets
    {
        public static string List(string screenFilter, int limit, out int status, out string code, out string message)
        {
            status = 200; code = null; message = null;
            if (limit <= 0) limit = 400;
            if (limit > 3000) limit = 3000;
            if (!MainThread.Ready) { status = 503; code = "NOT_READY"; message = "main-thread pump is not running yet"; return null; }

            string data = null, fail = null;
            int okT; bool busy;
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

                    var sb = new StringBuilder(8192);
                    sb.Append("{\"screens\":[");
                    int emittedScreens = 0, totalWidgets = 0;

                    for (int i = 0; i < all.Length; i++)
                    {
                        var s = all[i];
                        if (s == null) continue;
                        bool active = false;
                        try { active = s.IsActive(); } catch { }
                        if (!active) continue;

                        string sname = null;
                        try { sname = s.screenName; } catch { }
                        if (string.IsNullOrEmpty(sname)) { try { sname = s.displayName; } catch { } }
                        if (string.IsNullOrEmpty(sname)) { try { sname = s.gameObject != null ? s.gameObject.name : null; } catch { } }
                        if (!string.IsNullOrEmpty(screenFilter) && !string.Equals(sname, screenFilter, System.StringComparison.OrdinalIgnoreCase)) continue;

                        UnityEngine.MonoBehaviour[] comps = null;
                        try { comps = s.GetComponentsInChildren<UnityEngine.MonoBehaviour>(true); } catch { }
                        if (comps == null) continue;

                        var body = new StringBuilder(4096);
                        int emitted = 0, clickableSeen = 0;

                        for (int k = 0; k < comps.Length; k++)
                        {
                            var c = comps[k];
                            if (c == null) continue;

                            bool isClick = false;
                            try
                            {
                                isClick = (c is UnityEngine.EventSystems.IPointerClickHandler)
                                       || (c is UnityEngine.EventSystems.ISubmitHandler);
                            }
                            catch { }
                            if (!isClick) continue;
                            clickableSeen++;
                            if (emitted >= limit) continue;

                            string kind = null;
                            try { kind = c.GetType().Name; } catch { }

                            string text = null;
                            try
                            {
                                var lt = c.GetComponentInChildren<LocText>(true);
                                if (lt != null) text = lt.text;
                            }
                            catch { }

                            bool? interactable = null;
                            try
                            {
                                var kb = c as KButton;
                                if (kb != null) interactable = kb.isInteractable;
                                else
                                {
                                    var ub = c as UnityEngine.UI.Button;
                                    if (ub != null) interactable = ub.interactable;
                                }
                            }
                            catch { }

                            string path = null;
                            try { path = PathOf(c.transform, s.transform); } catch { }
                            bool vis = false;
                            try { vis = c.gameObject != null && c.gameObject.activeInHierarchy; } catch { }

                            if (emitted > 0) body.Append(',');
                            emitted++; totalWidgets++;
                            body.Append("{\"kind\":").Append(Snap.Str(kind));
                            body.Append(",\"text\":").Append(Snap.Str(text));
                            body.Append(",\"path\":").Append(Snap.Str(path));
                            body.Append(",\"interactable\":").Append(interactable.HasValue ? (interactable.Value ? "true" : "false") : "null");
                            body.Append(",\"activeInHierarchy\":").Append(vis ? "true" : "false");
                            body.Append('}');
                        }

                        if (emittedScreens > 0) sb.Append(',');
                        emittedScreens++;
                        sb.Append("{\"screen\":").Append(Snap.Str(sname));
                        sb.Append(",\"clickableTotal\":").Append(clickableSeen);
                        sb.Append(",\"emitted\":").Append(emitted);
                        sb.Append(",\"truncated\":").Append(clickableSeen > emitted ? "true" : "false");
                        sb.Append(",\"widgets\":[").Append(body.ToString()).Append("]}");
                    }

                    sb.Append("],\"screenCount\":").Append(emittedScreens);
                    sb.Append(",\"widgetCount\":").Append(totalWidgets);
                    sb.Append(",\"limit\":").Append(limit);
                    sb.Append(",\"scope\":").Append(Snap.Str("active-screens-only"));
                    sb.Append(",\"detect\":").Append(Snap.Str("MonoBehaviour is IPointerClickHandler || ISubmitHandler（不依赖类名）"));
                    sb.Append(",\"note\":").Append(Snap.Str(
                        "只读：本端点不触发任何点击。`interactable` 仅对 KButton / UnityEngine.UI.Button 有值，其它类型为 null"
                        + "（不是 false —— null 表示我们读不到，不是不可点）"));
                    sb.Append('}');
                    data = sb.ToString();
                }
                catch (System.Exception e) { fail = e.GetType().Name + ": " + e.Message; }
            }, 5000, 30000, out okT, out busy);

            if (!ran) { status = 503; code = "RETRY_LATER"; message = "main thread busy；请 1 秒后重试"; return null; }
            if (fail != null) { status = 500; code = "UI_UNAVAILABLE"; message = fail; return null; }
            if (data == null) { status = 500; code = "INTERNAL"; message = "widgets 采集返回空"; return null; }
            return data;
        }

        internal static string PathOf(UnityEngine.Transform t, UnityEngine.Transform root)
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
    }
}
