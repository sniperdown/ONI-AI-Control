using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

// ⚠️ 不要 using System 之后直接用 Action/DateTime：Assembly-CSharp 里的全局 Action 枚举会遮蔽 System.Action。
namespace OniAiBridge
{
    /// <summary>
    /// 设置页（M4.4：**攒改动 + 保存才写盘**）。
    ///
    /// 本版按用户要求重排：
    ///  * 底部只留 `[保存] [打开配置目录] [关闭]`（`[−]`/`[+]`/`[下一个字段]` 已下线；
    ///    `AdjustStep`/`CycleField` 代码保留但**不再调用**，便于回退）；
    ///  * 删掉 沙盒(存档)/沙盒(运行时)/Debug 模式/作弊闸门 与底部那句提示；
    ///  * `port` 改成数值文本框（1024–65535），保存后提示"改端口需重启 mod"；
    ///  * 所有控件只改**内存草稿**，点 `[保存]` 才一次性校验 + 写盘 + 回读；
    ///    任一项校验失败 ⇒ **整批不写**，状态行显示 `<字段> 无效：<原因>`；
    ///  * 有未保存改动时状态行提示；关闭前若仍有未保存改动，至少打 `Log.Warn`（ONI 没有确认框）。
    /// </summary>
    internal static class BridgeOptionsScreen
    {
        static InfoDialogScreen _open;
        static MultiToggle _allowCheatsToggle;

        /// <summary>M5.3：`AIEnableFog` 勾选框（勾上 = 有迷雾 = AI 只看看得见的地方）。</summary>
        static MultiToggle _fogToggle;

        /// <summary>M6.89：`allowMap` 勾选框（勾上 = 允许 `GET /v1/map` 读地图降采样；默认**不勾**）。</summary>
        static MultiToggle _mapToggle;

        /// <summary>输入框（前 4 个对应数值项，第 5 个是 port）。</summary>
        static readonly KInputTextField[] _fields = new KInputTextField[5];
        static readonly List<InfoScreenLineItem> _rows = new List<InfoScreenLineItem>();
        static InfoScreenLineItem _statusRow;
        static bool _inputFieldUnavailable;
        static bool _structureLogged;
        static bool _dirty;

        const float RightMargin = 12f;
        const float FieldW = 80f, FieldH = 26f;

        // ---------------- 字段表（数值 + 端口，表驱动）----------------
        struct Ctrl
        {
            public string Key;            // 也用于行文字与日志
            public float Min, Max;
            public int Decimals;
            public System.Func<BridgeSettings, float> Get;
            public System.Action<BridgeSettings, float> Set;
        }

        static readonly Ctrl[] Ctrls = new[]
        {
            new Ctrl { Key = "alertStress", Min = 0f, Max = 100f, Decimals = 0,
                       Get = s => s.AlertStress, Set = (s, v) => s.AlertStress = v },
            new Ctrl { Key = "alertCaloriesKcal", Min = 100f, Max = 10000f, Decimals = 0,
                       Get = s => s.AlertCaloriesKcal, Set = (s, v) => s.AlertCaloriesKcal = v },
            new Ctrl { Key = "alertBladder", Min = 0f, Max = 100f, Decimals = 0,
                       Get = s => s.AlertBladder, Set = (s, v) => s.AlertBladder = v },
            new Ctrl { Key = "amountEpsilon", Min = 0f, Max = 10f, Decimals = 2,
                       Get = s => s.AmountEpsilon, Set = (s, v) => s.AmountEpsilon = v },
            new Ctrl { Key = "port", Min = 1024f, Max = 65535f, Decimals = 0,
                       Get = s => s.Port, Set = (s, v) => s.Port = (int)System.Math.Round(v) },
        };

        const int PortIndex = 4;

