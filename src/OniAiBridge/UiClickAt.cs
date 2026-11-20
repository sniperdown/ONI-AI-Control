using System.Collections.Generic;
using System.Text;

namespace OniAiBridge
{
    /// <summary>
    /// 坐标点击（0.110.0，**整份重写**；本文件已因"继续打补丁"崩过两次，别再补）。
    ///
    /// 四条硬结论（2026-11-20 实测，别重新推导）：
    ///   1. `hitAncestry = "Title[] > Header[KButton]"` —— 命中的 `Title` 没有交互组件，
    ///      **可点的是父级 `Header` 上的 `KButton`**。
    ///   2. `kbuttonMethods = null` —— `KButton` 上**没有** Click/Press/Signal 之类的公开方法。
    ///   3. `onClickField = True` —— 它是**事件字段模式**（`onClick` 持有 Delegate），不是方法模式。
    ///   4. `ExecuteEvents(x5)` 派发后游戏**毫无反应** —— 这条路走不通，别试第三次。
    ///
    /// 坐标系：截图 1280×720 而屏幕 3840×2160（3 倍）；`Input.mousePosition` 左下原点。
    /// 接口吃**归一化坐标** fx/fy（0..1，相对当前窗口），y 翻转与缩放由这里负责。
    ///
    /// 四层结果分列输出：
    ///   命中层 hitName/hitText · 可点层 hitAncestry/hitTargetType
    ///   动作层 kbuttonMethods/onClickField/onClickSubs/onClickEmpty
    ///   派发层 dispatched/dispatchMethod/dispatchError
    /// </summary>
    internal static class UiClickAt
    {
        public static string Run(string body, out int status, out string code, out string message)
        {
            status = 200; code = null; message = null;

            bool useNorm = body.IndexOf("\"fx\"", System.StringComparison.Ordinal) >= 0;
            double fx = Frac(body, "fx"), fy = Frac(body, "fy");
            int x = Num(body, "x"), y = Num(body, "y");
            if (useNorm)
            {
                if (fx < 0 || fy < 0) { status = 400; code = "BAD_REQUEST"; message = "fx/fy 应为 0..1"; return null; }
            }
            else if (x < 0 || y < 0) { status = 400; code = "BAD_REQUEST"; message = "需要 fx/fy（0..1）或 x/y"; return null; }

            string data = null; string fatal = null;
            int okR; bool busyR;
            bool ran = MainThread.InvokeAdaptive(() =>
            {
                try
                {
                    int sw = UnityEngine.Screen.width;
                    int sh = UnityEngine.Screen.height;
                    int px = useNorm ? (int)System.Math.Round(fx * sw) : x;
                    int py = useNorm ? (int)System.Math.Round(fy * sh) : y;
                    var screenPos = new UnityEngine.Vector2(px, sh - py);

                    var sb = new StringBuilder(1024);
                    sb.Append("{\"x\":").Append(px).Append(",\"y\":").Append(py);
                    sb.Append(",\"screenWidth\":").Append(sw).Append(",\"screenHeight\":").Append(sh);
                    sb.Append(",\"normalized\":").Append(useNorm ? "true" : "false");
                    if (useNorm)
                    {
                        sb.Append(",\"fx\":").Append(fx.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture));
                        sb.Append(",\"fy\":").Append(fy.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture));
                    }

                    var es = UnityEngine.EventSystems.EventSystem.current;
                    if (es == null)
                    {
                        sb.Append(",\"reason\":").Append(Snap.Str("EventSystem.current 为 null")).Append('}');
                        data = sb.ToString();
                        return;
                    }

                    var ped = new UnityEngine.EventSystems.PointerEventData(es);
                    ped.position = screenPos;
                    ped.pressPosition = screenPos;
                    var hits = new List<UnityEngine.EventSystems.RaycastResult>();
                    es.RaycastAll(ped, hits);

                    string hitName = hits.Count > 0 && hits[0].gameObject != null ? hits[0].gameObject.name : null;
                    string hitText = hits.Count > 0 ? TextsOf(hits[0].gameObject) : null;

                    UnityEngine.Component actionComp = null;
                    UnityEngine.GameObject actionGo = null;
                    string targetType = null;
                    string ancestry = hits.Count > 0 ? Ancestry(hits[0].gameObject, out actionGo, out targetType, out actionComp) : null;

                    // 动作层：KButton 的方法表 + onClick 字段
                    string kbuttonMethods = null;
                    bool onClickField = false;
                    int onClickSubs = -1;
                    bool onClickEmpty = false;

                    if (actionComp != null && targetType == "KButton")
                    {
                        kbuttonMethods = MethodNames(actionComp);

                        System.Reflection.FieldInfo fi = FindField(actionComp.GetType(), "onClick");
                        onClickField = fi != null;
                        if (fi != null)
                        {
                            try
                            {
                                var dlg = fi.GetValue(actionComp) as System.Delegate;
                                var inv = dlg != null ? dlg.GetInvocationList() : null;
                                onClickSubs = inv != null ? inv.Length : 0;
                                if (onClickSubs == 0) onClickEmpty = true;
                            }
                            catch { onClickSubs = -2; }
                        }
                    }

                    string method = null;
                    string err = null;
                    bool done = false;

                    // 派发：优先 onClick 的 Delegate（事件字段模式）
                    System.Reflection.FieldInfo fld = actionComp != null ? FindField(actionComp.GetType(), "onClick") : null;
                    if (fld != null && !onClickEmpty)
                    {
                        try
                        {
                            var dlg = fld.GetValue(actionComp) as System.Delegate;
                            var inv = dlg != null ? dlg.GetInvocationList() : null;
                            if (inv != null && inv.Length > 0)
                            {
                                for (int k = 0; k < inv.Length && !done; k++)
                                {
                                    try
                                    {
                                        inv[k].DynamicInvoke();
                                        method = "onClick.DynamicInvoke(" + inv.Length + ")";
                                        done = true;
                                    }
                                    catch (System.Exception e4)
                                    {
                                        err = "DynamicInvoke:" + e4.GetType().Name + ":" + e4.Message
                                            + (e4.InnerException != null ? " inner:" + e4.InnerException.GetType().Name + ":" + e4.InnerException.Message : "");
                                    }
                                }
                            }
                        }
                        catch (System.Exception e5) { err = "onClickRead:" + e5.GetType().Name + ":" + e5.Message; }
                    }

                    // 退路：UGUI 派发（实测对本界面无效，保留只为观测）
                    if (!done && hits.Count > 0)
                    {
                        try
                        {
                            int n = 0;
                            for (int i = 0; i < hits.Count && n < 5; i++)
                            {
                                var go = hits[i].gameObject;
                                if (go == null) continue;
                                UnityEngine.EventSystems.ExecuteEvents.Execute(
                                    go, ped, UnityEngine.EventSystems.ExecuteEvents.pointerClickHandler);
                                n++;
                            }
                            method = "ExecuteEvents(x" + n + ")";
                            done = n > 0;
                        }
                        catch (System.Exception e3) { err = "ExecuteEvents:" + e3.GetType().Name + ":" + e3.Message; }
                    }

                    sb.Append(",\"hitCount\":").Append(hits.Count);
                    sb.Append(",\"hitName\":").Append(hitName == null ? "null" : Snap.Str(hitName));
                    sb.Append(",\"hitText\":").Append(hitText == null ? "null" : Snap.Str(hitText));
                    sb.Append(",\"hitAncestry\":").Append(ancestry == null ? "null" : Snap.Str(ancestry));
                    sb.Append(",\"hitTargetType\":").Append(targetType == null ? "null" : Snap.Str(targetType));
                    sb.Append(",\"kbuttonMethods\":").Append(kbuttonMethods == null ? "null" : Snap.Str(kbuttonMethods));
                    sb.Append(",\"onClickField\":").Append(onClickField ? "true" : "false");
                    sb.Append(",\"onClickSubs\":").Append(onClickSubs);
                    sb.Append(",\"onClickEmpty\":").Append(onClickEmpty ? "true" : "false");
                    sb.Append(",\"dispatched\":").Append(done ? "true" : "false");
                    sb.Append(",\"dispatchMethod\":").Append(method == null ? "null" : Snap.Str(method));
                    if (err != null) sb.Append(",\"dispatchError\":").Append(Snap.Str(err));
                    sb.Append('}');
                    data = sb.ToString();
                }
                catch (System.Exception e) { fatal = e.GetType().Name + ": " + e.Message; }
            }, 3000, 15000, out okR, out busyR);

