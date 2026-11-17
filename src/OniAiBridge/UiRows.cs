using System.Text;

// ⚠️ 不要 using System 之后直接用 Action/DateTime：Assembly-CSharp 里的全局 Action 枚举会遮蔽 System.Action。
namespace OniAiBridge
{
    /// <summary>
    /// M6.86：`/v1/ui` 与 `/v1/ui/click` 共用的「可点击元素」判定 / 采集 / 点击分派。
    ///
    /// 为什么不再只认 `KButton`（游戏源码逐类型核对，见 `docs/API.md` §3.39.6）：
    /// * `ColonyDiagnosticScreen.DiagnosticRow` 的行对象上挂的是 **`MultiToggle`**
    ///   —— `button = gameObject.GetComponent<MultiToggle>()`，点击回调用 `Delegate.Combine` 挂到
    ///   `MultiToggle.onClick`，回调体做 `SelectTool.Instance.SelectAndFocus` / `ActiveWorldStarWipe`；
    /// * `AllDiagnosticsScreen.SpawnRow` 的每一行同理：`HierarchyReferences` 里的
    ///   `MainToggle` / `SubrowToggle` / `PinToggle` 全是 `MultiToggle`；
    /// * `ReportScreenEntryRow` 的行也是 `MultiToggle`（`public MultiToggle toggle`）；
    /// * `TableScreen` 的复选 / 优先级格子同样是 `MultiToggle`（`widget_go.GetComponent<MultiToggle>()`）。
    /// ⇒ 这类「行」不在 `KButton` 集合里：`/v1/ui` 只列 `KButton` 时它们根本不出现，
    ///   点击路径也只认 `KButton`，于是表现为「这一行点不到」。
    ///
    /// 判定口径：**按接口而不是按类名** —— 凡是实现了 `IPointerClickHandler` / `ISubmitHandler` 的
    /// `MonoBehaviour` 都算可点击元素。`KButton` / `KToggle` / `MultiToggle` / UGUI `Button` / `Toggle` /
    /// 任意自定义行类都被同一条判据覆盖，不需要知道类名。
    /// 同一 `GameObject` 上挂多个可点击组件时按优先级只保留一个（`KButton` > `KToggle` > `MultiToggle` >
    /// UGUI `Button` > UGUI `Toggle` > 其它），避免同一行在输出里重复出现、点击时又互相抢。
    ///
    /// 点击入口按类型分派（**不写死一种**）：
    /// | 组件 | 入口 | 说明 |
    /// |---|---|---|
    /// | `KButton` | `SignalClick(KKeyCode)` | 自带 `interactable` 门槛，游戏自己走的就是这条 |
    /// | `KToggle` | `Click()` | `KToggle` 的公开入口（含 `KInputManager.isFocused` 门槛） |
    /// | `MultiToggle` | `OnPointerClick(PointerEventData)` | 虚方法，保留子类覆写与 `AllowClick` 门槛 |
    /// | UGUI `Button` | `onClick.Invoke()` | 与 `UiShot.Click` 既有行为一致 |
    /// | UGUI `Toggle` | `OnPointerClick(PointerEventData)` | UGUI 自带的翻状态语义 |
    /// | 其它 `IPointerClickHandler` | `ExecuteEvents.Execute(...)` | 兜底，不认识的类也点得到 |
    ///
    /// 注意：`MultiToggle.OnPointerClick` 的 IL（已核对）**只做 `AllowClick` 门槛 + `onClick.Invoke()`**，
    /// **不会**自动调用 `NextState()`（那是 UGUI `Toggle` 的语义，见 `KToggle`）。
    /// 因此对「行」点击一次等于触发该行挂的回调，不会把行状态翻掉。
    /// </summary>
    internal static class UiRows
    {
        /// <summary>一个可点击元素在 `/v1/ui` 里的完整描述（两个端点共用同一份对象）。</summary>
        internal sealed class Element
        {
            public UnityEngine.MonoBehaviour Comp;
            public UnityEngine.GameObject Go;

            public string Kind;          // 组件类名：KButton / KToggle / MultiToggle / Button / Toggle / 自定义
            public string Role;          // 归一化种类：button / toggle / row / uguiButton / uguiToggle / pointerClick
            public string ClickVia;      // 点击入口（与 Click() 的实际分派一致）
            public string Screen;        // 所属 KScreen 名