        // ---------------- 打开 ----------------
        public static void Open()
        {
            try
            {
                if (_open != null && _open.gameObject != null)
                {
                    Log.Info("设置页已开着，忽略重复打开");
                    return;
                }
                ScreenPrefabs prefs = ScreenPrefabs.Instance;
                if (prefs == null || prefs.InfoDialogScreen == null)
                {
                    Log.Warn("打开设置页失败：ScreenPrefabs.Instance.InfoDialogScreen 不可用");
                    return;
                }
                KScreenManager mgr = KScreenManager.Instance;
                if (mgr == null)
                {
                    Log.Warn("打开设置页失败：KScreenManager.Instance == null");
                    return;
                }

                string trace;
                GameObject parent = ResolveParent(out trace);
                KScreen raw = mgr.StartScreen(prefs.InfoDialogScreen.gameObject, parent);
                var screen = raw as InfoDialogScreen;
                if (screen == null)
                {
                    Log.Warn("打开设置页失败：StartScreen 返回 " + (raw == null ? "null" : raw.GetType().Name)
                             + "；canvas 探测=" + trace);
                    return;
                }

                _open = screen;
                _allowCheatsToggle = null;
                _fogToggle = null;
                _mapToggle = null;
                _dirty = false;
                for (int i = 0; i < _fields.Length; i++) _fields[i] = null;
                _rows.Clear();
                screen.onDeactivateFn = () =>
                {
                    if (_dirty) Log.Warn("[设置页] 关闭时有未保存改动，已丢弃（没有写盘）");
                    _open = null; _allowCheatsToggle = null; _fogToggle = null; _mapToggle = null;
                };

                screen.SetHeader("ONI AI Bridge · 设置");

                AddInfoRows(screen);
                AddCtrlRows(screen, GetContentContainer(screen));
                AddAllowCheatsRow(screen);
                Separator(screen);                 // 空行：刻意把两个勾选框隔开（它们不是一回事）
                AddFogRow(screen);
                Separator(screen);                 // 空行：地图开关与迷雾开关也是两件事（一个管"能看多少"，一个管"能不能读地图"）
                AddMapRow(screen);
                AddActionButtons(screen);

                Log.Info("设置页已打开（M4.4 草稿+保存；parent="
                         + (parent == null ? "<ONI 默认>" : parent.name) + "；探测=" + trace + "）");
            }
            catch (System.Exception e)
            {
                _open = null;
                Log.Warn("打开设置页失败（不影响 HTTP 与 json 配置）: " + e);
            }
        }

        // ---------------- 只读信息行（保留四行）----------------
        static void AddInfoRows(InfoDialogScreen screen)
        {
            try
            {
                BridgeSettings st = Bridge.SettingsRef;
                bool hasToken = st != null && !string.IsNullOrEmpty(st.Token);
                AddRowTracked(screen, GetContentContainer(screen), L("配置文件", "见 tooltip"), BridgeSettings.ConfigPath);
                AddRowTracked(screen, GetContentContainer(screen), L("写盘可用", BridgeSettings.IsConfigWritable() ? "是" : "否（改动无法保存）"), null);
                AddRowTracked(screen, GetContentContainer(screen), L("token", hasToken ? ("长度 " + st.Token.Length) : "(未生成)"), null);
                AddRowTracked(screen, GetContentContainer(screen), L("版本", "mod " + Bridge.ModVersion + " · 阶段 " + Bridge.Stage), null);
            }
            catch (System.Exception e) { Log.Warn("[设置页] 建信息行失败: " + e.Message); }
        }

        // ---------------- 可编辑行（4 数值 + 端口）+ 状态行 ----------------
        static void AddCtrlRows(InfoDialogScreen screen, GameObject cc)
        {
            try
            {
                BridgeSettings st = Bridge.SettingsRef;
                for (int i = 0; i < Ctrls.Length; i++)
                {
                    var item = AddRowTracked(screen, cc, CtrlRowText(i, st));
                    _rows.Add(item);
                }
                _statusRow = AddRowTracked(screen, cc, "提示：改完点 [保存] 才会写盘；端口改动需重启 mod 才生效。", null);

                CreateInputFields(screen, cc);
                var localRows = new List<InfoScreenLineItem>(_rows);
                MainThread.Enqueue(() =>
                {
                    try
                    {
                        for (int i = 0; i < Ctrls.Length && i < _fields.Length; i++)
                        {
                            if (_fields[i] == null) continue;
                            RectTransform rowRt = null;
                            // _rows 里前 5 条就是这 5 个控件行
                            if (i < localRows.Count && localRows[i] != null) rowRt = localRows[i].transform as RectTransform;
                            PlaceInputField(cc, rowRt, _fields[i]);
                        }
                        SyncFieldText();
                        Log.Info("[设置页] 输入框延迟定位完成（" + Ctrls.Length + " 行）");
                    }
                    catch (System.Exception e) { Log.Warn("[设置页] 输入框延迟定位异常: " + e); }
                });
            }
            catch (System.Exception e) { Log.Warn("[设置页] 建控件行失败: " + e); }
        }

        static string CtrlRowText(int i, BridgeSettings st)
        {
            var c = Ctrls[i];
            float v = st != null ? c.Get(st) : 0f;
            return L(c.Key, Fmt(c, v));
        }

