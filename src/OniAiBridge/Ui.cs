using System.Text;

// ⚠️ 不要 using System 之后直接用 Action/DateTime：Assembly-CSharp 里的全局 Action 枚举会遮蔽 System.Action。
namespace OniAiBridge
{
    /// <summary>
    /// M6.22：`GET /v1/ui` —— **只读**列出"当前激活的界面 + 界面上的按钮（本地化文字 + 可点状态）"。
    ///
    /// 来源（**都已过编译验证**；dump 报的字段名一律不采信，见 DESIGN §10.6ai）：
    /// * `KScreenManager.Instance`（Assembly-CSharp-firstpass，static public）
    /// * `KScreen.IsActive()`（public）、`KScreen.screenName`（public 属性）、`KScreen.displayName`（public 字段）
    /// * `KButton.isInteractable`（public 属性）、`KButton.gameObject`
    /// * `LocText.text`（public 属性 → **本地化后的文字**，不是 key）
    ///
    /// 口径：
    /// * **只列激活的 screen**（`scope` 里写明）；
    /// * 不可点的按钮**也列出来**，标 `interactable=false`；
    /// * 文字拿不到就给 `null` + 原因，**不用 GameObject 名字冒充**；
    /// * `KScreenManager` 拿不到 ⇒ 明确报错，**不返回空列表冒充"没有按钮"**。
    ///
    /// M6.86 起每个界面多一个 **`clickables`** 数组（`UiRows` 采集）：判定按接口
    /// （`IPointerClickHandler` / `ISubmitHandler`），所以**不是 KButton 的行**也在里面
    /// —— 诊断行 / 报告行 / 表格格子用的都是 `MultiToggle`（游戏源码逐类型核对见 §3.39.6）。
    /// 每条带 `kind`（组件类名）、`role`（`button`/`toggle`/`row`/`uguiButton`/`uguiToggle`/`pointerClick`）、
    /// `clickVia`（`/v1/ui/click` 会调用的那个入口）与 `path` / `index` / `rect`；
    /// `buttons` 数组保持旧口径（仅 `KButton`、字段不变），老调用方不受影响。
    ///
    /// 0.93.3：`clickables` 的默认输出上限 500 → 2000（实测 `ToolMenu` 724 个按钮被截断），
    /// 可用 `?clickablesLimit=N` 覆盖（上限 `ClickablesLimitMax`，非法值回落默认、不报错）。
    ///
    /// 0.93.4：`clickables` 的**默认输出模式改为 `compact`**（实测默认全量 1,714,519 字节，精简后 898,280 字节，
    /// 降约 48%）；需要完整字段时显式给 `?clickables=full`。两种模式共用同一份元素采集与同一条输出上限，
    /// 因此四个计数恒自洽：`clickablesEmitted = min(clickableTotal, clickablesLimit)`、
    /// `clickablesTruncated = clickableTotal > clickablesLimit`。本次生效的模式写在 `data` 顶层的
    /// `clickablesMode`（紧邻 `screens`）；每个界面对象里同名的一份自 0.93.3 起就有，保留且取值一致。
    /// </summary>
    internal static class UiQuery
    {
        /// <summary>
        /// 每个界面默认最多输出多少条 `clickables`（只截断输出，`index` 含义不受影响）。
        ///
        /// 0.93.3：500 → **2000**。实测 `ToolMenu` 有 724 个按钮，500 的上限把大界面截掉近三成，
        /// 调用方拿到的"全集"其实是半集。2000 与采集上限 `CollectLimit` 同量级，
        /// 覆盖实测过的大界面；更大的界面用请求参数 `?clickablesLimit=N` 覆盖。
        /// </summary>
        internal const int ClickablesLimit = 2000;

        /// <summary>
        /// 请求参数 `?clickablesLimit=N` 允许的上限（再大就按默认值处理，不报错）。
        /// 主要防"一个参数把响应撑到几十 MB"：单条约 0.3 KB，20000 条约 6 MB，已是可接受的上界。
        /// </summary>
        internal const int ClickablesLimitMax = 20000;

        /// <summary>
        /// 采集上限（`buttons` 要完整，所以采集比输出宽；`clickables` 再按生效上限截断）。
        /// 每个元素要算 path 与屏幕矩形，2000 条在主线程上是可接受的上界（典型界面远小于它）。
        /// 请求把输出上限调高时，采集上限跟着调高到同一值（否则"想输出更多"却采不到更多）。
        /// </summary>
        internal const int CollectLimit = 2000;