            public string Text;          // 三级回退取到的文字（LocText → UGUI Text → GameObject.name）
            public string TextSource;    // LocText / UIText / GameObjectName
            public string TextError;     // 三级都取不到时的原因

            public string Path;          // 相对 KScreen 的父级链（与 UiQuery.PathOf 同源，同名兄弟带 #n）
            public string Rect;          // 屏幕坐标 [x0,y0,x1,y1]

            public int Index = -1;       // 在 clickables 里的 0-based 序号
            public int ButtonIndex = -1; // 在 KButton 列表（/v1/ui 的 buttons）里的 0-based 序号；非 KButton 为 -1

            public bool ActiveInHierarchy;
            public bool Interactable;

            public int State = -1;       // MultiToggle.CurrentState
            public int StateCount = -1;  // MultiToggle.states.Length
            public bool HasOnClick;      // MultiToggle.onClick 是否挂了回调
            public bool HasIsOn;         // 该元素有 isOn 语义（KToggle）
            public bool IsOn;            // KToggle.isOn
        }

        // ---------------- 判定 ----------------

        /// <summary>按接口判定是否"能接收鼠标点击"，不依赖类名。</summary>
        internal static bool IsClickable(UnityEngine.MonoBehaviour c)
        {
            if (c == null) return false;
            try
            {
                return (c is UnityEngine.EventSystems.IPointerClickHandler)
                    || (c is UnityEngine.EventSystems.ISubmitHandler);
            }
            catch { return false; }
        }

        /// <summary>同一 GameObject 上多个可点击组件时决定留哪个（数值大者优先）。</summary>
        static int PriorityOf(UnityEngine.MonoBehaviour c)
        {
            if (c == null) return -1;
            if (c is KButton) return 50;
            if (c is KToggle) return 40;              // 必须先于 UGUI Toggle（KToggle : Toggle）
            if (c is MultiToggle) return 30;
            if (c is UnityEngine.UI.Button) return 20;
            if (c is UnityEngine.UI.Toggle) return 18;
            if (c is UnityEngine.UI.Selectable) return 15;
            return 10;
        }

        internal static string KindOf(UnityEngine.MonoBehaviour c)
        {
            try { return c != null ? c.GetType().Name : null; } catch { return null; }
        }

        /// <summary>归一化成少数几个种类，供调用方按种类筛选 / 分派。</summary>
        internal static string RoleOf(UnityEngine.MonoBehaviour c)
        {
            if (c == null) return null;
            if (c is KButton) return "button";
            if (c is KToggle) return "toggle";
            if (c is MultiToggle) return "row";
            if (c is UnityEngine.UI.Button) return "uguiButton";
            if (c is UnityEngine.UI.Toggle) return "uguiToggle";
            return "pointerClick";
        }

        /// <summary>该元素会被用哪个入口点击；与实际 `Click()` 的分派必须一致。</summary>
        internal static string ClickViaOf(UnityEngine.MonoBehaviour c)
        {
            if (c == null) return null;
            if (c is KToggle) return "KToggle.Click()";
            if (c is KButton) return "KButton.SignalClick(KKeyCode.Mouse0)";
            if (c is MultiToggle) return "MultiToggle.OnPointerClick(PointerEventData)";
            if (c is UnityEngine.UI.Button) return "UnityEngine.UI.Button.onClick.Invoke()";
            if (c is UnityEngine.UI.Toggle) return "UnityEngine.UI.Toggle.OnPointerClick(PointerEventData)";
            if (IsClickable(c)) return "ExecuteEvents.Execute<IPointerClickHandler>";
            return null;
        }

        /// <summary>
        /// 可交互判定（严格按各自类型能读到的字段，读不到不冒充）：
        /// `KButton.isInteractable` / `Selectable.IsInteractable()` / `MultiToggle` 用 `enabled &amp;&amp; activeInHierarchy`
        /// （`MultiToggle` 本身没有 interactable 概念，`AllowClick` 只过滤右键）。
        /// </summary>
        internal static bool InteractableOf(UnityEngine.MonoBehaviour c)
        {
            if (c == null) return false;
            try
            {
                var kb = c as KButton;
                if (kb != null) return kb.isInteractable;

                var kt = c as KToggle;
                if (kt != null) return kt.IsInteractable();

                var mt = c as MultiToggle;
                if (mt != null) return mt.enabled && mt.gameObject != null && mt.gameObject.activeInHierarchy;

                var sel = c as UnityEngine.UI.Selectable;
                if (sel != null) return sel.IsInteractable();

                return c.enabled && c.gameObject != null && c.gameObject.activeInHierarchy;
            }
            catch { return false; }
        }