        // ---------------- allowCheats 行（勾选框）----------------
        static void Separator(InfoDialogScreen screen)
        {
            try { screen.AddLineItem(" ", null); } catch { }
        }

        /// <summary>
        /// `AIEnableFog`：勾上 = 有迷雾（AI 只报看得见的地方）；不勾 = 全图。
        /// ⚠️ 与 `allowCheats` 完全独立，界面上刻意隔开（中间一个空行）。
        /// </summary>
        static void AddFogRow(InfoDialogScreen screen)
        {
            try
            {
                BridgeSettings st = Bridge.SettingsRef;
                bool initial = st == null || st.AIEnableFog;

                screen.AddLineItem("开启ai视野迷雾，不勾选则能看到全图", "勾上 = AI 只能看到你去过的地方；不勾 = AI 能看全图（不影响作弊开关）");
                GameObject cc = GetContentContainer(screen);
                Transform rowT = null;
                if (cc != null && cc.transform.childCount > 0) rowT = cc.transform.GetChild(cc.transform.childCount - 1);

                var diag = new System.Text.StringBuilder();
                _fogToggle = PCheckboxLite.Create(cc != null ? cc.transform : screen.transform,
                    "AiEnableFogBox", 6f, initial, OnFogClicked, diag);
                var rt = (RectTransform)_fogToggle.transform;
                rt.SetAsLastSibling();
                Log.Info("[设置页] 迷雾勾选框已创建: " + diag);

                var localRow = rowT as RectTransform;
                MainThread.Enqueue(() =>
                {
                    try { PlaceAtContentRight(cc, localRow, rt); }
                    catch (System.Exception e) { Log.Warn("[设置页] 迷雾勾选框延迟定位异常: " + e); }
                });
            }
            catch (System.Exception e) { Log.Warn("[设置页] 建 AIEnableFog 行失败: " + e); }
        }

        static void OnFogClicked(bool nowChecked)
        {
            _dirty = true;
            Log.Info("[设置页] AIEnableFog 草稿 → " + nowChecked
                     + "（" + (nowChecked ? "有迷雾：AI 只看看得见的地方" : "全图：AI 能看全部") + "；点 [保存] 生效）");
            RefreshStatus("有未保存改动：AIEnableFog=" + nowChecked);
        }

        /// <summary>
        /// `allowMap`：勾上 = 允许 `GET /v1/map`（地图降采样，bbox + stride，单次最多 64×64 格）；
        /// 不勾 = 该端点回 `403 MAP_DISABLED`（**默认就是不勾**，DESIGN.md §2.3 第 4 条）。
        /// ⚠️ 与 `allowCheats` / `AIEnableFog` 都独立：它只管"能不能逐格读地图"。
        /// </summary>
        static void AddMapRow(InfoDialogScreen screen)
        {
            try
            {
                BridgeSettings st = Bridge.SettingsRef;
                bool initial = st != null && st.AllowMap;   // 默认 false（与 allowCheats 同向）

                screen.AddLineItem("允许ai读取地图格子数据（/v1/map）",
                                   "勾上 = 允许 GET /v1/map 按 bbox+stride 抽样读格子（元素/温度/质量，单次最多 64×64 格）；"
                                   + "不勾 = 该端点返回 403 MAP_DISABLED。与迷雾开关、作弊开关互不影响。");
                GameObject cc = GetContentContainer(screen);
                Transform rowT = null;
                if (cc != null && cc.transform.childCount > 0) rowT = cc.transform.GetChild(cc.transform.childCount - 1);

                var diag = new System.Text.StringBuilder();
                _mapToggle = PCheckboxLite.Create(cc != null ? cc.transform : screen.transform,
                    "AllowMapBox", 6f, initial, OnMapClicked, diag);
                var rt = (RectTransform)_mapToggle.transform;
                rt.SetAsLastSibling();
                Log.Info("[设置页] 地图勾选框已创建: " + diag);

                var localRow = rowT as RectTransform;
                MainThread.Enqueue(() =>
                {
                    try { PlaceAtContentRight(cc, localRow, rt); }
                    catch (System.Exception e) { Log.Warn("[设置页] 地图勾选框延迟定位异常: " + e.Message); }
                });
            }
            catch (System.Exception e) { Log.Warn("[设置页] 建 allowMap 行失败: " + e); }
        }

        static void OnMapClicked(bool nowChecked)
        {
            _dirty = true;
            Log.Info("[设置页] allowMap 草稿 → " + nowChecked
                     + "（" + (nowChecked ? "允许 /v1/map 读地图降采样" : "/v1/map 将回 403 MAP_DISABLED") + "；点 [保存] 生效）");
            RefreshStatus("有未保存改动：allowMap=" + nowChecked);
        }