        /// <summary>
        /// 请求里的 `clickablesLimit` → 生效上限。非法值（非数字 / ≤0）与超范围（&gt; `ClickablesLimitMax`）
        /// **一律回落到默认值，不报错** —— 这是查询参数，调用方写错一个数字不该让整次读取失败。
        /// </summary>
        internal static int EffectiveClickablesLimit(int requested)
        {
            if (requested >= 1 && requested <= ClickablesLimitMax) return requested;
            return ClickablesLimit;
        }

        /// <summary>
        /// 请求参数 `clickables` 的取值 → 是否走单条精简输出（`true` = compact）。
        ///
        /// 取值规则（大小写不敏感）：
        /// * 缺省 / 空串 ⇒ **compact**（0.93.4 起的默认模式）；
        /// * `compact` ⇒ compact（与缺省一致，显式写与不写行为相同）；
        /// * `full`（等价写法 `all`）⇒ **full**，即 0.93.3 及以前默认的那套完整字段；
        /// * 其它任何取值（例如 `xyz`）⇒ 回落到默认 **compact**，**不报错** —— 这是查询参数，
        ///   写错一个值不该让整次读取失败（与 `clickablesLimit` 的处理口径一致）。
        /// </summary>
        internal static bool ParseClickablesMode(string value)
        {
            if (string.IsNullOrEmpty(value)) return true;                    // 缺省 ⇒ compact
            string v = value.Trim();
            if (v.Equals("full", System.StringComparison.OrdinalIgnoreCase)) return false;
            if (v.Equals("all", System.StringComparison.OrdinalIgnoreCase)) return false;   // `full` 的等价写法
            return true;                                                     // compact / 非法值 ⇒ 默认 compact
        }

