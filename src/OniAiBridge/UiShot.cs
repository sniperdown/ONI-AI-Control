using System.Text;
using System.Linq;

// ⚠️ 不要 using System 之后直接用 Action/DateTime：Assembly-CSharp 里的全局 Action 枚举会遮蔽 System.Action。
namespace OniAiBridge
{
    /// <summary>
    /// M6.84（0.93.0）：**游戏内截图** + **UI 按钮点击（path/name/index + dryRun）**。
    ///
    /// 截图链路（实测核对过的 API）：
    /// * 相机选择见 `CollectCameras` / `PickWorldCamera` —— 来源是游戏相机注册表 + `Camera.allCameras` +
    ///   `FindObjectsOfType&lt;Camera&gt;(true)` 的并集，按角色与分数排序；**不用 `Camera.main`**（0.93.0 用它截到全黑）；
    /// * `new RenderTexture(w, h, 24)` → `cam.targetTexture = rt` → `cam.Render()` → `RenderTexture.active = rt`
    ///   → `Texture2D.ReadPixels(new Rect(0,0,w,h), 0, 0)` → `Apply()` → `EncodeToPNG()` → `File.WriteAllBytes`；
    /// * 全程在主线程（`MainThread.InvokeAdaptive`），**不改变任何游戏状态**（只做一次离屏渲染）。
    ///
    /// 点击链路（M6.86 起候选集合为**全部可点击元素**，不再只有 `KButton`，见 `UiRows`）：
    /// * 目标解析优先级：`path` > `name` > `index`（`index` 仍是 **KButton 序号**，与 `/v1/ui` 的 `buttons[].index` 一致）；
    /// * 只考虑 `activeInHierarchy`（不可见 ⇒ `400 NOT_VISIBLE`）；不可交互 ⇒ `400 NOT_INTERACTABLE`；
    /// * 多个命中 ⇒ `409 AMBIGUOUS_TARGET` + 候选（path/name/kind/role/index）；
    /// * `dryRun:true` ⇒ 只报会点哪个、会用哪个入口，不点；
    /// * 真点按组件类型分派：`KButton.SignalClick` / `KToggle.Click` / **`MultiToggle.OnPointerClick`** /
    ///   UGUI `Button.onClick` / UGUI `Toggle.OnPointerClick` / `ExecuteEvents` 兜底。
    /// </summary>
    internal static class UiShot
    {
        // ================= ③ 截图 =================
        public static string Shot(int wantW, int wantH, int quality, int cx, int cy, int cw, int ch,
                                  out int status, out string code, out string message)
        {
            status = 200; code = null; message = null;
            if (wantW <= 0) wantW = 1600;
            if (wantH <= 0) wantH = (int)System.Math.Round(wantW * 9.0 / 16.0);
            if (quality <= 0) quality = 80;

            long pixels = (long)wantW * wantH;
            const long MaxPixels = 4194304;
            if (pixels > MaxPixels)
            {
                status = 400; code = "TOO_MANY_PIXELS";
                message = "请求 " + wantW + "x" + wantH + " = " + pixels + " 像素，超过上限 " + MaxPixels + "（4194304）";
                return null;
            }

            string data = null, fail = null, failCode = null; int failStatus = 500;
            int okT; bool busy;
            bool ran = MainThread.InvokeAdaptive(() =>
            {
                try
                {
                    // ★ M6.85：**不要用 `Camera.main`**（实测选到 `overlayNoDepth` ⇒ 全黑）
                    var cam = PickWorldCamera();
                    if (cam == null)
                    {
                        failCode = "NO_CAMERA";
                        fail = "PickWorldCamera() == null（没有可用的世界相机；可 GET /v1/shot/cameras 看候选）";
                        return;
                    }

                    int w = wantW, h = wantH;
                    // 可选裁剪：cx/cy 是**屏幕左下角**为原点的像素坐标；宽高缺省用整屏
                    int sx = cx, sy = cy;
                    if (cw > 0) w = cw;
                    if (ch > 0) h = ch;
                    if (sx < 0) sx = 0;
                    if (sy < 0) sy = 0;
                    if (sx + w > UnityEngine.Screen.width) w = UnityEngine.Screen.width - sx;
                    if (sy + h > UnityEngine.Screen.height) h = UnityEngine.Screen.height - sy;
                    if (w <= 0 || h <= 0)
                    {
                        failCode = "BAD_CROP";
                        fail = "裁剪区域为空（屏幕 " + UnityEngine.Screen.width + "x" + UnityEngine.Screen.height + "）";
                        return;
                    }

                    UnityEngine.Texture2D tex = null;
                    string renderPath = null;

                    // ① 首选 ScreenCapture.CaptureScreenshotAsTexture()（同步、只含游戏视图、不含桌面）
                    try
                    {
                        tex = UnityEngine.ScreenCapture.CaptureScreenshotAsTexture();
                        if (tex != null) renderPath = "ScreenCapture";
                    }
                    catch { tex = null; }

                    // ② 退回离屏渲染（用**选好的世界相机**）
                    if (tex == null)
                    {
                        var rt = new UnityEngine.RenderTexture(w, h, 24);
                        var prev = cam.targetTexture;
                        UnityEngine.RenderTexture prevActive = UnityEngine.RenderTexture.active;
                        try
                        {
                            cam.targetTexture = rt;
                            cam.Render();
                            UnityEngine.RenderTexture.active = rt;
                            tex = new UnityEngine.Texture2D(w, h, UnityEngine.TextureFormat.RGB24, false);
                            tex.ReadPixels(new UnityEngine.Rect(0, 0, w, h), 0, 0);
                            tex.Apply();
                            renderPath = "RenderTexture";
                        }
                        finally
                        {
                            try { cam.targetTexture = prev; } catch { }
                            try { UnityEngine.RenderTexture.active = prevActive; } catch { }
                        }
                    }
                    if (tex == null) { failCode = "SHOT_FAILED"; fail = "ScreenCapture 与 RenderTexture 都没拿到纹理"; return; }

                    // ③ 尺寸统一（ScreenCapture 给的是全屏尺寸）
                    if (tex.width != w || tex.height != h)
                    {
                        var rt2 = UnityEngine.RenderTexture.GetTemporary(w, h, 0);
                        var prevA = UnityEngine.RenderTexture.active;
                        UnityEngine.Graphics.Blit(tex, rt2);
                        UnityEngine.RenderTexture.active = rt2;
                        var scaled = new UnityEngine.Texture2D(w, h, UnityEngine.TextureFormat.RGB24, false);
                        scaled.ReadPixels(new UnityEngine.Rect(0, 0, w, h), 0, 0);
                        scaled.Apply();
                        UnityEngine.RenderTexture.active = prevA;
                        UnityEngine.RenderTexture.ReleaseTemporary(rt2);
                        try { UnityEngine.Object.Destroy(tex); } catch { }
                        tex = scaled;
                    }

                    // ④ 黑图检测：平均亮度 < 1/255 视为失败（别把黑图当成功）
                    int luma = AverageLuma(tex);
                    if (luma >= 0 && luma < 1)
                    {
                        int l0 = luma;
                        try { UnityEngine.Object.Destroy(tex); } catch { }
                        failCode = "SHOT_BLACK"; failStatus = 500;
                        fail = "截图为全黑（平均亮度 " + l0 + "/255），camera=" + SafeCamName(cam) + "，renderPath=" + renderPath;
                        return;
                    }

                    byte[] png = null;
                    try { png = UnityEngine.ImageConversion.EncodeToPNG(tex); } catch (System.Exception e) { fail = "EncodeToPNG 失败: " + e.GetType().Name; }
                    try { UnityEngine.Object.Destroy(tex); } catch { }
                    if (png == null) { failCode = "ENCODE_FAILED"; fail = fail ?? "PNG 编码返回 null"; return; }

                    string dir = null;
                    try { dir = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(typeof(UiShot).Assembly.Location), "shots"); } catch { }
                    if (string.IsNullOrEmpty(dir)) dir = System.IO.Path.GetTempPath();
                    try { System.IO.Directory.CreateDirectory(dir); } catch { }
                    string name = "shot-" + System.DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + ".png";
                    string full = System.IO.Path.Combine(dir, name);
                    System.IO.File.WriteAllBytes(full, png);

                    var sb = new StringBuilder(512);
                    sb.Append("{\"path\":").Append(Snap.Str(full));
                    sb.Append(",\"width\":").Append(w).Append(",\"height\":").Append(h);
                    sb.Append(",\"bytes\":").Append(png.Length);
                    sb.Append(",\"format\":\"png\"");
                    sb.Append(",\"crop\":{\"x\":").Append(sx).Append(",\"y\":").Append(sy).Append('}');
                    sb.Append(",\"screen\":{\"width\":").Append(UnityEngine.Screen.width)
                      .Append(",\"height\":").Append(UnityEngine.Screen.height).Append('}');
                    sb.Append(",\"camera\":").Append(Snap.Str(SafeCamName(cam)));
                    sb.Append(",\"cameraRole\":").Append(Snap.Str(RoleOfCamera(cam)));
                    sb.Append(",\"renderPath\":").Append(Snap.Str(renderPath));
                    sb.Append(",\"avgLuma\":").Append(luma);
                    sb.Append(",\"note\":").Append(Snap.Str("游戏内离屏渲染（不含 Windows 桌面）；只渲染一次，不改变游戏状态。"));
                    sb.Append('}');
                    data = sb.ToString();
                    Log.Info("[shot] " + w + "x" + h + " bytes=" + png.Length + " -> " + full);
                }
                catch (System.Exception e) { fail = e.GetType().Name + ": " + e.Message; }
            }, 8000, 40000, out okT, out busy);