        static void AddAllowCheatsRow(InfoDialogScreen screen)
        {
            try
            {
                BridgeSettings st = Bridge.SettingsRef;
                bool initial = st != null && st.AllowCheats;

                screen.AddLineItem(L("allowCheats（在此选项开启后，ai 可以在沙盒模式或 debug 模式下使用作弊指令）", ""),
                                   "点右侧方框切换");
                GameObject cc = GetContentContainer(screen);
                Transform rowT = null;
                if (cc != null && cc.transform.childCount > 0) rowT = cc.transform.GetChild(cc.transform.childCount - 1);

                var diag = new System.Text.StringBuilder();
                _allowCheatsToggle = PCheckboxLite.Create(cc != null ? cc.transform : screen.transform,
                    "AllowCheatsBox", 6f, initial, OnAllowCheatsClicked, diag);
                var rt = (RectTransform)_allowCheatsToggle.transform;
                rt.SetAsLastSibling();
                Log.Info("[设置页] 勾选框已创建: " + diag);

                var localRow = rowT as RectTransform;
                MainThread.Enqueue(() =>
                {
                    try { PlaceAtContentRight(cc, localRow, rt); }
                    catch (System.Exception e) { Log.Warn("[设置页] 勾选框延迟定位异常: " + e); }
                });
            }
            catch (System.Exception e) { Log.Warn("[设置页] 建 allowCheats 行失败: " + e); }
        }

        static void OnAllowCheatsClicked(bool nowChecked)
        {
            _dirty = true;
            Log.Info("[设置页] allowCheats 草稿 → " + nowChecked + "（未写盘，点 [保存] 生效）");
            RefreshStatus("有未保存改动：allowCheats=" + nowChecked);
        }

        // ---------------- 底部三个按钮 ----------------
        static void AddActionButtons(InfoDialogScreen screen)
        {
            try
            {
                screen.AddOption("保存", s => SaveAll(), true);
                screen.AddOption("打开配置目录", s => OpenConfigFolder(), false);
                screen.AddOption("关闭", s => { if (s != null) s.Deactivate(); }, false);
                Log.Info("[设置页] 按钮：保存 / 打开配置目录 / 关闭");
            }
            catch (System.Exception e) { Log.Warn("[设置页] 加按钮失败: " + e); }
        }

        // ---------------- 保存（一次性校验 + 写盘 + 回读）----------------
        static void SaveAll()
        {
            try
            {
                BridgeSettings st = Bridge.SettingsRef;
                if (st == null) { Log.Warn("[设置页] 保存失败：SettingsRef == null"); return; }

                // 1) 逐字段校验（不写盘）
                var vals = new float[Ctrls.Length];
                for (int i = 0; i < Ctrls.Length; i++)
                {
                    if (_fields[i] == null) { vals[i] = Ctrls[i].Get(st); continue; }   // 没建成输入框 ⇒ 保持原值
                    string text = _fields[i].text;
                    float v;
                    if (!float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out v))
                    {
                        Fail(Ctrls[i].Key, "不是合法数字（\"" + text + "\"）");
                        return;
                    }
                    var c = Ctrls[i];
                    if (v < c.Min || v > c.Max) { Fail(c.Key, "超出范围 " + Fmt(c, c.Min) + "–" + Fmt(c, c.Max)); return; }
                    vals[i] = (float)System.Math.Round(v, c.Decimals);
                }
                bool wantCheat = _allowCheatsToggle != null && _allowCheatsToggle.CurrentState == PCheckboxLite.Checked;
                bool wantFog = _fogToggle == null || _fogToggle.CurrentState == PCheckboxLite.Checked;   // 默认 true（有迷雾）
                // 勾选框没建成时**保持磁盘现值**（不能因为"界面没显示出来"就把 allowMap 悄悄关掉）
                bool wantMap = _mapToggle == null ? (st.AllowMap) : _mapToggle.CurrentState == PCheckboxLite.Checked;

                // 2) 备份
                string bak; string bakErr;
                bak = BridgeSettings.BackupConfig(out bakErr);
                if (bakErr != null) Log.Warn("[设置页] 备份失败（继续保存）: " + bakErr);
                else Log.Info("[设置页] 已备份: " + (bak ?? "<无旧文件，跳过>"));

                // 3) 应用 + 写盘 + 回读校验
                bool oldCheat = st.AllowCheats;
                bool oldFog = st.AIEnableFog;
                bool oldMap = st.AllowMap;
                var oldVals = new float[Ctrls.Length];
                for (int i = 0; i < Ctrls.Length; i++) oldVals[i] = Ctrls[i].Get(st);

                st.AllowCheats = wantCheat;
                st.AIEnableFog = wantFog;
                st.AllowMap = wantMap;
                for (int i = 0; i < Ctrls.Length; i++) Ctrls[i].Set(st, vals[i]);

                string err;
                bool ok = st.SaveAndVerify(out err);
                if (!ok)
                {
                    // 整批回滚
                    st.AllowCheats = oldCheat;
                    st.AIEnableFog = oldFog;
                    st.AllowMap = oldMap;
                    for (int i = 0; i < Ctrls.Length; i++) Ctrls[i].Set(st, oldVals[i]);
                    Log.Warn("[设置页] 保存失败（已回滚内存值）: " + err);
                    RefreshStatus("保存失败：" + err);
                    SyncFieldText();
                    return;
                }

                _dirty = false;
                Log.Info("[设置页] ② 写盘成功，已回读校验一致");
                var disk = BridgeSettings.LoadFromDisk();
                Log.Info("[设置页] ③ 磁盘回读: allowCheats=" + disk.AllowCheats
                         + " allowMap=" + disk.AllowMap
                         + " stress=" + disk.AlertStress + " kcal=" + disk.AlertCaloriesKcal
                         + " bladder=" + disk.AlertBladder + " eps=" + disk.AmountEpsilon + " port=" + disk.Port);

                string msg = "已保存";
                bool portChanged = System.Math.Abs(oldVals[PortIndex] - vals[PortIndex]) > 0.5f;
                if (portChanged) msg += "（端口 " + (int)oldVals[PortIndex] + " → " + (int)vals[PortIndex] + "，需重启 mod 才生效）";
                RefreshStatus(msg);
                SyncFieldText();
            }
            catch (System.Exception e)
            {
                Log.Warn("[设置页] 保存异常（已捕获）: " + e);
                RefreshStatus("保存异常：" + e.Message);
            }
        }