        public static string List(bool withLabels, int labelsLimit, int clickablesLimitRequest, bool clickablesCompact,
                                  out int status, out string code, out string message)
        {
            status = 200; code = null; message = null;

            if (!MainThread.Ready)
            {
                status = 503; code = "NOT_READY"; message = "main-thread pump is not running yet";
                return null;
            }

            int cLimit = EffectiveClickablesLimit(clickablesLimitRequest);
            int collectLimit = cLimit > CollectLimit ? cLimit : CollectLimit;
            string cMode = clickablesCompact ? "compact" : "full";

            string data = null;
            string fail = null;
            int okTimeout; bool busy;

            bool ran = MainThread.InvokeAdaptive(() =>
            {
                try
                {
                    var mgr = KScreenManager.Instance;
                    if (mgr == null)
                    {
                        fail = "KScreenManager.Instance 为 null（UI 还没建，例如仍在加载/主菜单初始化中）";
                        return;
                    }

                    // 只找 KScreen 这一个类型（数量很少），不扫全场景
                    KScreen[] all = null;
                    try { all = UnityEngine.Object.FindObjectsOfType<KScreen>(); }
                    catch (System.Exception e) { fail = "FindObjectsOfType<KScreen> 失败：" + e.GetType().Name + ": " + e.Message; return; }

                    var labelSb = new StringBuilder(2048);
                    int labelCount = 0, labelEmitted = 0;
                    bool labelsTruncated = false;
                    var sb = new StringBuilder(2048);
                    sb.Append("{\"screens\":[");
                    int emittedScreens = 0, emittedButtons = 0;

                    if (all != null)
                    {
                        for (int i = 0; i < all.Length; i++)
                        {
                            var s = all[i];
                            if (s == null) continue;

                            bool active = false;
                            try { active = s.IsActive(); } catch { }
                            if (!active) continue;              // ★ 只列激活的

                            string title = null;
                            try { title = s.screenName; } catch { }
                            if (string.IsNullOrEmpty(title))
                            {
                                try { title = s.displayName; } catch { }
                            }
                            if (string.IsNullOrEmpty(title))
                            {
                                try { title = s.gameObject != null ? s.gameObject.name : null; } catch { }
                            }

                            if (emittedScreens > 0) sb.Append(',');
                            emittedScreens++;
                            sb.Append("{\"name\":").Append(Snap.Str(title));
                            sb.Append(",\"active\":true");
                            sb.Append(",\"buttons\":[");

                            // ★ M6.86：先一次性采集该界面下**全部**可点击元素（判定见 UiRows：
                            //   `IPointerClickHandler` / `ISubmitHandler`，因此 KButton 之外的「行」——诊断行 / 报告行 /
                            //   表格格子上的 `MultiToggle`——也在内），再分别输出两个数组：
                            //   * `buttons`：**保持旧口径**（只收 KButton，字段与 0.93.1 完全一致，老调用方不受影响）；
                            //   * `clickables`：全部可点击元素，逐条给出 `kind` / `role` / `clickVia`，即"这一行是可点的、
                            //     它的点击入口是什么"。
                            int clickTotal = 0;
                            var els = UiRows.Collect(s, collectLimit, out clickTotal);

                            // ---- buttons：只收 KButton（旧口径，字段不变）----
                            int bEmitted = 0;
                            for (int k = 0; k < els.Count; k++)
                            {
                                var e = els[k];
                                if (e.ButtonIndex < 0) continue;              // buttons 只收 KButton
                                if (bEmitted > 0) sb.Append(',');
                                bEmitted++;
                                emittedButtons++;
                                sb.Append("{\"text\":").Append(Snap.Str(e.Text));
                                if (e.TextSource != null) sb.Append(",\"textSource\":").Append(Snap.Str(e.TextSource));
                                if (e.Text == null && e.TextError != null) sb.Append(",\"textUnavailable\":").Append(Snap.Str(e.TextError));
                                sb.Append(",\"name\":").Append(Snap.Str(UiRows.NameOf(e.Go)));
                                sb.Append(",\"path\":").Append(Snap.Str(e.Path));
                                sb.Append(",\"index\":").Append(e.ButtonIndex);
                                if (e.Rect != null) sb.Append(",\"rect\":").Append(e.Rect);
                                sb.Append(",\"interactable\":").Append(e.Interactable ? "true" : "false");
                                sb.Append(",\"activeInHierarchy\":").Append(e.ActiveInHierarchy ? "true" : "false");
                                sb.Append('}');
                            }
                            sb.Append(']');

                            // ---- clickables：全部可点击元素（KButton / KToggle / MultiToggle 行 / UGUI 控件 / 自定义行类）----
                            //      只截断**输出条数**（生效上限），序号含义不受截断影响。
                            //      计数四件套必须自洽：emitted = min(total, limit)、truncated = total > limit。
                            //      0.93.3：默认上限 2000，可用 ?clickablesLimit=N 覆盖。
                            //      0.93.4：默认模式 = compact（每条只 kind/role/text/path）；?clickables=full 取全字段。
                            //      上限与模式正交：两种模式都按同一个 cLimit 截断。
                            int cEmitted = 0;
                            sb.Append(",\"clickables\":[");
                            for (int k = 0; k < els.Count && cEmitted < cLimit; k++)
                            {
                                if (cEmitted > 0) sb.Append(',');
                                cEmitted++;
                                if (clickablesCompact) UiRows.AppendJsonCompact(sb, els[k]);
                                else UiRows.AppendJson(sb, els[k]);
                            }
                            sb.Append(']');
                            sb.Append(",\"clickableTotal\":").Append(clickTotal);
                            sb.Append(",\"clickablesEmitted\":").Append(cEmitted);
                            sb.Append(",\"clickablesTruncated\":").Append(clickTotal > cLimit ? "true" : "false");
                            sb.Append(",\"clickablesLimit\":").Append(cLimit);
                            if (clickablesLimitRequest > 0 && clickablesLimitRequest != cLimit)
                                sb.Append(",\"clickablesLimitRequested\":").Append(clickablesLimitRequest);
                            // 每个界面内保留一份模式字段（0.93.3 起就在这一层，属既有契约，不删除）；
                            // 本次请求的权威值另有 data 顶层那一份，见该字段的注释。
                            sb.Append(",\"clickablesMode\":").Append(Snap.Str(cMode));

                            // ---- labels=1：把该界面里的**所有 LocText** 也列出来（用于定位"存档名"这类非按钮文字）----
                            if (withLabels)
                            {
                                LocText[] lts = null;
                                try { lts = s.GetComponentsInChildren<LocText>(true); } catch { }
                                if (lts != null)
                                {
                                    for (int li = 0; li < lts.Length; li++)
                                    {
                                        var lt = lts[li];
                                        if (lt == null) continue;
                                        string ltext = null;
                                        try { ltext = lt.text; } catch { }
                                        if (string.IsNullOrEmpty(ltext) || ltext.Trim().Length == 0) continue;   // ★ 空文字跳过
                                        labelCount++;
                                        if (labelEmitted >= labelsLimit) { labelsTruncated = true; continue; }

                                        string lpath = null;
                                        try { lpath = PathOf(lt.transform, s.transform); } catch { }
                                        if (labelEmitted > 0) labelSb.Append(",");
                                        labelEmitted++;
                                        labelSb.Append("{\"text\":").Append(Snap.Str(ltext));
                                        labelSb.Append(",\"path\":").Append(Snap.Str(lpath));
                                        labelSb.Append(",\"screen\":").Append(Snap.Str(title));
                                        labelSb.Append(",\"activeInHierarchy\":").Append((lt.gameObject != null && lt.gameObject.activeInHierarchy) ? "true" : "false");
                                        labelSb.Append("}");
                                    }
                                }
                            }
                            // ★ 闭合当前 screen 对象。M6.86 重排结构时漏写此花括号，
                            //   导致 clickablesLimit 之后直接跟下一个 screen 的 '{'，
                            //   手工拼出的 JSON 不合法（/v1/ui 恒 500 INVALID_JSON）。
                            sb.Append('}');
                        }
                    }

                    // ★ 0.93.4：本次请求生效的 clickables 输出模式，写在 data 顶层、紧邻 screens 数组。
                    //   0.93.3 只把 clickablesMode 写进了每个 screen 对象内部（在 clickablesLimit 之后），
                    //   真机从响应顶层读不到，故在顶层补一份；两处取值同源，恒一致。
                    sb.Append(']');                                       // 结束 screens 数组
                    sb.Append(",\"clickablesMode\":").Append(Snap.Str(cMode));
                    sb.Append(",\"count\":").Append(emittedScreens);
                    sb.Append(",\"buttonCount\":").Append(emittedButtons);
                    if (withLabels)
                    {
                        sb.Append(",\"labels\":[").Append(labelSb.ToString()).Append("]");
                        sb.Append(",\"labelCount\":").Append(labelCount);
                        sb.Append(",\"labelEmitted\":").Append(labelEmitted);
                        sb.Append(",\"labelsTruncated\":").Append(labelsTruncated ? "true" : "false");
                        sb.Append(",\"labelsLimit\":").Append(labelsLimit);
                        sb.Append(",\"labelsNote\":").Append(Snap.Str(
                            "path 仅供定位/理解，**不是点击目标**；空文字的 LocText 已跳过；只含激活界面里的 LocText"));
                    }
                    sb.Append(",\"scope\":").Append(Snap.Str("active-screens-only"));
                    sb.Append(",\"note\":").Append(Snap.Str(
                        "只列 IsActive()==true 的界面；按钮文字是 LocText 的本地化文本（不是 key）；"
                        + "不可点的按钮也会列出（interactable=false）。"
                        + "`clickables` 是**全部**可点击元素（含 KButton 之外的行：kind=MultiToggle、role=row），"
                        + "每条带 clickVia（/v1/ui/click 会调用的入口）；`buttons` 仍只含 KButton，index 为该数组内序号，"
                        + "clickables 条目的 buttonIndex 才对应 buttons 的 index。"
                        + "clickables 默认每个界面最多输出 2000 条（?clickablesLimit=N 可覆盖，上限 20000，非法值回落默认）；"
                        + "计数四件套自洽：clickablesEmitted=min(clickableTotal, clickablesLimit)、"
                        + "clickablesTruncated=clickableTotal>clickablesLimit；"
                        + "clickables 默认模式为 compact（每条只输出 kind/role/text/path），"
                        + "?clickables=full 取回全字段（limit 与模式正交）；"
                        + "本次模式见 data 顶层的 clickablesMode（每个 screen 内另有一份同值副本）"));
                    sb.Append('}');
                    data = sb.ToString();
                }
                catch (System.Exception e) { fail = e.GetType().Name + ": " + e.Message; }
            }, 3000, 30000, out okTimeout, out busy);

            if (!ran)
            {
                status = 503; code = "RETRY_LATER"; message = "main thread busy；请 1 秒后重试";
                return null;
            }
            if (fail != null)
            {
                status = 500; code = "UI_UNAVAILABLE"; message = fail;
                Log.Info("[ui] 读取失败: " + fail);
                return null;
            }
            if (data == null)
            {
                status = 500; code = "INTERNAL"; message = "UI 读取返回空";
                return null;
            }

            // 手工拼的 JSON 下发前自检（JsonCheck 的硬规矩）：M6.86 重排过结构，坏 JSON 不许静默发给调用方。
            try { JsonCheck.Validate(data, "ui"); }
            catch (System.Exception e)
            {
                status = 500; code = "INVALID_JSON";
                message = "生成的 JSON 未通过自检：" + e.Message;
                return null;
            }

            Log.Info("[ui] 已采集（只读）");
            return data;
        }

