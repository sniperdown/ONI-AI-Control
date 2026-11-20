using System.Text;

// ⚠️ 不要 using System 之后直接用 Action/DateTime：Assembly-CSharp 里的全局 Action 枚举会遮蔽 System.Action。
namespace OniAiBridge
{
    /// <summary>
    /// M6.23：`POST /v1/ui/click` —— 按**文字**（或 `path` / `labelClick` / `techId`）点一下。
    ///
    /// 点击入口的选择（IL 已核）：
    /// * `KButton.SignalClick(KKeyCode)`：`if (!interactable) return;` → `onClick.Invoke()` → `onBtnClick(...)`
    ///   ⇒ 这是**游戏自己走的那条**（`OnPointerClick` 内部也走它），且自带"不可点就不点"的门槛；
    /// * `OnPointerClick(PointerEventData)`：需要我们伪造 `PointerEventData`（含 EventSystem），**不这么做**。
    ///
    /// M6.86 起：候选集合与点击分派都交给 `UiRows` —— 「行」这类挂 `MultiToggle` 的控件
    /// （诊断行 / 报告行 / 表格格子）不再是"点不到"的盲区，`text` / `labelClick` / `path` 三条定位路径都能命中。
    ///
    /// 四条边界（都在实测里出现过）：
    /// 1. **同名多处**：只考虑 `activeInHierarchy && interactable`；过滤后仍 ≥2 ⇒ `409 AMBIGUOUS_TARGET` + 候选；
    /// 2. **不可见的跳过**，并把"匹配 N 个 / 跳过 M 个"如实报出来（不只说"没找到"）；
    /// 3. 点完 `waitMs`（默认 300）再返回，让调用方能立刻 `GET /v1/ui` 看到新界面；
    /// 4. **禁止点"退出"**（含 `quit`/`exit`）⇒ `400 NOT_ALLOWED`。
    /// </summary>
    internal static class UiClick
    {
        sealed class Hit
        {
            public string Text;
            public string Screen;
            public bool Interactable;
            public bool ActiveInHierarchy;
            public KButton Button;                 // 仅 KButton 命中时非 null（保留旧字段，便于老调用方理解）
            public UnityEngine.MonoBehaviour Comp; // 实际要点的组件（KButton / KToggle / MultiToggle / UGUI 控件）
            public string Kind;                    // 组件类名
            public UiRows.Element El;               // 命中的那份采集结果：`path` / `role` / 序号与 clickables 同源
        }