        static void Fail(string key, string why)
        {
            Log.Warn("[设置页] 校验失败：" + key + " — " + why + "（整批未写盘）");
            RefreshStatus(key + " 无效：" + why + "（整批未写盘）");
        }

        // ---------------- 输入框（克隆官方 FileNameDialog.inputField）----------------
        static void CreateInputFields(InfoDialogScreen screen, GameObject cc)
        {
            KInputTextField template = null;
            try
            {
                ScreenPrefabs prefs = ScreenPrefabs.Instance;
                FileNameDialog dlg = prefs != null ? prefs.FileNameDialog : null;
                if (dlg == null) { FailCreate("ScreenPrefabs.Instance.FileNameDialog 为空"); return; }

                FieldInfo fi = typeof(FileNameDialog).GetField("inputField",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (fi == null) { FailCreate("FileNameDialog.inputField 字段不存在"); return; }
                template = fi.GetValue(dlg) as KInputTextField;
                if (template == null) { FailCreate("FileNameDialog.inputField 取值为空"); return; }
            }
            catch (System.Exception e) { FailCreate("取模板异常: " + e.Message); return; }

            BridgeSettings st = Bridge.SettingsRef;
            int made = 0;
            for (int i = 0; i < Ctrls.Length; i++)
            {
                int idx = i;
                try
                {
                    GameObject go = Util.KInstantiateUI(template.gameObject, screen.gameObject, true);
                    if (go == null) { Log.Warn("[设置页] 第 " + idx + " 个输入框实例化返回 null"); continue; }
                    var field = go.GetComponent<KInputTextField>();
                    if (field == null) { Log.Warn("[设置页] 第 " + idx + " 个克隆体没有 KInputTextField"); SafeDestroy(go); continue; }

                    var c = Ctrls[idx];
                    var rt = go.transform as RectTransform;
                    if (rt != null) { rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f); rt.pivot = new Vector2(0.5f, 0.5f); rt.sizeDelta = new Vector2(FieldW, FieldH); }

                    // 内部 Text Area / Text / Placeholder 拉满（否则文字错位、被裁剪）
                    LayoutFieldChildren(go, field);

                    field.contentType = c.Decimals > 0 ? TMP_InputField.ContentType.DecimalNumber : TMP_InputField.ContentType.IntegerNumber;
                    field.lineType = TMP_InputField.LineType.SingleLine;
                    field.text = st != null ? Fmt(c, c.Get(st)) : "";

                    field.onValueChanged.AddListener(t =>
                    {
                        try
                        {
                            _dirty = true;
                            Log.Info("[设置页] 文本变化[" + Ctrls[idx].Key + "]: \"" + t + "\"（草稿，未写盘）");
                            RefreshStatus("有未保存改动（" + Ctrls[idx].Key + "）");
                        }
                        catch { }
                    });
                    field.onSelect.AddListener(t =>
                    {
                        try
                        {
                            Log.Info("[设置页] 焦点获得[" + Ctrls[idx].Key + "]");
                            if (CameraController.Instance != null) CameraController.Instance.DisableUserCameraControl = true;
                        }
                        catch (System.Exception e) { Log.Warn("[设置页] onSelect 异常: " + e.Message); }
                    });
                    field.onDeselect.AddListener(t =>
                    {
                        try
                        {
                            Log.Info("[设置页] 焦点丢失[" + Ctrls[idx].Key + "]");
                            if (CameraController.Instance != null) CameraController.Instance.DisableUserCameraControl = false;
                        }
                        catch (System.Exception e) { Log.Warn("[设置页] onDeselect 异常: " + e.Message); }
                    });
                    // 回车/失焦只记录，不写盘（写盘统一走 [保存]）
                    field.onEndEdit.AddListener(t =>
                    {
                        try { Log.Info("[设置页] 输入结束[" + Ctrls[idx].Key + "]: \"" + t + "\"（等 [保存]）"); } catch { }
                    });

                    EnsureClickSurface(go);
                    try { go.AddComponent<InputFieldClickProbe>(); } catch { }

                    _fields[idx] = field;
                    made++;
                    Log.Info("[设置页] 输入框[" + c.Key + "] 已创建: contentType=" + field.contentType + " text=\"" + field.text + "\"");
                }
                catch (System.Exception e) { Log.Warn("[设置页] 创建第 " + idx + " 个输入框异常: " + e.Message); }
            }

            _inputFieldUnavailable = made == 0;
            Log.Info("[设置页] 输入框创建完成: " + made + "/" + Ctrls.Length
                     + (_inputFieldUnavailable ? "（全部失败 ⇒ 只能看，不能改）" : ""));
        }

