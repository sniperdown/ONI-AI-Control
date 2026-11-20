using System.Text;

namespace OniAiBridge
{
    /// <summary>
    /// 坐标点击（0.108.0）—— **唯一不依赖序号/名字的定位方式**。
    ///
    /// 为什么：装载列表顺序在两次启动之间会变（实测：`#3` 上一轮是「第七次重启」，
    /// 这一轮「首都」跑到第一行）⇒ 任何依赖序号或名字枚举的方案都不可靠。
    ///
    /// 坐标系：**截图左上原点**；`Input.mousePosition` 是**左下原点** ⇒ y 要翻转（H - y）。
    ///
    /// 做法：用 UGUI 的 EventSystem 在屏幕坐标做 Raycast，命中哪个控件就对它派发 PointerClick，
    /// 不猜对象名、不猜层级。
    /// </summary>
    internal static class UiClickAt
    {
        public static string Run(string body, out int status, out string code, out string message)
        {
            status = 200; code = null; message = null;

            // 0.108.1：**归一化坐标优先**（fx/fy 都是 0..1，相对当前窗口），
            // 这样"读截图的人"只给百分比，分辨率/缩放由接口管。
            bool useNorm = body.IndexOf("\"fx\"", System.StringComparison.Ordinal) >= 0;
            double fx = Frac(body, "fx"), fy = Frac(body, "fy");
            int x = -1, y = -1;
            if (!useNorm)
            {
                x = Num(body, "x"); y = Num(body, "y");
                if (x < 0 || y < 0) { status = 400; code = "BAD_REQUEST"; message = "需要 fx/fy（0..1）或 x/y（截图左上原点）"; return null; }
            }

            string data = null; string fatal = null;
            int okR; bool busyR;
            bool ran = MainThread.InvokeAdaptive(() =>
            {
                try
                {
                    int h = UnityEngine.Screen.height;   // 每次现读（窗口模式/改窗口会变）
                    int w = UnityEngine.Screen.width;
                    int px, py;
                    if (useNorm)
                    {
                        px = (int)System.Math.Round(fx * w);
                        py = (int)System.Math.Round(fy * h);
                    }
                    else { px = x; py = y; }
                    var screenPos = new UnityEngine.Vector2(px, h - py);   // y 翻转成左下原点

                    var es = UnityEngine.EventSystems.EventSystem.current;
                    var sb = new StringBuilder(512);
                    sb.Append("{\"x\":").Append(px).Append(",\"y\":").Append(py);
                    sb.Append(",\"screenHeight\":").Append(h).Append(",\"screenWidth\":").Append(w);
                    sb.Append(",\"normalized\":").Append(useNorm ? "true" : "false");
                    if (useNorm) sb.Append(",\"fx\":").Append(fx.ToString("0.####")).Append(",\"fy\":").Append(fy.ToString("0.####"));
                    sb.Append(",\"clicked\":false");

                    if (es == null)
                    {
                        sb.Append(",\"reason\":").Append(Snap.Str("EventSystem.current 为 null"));
                        sb.Append('}');
                        data = sb.ToString();
                        return;
                    }

                    var ped = new UnityEngine.EventSystems.PointerEventData(es);
                    ped.position = screenPos;
                    var hits = new System.Collections.Generic.List<UnityEngine.EventSystems.RaycastResult>();
                    es.RaycastAll(ped, hits);

                    sb.Append(",\"hitCount\":").Append(hits.Count);
                    if (hits.Count > 0)
                    {
                        var top = hits[0];
                        string nm = top.gameObject != null ? top.gameObject.name : null;
                        sb.Append(",\"hitName\":").Append(nm == null ? "null" : Snap.Str(nm));
                        // 往上找可点击的 Button
                        UnityEngine.GameObject target = top.gameObject;
                        var btn = target != null ? target.GetComponentInParent<UnityEngine.UI.Button>() : null;
                        if (btn != null && btn.gameObject != null) target = btn.gameObject;
                        sb.Append(",\"targetName\":").Append(target != null ? Snap.Str(target.name) : "null");
                        // 命中对象上"写着什么" —— 一眼判断点对没点对
                        {
                            var tb = new StringBuilder();
                            try
                            {
                                var src = top.gameObject;
                                var lts = src != null ? src.GetComponentsInChildren<LocText>(true) : null;
                                if (lts != null) for (int i2 = 0; i2 < lts.Length; i2++)
                                {
                                    var lk = lts[i2]; if (lk == null) continue;
                                    string s2 = null; try { s2 = lk.text; } catch { }
                                    if (string.IsNullOrEmpty(s2)) continue;
                                    if (tb.Length > 0) tb.Append(" | ");
                                    tb.Append(s2);
                                }
                            }
                            catch { }
                            sb.Append(",\"hitText\":").Append(tb.Length > 0 ? Snap.Str(tb.ToString()) : "null");
                        }

                        if (target != null)
                        {
                            UnityEngine.EventSystems.ExecuteEvents.Execute(
                                target,
                                ped,
                                UnityEngine.EventSystems.ExecuteEvents.pointerClickHandler);
                            sb.Append(",\"clicked\":true");
                        }
                    }
                    else
                    {
                        sb.Append(",\"reason\":").Append(Snap.Str("该坐标没有命中任何 UGUI 控件"));
                    }
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