        /// <summary>`kind` 过滤：既接受组件类名（`MultiToggle`），也接受归一化种类（`row` / `button`）。</summary>
        static bool KindMatches(UiRows.Element e, string want)
        {
            if (e == null) return false;
            if (!string.IsNullOrEmpty(e.Kind) && string.Equals(e.Kind, want, System.StringComparison.OrdinalIgnoreCase)) return true;
            if (!string.IsNullOrEmpty(e.Role) && string.Equals(e.Role, want, System.StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        /// <summary>禁止点击名单：退出 + 存档结构迁移（后者会改动所有存档文件）。</summary>
        static bool IsForbiddenName(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            string low = s.ToLowerInvariant();
            if (low.Contains("退出") || low.Contains("quit") || low.Contains("exit")) return true;
            if (s.Contains("迁移") || s.Contains("更新存档")) return true;
            // ★ 0.97.3：同上，避免 "Immigration" 被误判（打印门/移民屏是正常功能）
            if (low.Contains("migration") && !low.Contains("immigration")) return true;
            return false;
        }

        /// <summary>整个 Migration 面板下的节点都不允许点（按 path 前缀判断）。</summary>
        static bool IsUnderMigration(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            // ★ 0.97.3 修 bug：原来子串匹配 "Migration" 把打印门的 `Immigration`（移民屏）也封了
            //   ⇒ 打印门点不开、400 NOT_ALLOWED。改成按路径段精确匹配。
            {
                string[] segs = path.Split('/');
                for (int i = 0; i < segs.Length; i++)
                {
                    string s = segs[i];
                    if (string.IsNullOrEmpty(s)) continue;
                    if (string.Equals(s, "Migration", System.StringComparison.OrdinalIgnoreCase)) return true;
                    if (s.StartsWith("Migration", System.StringComparison.OrdinalIgnoreCase)
                        && (s.Length == 9 || !char.IsLetter(s[9]))) return true;
                }
            }
            return false;
        }
        public static string Click(string body, out int status, out string code, out string message)
        {
            status = 200; code = null; message = null;

            string labelWant = MatchStr(body, "labelClick");
            string want = MatchStr(body, "text");
            string techWant0 = MatchStr(body, "techId");
            string pathWant0 = MatchJsonString(body, "path");            // 支持 \" 转义（<link=\"X\"> 这类路径）
            string pathContains0 = MatchJsonString(body, "pathContains"); // 子串匹配，调用方不用碰引号
            if (!string.IsNullOrEmpty(pathWant0) || !string.IsNullOrEmpty(pathContains0))
                return ClickByWidget(null, pathWant0, pathContains0, MatchStr(body, "screen"), body, out status, out code, out message);
            if (!string.IsNullOrEmpty(techWant0))
                return ClickByWidget(techWant0, null, null, MatchStr(body, "screen"), body, out status, out code, out message);

            if (!string.IsNullOrEmpty(labelWant) && !string.IsNullOrEmpty(want))
            {
                status = 400; code = "BAD_REQUEST";
                message = "labelClick 与 text 互斥，只能给一个";
                return null;
            }
            if (!string.IsNullOrEmpty(labelWant))
                return ClickByLabel(labelWant, MatchStr(body, "screen"), body, out status, out code, out message);
            string wantScreen = MatchStr(body, "screen");
            int index = -1;
            { int n; if (TryInt(body, "index", out n)) index = n; }
            int waitMs = 300;
            { int n; if (TryInt(body, "waitMs", out n)) waitMs = n; }
            if (waitMs < 0) waitMs = 0;
            if (waitMs > 5000) waitMs = 5000;

            if (string.IsNullOrEmpty(want))
            {
                status = 400; code = "BAD_REQUEST";
                message = "需要 text（按钮上显示的文字，去 GET /v1/ui 拿）";
                return null;
            }

            // ★ 护栏：禁止名单（退出 + 存档结构迁移）
            if (IsForbiddenName(want))
            {
                status = 400; code = "NOT_ALLOWED";
                message = "本版禁止通过 API 点击该按钮（text=" + want + "）：退出会关游戏，"
                          + "存档结构迁移会改动所有存档文件";
                Log.Info("[ui/click] 拒绝：NOT_ALLOWED text=" + want);
                return null;
            }

            if (!MainThread.Ready) { status = 503; code = "NOT_READY"; message = "main-thread pump is not running yet"; return null; }

            var hits = new System.Collections.Generic.List<Hit>();
            int invisibleSkipped = 0, matchedText = 0;
            string fail = null;
            UnityEngine.MonoBehaviour target = null;
            UiRows.Element targetEl = null;
            string targetText = null, targetScreen = null, targetKind = null;
            bool targetInteractable = false;

            // 可选 `kind`：只点某一种组件（组件类名或归一化种类），用于同名文字消歧（多了一个筛选维度，不影响旧请求）
            string kindWant = MatchStr(body, "kind");

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
                        if (!string.IsNullOrEmpty(wantScreen) && !string.Equals(sname, wantScreen, System.StringComparison.OrdinalIgnoreCase)) continue;

                        // ★ M6.86：按接口采集该界面的**全部**可点击元素（含 `MultiToggle` 行），
                        //   不再只扫 `KButton` —— 否则诊断行 / 报告行这类「行」永远匹配不到。
                        int total = 0;
                        var els = UiRows.Collect(s, UiQuery.CollectLimit, out total);
                        for (int k = 0; k < els.Count; k++)
                        {
                            var e = els[k];
                            if (e == null || e.Comp == null) continue;
                            if (!string.IsNullOrEmpty(kindWant) && !KindMatches(e, kindWant)) continue;

                            string txt = e.Text;
                            if (txt == null) continue;

                            bool exact = string.Equals(txt.Trim(), want.Trim(), System.StringComparison.Ordinal);
                            bool contains = !exact && txt.IndexOf(want, System.StringComparison.Ordinal) >= 0;
                            if (!exact && !contains) continue;
                            matchedText++;

                            if (!e.ActiveInHierarchy) { invisibleSkipped++; continue; }      // ★ 看不见的跳过

                            var h = new Hit
                            {
                                Text = txt,
                                Screen = e.Screen ?? sname,
                                Interactable = e.Interactable,
                                ActiveInHierarchy = e.ActiveInHierarchy,
                                Comp = e.Comp,
                                Button = e.Comp as KButton,
                                Kind = e.Kind,
                                El = e
                            };
                            hits.Add(h);
                        }
                    }

                    if (hits.Count == 0) return;

                    // 优先互动为 true 的（不可点的按钮"点了也不生效"，按规则只点 active && interactable）
                    var usable = new System.Collections.Generic.List<Hit>();
                    for (int i = 0; i < hits.Count; i++) if (hits[i].Interactable) usable.Add(hits[i]);

                    if (index >= 0)
                    {
                        if (index < usable.Count) { target = usable[index].Comp; targetEl = usable[index].El; targetText = usable[index].Text; targetScreen = usable[index].Screen; targetKind = usable[index].Kind; targetInteractable = true; }
                        return;
                    }
                    if (usable.Count == 1)
                    {
                        target = usable[0].Comp; targetEl = usable[0].El; targetText = usable[0].Text; targetScreen = usable[0].Screen; targetKind = usable[0].Kind; targetInteractable = true;
                        return;
                    }
                    // 0 个可用 / ≥2 个可用 ⇒ 交给外面判
                }
                catch (System.Exception e) { fail = e.GetType().Name + ": " + e.Message; }
            }, 3000, 30000, out okT, out busy);