        static void FailCreate(string why)
        {
            _inputFieldUnavailable = true;
            for (int i = 0; i < _fields.Length; i++) _fields[i] = null;
            Log.Warn("[设置页] 输入框不可用（" + why + "）⇒ 本页只能查看");
            RefreshStatus("输入框不可用（" + why + "），本页只能查看");
        }

        static void SafeDestroy(GameObject go) { try { UnityEngine.Object.Destroy(go); } catch { } }

        static void SyncFieldText()
        {
            try
            {
                BridgeSettings st = Bridge.SettingsRef;
                if (st == null) return;
                for (int i = 0; i < Ctrls.Length && i < _fields.Length; i++)
                {
                    if (_fields[i] == null || _fields[i].isFocused) continue;
                    string txt = Fmt(Ctrls[i], Ctrls[i].Get(st));
                    if (_fields[i].text != txt) _fields[i].text = txt;
                }
                for (int i = 0; i < _rows.Count && i < Ctrls.Length; i++)
                {
                    if (_rows[i] == null) continue;
                    _rows[i].SetText(CtrlRowText(i, st));
                }
            }
            catch (System.Exception e) { Log.Warn("[设置页] SyncFieldText 异常: " + e.Message); }
        }

        static void RefreshStatus(string text)
        {
            try { if (_statusRow != null) _statusRow.SetText(text); } catch { }
        }

        // ---------------- 定位 ----------------
        static void PlaceInputField(GameObject cc, RectTransform rowRt, KInputTextField field)
        {
            try
            {
                if (field == null) return;
                var rt = field.transform as RectTransform;
                if (rt == null) return;
                RectTransform ccRt = cc != null ? cc.transform as RectTransform : null;

                Vector3 target;
                if (ccRt != null)
                {
                    Vector3 rightEdge = ccRt.TransformPoint(new Vector3(ccRt.rect.xMax, 0f, 0f));
                    float y = rowRt != null ? rowRt.TransformPoint(rowRt.rect.center).y : rightEdge.y;
                    float halfW = rt.sizeDelta.x * 0.5f * rt.lossyScale.x;
                    target = new Vector3(rightEdge.x - RightMargin - halfW, y, rightEdge.z);
                }
                else if (rowRt != null) target = rowRt.TransformPoint(rowRt.rect.center);
                else { Log.Warn("[设置页] 输入框定位缺少参考点"); return; }

                rt.position = target;
                rt.SetAsLastSibling();
            }
            catch (System.Exception e) { Log.Warn("[设置页] PlaceInputField 异常: " + e); }
        }