        /// <summary>名字三级回退（取到就停）：递归 LocText → 递归 UGUI Text → GameObject.name。</summary>
        internal static string TextOf(UnityEngine.MonoBehaviour c, out string src, out string err)
        {
            src = null; err = null;
            if (c == null) { err = "组件为 null"; return null; }
            try
            {
                var lt = c.GetComponentInChildren<LocText>(true);
                if (lt != null && !string.IsNullOrEmpty(lt.text)) { src = "LocText"; return lt.text; }
            }
            catch (System.Exception ex1) { err = "LocText: " + ex1.GetType().Name; }
            try
            {
                var ut = c.GetComponentInChildren<UnityEngine.UI.Text>(true);
                if (ut != null && !string.IsNullOrEmpty(ut.text)) { src = "UIText"; return ut.text; }
            }
            catch { }
            try
            {
                string gn = c.gameObject != null ? c.gameObject.name : null;
                if (!string.IsNullOrEmpty(gn)) { src = "GameObjectName"; return gn; }
            }
            catch { }
            err = err ?? "没有 LocText / UIText，连 GameObject.name 也是空";
            return null;
        }

        /// <summary>KScreen 的屏幕名：`screenName` → `displayName` → `GameObject.name`（与 `/v1/ui` 同口径）。</summary>
        internal static string ScreenNameOf(KScreen s)
        {
            if (s == null) return null;
            string n = null;
            try { n = s.screenName; } catch { }
            if (string.IsNullOrEmpty(n)) { try { n = s.displayName; } catch { } }
            if (string.IsNullOrEmpty(n)) { try { n = s.gameObject != null ? s.gameObject.name : null; } catch { } }
            return n;
        }

        // ---------------- 采集 ----------------

        /// <summary>
        /// 采集某个 KScreen 下的全部可点击元素。
        /// `limit` 只限制**构造出来**的元素个数（防大屏把响应撑爆），不影响 `Index` / `ButtonIndex` 的推进 ——
        /// 因此截断前后序号含义不变，`total` 始终是去重后的真实总数。
        /// </summary>
        internal static System.Collections.Generic.List<Element> Collect(KScreen s, int limit, out int total)
        {
            var list = new System.Collections.Generic.List<Element>();
            total = 0;
            if (s == null) return list;
            if (limit <= 0) limit = 400;

            UnityEngine.MonoBehaviour[] comps = null;
            try { comps = s.GetComponentsInChildren<UnityEngine.MonoBehaviour>(true); } catch { }
            if (comps == null) return list;

            var byGo = new System.Collections.Generic.Dictionary<UnityEngine.GameObject, UnityEngine.MonoBehaviour>();
            var order = new System.Collections.Generic.List<UnityEngine.GameObject>();
            for (int i = 0; i < comps.Length; i++)
            {
                var c = comps[i];
                if (!IsClickable(c)) continue;
                var go = c.gameObject;
                if (go == null) continue;
                UnityEngine.MonoBehaviour have;
                if (!byGo.TryGetValue(go, out have)) { byGo[go] = c; order.Add(go); }
                else if (PriorityOf(c) > PriorityOf(have)) byGo[go] = c;
            }
            total = order.Count;      // 去重后的真实总数（同一 GameObject 上多个可点击组件只算一个）

            string screenName = ScreenNameOf(s);
            int elementCount = 0, buttonCount = 0;
            for (int i = 0; i < order.Count; i++)
            {
                var go = order[i];
                UnityEngine.MonoBehaviour comp;
                if (!byGo.TryGetValue(go, out comp) || comp == null) continue;

                bool isButton = comp is KButton;
                int buttonIndex = isButton ? buttonCount : -1;
                if (isButton) buttonCount++;

                int elementIndex = elementCount;
                elementCount++;

                if (list.Count >= limit) continue;      // 序号已推进，只是不再构造对象

                var e = new Element();
                e.Comp = comp;
                e.Go = go;
                e.Kind = KindOf(comp);
                e.Role = RoleOf(comp);
                e.ClickVia = ClickViaOf(comp);
                e.Screen = screenName;
                e.Text = TextOf(comp, out e.TextSource, out e.TextError);
                try { e.Path = UiQuery.PathOf(comp.transform, s.transform); } catch { }
                try
                {
                    var rt = comp.transform as UnityEngine.RectTransform;
                    UnityEngine.Vector2 mn, mx;
                    if (rt != null && UiQuery.ScreenRectOf(rt, out mn, out mx))
                        e.Rect = "[" + F(mn.x) + "," + F(mn.y) + "," + F(mx.x) + "," + F(mx.y) + "]";
                }
                catch { }
                e.Index = elementIndex;
                e.ButtonIndex = buttonIndex;
                try { e.ActiveInHierarchy = go.activeInHierarchy; } catch { }
                e.Interactable = InteractableOf(comp);

                var mt = comp as MultiToggle;
                if (mt != null)
                {
                    try { e.State = mt.CurrentState; } catch { }
                    try { e.StateCount = mt.states != null ? mt.states.Length : -1; } catch { }
                    try { e.HasOnClick = mt.onClick != null; } catch { }
                }
                var kt = comp as KToggle;
                if (kt != null)
                {
                    e.HasIsOn = true;
                    try { e.IsOn = kt.isOn; } catch { }
                }

                list.Add(e);
            }
            return list;
        }