            if (!ran) { status = 503; code = "RETRY_LATER"; message = "main thread busy；请 1 秒后重试"; return null; }
            if (fail != null) { status = 500; code = "UI_UNAVAILABLE"; message = fail; Log.Info("[ui/click] " + fail); return null; }

            if (target == null)
            {
                if (hits.Count == 0)
                {
                    status = 404; code = "NOT_FOUND";
                    message = "没有匹配的可点击元素：text=" + want
                              + (string.IsNullOrEmpty(wantScreen) ? "" : "，screen=" + wantScreen)
                              + (string.IsNullOrEmpty(kindWant) ? "" : "，kind=" + kindWant)
                              + "（匹配到文字 " + matchedText + " 个，其中 " + invisibleSkipped + " 个不可见已跳过）";
                    return null;
                }
                // 有命中但都不可交互 或 有多个可用
                int usableCount = 0;
                for (int i = 0; i < hits.Count; i++) if (hits[i].Interactable) usableCount++;
                if (usableCount == 0)
                {
                    status = 400; code = "NOT_INTERACTABLE";
                    message = "匹配到 " + hits.Count + " 个可点击元素，但都不可点（interactable=false）"
                              + "；匹配到文字 " + matchedText + " 个，其中 " + invisibleSkipped + " 个不可见已跳过";
                    return null;
                }
                status = 409; code = "AMBIGUOUS_TARGET";
                message = "匹配到 " + usableCount + " 个可点元素，必须消歧（加 screen / kind / index）"
                          + "；匹配到文字 " + matchedText + " 个，其中 " + invisibleSkipped + " 个不可见已跳过";
                var sb0 = new StringBuilder(512);
                sb0.Append("{\"candidates\":[");
                int em = 0;
                for (int i = 0; i < hits.Count; i++)
                {
                    if (!hits[i].Interactable) continue;
                    if (em > 0) sb0.Append(',');
                    em++;
                    sb0.Append("{\"index\":").Append(em - 1);
                    sb0.Append(",\"text\":").Append(Snap.Str(hits[i].Text));
                    sb0.Append(",\"screen\":").Append(Snap.Str(hits[i].Screen));
                    sb0.Append(",\"kind\":").Append(Snap.Str(hits[i].Kind));
                    sb0.Append(",\"interactable\":true}");
                }
                sb0.Append("]}");
                LastDetailsHolder.Value = sb0.ToString();
                return null;
            }

            // ---- 真正点击（主线程，按组件类型分派：KButton / KToggle / MultiToggle / UGUI 控件，见 UiRows.Click）----
            string clickedVia = null;
            string clickErr = null;
            int okT2; bool busy2;
            MainThread.InvokeAdaptive(() =>
            {
                try { UiRows.Click(target, out clickedVia, out clickErr); }
                catch (System.Exception e) { clickErr = e.GetType().Name + ": " + e.Message; }
            }, 3000, 30000, out okT2, out busy2);

            if (clickedVia == null)
            {
                status = 500; code = "CLICK_FAILED";
                message = "点击失败（kind=" + (targetKind ?? "?") + "）：" + (clickErr ?? "未知");
                return null;
            }

            if (waitMs > 0)
            {
                try { System.Threading.Thread.Sleep(waitMs); } catch { }
            }

            var sb = new StringBuilder(512);
            sb.Append("{\"clicked\":true");
            sb.Append(",\"target\":");
            UiRows.AppendTargetJson(sb, targetEl);          // ★ 0.93.3：这次真正点到的元素（path 与 clickables 同源）
            sb.Append(",\"path\":").Append(Snap.Str(TargetPath(targetEl)));
            sb.Append(",\"text\":").Append(Snap.Str(targetText));
            sb.Append(",\"screen\":").Append(Snap.Str(targetScreen));
            sb.Append(",\"kind\":").Append(Snap.Str(targetKind));
            sb.Append(",\"clickedVia\":").Append(Snap.Str(clickedVia));
            sb.Append(",\"matched\":").Append(matchedText);
            sb.Append(",\"candidates\":").Append(hits.Count);
            sb.Append(",\"invisibleSkipped\":").Append(invisibleSkipped);
            sb.Append(",\"interactable\":").Append(targetInteractable ? "true" : "false");
            sb.Append(",\"activeInHierarchy\":true");
            sb.Append(",\"waitedMs\":").Append(waitMs);
            sb.Append(",\"method\":").Append(Snap.Str(clickedVia));
            sb.Append(",\"note\":").Append(Snap.Str("已等待 " + waitMs + " ms 后返回；界面可能仍在切换，可再 GET /v1/ui 确认"));
            // ★ 0.93.3：点后回读对**所有组件类型**都填（以前只有 index/name/dryRun 那条实现有）。
            AppendReadback(sb, target, targetScreen);
            sb.Append('}');