        static void PlaceAtContentRight(GameObject cc, RectTransform rowRt, RectTransform cbRt)
        {
            if (cbRt == null) return;
            cbRt.anchorMin = cbRt.anchorMax = new Vector2(1f, 0.5f);
            cbRt.pivot = new Vector2(1f, 0.5f);
            try
            {
                RectTransform ccRt = cc != null ? cc.transform as RectTransform : null;
                Vector3 target;
                if (ccRt != null)
                {
                    Vector3 rightEdge = ccRt.TransformPoint(new Vector3(ccRt.rect.xMax, 0f, 0f));
                    float y = rowRt != null ? rowRt.TransformPoint(rowRt.rect.center).y : rightEdge.y;
                    target = new Vector3(rightEdge.x - RightMargin, y, rightEdge.z);
                }
                else if (rowRt != null) target = rowRt.TransformPoint(rowRt.rect.center);
                else return;
                cbRt.position = target;
                cbRt.SetAsLastSibling();
            }
            catch (System.Exception e) { Log.Warn("[设置页] PlaceAtContentRight 异常: " + e.Message); }
        }

        // ---------------- 行工具 ----------------
        static InfoScreenLineItem AddRowTracked(InfoDialogScreen screen, GameObject cc, string text, string tooltip)
        {
            try
            {
                int before = cc != null ? cc.transform.childCount : -1;
                screen.AddLineItem(text, tooltip);
                if (cc == null) return null;
                if (cc.transform.childCount <= before) { Log.Warn("[设置页] 行计数未增加: " + text); return null; }
                Transform t = cc.transform.GetChild(cc.transform.childCount - 1);
                return t.GetComponent<InfoScreenLineItem>();
            }
            catch (System.Exception e) { Log.Warn("[设置页] AddRowTracked 异常: " + e.Message); return null; }
        }

        static InfoScreenLineItem AddRowTracked(InfoDialogScreen screen, GameObject cc, string text)
        {
            return AddRowTracked(screen, cc, text, null);
        }

        static string L(string label, string value)
        {
            return label + "    " + (value ?? "");
        }

        static string Fmt(Ctrl c, float v)
        {
            return v.ToString("F" + c.Decimals, CultureInfo.InvariantCulture);
        }

        static GameObject GetContentContainer(InfoDialogScreen screen)
        {
            try
            {
                FieldInfo f = typeof(InfoDialogScreen).GetField("contentContainer", BindingFlags.Instance | BindingFlags.NonPublic);
                var cc = f == null ? null : f.GetValue(screen) as GameObject;
                if (cc == null) Log.Warn("[设置页] 拿不到 contentContainer");
                return cc;
            }
            catch (System.Exception e) { Log.Warn("[设置页] 反射 contentContainer 异常: " + e.Message); return null; }
        }

        // ---------------- canvas 选择（按分数）----------------
        static GameObject ResolveParent(out string trace)
        {
            var sb = new System.Text.StringBuilder(320);
            GameObject p = null;

            GameScreenManager gsm = null;
            try { gsm = GameScreenManager.Instance; } catch { }
            if (gsm == null) sb.Append("GameScreenManager.Instance=null; ");
            else
            {
                p = gsm.ssOverlayCanvas;
                sb.Append("ssOverlayCanvas=").Append(p != null ? "OK" : "null").Append("; ");
                if (p == null) { p = gsm.ssCameraCanvas; sb.Append("ssCameraCanvas=").Append(p != null ? "OK" : "null").Append("; "); }
            }
            if (p == null)
            {
                KScreen[] screens = null;
                try { screens = Object.FindObjectsOfType<KScreen>(); } catch { }
                sb.Append("liveKScreens=").Append(screens == null ? 0 : screens.Length).Append("; ");
                if (screens != null)
                {
                    KScreen best = null; int bestScore = int.MinValue;
                    for (int i = 0; i < screens.Length; i++)
                    {
                        KScreen sc = screens[i];
                        if (sc == null) continue;
                        int s = ScoreScreen(sc);
                        Canvas c = null; try { c = sc.canvas; } catch { }
                        sb.Append(sc.GetType().Name).Append(':').Append(s).Append(c != null ? "+canvas" : "-nocanvas").Append(' ');
                        if (c == null || s < 0) continue;
                        if (s > bestScore) { bestScore = s; best = sc; }
                    }
                    if (best != null) { p = best.canvas.gameObject; sb.Append("=> picked=").Append(best.GetType().Name).Append("; "); }
                }
            }
            if (p == null)
            {
                Canvas[] canvases = null;
                try { canvases = Object.FindObjectsOfType<Canvas>(); } catch { }
                sb.Append("liveCanvases=").Append(canvases == null ? 0 : canvases.Length).Append("; ");
                if (canvases != null)
                    for (int i = 0; i < canvases.Length && p == null; i++)
                    {
                        Canvas c = canvases[i];
                        if (c != null && c.renderMode == RenderMode.ScreenSpaceOverlay) { p = c.gameObject; sb.Append("usedOverlayCanvas; "); }
                    }
            }
            if (p == null) sb.Append("=> 全部失败，交给 ONI 默认父节点");
            trace = sb.ToString();
            return p;
        }