            if (!ran) { status = 503; code = "RETRY_LATER"; message = "main thread busy；请 1 秒后重试"; return null; }
            if (failCode != null) { status = failStatus; code = failCode; message = fail; return null; }
            if (fail != null) { status = 500; code = "SHOT_FAILED"; message = fail; return null; }
            if (data == null) { status = 500; code = "INTERNAL"; message = "截图返回空"; return null; }
            JsonCheck.Validate(data, "shot");
            return data;
        }

        static string SafeCamName(UnityEngine.Camera cam)
        {
            try { return cam.name; } catch { return null; }
        }

        // ================= ② 点击（path / name / index + dryRun） =================
        public static string Click(string body, out int status, out string code, out string message)
        {
            status = 200; code = null; message = null;

            string screenWant = MatchStr(body, "screen");
            string pathWant = MatchStr(body, "path");
            string nameWant = MatchStr(body, "name");
            int indexWant = -1;
            JobQuery.TryInt(body, "index", out indexWant);

            bool dryRun = false, confirm = false;
            JobQuery.MatchBool(body, "dryRun", out dryRun);
            JobQuery.MatchBool(body, "confirm", out confirm);

            if (string.IsNullOrEmpty(pathWant) && string.IsNullOrEmpty(nameWant) && indexWant < 0)
            {
                status = 400; code = "BAD_REQUEST";
                message = "需要 path / name / index 三者之一（path > name > index 优先级）";
                return null;
            }
            if (!dryRun && !confirm)
            {
                status = 400; code = "CONFIRM_REQUIRED";
                message = "点击按钮会改界面/游戏状态，必须显式 \"confirm\": true；只看候选请用 \"dryRun\": true";
                return null;
            }
            if (!MainThread.Ready) { status = 503; code = "NOT_READY"; message = "main-thread pump is not running yet"; return null; }

            string data = null, fail = null, failCode = null; int failStatus = 500;
            int okT; bool busy;
            bool ran = MainThread.InvokeAdaptive(() =>
            {
                try
                {
                    var screens = UnityEngine.Object.FindObjectsOfType<KScreen>(true);
                    if (screens == null || screens.Length == 0) { failCode = "NO_SCREEN"; fail = "没有 KScreen"; failStatus = 404; return; }

                    // ★ M6.86：候选不再只有 KButton —— 用与 `/v1/ui` 同一个采集器（`UiRows.Collect`），
                    //   诊断行 / 报告行 / 表格格子的 `MultiToggle` 也进候选，path 与 `/v1/ui` 列出的完全一致。
                    var cands = new System.Collections.Generic.List<UiRows.Element>();
                    var btnIndexGlobal = new System.Collections.Generic.List<int>();   // 该元素在 KButton 序列里的全局序号（-1 = 不是 KButton）
                    int kbtnSeq = 0;
                    for (int i = 0; i < screens.Length; i++)
                    {
                        var s = screens[i];
                        if (s == null) continue;
                        // ★ M6.85：KScreen 的「屏幕名」是 `screenName` 属性，不是 `name`（GameObject 名常是 ScreenPrefab/…）
                        if (!string.IsNullOrEmpty(screenWant) && !ScreenMatches(s, screenWant)) continue;

                        int totalEls = 0;
                        var els = UiRows.Collect(s, UiQuery.CollectLimit, out totalEls);
                        for (int b = 0; b < els.Count; b++)
                        {
                            var el = els[b];
                            if (el == null || el.Comp == null) continue;
                            cands.Add(el);
                            if (el.ButtonIndex >= 0) btnIndexGlobal.Add(kbtnSeq++);
                            else btnIndexGlobal.Add(-1);
                        }
                    }
                    if (cands.Count == 0) { failCode = "NO_TARGET"; fail = "该界面下没有可点击元素"; failStatus = 404; return; }

                    // 三级匹配（`index` 仍是 **KButton 序号**，与 0.93.x 语义一致；行请用 path）
                    var hits = new System.Collections.Generic.List<int>();
                    for (int i = 0; i < cands.Count; i++)
                    {
                        var el = cands[i];
                        bool m = false;
                        if (!string.IsNullOrEmpty(pathWant)) m = string.Equals(el.Path ?? "", pathWant, System.StringComparison.Ordinal);
                        else if (!string.IsNullOrEmpty(nameWant))
                        {
                            string gn = UiRows.NameOf(el.Go);
                            m = string.Equals(gn ?? "", nameWant, System.StringComparison.OrdinalIgnoreCase)
                                || string.Equals(el.Path ?? "", nameWant, System.StringComparison.OrdinalIgnoreCase);
                        }
                        else if (indexWant >= 0) m = (btnIndexGlobal[i] == indexWant);
                        if (m) hits.Add(i);
                    }
                    if (hits.Count == 0) { failCode = "NO_TARGET"; fail = "没有匹配的可点击元素"; failStatus = 404; return; }

                    // 候选描述
                    string CandJson(System.Collections.Generic.List<int> list, int max)
                    {
                        var sb = new StringBuilder(1024);
                        int n = list.Count < max ? list.Count : max;
                        for (int i = 0; i < n; i++)
                        {
                            var el = cands[list[i]];
                            if (i > 0) sb.Append(',');
                            // ★ 0.93.3：`path` 与 `/v1/ui` 的 `clickables[].path` 同源；拼不出父链时
                            //   由 `PathForTarget` 给出带说明的回退值，不留无解释的 null。
                            string pSrc, pUnavail;
                            string pPath = UiRows.PathForTarget(el, out pSrc, out pUnavail);
                            sb.Append("{\"path\":").Append(Snap.Str(pPath));
                            if (pSrc != null) sb.Append(",\"pathSource\":").Append(Snap.Str(pSrc));
                            if (pUnavail != null) sb.Append(",\"pathUnavailable\":").Append(Snap.Str(pUnavail));
                            sb.Append(",\"name\":").Append(Snap.Str(UiRows.NameOf(el.Go)));
                            sb.Append(",\"kind\":").Append(Snap.Str(el.Kind));
                            sb.Append(",\"role\":").Append(Snap.Str(el.Role));
                            sb.Append(",\"index\":").Append(btnIndexGlobal[list[i]]);       // KButton 序号；非 KButton 为 -1
                            sb.Append(",\"elementIndex\":").Append(el.Index);               // 该元素在所属 KScreen 的 clickables 里的序号
                            sb.Append(",\"activeInHierarchy\":").Append(el.ActiveInHierarchy ? "true" : "false");
                            sb.Append(",\"interactable\":").Append(el.Interactable ? "true" : "false");
                            sb.Append('}');
                        }
                        return sb.ToString();
                    }

                    // 过滤：可见 + 可交互；不可见的单独报 NOT_VISIBLE
                    var usable = new System.Collections.Generic.List<int>();
                    var invisible = new System.Collections.Generic.List<int>();
                    for (int i = 0; i < hits.Count; i++)
                    {
                        if (cands[hits[i]].ActiveInHierarchy) usable.Add(hits[i]); else invisible.Add(hits[i]);
                    }

                    if (usable.Count == 0)
                    {
                        failCode = "NOT_VISIBLE"; failStatus = 400;
                        fail = "匹配到的元素都不可见（activeInHierarchy=false）：" + CandJson(invisible, 5);
                        return;
                    }
                    if (usable.Count > 1)
                    {
                        failCode = "AMBIGUOUS_TARGET"; failStatus = 409;
                        fail = "多个候选，请用 path 精确定位：" + CandJson(usable, 10);
                        return;
                    }

                    int pick = usable[0];
                    var chosen = cands[pick];
                    if (!chosen.Interactable)
                    {
                        failCode = "NOT_INTERACTABLE"; failStatus = 400;
                        fail = "元素不可交互（kind=" + (chosen.Kind ?? "?") + "）：" + CandJson(new System.Collections.Generic.List<int> { pick }, 1);
                        return;
                    }

                    string before = null;
                    try { before = chosen.Screen; } catch { }
                    if (string.IsNullOrEmpty(before)) { try { before = ScreenNameOf(chosen.Comp); } catch { } }
                    bool clicked = false;
                    string clickedVia = null, clickErr = null;
                    if (!dryRun)
                    {
                        // ★ M6.86：按组件类型分派（KButton / KToggle / MultiToggle / UGUI 控件 / 其它 IPointerClickHandler）
                        clicked = UiRows.Click(chosen.Comp, out clickedVia, out clickErr);
                        if (!clicked)
                        {
                            failCode = "CLICK_FAILED"; failStatus = 500;
                            fail = "点击失败（kind=" + (chosen.Kind ?? "?") + "）：" + (clickErr ?? "未知");
                            return;
                        }
                    }

                    var sb2 = new StringBuilder(1024);
                    sb2.Append("{\"dryRun\":").Append(dryRun ? "true" : "false");
                    sb2.Append(",\"clicked\":").Append(clicked ? "true" : "false");
                    sb2.Append(",\"target\":").Append(CandJson(new System.Collections.Generic.List<int> { pick }, 1));
                    sb2.Append(",\"kind\":").Append(Snap.Str(chosen.Kind));
                    sb2.Append(",\"clickedVia\":").Append(Snap.Str(dryRun ? chosen.ClickVia : clickedVia));
                    sb2.Append(",\"screenCandidate\":").Append(Snap.Str(screenWant));
                    // ★ 0.93.3：回读统一走 `UiRows.AppendReadbackJson` —— 与 text/path/labelClick 三条定位路径
                    //   共用同一份写法和同一套取值规则（点后目标可能已被这次点击关闭；取不到就写明原因）。
                    UiRows.AppendReadbackJson(sb2, chosen.Comp, before, !dryRun);
                    sb2.Append(",\"note\":").Append(Snap.Str(
                        "path 是**相对 UIScreen** 的父级链（跨屏共享按钮必须用 path，否则可能点到别的屏的同名按钮）。"
                        + "candidates 里 role=row 的元素是 `MultiToggle` 行（诊断行 / 报告行 / 表格格子），点击走 OnPointerClick。"
                        + "dryRun 只解析不点。真点后建议再 GET /v1/ui 复核界面是否切换。"));
                    sb2.Append('}');
                    data = sb2.ToString();
                    Log.Info("[ui/click] dryRun=" + dryRun + " clicked=" + clicked + " kind=" + chosen.Kind
                             + " via=" + clickedVia + " path=" + (chosen.Path ?? "<null>"));
                }
                catch (System.Exception e) { fail = e.GetType().Name + ": " + e.Message; }
            }, 8000, 40000, out okT, out busy);

            if (!ran) { status = 503; code = "RETRY_LATER"; message = "main thread busy；请 1 秒后重试"; return null; }
            if (failCode != null) { status = failStatus; code = failCode; message = fail; return null; }
            if (fail != null) { status = 500; code = "UI_CLICK_FAILED"; message = fail; return null; }
            if (data == null) { status = 500; code = "INTERNAL"; message = "点击返回空"; return null; }
            JsonCheck.Validate(data, "ui-click");
            return data;
        }