            Log.Info("[ui/click] 已点：text=" + targetText + " kind=" + targetKind + " via=" + clickedVia
                     + " screen=" + targetScreen
                     + " matched=" + matchedText + " candidates=" + hits.Count
                     + " invisibleSkipped=" + invisibleSkipped + " waitedMs=" + waitMs);
            return sb.ToString();
        }

        /// <summary>409 的候选列表通过这里交给 Bridge（显式通道：同一次调用内写/读）。</summary>
        internal static class LastDetailsHolder
        {
            [System.ThreadStatic] public static string Value;
        }

        /// <summary>目标元素的路径（含"拼不出父链"时的回退与说明，规则见 `UiRows.PathForTarget`）。</summary>
        static string TargetPath(UiRows.Element el)
        {
            string src, unavail;
            return UiRows.PathForTarget(el, out src, out unavail);
        }

        /// <summary>
        /// 点后回读：把 `readback` 追加到响应里（**必须在主线程执行**，要读 Unity 对象）。
        ///
        /// 为什么回读要重新进主线程而不是复用点击前的采集结果：目标组件可能在这次点击里被关闭/销毁
        /// （主菜单点「新游戏」、诊断界面点「关闭」都是这种），那时读它的 `transform` 会抛
        /// `MissingReferenceException`；`UiRows.AppendReadbackJson` 内部按"存活/已销毁"分两种情况取值。
        ///
        /// 连主线程都进不去（busy / 超时）时仍然写一份 `readback`，并明确写"回读未执行"及原因 ——
        /// 让调用方看到的是"没读到 + 为什么"，而不是一个缺失的字段。
        /// </summary>
        static void AppendReadback(StringBuilder sb, UnityEngine.MonoBehaviour target, string screenBefore)
        {
            var rb = new StringBuilder(1024);
            string rbErr = null;
            int okT; bool busy;
            MainThread.InvokeAdaptive(() =>
            {
                try { rb.Length = 0; UiRows.AppendReadbackJson(rb, target, screenBefore, true); }
                catch (System.Exception e) { rb.Length = 0; rbErr = e.GetType().Name + ": " + e.Message; }
            }, 3000, 30000, out okT, out busy);

            if (rb.Length > 0) { sb.Append(rb); return; }

            string why = "回读未能在超时内进入主线程"
                         + (rbErr != null ? "（" + rbErr + "）" : "")
                         + "；可稍后 GET /v1/ui 复核界面与可交互状态";
            sb.Append(",\"readback\":{\"screenBefore\":").Append(Snap.Str(screenBefore));
            sb.Append(",\"screenAfter\":null");
            sb.Append(",\"screenAfterUnavailable\":").Append(Snap.Str(why));
            sb.Append(",\"interactableAfter\":null");
            sb.Append(",\"interactableAfterUnavailable\":").Append(Snap.Str(why));
            sb.Append('}');
        }

        /// <summary>
        /// `labelClick`：找到文字恰好等于 `label` 的 `LocText`，从它**向上找最近的 KButton**，点那个。
        /// （"外世"这类存档名是 LocText，不是按钮本体。）
        /// </summary>
        /// <summary>
        /// M6.43（第 2 步）：按 **techId** 或 **path** 点击扩展控件。
        ///
        /// 实测：研究界面的科技节点是 **KToggle**（100 个 = techCount），
        /// `text` 形如 `<link="FINEDINING">食物制备</link>`，`<link="X">` 里的 X 就是 `/v1/research` 的 techId。
        ///
        /// 点击入口（均已核）：`KToggle.Click()`（public，首选）/ `KButton.SignalClick` / `UIButton.onClick.Invoke()`。
        /// 其它类型 ⇒ `NOT_CLICKABLE`（如实报 kind，不假装成功）。
        /// </summary>
        /// <summary>按 JSON 字符串规则取字段（支持 \" 等转义）—— `MatchStr` 遇到转义会截断。</summary>