        /// <summary>把 transform 父链拼成可读路径（到 screen 根为止，含根名）。**只供定位，不用于点击**。</summary>
        internal static string PathOf(UnityEngine.Transform t, UnityEngine.Transform root)
        {
            try
            {
                var parts = new System.Collections.Generic.List<string>();
                var cur = t;
                int guard = 0;
                while (cur != null && guard++ < 32)
                {
                    // ★ M6.85：同名兄弟加序号（`ButtonTemplate#4`）—— 主菜单 10 个菜单项是同一预制体克隆，
                    //   只靠 name 无法区分 ⇒ path 必须唯一，否则 click 永远 409。
                    string nm = cur.name;
                    try
                    {
                        var par = cur.parent;
                        if (par != null)
                        {
                            int same = 0;
                            for (int i = 0; i < par.childCount; i++)
                            {
                                var ch = par.GetChild(i);
                                if (ch != null && ch.name == nm)
                                {
                                    if (ch == cur) break;
                                    same++;
                                }
                            }
                            int total = 0;
                            for (int i = 0; i < par.childCount; i++)
                            {
                                var ch = par.GetChild(i);
                                if (ch != null && ch.name == nm) total++;
                            }
                            if (total > 1) nm = nm + "#" + same;
                        }
                    }
                    catch { }
                    parts.Add(nm);
                    if (root != null && cur == root) break;
                    cur = cur.parent;
                }


                parts.Reverse();
                return string.Join("/", parts.ToArray());
            }
            catch { return null; }
        }
        /// <summary>
        /// RectTransform 四角 → **屏幕坐标**（pixels，左下原点）(min,max)，给调用方一个可点击区域
        /// （`UiRows` 也用同一份）。
        ///
        /// ★ 0.111.0 修：原先传 `Camera.main`，对 **Screen Space - Overlay** 的 Canvas 是错的 ——
        ///   Overlay 画布不经过任何相机，`WorldToScreenPoint(Camera.main, ...)` 会用世界相机的投影矩阵，
        ///   得到 `[175739.1, -1950.0, 179330.1, 1641.0]` 这种"看着像坐标、其实完全不在屏幕上"的值
        ///   （实测：沙盒工具条 `SandboxActionToggle#6`）。调用方据此点击必然点空 —— 这就是
        ///   "路径/控件都对、点了没反应"的一个真实成因。
        ///   正确做法：Overlay 传 `null`；`Screen Space - Camera` / `World Space` 才传该 Canvas 的 `worldCamera`。
        /// </summary>
        internal static bool ScreenRectOf(UnityEngine.RectTransform rt, out UnityEngine.Vector2 min, out UnityEngine.Vector2 max)
        {
            min = default(UnityEngine.Vector2); max = default(UnityEngine.Vector2);
            try
            {
                var corners = new UnityEngine.Vector3[4];
                rt.GetWorldCorners(corners);

                UnityEngine.Camera cam = null;
                try
                {
                    var canvas = rt.GetComponentInParent<UnityEngine.Canvas>();
                    if (canvas != null && canvas.renderMode != UnityEngine.RenderMode.ScreenSpaceOverlay)
                        cam = canvas.worldCamera;
                }
                catch { }

                UnityEngine.Vector2 p0 = UnityEngine.RectTransformUtility.WorldToScreenPoint(cam, corners[0]);
                UnityEngine.Vector2 p2 = UnityEngine.RectTransformUtility.WorldToScreenPoint(cam, corners[2]);
                min = new UnityEngine.Vector2(UnityEngine.Mathf.Min(p0.x, p2.x), UnityEngine.Mathf.Min(p0.y, p2.y));
                max = new UnityEngine.Vector2(UnityEngine.Mathf.Max(p0.x, p2.x), UnityEngine.Mathf.Max(p0.y, p2.y));
                return true;
            }
            catch { return false; }
        }
    }
}