        /// <summary>
        /// 为**单个组件**临时构造一份元素描述，口径与 `Collect` 完全一致
        /// （`path` 走同一个 `UiQuery.PathOf(comp.transform, s.transform)`，`role` / `clickVia` / `interactable`
        /// 走同一套判定）。
        ///
        /// 用途：`labelClick` 是"先找到文字、再向上找最近的可点击组件"，那条路径没有经过 `Collect`，
        /// 但响应里的 `target.path` 必须与 `/v1/ui` 的 `clickables[].path` 是同一份字符串
        /// （否则"列出来的 path"和"点到的元素"会漂移）。此方法给的就是同一份拼法。
        /// 因为没经过 `Collect`，`Index` / `ButtonIndex` 保持 -1（序号不可凭空算出，如实留空而不是猜）。
        /// </summary>
        internal static Element Describe(UnityEngine.MonoBehaviour comp, KScreen s, string screenName)
        {
            if (comp == null) return null;
            var e = new Element();
            try
            {
                e.Comp = comp;
                try { e.Go = comp.gameObject; } catch { }
                e.Kind = KindOf(comp);
                e.Role = RoleOf(comp);
                e.ClickVia = ClickViaOf(comp);
                string nm = screenName;
                if (string.IsNullOrEmpty(nm)) { try { nm = ScreenNameOf(s); } catch { } }
                e.Screen = nm;
                e.Text = TextOf(comp, out e.TextSource, out e.TextError);
                try { e.Path = UiQuery.PathOf(comp.transform, s != null ? s.transform : null); } catch { }
                try { e.ActiveInHierarchy = e.Go != null && e.Go.activeInHierarchy; } catch { }
                e.Interactable = InteractableOf(comp);

                var mt = comp as MultiToggle;
                if (mt != null)
                {
                    try { e.State = mt.CurrentState; } catch { }
                    try { e.StateCount = mt.states != null ? mt.states.Length : -1; } catch { }
                    try { e.HasOnClick = mt.onClick != null; } catch { }
                }
                var kt = comp as KToggle;
                if (kt != null)
                {
                    e.HasIsOn = true;
                    try { e.IsOn = kt.isOn; } catch { }
                }
            }
            catch { }
            return e;
        }

        // ---------------- 输出 ----------------