        /// <summary>读布尔参数：接受 true / "true" / 1。</summary>
        static bool MatchBoolVal(string body, string key)
        {
            if (string.IsNullOrEmpty(body)) return false;
            var m = System.Text.RegularExpressions.Regex.Match(body, "\"" + key + "\"\\s*:\\s*(true|false|\"true\"|\"false\"|1|0)");
            if (!m.Success) return false;
            string v = m.Groups[1].Value;
            return v == "true" || v == "\"true\"" || v == "1";
        }
        static string MatchJsonString(string body, string key)
        {
            if (string.IsNullOrEmpty(body)) return null;
            var m = System.Text.RegularExpressions.Regex.Match(body, "\"" + key + "\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"");
            if (!m.Success) return null;
            string s = m.Groups[1].Value;
            var sb = new StringBuilder(s.Length);
            for (int i2 = 0; i2 < s.Length; i2++)
            {
                if (s[i2] == (char)92 && i2 + 1 < s.Length)
                {
                    char n = s[i2 + 1];
                    if (n == (char)34) { sb.Append((char)34); i2++; continue; }
                    if (n == (char)92) { sb.Append((char)92); i2++; continue; }
                }
                sb.Append(s[i2]);
            }
            return sb.ToString();
        }

        static string ClickByWidget(string techWant, string pathWant, string pathContains, string wantScreen, string body,
                                    out int status, out string code, out string message)
        {
            status = 200; code = null; message = null;

            int waitMs = 300;
            { int n; if (TryInt(body, "waitMs", out n)) waitMs = n; }
            if (waitMs < 0) waitMs = 0;
            if (waitMs > 5000) waitMs = 5000;

            string probe = techWant ?? pathWant;
            if (IsForbiddenName(probe))
            {
                status = 400; code = "NOT_ALLOWED";
                message = "本版禁止通过 API 点击该目标（" + probe + "）";
                return null;
            }
            if (!MainThread.Ready) { status = 503; code = "NOT_READY"; message = "main-thread pump is not running yet"; return null; }

            var hits = new System.Collections.Generic.List<UnityEngine.MonoBehaviour>();
            var hitEls = new System.Collections.Generic.List<UiRows.Element>();
            var hitPaths = new System.Collections.Generic.List<string>();
            var hitKinds = new System.Collections.Generic.List<string>();
            var hitScreens = new System.Collections.Generic.List<string>();
            string fail = null;
            int okT; bool busy;

            bool ran = MainThread.InvokeAdaptive(() =>
            {
                try
                {
                    var mgr = KScreenManager.Instance;
                    if (mgr == null) { fail = "KScreenManager.Instance 为 null（UI 还没建）"; return; }

                    KScreen[] all = null;
                    try { all = UnityEngine.Object.FindObjectsOfType<KScreen>(); }
                    catch (System.Exception e) { fail = "FindObjectsOfType<KScreen> 失败：" + e.GetType().Name; return; }
                    if (all == null) { fail = "没有找到任何 KScreen"; return; }

                    string needle = string.IsNullOrEmpty(techWant) ? null : ("link=\"" + techWant + "\"");

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
                        if (!string.IsNullOrEmpty(wantScreen) && !string.Equals(sname, wantScreen, System.StringComparison.OrdinalIgnoreCase)) continue;

                        // ★ M6.86：用与 `/v1/ui` **同一个**采集器（同一份 path、同一套去重）——
                        //   否则"`/v1/ui` 里列出来的 path"和"click 能匹配到的 path"会漂移。
                        int totalEls = 0;
                        var els = UiRows.Collect(s, UiQuery.CollectLimit, out totalEls);
                        for (int k = 0; k < els.Count; k++)
                        {
                            var el = els[k];
                            if (el == null || el.Comp == null) continue;
                            var c = el.Comp;

                            string p = el.Path;
                            // `/v1/ui/widgets` 的 path 走另一份拼法（同名兄弟**不带** `#n`）；两种写法都接受，
                            // 但返回体里一律回 `/v1/ui` 的那份（也就是 `path` 里列出来的那份）。
                            string pLegacy = null;
                            try { pLegacy = UiWidgets.PathOf(c.transform, s.transform); } catch { }
                            if (IsUnderMigration(p)) continue;

                            bool match = false;
                            if (!string.IsNullOrEmpty(pathWant))
                            {
                                match = string.Equals(p ?? "", pathWant, System.StringComparison.Ordinal)
                                     || string.Equals(pLegacy ?? "", pathWant, System.StringComparison.Ordinal);
                            }
                            else if (!string.IsNullOrEmpty(pathContains))
                            {
                                if ((p ?? "").IndexOf(pathContains, System.StringComparison.OrdinalIgnoreCase) >= 0
                                    || (pLegacy ?? "").IndexOf(pathContains, System.StringComparison.OrdinalIgnoreCase) >= 0) match = true;
                            }
                            else if (needle != null)
                            {
                                if ((p ?? "").IndexOf(needle, System.StringComparison.OrdinalIgnoreCase) >= 0) match = true;
                                else if (el.Text != null && el.Text.IndexOf(needle, System.StringComparison.OrdinalIgnoreCase) >= 0) match = true;
                            }
                            if (!match) continue;

                            // ★ techId 匹配只收 KToggle（实测：同名科技在侧栏 MultiToggle 里也会出现 9+ 次）
                            string kindNow = el.Kind;
                            if (needle != null && string.IsNullOrEmpty(pathWant) && string.IsNullOrEmpty(pathContains))
                            {
                                if (kindNow != "KToggle") { continue; }
                            }
                            hits.Add(c);
                            hitEls.Add(el);
                            hitPaths.Add(p);
                            hitKinds.Add(kindNow);
                            hitScreens.Add(el.Screen ?? sname);
                        }
                    }
                }
                catch (System.Exception e) { fail = e.GetType().Name + ": " + e.Message; }
            }, 5000, 30000, out okT, out busy);

            if (!ran) { status = 503; code = "RETRY_LATER"; message = "main thread busy；请 1 秒后重试"; return null; }
            if (fail != null) { status = 500; code = "UI_UNAVAILABLE"; message = fail; return null; }

            if (hits.Count == 0)
            {
                status = 404; code = "NOT_FOUND";
                message = "没有匹配的控件（techId=" + (techWant ?? "<none>") + "，path=" + (pathWant ?? "<none>")
                          + "）；可先 GET /v1/ui/widgets 看有什么";
                return null;
            }
            if (hits.Count > 1)
            {
                status = 409; code = "AMBIGUOUS_TARGET";
                message = "匹配到 " + hits.Count + " 个控件，必须消歧（改用 path）";
                var sb0 = new StringBuilder(1024);
                sb0.Append("{\"candidates\":[");
                for (int i = 0; i < hits.Count; i++)
                {
                    if (i > 0) sb0.Append(',');
                    sb0.Append("{\"index\":").Append(i);
                    sb0.Append(",\"kind\":").Append(Snap.Str(hitKinds[i]));
                    sb0.Append(",\"path\":").Append(Snap.Str(hitPaths[i]));
                    sb0.Append(",\"screen\":").Append(Snap.Str(hitScreens[i])).Append('}');
                }
                sb0.Append("]}");
                LastDetailsHolder.Value = sb0.ToString();
                return null;
            }

            var target = hits[0];
            string kindName = hitKinds[0];
            string clickedVia = null;
            string clickErr = null;
            bool allowToggleOff = MatchBoolVal(body, "allowToggleOff");   // 显式放行"取消选中"
            bool isToggleOff = false;
            bool pAlreadyOn = false;

            int okT2; bool busy2;
            MainThread.InvokeAdaptive(() =>
            {
                try
                {
                    var kt = target as KToggle;
                    if (kt != null)
                    {
                        // ★ 护栏：KToggle 是"切换"语义 —— 点已开启的开关会把它关掉。
                        //   实测事故：点已选中的 FINEDINING ⇒ activeTechId 被清空（研究方向丢失）。
                        bool alreadyOn = false;
                        try { alreadyOn = kt.isOn; } catch { }
                        if (alreadyOn && !allowToggleOff) { pAlreadyOn = true; clickErr = "该开关已处于开启状态，点击会把它关掉，已拒绝（要取消请显式传 allowToggleOff:true）"; return; }
                        if (alreadyOn && allowToggleOff) isToggleOff = true;   // 显式取消 ⇒ jobState:toggled-off
                    }

                    // ★ M6.86：按组件类型分派（KButton / KToggle / MultiToggle / UGUI 控件 / 其它 IPointerClickHandler），
                    //   「行」用的 MultiToggle 由此才点得到。
                    UiRows.Click(target, out clickedVia, out clickErr);
                    if (clickedVia == null && string.IsNullOrEmpty(clickErr)) clickErr = "该控件类型没有已知的 public 点击入口";
                }
                catch (System.Exception e) { clickErr = e.GetType().Name + ": " + e.Message; }
            }, 3000, 30000, out okT2, out busy2);

            if (clickedVia == null)
            {
                if (pAlreadyOn)
                {
                    status = 409; code = "ALREADY_ON";
                    message = "该开关已处于开启状态，点击会把它关掉，已拒绝（kind=" + kindName + "）";
                    LastDetailsHolder.Value = "{\"clicked\":false,\"kind\":" + Snap.Str(kindName) + ",\"path\":" + Snap.Str(hitPaths[0]) + "}";
                    Log.Info("[ui/click] 拒绝 ALREADY_ON：kind=" + kindName + " path=" + (hitPaths[0] ?? "<null>"));
                    return null;
                }
                status = 400; code = "NOT_CLICKABLE";
                message = "找到控件（kind=" + kindName + "）但无法点击：" + (clickErr ?? "未知");
                return null;
            }

            if (waitMs > 0) { try { System.Threading.Thread.Sleep(waitMs); } catch { } }

            var sb = new StringBuilder(512);
            sb.Append("{\"clicked\":true");
            sb.Append(",\"target\":");
            UiRows.AppendTargetJson(sb, hitEls.Count > 0 ? hitEls[0] : null);   // ★ 0.93.3：真正点到的元素
            sb.Append(",\"kind\":").Append(Snap.Str(kindName));
            sb.Append(",\"screen\":").Append(Snap.Str(hitScreens[0]));
            sb.Append(",\"path\":").Append(Snap.Str(hitEls.Count > 0 ? TargetPath(hitEls[0]) : hitPaths[0]));
            sb.Append(",\"techId\":").Append(Snap.Str(techWant));
            sb.Append(",\"clickedVia\":").Append(Snap.Str(clickedVia));
            sb.Append(",\"jobState\":").Append(Snap.Str(isToggleOff ? "toggled-off" : "clicked"));
            sb.Append(",\"waitedMs\":").Append(waitMs);
            sb.Append(",\"note\":").Append(Snap.Str("请到游戏里确认界面/状态是否真的变化；本字段只说明调用了哪个入口"));
            // ★ 0.93.3：点后回读（以前只有 index/name/dryRun 那条实现有，path/techId 这条一直是空的）。
            AppendReadback(sb, target, hitScreens[0]);
            sb.Append('}');
            Log.Info("[ui/click] widget 已点：kind=" + kindName + " via=" + clickedVia + " path=" + (hitPaths[0] ?? "<null>"));
            return sb.ToString();
        }