            if (!ran) { status = 503; code = "MAIN_THREAD_BUSY"; message = "主线程泵未响应"; return null; }
            if (fatal != null) { status = 500; code = "CLICK_AT_FAILED"; message = fatal; return null; }
            JsonCheck.Validate(data, "ui-click-at");
            return data;
        }

        /// <summary>`onClick` 可能在基类上 ⇒ 沿继承链找。</summary>
        static System.Reflection.FieldInfo FindField(System.Type t, string name)
        {
            try
            {
                var ty = t;
                while (ty != null)
                {
                    var fi = ty.GetField(name, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic
                        | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly);
                    if (fi != null) return fi;
                    ty = ty.BaseType;
                }
            }
            catch { }
            return null;
        }

        static string MethodNames(UnityEngine.Component comp)
        {
            var names = new StringBuilder();
            try
            {
                var ms = comp.GetType().GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic
                    | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly);
                int n = 0;
                for (int i = 0; i < ms.Length; i++)
                {
                    string nm = ms[i].Name;
                    bool interesting = nm.IndexOf("lick", System.StringComparison.OrdinalIgnoreCase) >= 0
                        || nm.IndexOf("ress", System.StringComparison.OrdinalIgnoreCase) >= 0
                        || nm.IndexOf("ignal", System.StringComparison.OrdinalIgnoreCase) >= 0;
                    if (!interesting) continue;
                    if (n++ > 0) names.Append(", ");
                    names.Append(nm).Append('/').Append(ms[i].GetParameters().Length);
                }
                return n == 0 ? null : names.ToString();
            }
            catch { return null; }
        }

        static string Ancestry(UnityEngine.GameObject start, out UnityEngine.GameObject go, out string type, out UnityEngine.Component comp)
        {
            var sb = new StringBuilder();
            go = null; type = null; comp = null;
            try
            {
                var cur = start;
                int depth = 0;
                while (cur != null && depth < 8)
                {
                    if (depth > 0) sb.Append(" > ");
                    sb.Append(cur.name).Append('[');
                    int wrote = 0;
                    var comps = cur.GetComponents<UnityEngine.Component>();
                    for (int i = 0; i < comps.Length; i++)
                    {
                        var c = comps[i];
                        if (c == null) continue;
                        string tn = c.GetType().Name;
                        bool interesting = tn == "KButton" || tn == "MultiToggle" || tn == "Button"
                                           || tn == "Toggle" || tn == "KToggle" || tn == "EventTrigger";
                        if (!interesting) continue;
                        if (wrote++ > 0) sb.Append(',');
                        sb.Append(tn);
                        if (comp == null && (tn == "KButton" || tn == "MultiToggle" || tn == "Button" || tn == "Toggle" || tn == "KToggle"))
                        { comp = c; go = cur; type = tn; }
                    }
                    sb.Append(']');
                    if (comp != null) break;
                    cur = cur.transform.parent != null ? cur.transform.parent.gameObject : null;
                    depth++;
                }
            }
            catch { }
            return sb.ToString();
        }

        static string TextsOf(UnityEngine.GameObject go)
        {
            var sb = new StringBuilder();
            try
            {
                var lts = go.GetComponentsInChildren<LocText>(true);
                for (int i = 0; i < lts.Length; i++)
                {
                    var lk = lts[i]; if (lk == null) continue;
                    string s = null; try { s = lk.text; } catch { }
                    if (string.IsNullOrEmpty(s)) continue;
                    if (sb.Length > 0) sb.Append(" | ");
                    sb.Append(s);
                }
            }
            catch { }
            return sb.Length > 0 ? sb.ToString() : null;
        }

        static double Frac(string body, string key)
        {
            int k = body.IndexOf("\"" + key + "\"", System.StringComparison.Ordinal);
            if (k < 0) return -1;
            int colon = body.IndexOf(':', k);
            if (colon < 0) return -1;
            int i = colon + 1;
            while (i < body.Length && (body[i] == ' ' || body[i] == '"')) i++;
            int s = i;
            while (i < body.Length && (char.IsDigit(body[i]) || body[i] == '.' || body[i] == '-')) i++;
            double v;
            if (i > s && double.TryParse(body.Substring(s, i - s), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out v)) return v;
            return -1;
        }

        static int Num(string body, string key)
        {
            int k = body.IndexOf("\"" + key + "\"", System.StringComparison.Ordinal);
            if (k < 0) return -1;
            int colon = body.IndexOf(':', k);
            if (colon < 0) return -1;
            int i = colon + 1;
            while (i < body.Length && (body[i] == ' ' || body[i] == '"')) i++;
            int s = i;
            while (i < body.Length && (char.IsDigit(body[i]) || body[i] == '-')) i++;
            int v;
            if (i > s && int.TryParse(body.Substring(s, i - s), out v)) return v;
            return -1;
        }
    }
}