        /// <summary>把一个元素写成 JSON（字段顺序固定，便于 diff）。</summary>
        internal static void AppendJson(StringBuilder sb, Element e)
        {
            if (e == null || sb == null) return;
            sb.Append("{\"kind\":").Append(Snap.Str(e.Kind));
            sb.Append(",\"role\":").Append(Snap.Str(e.Role));
            sb.Append(",\"text\":").Append(Snap.Str(e.Text));
            if (e.TextSource != null) sb.Append(",\"textSource\":").Append(Snap.Str(e.TextSource));
            if (e.Text == null && e.TextError != null) sb.Append(",\"textUnavailable\":").Append(Snap.Str(e.TextError));
            sb.Append(",\"name\":").Append(Snap.Str(NameOf(e.Go)));
            sb.Append(",\"path\":").Append(Snap.Str(e.Path));
            sb.Append(",\"index\":").Append(e.Index);
            if (e.ButtonIndex >= 0) sb.Append(",\"buttonIndex\":").Append(e.ButtonIndex);
            if (e.Rect != null) sb.Append(",\"rect\":").Append(e.Rect);
            sb.Append(",\"clickable\":").Append(e.ClickVia != null ? "true" : "false");
            sb.Append(",\"clickVia\":").Append(Snap.Str(e.ClickVia));
            sb.Append(",\"interactable\":").Append(e.Interactable ? "true" : "false");
            sb.Append(",\"activeInHierarchy\":").Append(e.ActiveInHierarchy ? "true" : "false");
            if (e.State >= 0) sb.Append(",\"state\":").Append(e.State);
            if (e.StateCount >= 0) sb.Append(",\"stateCount\":").Append(e.StateCount);
            if (e.State >= 0) sb.Append(",\"hasOnClick\":").Append(e.HasOnClick ? "true" : "false");
            if (e.HasIsOn) sb.Append(",\"isOn\":").Append(e.IsOn ? "true" : "false");
            sb.Append('}');
        }

        /// <summary>
        /// 单条精简模式（0.93.4 起为 `GET /v1/ui` 的**默认**输出；亦可显式写作 `?clickables=compact`）：
        /// 只写 `kind` / `role` / `text` / `path` 四个字段，字段名与全量模式完全一致，不新增也不删除。
        ///
        /// 存在意义：大界面（例如 `ToolMenu` 一千五百余个可点击元素）在全量输出时体积可观
        /// （0.93.3 真机默认全量 1,714,519 字节），而"定位一个元素"只需要这四项
        /// （同一响应精简后 898,280 字节）。需要 `index` / `rect` / `clickVia` 等完整字段时，
        /// 请求显式加 `?clickables=full` 即可取回全量输出；两种模式共享同一份采集结果与同一条输出上限。
        /// </summary>
        internal static void AppendJsonCompact(StringBuilder sb, Element e)
        {
            if (e == null || sb == null) return;
            sb.Append("{\"kind\":").Append(Snap.Str(e.Kind));
            sb.Append(",\"role\":").Append(Snap.Str(e.Role));
            sb.Append(",\"text\":").Append(Snap.Str(e.Text));
            sb.Append(",\"path\":").Append(Snap.Str(e.Path));
            sb.Append('}');
        }

        /// <summary>
        /// 写"这次真正点到的元素"（点击响应的 `target` 对象）。字段与 `clickables[]` 同源：
        /// `path` 来自同一个 `UiQuery.PathOf`，`kind` / `role` / `clickVia` 来自同一套判定，
        /// `elementIndex` 就是该元素在所属界面 `clickables` 数组里的序号。
        ///
        /// 定位路径不同、来源不同（`index` / `path` / `text` / `labelClick`），但 `target` 的写法只有这一份，
        /// 调用方不需要按定位方式猜字段。序号取不到时（`labelClick` 由文字向上找组件，未经过 `clickables` 采集）
        /// 写 `elementIndex:-1` 并附 `elementIndexUnavailable` 说明原因，不留无解释的空值。
        /// </summary>
        internal static void AppendTargetJson(StringBuilder sb, Element e)
        {
            if (sb == null) return;
            if (e == null) { sb.Append("null"); return; }

            string pathSource, pathUnavailable;
            string path = PathForTarget(e, out pathSource, out pathUnavailable);

            sb.Append("{\"path\":").Append(Snap.Str(path));
            if (pathSource != null) sb.Append(",\"pathSource\":").Append(Snap.Str(pathSource));
            if (pathUnavailable != null) sb.Append(",\"pathUnavailable\":").Append(Snap.Str(pathUnavailable));
            sb.Append(",\"kind\":").Append(Snap.Str(e.Kind));
            sb.Append(",\"role\":").Append(Snap.Str(e.Role));
            sb.Append(",\"text\":").Append(Snap.Str(e.Text));
            sb.Append(",\"name\":").Append(Snap.Str(NameOf(e.Go)));
            sb.Append(",\"screen\":").Append(Snap.Str(e.Screen));
            sb.Append(",\"elementIndex\":").Append(e.Index);
            if (e.Index < 0)
                sb.Append(",\"elementIndexUnavailable\":").Append(Snap.Str(
                    "该元素由 labelClick 从文字向上查找得到，未经过 clickables 采集，因此没有 clickables 序号"));
            if (e.ButtonIndex >= 0) sb.Append(",\"buttonIndex\":").Append(e.ButtonIndex);
            sb.Append(",\"clickVia\":").Append(Snap.Str(e.ClickVia));
            sb.Append(",\"interactable\":").Append(e.Interactable ? "true" : "false");
            sb.Append(",\"activeInHierarchy\":").Append(e.ActiveInHierarchy ? "true" : "false");
            sb.Append('}');
        }