        static int ScoreScreen(KScreen s)
        {
            if (s is ToolTipScreen) return -1;
            if (s is HoverTextScreen) return -1;
            if (s is NameDisplayScreen) return -1;
            if (s is MainMenu) return 100;
            if (s is ModsScreen) return 90;
            if (s is OptionsMenuScreen) return 80;
            if (s is KModalScreen) return 70;
            if (s is Hud) return 60;
            return 10;
        }

        // ---------------- 已下线但保留（便于回退）：加减按钮 / 切换字段 ----------------
        static void AdjustStep(int dir)
        {
            // M4.4 起不再有 [-]/[+] 按钮；保留实现只是为了让回退时少改代码。
            Log.Info("[设置页] AdjustStep 已下线（dir=" + dir + "）");
        }

        static void CycleField()
        {
            Log.Info("[设置页] CycleField 已下线");
        }

        // ---------------- 输入框内部布局（M4.3c-2b）----------------
        static void LayoutFieldChildren(GameObject go, KInputTextField field)
        {
            try
            {
                if (!_structureLogged)
                {
                    _structureLogged = true;
                    var names = new System.Text.StringBuilder();
                    for (int i = 0; i < go.transform.childCount; i++)
                    {
                        if (names.Length > 0) names.Append(", ");
                        names.Append(go.transform.GetChild(i).name);
                    }
                    Log.Info("[设置页] 输入框内部子物体: " + names);
                }

                Transform area = FindChildByName(go.transform, "Text Area");
                if (area != null) Fill(area as RectTransform, 4f, 1f);
                else Log.Warn("[设置页] 没找到 'Text Area' 子物体");

                var text = field.textComponent;
                if (text != null)
                {
                    Fill(text.rectTransform, 0f, 0f);
                    text.alignment = TextAlignmentOptions.MidlineRight;
                    if (text.fontSize > 18f) text.fontSize = 16f;
                }
                var ph = field.placeholder;
                if (ph != null) Fill(ph.rectTransform, 4f, 1f);
            }
            catch (System.Exception e) { Log.Warn("[设置页] LayoutFieldChildren 异常: " + e.Message); }
        }

        static void Fill(RectTransform rt, float padX, float padY)
        {
            if (rt == null) return;
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = new Vector2(padX, padY);
            rt.offsetMax = new Vector2(-padX, -padY);
        }

        static Transform FindChildByName(Transform parent, string name)
        {
            if (parent == null) return null;
            for (int i = 0; i < parent.childCount; i++)
            {
                Transform c = parent.GetChild(i);
                if (c.name == name) return c;
                Transform nested = FindChildByName(c, name);
                if (nested != null) return nested;
            }
            return null;
        }

        static void EnsureClickSurface(GameObject go)
        {
            try
            {
                var gfx = go.GetComponents<Graphic>();
                if (gfx.Length > 0)
                {
                    bool any = false;
                    for (int i = 0; i < gfx.Length; i++) if (gfx[i].raycastTarget) { any = true; break; }
                    if (!any) { for (int i = 0; i < gfx.Length; i++) gfx[i].raycastTarget = true; }
                    return;
                }
                var img = go.AddComponent<Image>();
                img.color = new Color(1f, 1f, 1f, 0.01f);
                img.raycastTarget = true;
            }
            catch (System.Exception e) { Log.Warn("[设置页] EnsureClickSurface 异常: " + e.Message); }
        }

        static void OpenConfigFolder()
        {
            string dir = BridgeSettings.ConfigDir;
            Log.Info("[设置页] 打开配置目录: " + dir);
            try { System.Diagnostics.Process.Start("explorer.exe", "\"" + dir + "\""); }
            catch (System.Exception e) { Log.Warn("[设置页] 打开目录失败: " + e.Message); }
        }
    }
}