        static string ClickByLabel(string label, string wantScreen, string body, out int status, out string code, out string message)
        {
            status = 200; code = null; message = null;

            int waitMs = 300;
            { int n; if (TryInt(body, "waitMs", out n)) waitMs = n; }
            if (waitMs < 0) waitMs = 0;
            if (waitMs > 5000) waitMs = 5000;

            if (IsForbiddenName(label))
            {
                status = 400; code = "NOT_ALLOWED";
                message = "本版禁止通过 API 点击该文字（labelClick=" + label + "）：退出会关游戏，"
                          + "存档结构迁移会改动所有存档文件";
                return null;
            }
            if (!MainThread.Ready) { status = 503; code = "NOT_READY"; message = "main-thread pump is not running yet"; return null; }

            var cands = new System.Collections.Generic.List<string>();
            var candScreens = new System.Collections.Generic.List<string>();
            UnityEngine.MonoBehaviour target = null;
            UiRows.Element targetEl = null;
            string targetLabelPath = null, targetButtonPath = null, targetScreen = null, targetKind = null;
            string fail = null;
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

                    var usableButtons = new System.Collections.Generic.List<UnityEngine.MonoBehaviour>();
                    var usableKinds = new System.Collections.Generic.List<string>();
                    var usablePaths = new System.Collections.Generic.List<string>();
                    var usableScreens = new System.Collections.Generic.List<string>();
                    var usableScreenObjs = new System.Collections.Generic.List<KScreen>();

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
                        if (!string.IsNullOrEmpty(wantScreen) && !string.Equals(sname, wantScreen, System.StringComparison.OrdinalIgnoreCase)) continue;

                        LocText[] lts = null;
                        try { lts = s.GetComponentsInChildren<LocText>(true); } catch { }
                        if (lts == null) continue;

                        for (int k = 0; k < lts.Length; k++)
                        {
                            var lt = lts[k];
                            if (lt == null) continue;
                            string ltext = null;
                            try { ltext = lt.text; } catch { }
                            if (ltext == null) continue;
                            if (!string.Equals(ltext.Trim(), label.Trim(), System.StringComparison.Ordinal)) continue;

                            string p = null;
                            try { p = PathOf(lt.transform, s.transform); } catch { }
                            if (IsUnderMigration(p)) { fail = "目标在 Migration 面板下，拒绝点击：path=" + p; return; }

                            cands.Add(p);
                            candScreens.Add(sname);

                            // ★ M6.86：从文字向上找**最近的可点击组件**，不再只认 KButton ——
                            //   诊断行 / 报告行 / 表格格子的文字是 LocText，可点击组件是父级上的 `MultiToggle`。
                            string ancKind = null;
                            UnityEngine.MonoBehaviour anc = null;
                            try { anc = UiRows.FindClickableAncestor(lt.transform, out ancKind); } catch { }
                            if (anc == null) continue;
                            bool vis = false;
                            try { vis = anc.gameObject != null && anc.gameObject.activeInHierarchy; } catch { }
                            if (!vis) continue;
                            if (!UiRows.InteractableOf(anc)) continue;

                            usableButtons.Add(anc);
                            usableKinds.Add(ancKind);
                            usablePaths.Add(p);
                            usableScreens.Add(sname);
                            usableScreenObjs.Add(s);
                        }
                    }