        /// <summary>
        /// 目标元素的 `path`：正常就是 `clickables[].path`（同一个 `UiQuery.PathOf`）。
        /// 只有拼父链时抛异常才会是空 —— 那种情况下退一步给"整条场景父链"这个真实值并标明
        /// `pathSource:"fullSceneFallback"`；连父链都拼不出来才给 `pathUnavailable` 说明原因。
        /// **任何情况下都不返回"没有解释的 null"**。
        /// </summary>
        internal static string PathForTarget(Element e, out string pathSource, out string pathUnavailable)
        {
            pathSource = null; pathUnavailable = null;
            if (e == null) { pathUnavailable = "目标元素为空，没有可拼的父链"; return null; }
            string path = e.Path;
            if (string.IsNullOrEmpty(path))
            {
                try { path = UiQuery.PathOf(e.Comp.transform, null); } catch { }
                if (!string.IsNullOrEmpty(path)) pathSource = "fullSceneFallback";
                else pathUnavailable = "拼不出父链路径（组件已失效或父链超过 32 层）";
            }
            return path;
        }

        /// <summary>
        /// 组件是否仍然存活（Unity 的销毁语义：已销毁对象与 `null` 比较为 true，直接读它的
        /// `transform` 会抛 `MissingReferenceException`）。回读之前必须先过这一关。
        /// </summary>
        internal static bool Alive(UnityEngine.MonoBehaviour comp)
        {
            try { return comp != null; } catch { return false; }
        }

        /// <summary>组件所属 KScreen 的名字（向上找最近的 KScreen，名字走 `ScreenNameOf` 的三级回退）。</summary>
        internal static string ScreenNameOfComponent(UnityEngine.MonoBehaviour comp)
        {
            try
            {
                var t = comp != null ? comp.transform : null;
                int guard = 0;
                while (t != null && guard++ < 32)
                {
                    var s = t.GetComponent<KScreen>();
                    if (s != null) return ScreenNameOf(s);
                    t = t.parent;
                }
            }
            catch { }
            return null;
        }

        /// <summary>
        /// 当前仍处于激活状态的界面名（`KScreen.IsActive()`，最多 `max` 条，口径与 `/v1/ui` 的
        /// `screens[].name` 一致）。用于"点完这个按钮当前界面被关掉了"时给出点击后的真实界面。
        /// </summary>
        internal static System.Collections.Generic.List<string> ActiveScreenNames(int max)
        {
            var list = new System.Collections.Generic.List<string>();
            if (max <= 0) max = 8;
            try
            {
                var all = UnityEngine.Object.FindObjectsOfType<KScreen>();
                if (all == null) return list;
                for (int i = 0; i < all.Length && list.Count < max; i++)
                {
                    var s = all[i];
                    if (s == null) continue;
                    bool active = false;
                    try { active = s.IsActive(); } catch { }
                    if (!active) continue;
                    string n = ScreenNameOf(s);
                    if (!string.IsNullOrEmpty(n)) list.Add(n);
                }
            }
            catch { }
            return list;
        }