        static string MatchStr(string body, string key)
        {
            if (string.IsNullOrEmpty(body)) return null;
            var m = System.Text.RegularExpressions.Regex.Match(body, "\"" + key + "\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"");
            if (!m.Success) return null;
            return System.Text.RegularExpressions.Regex.Unescape(m.Groups[1].Value);
        }

        /// <summary>元素所属的 KScreen 名字（往上找父级；名字与 `/v1/ui` 同口径，走 `UiRows.ScreenNameOf`）。</summary>
        static string ScreenNameOf(UnityEngine.MonoBehaviour b)
        {
            try
            {
                var t = b != null ? b.transform : null;
                while (t != null)
                {
                    var s = t.GetComponent<KScreen>();
                    if (s != null) return UiRows.ScreenNameOf(s);
                    t = t.parent;
                }
            }
            catch { }
            return null;
        }

        static bool ScreenMatches(KScreen s, string want)
        {
            try
            {
                if (s == null) return false;
                string sn = null, dn = null;
                try { sn = s.screenName; } catch { }
                try { dn = s.displayName; } catch { }
                string go = s.name;
                if (!string.IsNullOrEmpty(sn) && string.Equals(sn, want, System.StringComparison.OrdinalIgnoreCase)) return true;
                if (!string.IsNullOrEmpty(dn) && string.Equals(dn, want, System.StringComparison.OrdinalIgnoreCase)) return true;
                if (!string.IsNullOrEmpty(go) && string.Equals(go, want, System.StringComparison.OrdinalIgnoreCase)) return true;
                if (!string.IsNullOrEmpty(sn) && sn.IndexOf(want, System.StringComparison.OrdinalIgnoreCase) >= 0) return true;
                return false;
            }
            catch { return false; }
        }

        // ================= 相机：来源并集 + 分类 + 排序（`/v1/shot/cameras` 与 `/v1/shot` 共用） =================

        /// <summary>一台相机的分类结果。</summary>
        sealed class CamInfo
        {
            public UnityEngine.Camera Cam;
            public string Name;
            public string Role;              // base / overlay / overlayNoDepth / ui / lightBuffer / simOverlay / infrared / timelapse / registry / other
            public int Score;                // 越大越优先被 /v1/shot 选中
            public bool Enabled;
            public bool ActiveInHierarchy;
            public bool UsableForShot;
            public string UnusableReason;    // 不可用时说明原因（可用时为 null）
            public bool FromRegistry;        // 是否来自 `CameraController.Instance` 的具名字段
        }

        /// <summary>
        /// 收集相机候选。**三路来源取并集**（0.93.1 只用了第 ②，且只把结果压成一个字符串）：
        /// ① `CameraController.Instance` 的具名相机字段（`baseCamera` / `overlayCamera` / `uiCamera` / …）与 `cameras` 列表 ——
        ///    游戏自己的注册表，能确定"哪台是世界主相机"；
        /// ② `Camera.allCameras` —— **只含 enabled 的相机**；
        /// ③ `Object.FindObjectsOfType&lt;Camera&gt;(true)` —— 含 **disabled / 未激活** 的相机，补上 ② 漏掉的那部分。
        /// 只靠 ② 时，一旦世界相机在那一刻 disabled（加载、主菜单、界面遮挡等），整张列表就会是空的。
        /// </summary>
        static System.Collections.Generic.List<CamInfo> CollectCameras()
        {
            var list = new System.Collections.Generic.List<CamInfo>();
            var seen = new System.Collections.Generic.HashSet<int>();

            // ① 游戏注册表（名字最准：baseCamera 在场景里就叫 "baseCamera"）
            CameraController cc = null;
            try { cc = CameraController.Instance; } catch { }
            if (cc != null)
            {
                AddCam(list, seen, cc.baseCamera, "base", true);
                AddCam(list, seen, cc.overlayCamera, "overlay", true);
                AddCam(list, seen, cc.overlayNoDepthCamera, "overlayNoDepth", true);
                AddCam(list, seen, cc.uiCamera, "ui", true);
                AddCam(list, seen, cc.lightBufferCamera, "lightBuffer", true);
                AddCam(list, seen, cc.simOverlayCamera, "simOverlay", true);
                AddCam(list, seen, cc.infraredCamera, "infrared", true);
                AddCam(list, seen, cc.timelapseFreezeCamera, "timelapse", true);
                try
                {
                    var lst = cc.cameras;
                    if (lst != null)
                        for (int i = 0; i < lst.Count; i++) AddCam(list, seen, lst[i], "registry", true);
                }
                catch { }
            }

            // ② 所有 enabled 的相机
            try
            {
                var all = UnityEngine.Camera.allCameras;
                if (all != null) for (int i = 0; i < all.Length; i++) AddCam(list, seen, all[i], null, false);
            }
            catch { }

            // ③ 含 disabled / 未激活 的场景相机（预制体资产不算：用 includeInactive 重载，不用 FindObjectsOfTypeAll）
            try
            {
                var objs = UnityEngine.Object.FindObjectsOfType<UnityEngine.Camera>(true);
                if (objs != null) for (int i = 0; i < objs.Length; i++) AddCam(list, seen, objs[i], null, false);
            }
            catch { }

            return list;
        }

        static void AddCam(System.Collections.Generic.List<CamInfo> list, System.Collections.Generic.HashSet<int> seen,
                           UnityEngine.Camera cam, string roleHint, bool fromRegistry)
        {
            if (cam == null) return;
            int id = 0;
            try { id = cam.GetInstanceID(); } catch { }
            if (id != 0 && !seen.Add(id)) return;    // 先收的那个角色优先（注册表在 ①，名字最准）

            var info = new CamInfo();
            info.Cam = cam;
            info.Name = SafeCamName(cam);
            info.FromRegistry = fromRegistry;
            info.Role = string.IsNullOrEmpty(roleHint) ? RoleFromName(info.Name) : roleHint;
            ScoreAndClassify(info);
            list.Add(info);
        }

        /// <summary>按名字猜角色（拿不到注册表时的兜底）。顺序有意义：先判 `nodepth`，再判 `overlay`。</summary>
        static string RoleFromName(string n)
        {
            if (string.IsNullOrEmpty(n)) return "other";
            string low = n.ToLowerInvariant();
            if (low.Contains("overlaynodepth") || low.Contains("nodepth")) return "overlayNoDepth";
            if (low.Contains("overlay")) return "overlay";
            if (low.Contains("light")) return "lightBuffer";
            if (low.Contains("infrared")) return "infrared";
            if (low.Contains("timelapse")) return "timelapse";
            if (low.Contains("ui") || low.Contains("canvas")) return "ui";
            return "other";
        }

        /// <summary>
        /// 排序规则：角色基准分 + `targetTexture == null` 加 20 + 正交加 5；停用 −500、未激活 −1000。
        /// `base` 的基准分（1000）远高于其它角色，所以只要世界主相机在，它就是 `chosenForShot`。
        /// </summary>
        static void ScoreAndClassify(CamInfo i)
        {
            if (i == null || i.Cam == null) return;

            int baseScore;
            switch (i.Role)
            {
                case "base": baseScore = 1000; break;
                case "overlay": baseScore = 450; break;
                case "simOverlay": baseScore = 350; break;
                case "other": baseScore = 300; break;
                case "registry": baseScore = 250; break;
                case "overlayNoDepth": baseScore = 60; break;
                case "infrared": baseScore = 40; break;
                case "lightBuffer": baseScore = 10; break;
                case "timelapse": baseScore = -100; break;
                case "ui": baseScore = -200; break;
                default: baseScore = 200; break;
            }

            bool enabled = false, active = false;
            try { enabled = i.Cam.enabled; } catch { }
            try { active = i.Cam.gameObject != null && i.Cam.gameObject.activeInHierarchy; } catch { }

            int score = baseScore;
            try { if (i.Cam.targetTexture == null) score += 20; } catch { }
            try { if (i.Cam.orthographic) score += 5; } catch { }
            if (!enabled) score -= 500;
            if (!active) score -= 1000;
            i.Score = score;
            i.Enabled = enabled;
            i.ActiveInHierarchy = active;

            string reason = null;
            if (!active) reason = "GameObject 未激活（activeInHierarchy=false）";
            else if (!enabled) reason = "相机已停用（enabled=false）";
            else if (i.Role == "ui") reason = "UI 画布相机，不渲染世界";
            else if (i.Role == "lightBuffer") reason = "光照缓冲合成相机（输出到 RenderTexture，不是画面）";
            else if (i.Role == "overlayNoDepth") reason = "无深度叠加层（0.93.0 用它截出过全黑图）";
            else if (i.Role == "timelapse") reason = "延时摄影专用冻结相机";
            else if (i.Role == "infrared") reason = "红外调试视野，不是正常画面";
            i.UnusableReason = reason;
            i.UsableForShot = reason == null;
        }

        /// <summary>
        /// `/v1/shot` 用的那台：可用相机里分数最高者。
        /// 一台都不可用时**退到"enabled 且已激活"的相机里分数最高者**（宁可让调用方看到 `cameraRole`，
        /// 也不要因为角色判定把人挡住；出黑图仍由 `SHOT_BLACK` 拦截）。
        /// </summary>
        static UnityEngine.Camera PickWorldCamera()
        {
            var list = CollectCameras();
            CamInfo best = null;
            for (int i = 0; i < list.Count; i++)
            {
                var c = list[i];
                if (c == null || !c.UsableForShot) continue;
                if (best == null || c.Score > best.Score) best = c;
            }
            if (best == null)
            {
                for (int i = 0; i < list.Count; i++)
                {
                    var c = list[i];
                    if (c == null || !c.Enabled || !c.ActiveInHierarchy) continue;
                    if (best == null || c.Score > best.Score) best = c;
                }
            }
            return best != null ? best.Cam : null;
        }

        /// <summary>某台相机的角色名（用于 `/v1/shot` 响应里的 `cameraRole`，与 cameras 端点同源）。</summary>
        static string RoleOfCamera(UnityEngine.Camera cam)
        {
            if (cam == null) return null;
            var list = CollectCameras();
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i] != null && list[i].Cam == cam) return list[i].Role;
            }
            return RoleFromName(SafeCamName(cam));
        }

        /// <summary>采样若干像素算平均亮度；用于"出黑图不许当成功"。</summary>
        static int AverageLuma(UnityEngine.Texture2D tex)
        {
            try
            {
                var px = tex.GetPixels32();
                if (px == null || px.Length == 0) return -1;
                long sum = 0; int n = 0;
                int step = px.Length / 4096; if (step < 1) step = 1;
                for (int i = 0; i < px.Length; i += step)
                {
                    var p = px[i];
                    sum += (p.r * 299 + p.g * 587 + p.b * 114) / 1000;
                    n++;
                }
                return n > 0 ? (int)(sum / n) : -1;
            }
            catch { return -1; }
        }

        /// <summary>
        /// 给调用方看：**当前可用于截图的相机列表**（`GET /v1/shot/cameras`）。
        ///
        /// 输出结构（M6.86 起）：
        /// * `pickedForShot`：**数组** —— 按分数降序排列的"可用相机"，每项带角色 / 视野 / 是否渲染到贴图等字段；
        /// * `pickedCount`：可用相机数量（`pickedForShot` 非空 ⇔ 该值 &gt; 0）；
        /// * `chosenForShot`：`/v1/shot` 实际会渲染的那台（= `pickedForShot[0]`，没有可用相机时为 `null`）；
        /// * `cameras`：**全部**相机（含 disabled / 未激活 / UI / 光照缓冲），每项带 `usableForShot` 与 `unusableReason`；
        /// * `registryAvailable`：`CameraController.Instance` 是否可用（false ⇒ 角色只能按名字猜）。
        /// </summary>
        public static string Cameras(out int status, out string code, out string message)
        {
            status = 200; code = null; message = null;
            if (!MainThread.Ready) { status = 503; code = "NOT_READY"; message = "main-thread pump is not running yet"; return null; }
            string data = null, fail = null;
            int okT; bool busy;
            bool ran = MainThread.InvokeAdaptive(() =>
            {
                try
                {
                    bool registry = false;
                    try { registry = CameraController.Instance != null; } catch { }

                    var list = CollectCameras();
                    var usable = new System.Collections.Generic.List<CamInfo>();
                    for (int i = 0; i < list.Count; i++) if (list[i] != null && list[i].UsableForShot) usable.Add(list[i]);

                    // 角色判定把全部相机都排除了（例如只剩 UI 相机）⇒ 退一步：只要是 enabled 且已激活就列出来，
                    // 免得 `pickedForShot` 因为"分类太严"再次变成空的（这正是这次要修的症状）。
                    bool fallback = false;
                    if (usable.Count == 0)
                    {
                        for (int i = 0; i < list.Count; i++)
                        {
                            var c = list[i];
                            if (c != null && c.Enabled && c.ActiveInHierarchy) usable.Add(c);
                        }
                        fallback = usable.Count > 0;
                    }

                    // 分数降序（同分时名字稳定排序，便于逐次对比）
                    usable.Sort(delegate(CamInfo a, CamInfo b)
                    {
                        int d = b.Score.CompareTo(a.Score);
                        if (d != 0) return d;
                        return string.CompareOrdinal(a.Name ?? "", b.Name ?? "");
                    });

                    var sb = new StringBuilder(2048);

                    // ---- pickedForShot：可用相机列表 ----
                    sb.Append("{\"pickedForShot\":[");
                    for (int i = 0; i < usable.Count; i++)
                    {
                        if (i > 0) sb.Append(',');
                        AppendCameraJson(sb, usable[i], i == 0);
                    }
                    sb.Append(']');
                    sb.Append(",\"pickedCount\":").Append(usable.Count);
                    sb.Append(",\"pickedFallback\":").Append(fallback ? "true" : "false");

                    // ---- chosenForShot：/v1/shot 真正会用的那台 ----
                    sb.Append(",\"chosenForShot\":");
                    if (usable.Count > 0) AppendCameraJson(sb, usable[0], true);
                    else sb.Append("null");

                    // ---- cameras：全部相机（含不可用的）----
                    sb.Append(",\"cameras\":[");
                    for (int i = 0; i < list.Count; i++)
                    {
                        if (i > 0) sb.Append(',');
                        AppendCameraJson(sb, list[i], false);
                    }
                    sb.Append(']');
                    sb.Append(",\"count\":").Append(list.Count);
                    sb.Append(",\"registryAvailable\":").Append(registry ? "true" : "false");
                    sb.Append(",\"pickedNote\":").Append(Snap.Str(
                        "pickedForShot 是**当前可用于截图**的相机（enabled && activeInHierarchy，且不是 UI / 光照缓冲 / 无深度叠加层 / 延时冻结 / 红外视野），"
                        + "按分数降序；chosenForShot 是 /v1/shot 会用的那台。"));
                    sb.Append(",\"note\":").Append(Snap.Str(
                        "只读。相机来源为三路并集：CameraController.Instance 的具名相机与 cameras 列表、Camera.allCameras（仅 enabled）、"
                        + "Object.FindObjectsOfType<Camera>(true)（含 disabled/未激活）——所以 cameras 里可能出现 allCameras 看不到的相机。"
                        + "view 是该相机视口四角换算到世界坐标后的 [minX,minY,maxX,maxY]（正交相机即当前画面覆盖的世界范围）。"));
                    sb.Append('}');
                    data = sb.ToString();
                    Log.Info("[shot/cameras] 相机 " + list.Count + " 台，可用于截图 " + usable.Count + " 台"
                             + "，chosen=" + (usable.Count > 0 ? usable[0].Name : "<none>"));
                }
                catch (System.Exception e) { fail = e.GetType().Name + ": " + e.Message; }
            }, 5000, 30000, out okT, out busy);
            if (!ran) { status = 503; code = "RETRY_LATER"; message = "main thread busy"; return null; }
            if (fail != null) { status = 500; code = "CAMERAS_FAILED"; message = fail; return null; }
            if (data == null) { status = 500; code = "INTERNAL"; message = "cameras 返回空"; return null; }
            JsonCheck.Validate(data, "shot/cameras");
            return data;
        }

        /// <summary>
        /// 单台相机的 JSON。**先把所有值取到局部变量再拼接**：`Camera` 是 Unity 原生对象，
        /// 万一在遍历途中被销毁，字段 getter 会抛 `MissingReferenceException` ——
        /// 先取值可以保证"要么整条字段齐全、要么整条不写"，不会拼出半截 JSON。
        /// </summary>
        static void AppendCameraJson(StringBuilder sb, CamInfo info, bool markChosen)
        {
            if (sb == null || info == null || info.Cam == null) return;
            var c = info.Cam;

            bool hasEnabled = false, enabled = false;
            bool hasActive = false, active = false;
            bool hasDepth = false; string depth = null;
            bool hasMask = false; int mask = 0;
            bool hasFlags = false; string flags = null;
            bool hasTarget = false, targetIsNull = false; string target = null;
            bool hasOrtho = false, ortho = false;
            bool hasOrthoSize = false; string orthoSize = null;
            bool hasFov = false; string fov = null;
            bool hasRect = false; string rect = null;
            bool hasPixelRect = false; int pw = 0, ph = 0;

            try { enabled = c.enabled; hasEnabled = true; } catch { }
            try { active = c.gameObject != null && c.gameObject.activeInHierarchy; hasActive = true; } catch { }
            try { depth = F(c.depth); hasDepth = true; } catch { }
            try { mask = c.cullingMask; hasMask = true; } catch { }
            try { flags = c.clearFlags.ToString(); hasFlags = true; } catch { }
            try
            {
                var tt = c.targetTexture;
                targetIsNull = tt == null;
                target = tt == null ? null : tt.name;
                hasTarget = true;
            }
            catch { }
            try { ortho = c.orthographic; hasOrtho = true; } catch { }
            try { orthoSize = F(c.orthographicSize); hasOrthoSize = true; } catch { }
            try { fov = F(c.fieldOfView); hasFov = true; } catch { }
            try
            {
                var r = c.rect;
                rect = "[" + F(r.x) + "," + F(r.y) + "," + F(r.width) + "," + F(r.height) + "]";
                hasRect = true;
            }
            catch { }
            try { pw = c.pixelWidth; ph = c.pixelHeight; hasPixelRect = true; } catch { }

            // 视野：视口 (0,0)/(1,1) → 世界坐标（正交相机就是当前画面覆盖的世界范围；可再经 /v1/area 换算成格）
            string view = ViewOf(c);

            sb.Append("{\"name\":").Append(Snap.Str(info.Name));
            sb.Append(",\"role\":").Append(Snap.Str(info.Role));
            sb.Append(",\"score\":").Append(info.Score);
            if (markChosen) sb.Append(",\"chosen\":true");
            sb.Append(",\"fromRegistry\":").Append(info.FromRegistry ? "true" : "false");
            sb.Append(",\"usableForShot\":").Append(info.UsableForShot ? "true" : "false");
            if (info.UnusableReason != null) sb.Append(",\"unusableReason\":").Append(Snap.Str(info.UnusableReason));
            if (hasEnabled) sb.Append(",\"enabled\":").Append(enabled ? "true" : "false");
            if (hasActive) sb.Append(",\"activeInHierarchy\":").Append(active ? "true" : "false");
            if (hasDepth) sb.Append(",\"depth\":").Append(depth);
            if (hasMask) sb.Append(",\"cullingMask\":").Append(mask);
            if (hasFlags) sb.Append(",\"clearFlags\":").Append(Snap.Str(flags));
            if (hasTarget) sb.Append(",\"targetTexture\":").Append(targetIsNull ? "null" : Snap.Str(target));
            if (hasOrtho) sb.Append(",\"orthographic\":").Append(ortho ? "true" : "false");
            if (hasOrthoSize) sb.Append(",\"orthographicSize\":").Append(orthoSize);
            if (hasFov) sb.Append(",\"fieldOfView\":").Append(fov);
            if (hasRect) sb.Append(",\"rect\":").Append(rect);
            if (hasPixelRect) sb.Append(",\"pixelRect\":[").Append(pw).Append(',').Append(ph).Append(']');
            sb.Append(",\"view\":").Append(view ?? "null");
            sb.Append('}');
        }

        /// <summary>视口 (0,0) 与 (1,1) 换算到世界坐标，取 min/max；失败返回 null（不猜）。</summary>
        static string ViewOf(UnityEngine.Camera cam)
        {
            if (cam == null) return null;
            try
            {
                float z = cam.nearClipPlane;
                var p0 = cam.ViewportToWorldPoint(new UnityEngine.Vector3(0f, 0f, z));
                var p1 = cam.ViewportToWorldPoint(new UnityEngine.Vector3(1f, 1f, z));
                return "[" + F(UnityEngine.Mathf.Min(p0.x, p1.x)) + "," + F(UnityEngine.Mathf.Min(p0.y, p1.y)) + ","
                     + F(UnityEngine.Mathf.Max(p0.x, p1.x)) + "," + F(UnityEngine.Mathf.Max(p0.y, p1.y)) + "]";
            }
            catch { return null; }
        }
        static string F(float v) { return v.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture); }
    }
}