                    if (usableButtons.Count == 1)
                    {
                        target = usableButtons[0];
                        targetKind = usableKinds[0];
                        targetLabelPath = usablePaths[0];
                        targetScreen = usableScreens[0];
                        try { targetButtonPath = PathOf(target.transform, null); } catch { }
                        // ★ 0.93.3：`target.path` 必须与 `/v1/ui` 的 `clickables[].path` 同源 —
                        //   `Describe` 用的就是同一个 `UiQuery.PathOf(comp.transform, s.transform)`。
                        targetEl = UiRows.Describe(target, usableScreenObjs[0], usableScreens[0]);
                    }
                }
                catch (System.Exception e) { fail = e.GetType().Name + ": " + e.Message; }
            }, 3000, 30000, out okT, out busy);

            if (!ran) { status = 503; code = "RETRY_LATER"; message = "main thread busy；请 1 秒后重试"; return null; }
            if (fail != null) { status = 500; code = "UI_UNAVAILABLE"; message = fail; return null; }

            if (target == null)
            {
                if (cands.Count == 0)
                {
                    status = 404; code = "NOT_FOUND";
                    message = "没有文字恰等于 " + label + " 的 LocText（可先 GET /v1/ui?labels=1 看有什么）";
                    return null;
                }
                if (cands.Count == 1)
                {
                    status = 400; code = "NO_CLICKABLE_ANCESTOR";
                    message = "找到该文字，但从它向上找不到可点击组件（path=" + cands[0] + "）；该文字没有可点的父级控件";
                    return null;
                }
                status = 409; code = "AMBIGUOUS_TARGET";
                message = "有 " + cands.Count + " 个文字相同的 label，必须消歧（加 screen）";
                var sb0 = new StringBuilder(512);
                sb0.Append("{\"candidates\":[");
                for (int i = 0; i < cands.Count; i++)
                {
                    if (i > 0) sb0.Append(',');
                    sb0.Append("{\"index\":").Append(i);
                    sb0.Append(",\"labelPath\":").Append(Snap.Str(cands[i]));
                    sb0.Append(",\"screen\":").Append(Snap.Str(candScreens[i]));
                    sb0.Append('}');
                }
                sb0.Append("]}");
                LastDetailsHolder.Value = sb0.ToString();
                return null;
            }

            // ---- 真正点击（主线程，按组件类型分派，见 UiRows.Click）----
            string clickedVia = null;
            string clickErr = null;
            int okT2; bool busy2;
            MainThread.InvokeAdaptive(() =>
            {
                try { UiRows.Click(target, out clickedVia, out clickErr); }
                catch (System.Exception e) { clickErr = e.GetType().Name + ": " + e.Message; }
            }, 3000, 30000, out okT2, out busy2);

            if (clickedVia == null)
            {
                status = 500; code = "CLICK_FAILED";
                message = "点击失败（kind=" + (targetKind ?? "?") + "）：" + (clickErr ?? "未知");
                return null;
            }

            if (waitMs > 0) { try { System.Threading.Thread.Sleep(waitMs); } catch { } }

            var sb = new StringBuilder(512);
            sb.Append("{\"clicked\":true");
            sb.Append(",\"target\":");
            UiRows.AppendTargetJson(sb, targetEl);           // ★ 0.93.3：文字向上找到的那个组件
            sb.Append(",\"labelText\":").Append(Snap.Str(label));
            sb.Append(",\"labelPath\":").Append(Snap.Str(targetLabelPath));
            sb.Append(",\"buttonPath\":").Append(Snap.Str(targetButtonPath));
            sb.Append(",\"screen\":").Append(Snap.Str(targetScreen));
            sb.Append(",\"kind\":").Append(Snap.Str(targetKind));
            sb.Append(",\"clickedVia\":").Append(Snap.Str(clickedVia));
            sb.Append(",\"matched\":1,\"candidates\":1");
            sb.Append(",\"waitedMs\":").Append(waitMs);
            sb.Append(",\"method\":").Append(Snap.Str("LocText → 向上找最近可点击组件 → " + clickedVia));
            sb.Append(",\"note\":").Append(Snap.Str("已等待 " + waitMs + " ms 后返回；界面可能仍在切换，可再 GET /v1/ui 确认"));
            // ★ 0.93.3：点后回读（所有组件类型都有）。
            AppendReadback(sb, target, targetScreen);
            sb.Append('}');

            Log.Info("[ui/click] labelClick 已点：label=" + label + " kind=" + targetKind + " via=" + clickedVia
                     + " screen=" + targetScreen
                     + " buttonPath=" + (targetButtonPath ?? "<null>") + " waitedMs=" + waitMs);
            return sb.ToString();
        }

        /// <summary>把 transform 父链拼成可读路径（到 root 为止）。**只供定位，不用于点击**。</summary>
        static string PathOf(UnityEngine.Transform t, UnityEngine.Transform root)
        {
            try
            {
                var parts = new System.Collections.Generic.List<string>();
                var cur = t;
                int guard = 0;
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

        static string MatchStr(string body, string key)
        {
            if (string.IsNullOrEmpty(body)) return null;
            var m = System.Text.RegularExpressions.Regex.Match(body, "\"" + key + "\"\\s*:\\s*\"([^\"]*)\"");
            return m.Success ? m.Groups[1].Value : null;
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