        /// <summary>
        /// 写点击回读 `readback`（**必须在主线程调用**：要读 Unity 对象）。
        ///
        /// 三个必答项：`screenBefore`（点前所属界面）、`screenAfter`（点后界面）、`interactableAfter`（点后可交互）。
        /// 另外两项帮助阅读：
        /// * `screenAfterSource`：`screenAfter` 的出处 —— `target`（目标组件仍存活，读它所属的 KScreen）、
        ///   `activeScreenAfterClick`（目标已随界面关闭/销毁，退一步报点击后仍激活的界面）、
        ///   `targetBeforeClick`（本次没有真点，例如 `dryRun`）；
        /// * `activeScreensAfter`：点击后仍激活的界面名列表（最多 8 条）。
        ///
        /// 取不到的值一律附 `xxxUnavailable` 说明，**不留无解释的 null**。
        /// `afterClick:false` 用于 `dryRun`：回读与点击前同值，并标明"未点击"而不是"没读到"。
        /// </summary>
        internal static void AppendReadbackJson(StringBuilder sb, UnityEngine.MonoBehaviour comp,
                                               string screenBefore, bool afterClick)
        {
            if (sb == null) return;

            bool alive = Alive(comp);
            string screenAfter = null, screenAfterSource = null, screenAfterUnavailable = null;
            string screenBeforeUnavailable = null;
            bool interactableAfter = false;
            string interactableAfterUnavailable = null;

            if (string.IsNullOrEmpty(screenBefore))
                screenBeforeUnavailable = "点击前取不到目标所属界面名（KScreen 的 screenName / displayName / GameObject.name 三级回退都为空）";

            var activeAfter = ActiveScreenNames(8);

            if (!afterClick)
            {
                // dryRun：没有真点。screenAfter 就等于点击前的界面，明说是"未点击"而不是"没读到"。
                if (!string.IsNullOrEmpty(screenBefore))
                {
                    screenAfter = screenBefore;
                    screenAfterSource = "targetBeforeClick";
                }
                if (alive) interactableAfter = InteractableOf(comp);
                else interactableAfterUnavailable = "目标组件当前已不可访问（已被销毁或未激活），无法回读 interactable";
            }
            else
            {
                if (alive)
                {
                    try { screenAfter = ScreenNameOfComponent(comp); } catch { }
                    if (!string.IsNullOrEmpty(screenAfter)) screenAfterSource = "target";
                    interactableAfter = InteractableOf(comp);
                }
                else
                {
                    interactableAfterUnavailable = "目标组件在点击后已被销毁或关闭，无法回读 interactable";
                }
                // 点完界面被关掉（主菜单点「新游戏」、诊断界面点「关闭」）时，目标所属界面已经没了 ⇒
                // 退一步报"点击后仍激活的界面"，这比返回 null 有用，也比假装还是原界面诚实。
                if (string.IsNullOrEmpty(screenAfter) && activeAfter.Count > 0)
                {
                    screenAfter = activeAfter[0];
                    screenAfterSource = "activeScreenAfterClick";
                }
                if (string.IsNullOrEmpty(screenAfter))
                    screenAfterUnavailable = "点击后既读不到目标所属界面（目标已销毁），也没有任何 KScreen 处于激活状态";
            }

            sb.Append(",\"readback\":{\"screenBefore\":").Append(Snap.Str(screenBefore));
            if (screenBeforeUnavailable != null)
                sb.Append(",\"screenBeforeUnavailable\":").Append(Snap.Str(screenBeforeUnavailable));
            sb.Append(",\"screenAfter\":").Append(Snap.Str(screenAfter));
            if (screenAfterSource != null)
                sb.Append(",\"screenAfterSource\":").Append(Snap.Str(screenAfterSource));
            if (screenAfterUnavailable != null)
                sb.Append(",\"screenAfterUnavailable\":").Append(Snap.Str(screenAfterUnavailable));
            sb.Append(",\"interactableAfter\":").Append(interactableAfter ? "true" : "false");
            if (interactableAfterUnavailable != null)
                sb.Append(",\"interactableAfterUnavailable\":").Append(Snap.Str(interactableAfterUnavailable));
            sb.Append(",\"activeScreensAfter\":[");
            for (int i = 0; i < activeAfter.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(Snap.Str(activeAfter[i]));
            }
            sb.Append(']');
            sb.Append(",\"clickedDuringReadback\":").Append(afterClick ? "true" : "false");
            sb.Append('}');
        }

        internal static string NameOf(UnityEngine.GameObject go)
        {
            try { return go != null ? go.name : null; } catch { return null; }
        }

        static string F(float v) { return v.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture); }

        // ---------------- 点击 ----------------

        /// <summary>
        /// 按组件类型分派点击。返回 false 时 `err` 说明原因（不假装成功）。
        /// `via` 与 `ClickViaOf()` 的字符串一致，响应里两个字段可以互相印证。
        /// </summary>
        internal static bool Click(UnityEngine.MonoBehaviour comp, out string via, out string err)
        {
            via = null; err = null;
            if (comp == null) { err = "组件为 null"; return false; }
            try
            {
                var kt = comp as KToggle;
                if (kt != null)
                {
                    kt.Click();
                    via = "KToggle.Click()";
                    return true;
                }

                var kb = comp as KButton;
                if (kb != null)
                {
                    kb.SignalClick(KKeyCode.Mouse0);
                    via = "KButton.SignalClick(KKeyCode.Mouse0)";
                    return true;
                }

                var mt = comp as MultiToggle;
                if (mt != null)
                {
                    mt.OnPointerClick(NewPointerEventData());
                    via = "MultiToggle.OnPointerClick(PointerEventData)";
                    return true;
                }

                var ub = comp as UnityEngine.UI.Button;
                if (ub != null)
                {
                    if (ub.onClick == null) { err = "UGUI Button.onClick 为 null"; return false; }
                    ub.onClick.Invoke();
                    via = "UnityEngine.UI.Button.onClick.Invoke()";
                    return true;
                }

                var tg = comp as UnityEngine.UI.Toggle;
                if (tg != null)
                {
                    tg.OnPointerClick(NewPointerEventData());
                    via = "UnityEngine.UI.Toggle.OnPointerClick(PointerEventData)";
                    return true;
                }

                var go = comp.gameObject;
                if (go == null) { err = "组件所在 GameObject 为 null"; return false; }
                UnityEngine.EventSystems.ExecuteEvents.Execute(
                    go, NewPointerEventData(), UnityEngine.EventSystems.ExecuteEvents.pointerClickHandler);
                via = "ExecuteEvents.Execute<IPointerClickHandler>";
                return true;
            }
            catch (System.Exception e)
            {
                err = e.GetType().Name + ": " + e.Message;
                return false;
            }
        }

        /// <summary>
        /// 构造一次左键单击的指针事件。`MultiToggle.OnPointerClick` 只看 `button`（右键才被 `AllowClick` 拒），
        /// `clickCount=1` 用来避免误触 `onDoubleClick`。
        /// </summary>
        static UnityEngine.EventSystems.PointerEventData NewPointerEventData()
        {
            var ped = new UnityEngine.EventSystems.PointerEventData(UnityEngine.EventSystems.EventSystem.current);
            ped.button = UnityEngine.EventSystems.PointerEventData.InputButton.Left;
            ped.clickCount = 1;
            return ped;
        }

        /// <summary>
        /// 从文字（`LocText` / `UIText`）向上找最近的可点击组件。
        /// 「行」的文字是 LocText，可点击组件在父级上（`MultiToggle` 挂在行对象上，与 LocText 同一层或更高），
        /// 所以必须逐级向上找，而且不能只认 `KButton`。
        /// </summary>
        internal static UnityEngine.MonoBehaviour FindClickableAncestor(UnityEngine.Transform t, out string kind)
        {
            kind = null;
            var cur = t;
            int guard = 0;
            while (cur != null && guard++ < 32)
            {
                var best = BestOn(cur.gameObject);
                if (best != null) { kind = KindOf(best); return best; }
                cur = cur.parent;
            }
            return null;
        }

        static UnityEngine.MonoBehaviour BestOn(UnityEngine.GameObject go)
        {
            if (go == null) return null;
            UnityEngine.MonoBehaviour best = null;
            int bestPrio = -1;
            try
            {
                var comps = go.GetComponents<UnityEngine.MonoBehaviour>();
                if (comps == null) return null;
                for (int i = 0; i < comps.Length; i++)
                {
                    var c = comps[i];
                    if (!IsClickable(c)) continue;
                    int p = PriorityOf(c);
                    if (p > bestPrio) { bestPrio = p; best = c; }
                }
            }
            catch { }
            return best;
        }
    }
}
