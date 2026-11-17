using System;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;

namespace OniAiBridge
{
    /// <summary>
    /// 设置：落在 mods\config\OniAiBridge\settings.json（ONI 的既有约定，见 mods\config\&lt;staticID&gt;\）。
    /// M0 只有三个字段：port / allowCheats / token。
    /// 解析故意用正则而不是 JSON 库：入参只有三个已知字段，少一个依赖就少一种加载失败的可能。
    /// </summary>
    internal sealed class BridgeSettings
    {
        public int Port = 8770;
        public bool AllowCheats = false;

        /// <summary>
        /// M5.3 `AIEnableFog`：**勾上 = 有迷雾**（AI 只报"看得见"的地方）；不勾 = 全图。
        /// ⚠️ 与 `AllowCheats` **完全独立**：allowCheats 管"能不能动手改状态"，这个管"能看多少情报"。
        /// ⚠️ 键名按用户指定用 `AIEnableFog`（游戏内部概念叫 FogOfWar，此处刻意不改）。
        /// </summary>
        public bool AIEnableFog = true;

        /// <summary>
        /// M6.89 `allowMap`：**默认 false** —— `GET /v1/map`（地图降采样）的闸门。
        ///
        /// 依据 `DESIGN.md` §2.3 第 4 条「地图不默认下发」：逐格数据量大、也最容易被当成"透视"，
        /// 所以它必须由用户显式打开；关着时请求回 `403 MAP_DISABLED`（**不是**静默返回空）。
        /// ⚠️ 与 `allowCheats` 独立：它只允许"多读",不允许"动手改"。
        /// </summary>
        public bool AllowMap = false;

        /// <summary>
        /// M6.61 `FixPlanScreenPriorityNRE`：吞掉 `PlanScreen.GetBuildingPriority()` 的 NullReferenceException
        /// （该异常发生在建造**成功之后**的 PostProcessBuild 里，会让调用方以为失败）。
        /// 默认 true；若以后发现副作用，设 false 即可**不生效**（补丁仍挂，但只观察不吞）。
        /// </summary>
        public bool FixPlanScreenPriorityNRE = true;
        public string Token = "";
        /// <summary>属性值变化达到这个幅度才推 diff（避免每秒浮点抖动刷屏）。</summary>
        public float AmountEpsilon = 0.1f;
        // 阈值告警（SSE alert 事件）。文档里写明默认值，允许用户在 settings.json 覆盖。
        public float AlertStress = 60f;         // 压力 >= 它 => stress_high
        public float AlertCaloriesKcal = 1500f; // 卡路里 <= 它 => calories_low
        public float AlertBladder = 90f;        // 膀胱 >= 它 => bladder_full

        public const string StaticId = "OniAiBridge";

        public static string ConfigDir
        {
            get
            {
                string docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                return Path.Combine(docs, "Klei", "OxygenNotIncluded", "mods", "config", StaticId);
            }
        }

        public static string ConfigPath { get { return Path.Combine(ConfigDir, "settings.json"); } }

        /// <summary>只读解析磁盘上的 settings.json（不生成 token、不写盘），用于"写盘后回读校验"。</summary>
        public static BridgeSettings LoadFromDisk()
        {
            var s = new BridgeSettings();
            if (!File.Exists(ConfigPath)) return s;
            try
            {
                string txt = File.ReadAllText(ConfigPath);
                var mPort = Regex.Match(txt, "\"port\"\\s*:\\s*(\\d+)");
                if (mPort.Success) s.Port = int.Parse(mPort.Groups[1].Value, CultureInfo.InvariantCulture);
                var mCheat = Regex.Match(txt, "\"allowCheats\"\\s*:\\s*(true|false)", RegexOptions.IgnoreCase);
                if (mCheat.Success) s.AllowCheats = mCheat.Groups[1].Value.Equals("true", StringComparison.OrdinalIgnoreCase);
                var mFix = Regex.Match(txt, "\"FixPlanScreenPriorityNRE\"\\s*:\\s*(true|false)", RegexOptions.IgnoreCase);
            if (mFix.Success) s.FixPlanScreenPriorityNRE = mFix.Groups[1].Value.Equals("true", StringComparison.OrdinalIgnoreCase);
            var mFog = Regex.Match(txt, "\"AIEnableFog\"\\s*:\\s*(true|false)", RegexOptions.IgnoreCase);
                if (mFog.Success) s.AIEnableFog = mFog.Groups[1].Value.Equals("true", StringComparison.OrdinalIgnoreCase);
                // M6.89：allowMap（地图降采样闸门，默认 false；回读也要带上，否则"写盘校验"对它等于没校验）
                var mMap = Regex.Match(txt, "\"allowMap\"\\s*:\\s*(true|false)", RegexOptions.IgnoreCase);
                if (mMap.Success) s.AllowMap = mMap.Groups[1].Value.Equals("true", StringComparison.OrdinalIgnoreCase);
                var mTok = Regex.Match(txt, "\"token\"\\s*:\\s*\"([^\"]*)\"");
                if (mTok.Success) s.Token = mTok.Groups[1].Value;
                // M4.3a：数值项也要回读，否则"写盘校验"对它们等于没校验
                s.AmountEpsilon = ReadFloat(txt, "amountEpsilon", s.AmountEpsilon);
                s.AlertStress = ReadFloat(txt, "alertStress", s.AlertStress);
                s.AlertCaloriesKcal = ReadFloat(txt, "alertCaloriesKcal", s.AlertCaloriesKcal);
                s.AlertBladder = ReadFloat(txt, "alertBladder", s.AlertBladder);
            }
            catch (System.Exception e) { Log.Warn("回读 settings.json 失败: " + e.Message); }
            return s;
        }

        /// <summary>从文本里读一个 float 字段（读不到就返回 fallback）。</summary>
        static float ReadFloat(string txt, string key, float fallback)
        {
            var m = Regex.Match(txt, "\"" + key + "\"\\s*:\\s*(-?[0-9]+(?:\\.[0-9]+)?)");
            if (!m.Success) return fallback;
            float v;
            if (float.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out v)) return v;
            return fallback;
        }

        static bool Near(float a, float b) { return System.Math.Abs(a - b) <= 0.0005f; }

        /// <summary>写盘 + 回读校验。返回 false 时 error 说明原因（UI 要如实显示，不静默）。</summary>
        public bool SaveAndVerify(out string error)
        {
            error = null;
            try { Save(); }
            catch (System.Exception e) { error = "写盘异常: " + e.Message; return false; }
            if (!File.Exists(ConfigPath)) { error = "写盘后文件不存在"; return false; }
            BridgeSettings back = LoadFromDisk();
            if (back.AllowCheats != AllowCheats) { error = "回读 allowCheats=" + back.AllowCheats + " 与写入值 " + AllowCheats + " 不一致"; return false; }
            if (back.AIEnableFog != AIEnableFog) { error = "回读 AIEnableFog=" + back.AIEnableFog + " 与写入值 " + AIEnableFog + " 不一致"; return false; }
            if (back.AllowMap != AllowMap) { error = "回读 allowMap=" + back.AllowMap + " 与写入值 " + AllowMap + " 不一致"; return false; }
              if (back.Port != Port) { error = "回读 port=" + back.Port + " 与写入值 " + Port + " 不一致"; return false; }
            if (!Near(back.AmountEpsilon, AmountEpsilon)) { error = "回读 amountEpsilon=" + back.AmountEpsilon + " 与写入值 " + AmountEpsilon + " 不一致"; return false; }
            if (!Near(back.AlertStress, AlertStress)) { error = "回读 alertStress=" + back.AlertStress + " 与写入值 " + AlertStress + " 不一致"; return false; }
            if (!Near(back.AlertCaloriesKcal, AlertCaloriesKcal)) { error = "回读 alertCaloriesKcal=" + back.AlertCaloriesKcal + " 与写入值 " + AlertCaloriesKcal + " 不一致"; return false; }
            if (!Near(back.AlertBladder, AlertBladder)) { error = "回读 alertBladder=" + back.AlertBladder + " 与写入值 " + AlertBladder + " 不一致"; return false; }
            return true;
        }

        /// <summary>写盘前备份：复制成 settings.json.bak-&lt;时间戳&gt;。返回备份路径（失败返回 null）。</summary>
        public static string BackupConfig(out string error)
        {
            error = null;
            try
            {
                if (!File.Exists(ConfigPath)) { return null; }   // 还没有文件，无需备份
                string stamp = System.DateTime.Now.ToString("yyyyMMdd-HHmmss");
                string bak = ConfigPath + ".bak-" + stamp;
                File.Copy(ConfigPath, bak, true);
                return bak;
            }
            catch (System.Exception e)
            {
                error = e.Message;
                return null;
            }
        }
        /// <summary>配置目录能否写（借用 DGSM 的 IsDirectoryWritable 思路）：UI 上要能告诉用户"改了存不下"。</summary>
        public static bool IsConfigWritable()
        {
            try
            {
                Directory.CreateDirectory(ConfigDir);
                string probe = Path.Combine(ConfigDir, ".write-probe");
                File.WriteAllText(probe, "ok");
                File.Delete(probe);
                return true;
            }
            catch { return false; }
        }

        public static BridgeSettings Load()
        {
            var s = new BridgeSettings();
            try
            {
                if (File.Exists(ConfigPath))
                {
                    string txt = File.ReadAllText(ConfigPath);
                    var mPort = Regex.Match(txt, "\"port\"\\s*:\\s*(\\d+)");
                    if (mPort.Success) s.Port = int.Parse(mPort.Groups[1].Value, CultureInfo.InvariantCulture);
                    var mCheat = Regex.Match(txt, "\"allowCheats\"\\s*:\\s*(true|false)", RegexOptions.IgnoreCase);
                    if (mCheat.Success) s.AllowCheats = mCheat.Groups[1].Value.Equals("true", StringComparison.OrdinalIgnoreCase);
                    var mFix = Regex.Match(txt, "\"FixPlanScreenPriorityNRE\"\\s*:\\s*(true|false)", RegexOptions.IgnoreCase);
            if (mFix.Success) s.FixPlanScreenPriorityNRE = mFix.Groups[1].Value.Equals("true", StringComparison.OrdinalIgnoreCase);
            var mFog = Regex.Match(txt, "\"AIEnableFog\"\\s*:\\s*(true|false)", RegexOptions.IgnoreCase);
                    if (mFog.Success) s.AIEnableFog = mFog.Groups[1].Value.Equals("true", StringComparison.OrdinalIgnoreCase);
                    // M6.89：allowMap（地图降采样闸门，默认 false）
                    var mMap = Regex.Match(txt, "\"allowMap\"\\s*:\\s*(true|false)", RegexOptions.IgnoreCase);
                    if (mMap.Success) s.AllowMap = mMap.Groups[1].Value.Equals("true", StringComparison.OrdinalIgnoreCase);
                    var mTok = Regex.Match(txt, "\"token\"\\s*:\\s*\"([^\"]*)\"");
                    if (mTok.Success) s.Token = mTok.Groups[1].Value;
                    var mEps = Regex.Match(txt, "\"amountEpsilon\"\\s*:\\s*([0-9.]+)"); if (mEps.Success) s.AmountEpsilon = float.Parse(mEps.Groups[1].Value, CultureInfo.InvariantCulture);
                    var mSt = Regex.Match(txt, "\"alertStress\"\\s*:\\s*([0-9.]+)"); if (mSt.Success) s.AlertStress = float.Parse(mSt.Groups[1].Value, CultureInfo.InvariantCulture);
                    var mCal = Regex.Match(txt, "\"alertCaloriesKcal\"\\s*:\\s*([0-9.]+)"); if (mCal.Success) s.AlertCaloriesKcal = float.Parse(mCal.Groups[1].Value, CultureInfo.InvariantCulture);
                    var mBl = Regex.Match(txt, "\"alertBladder\"\\s*:\\s*([0-9.]+)"); if (mBl.Success) s.AlertBladder = float.Parse(mBl.Groups[1].Value, CultureInfo.InvariantCulture);
                }
            }
            catch (Exception e)
            {
                Log.Warn("读取 settings.json 失败，用默认值: " + e.Message);
            }

            if (string.IsNullOrEmpty(s.Token))
            {
                s.Token = Guid.NewGuid().ToString("N");          // 122 bit，够用
                s.Save();
                Log.Info("已生成新 token，写入 " + ConfigPath);
            }
            return s;
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(ConfigDir);
                string json =
                    "{\n" +
                    "  \"_comment\": \"ONI AI Bridge settings. allowCheats gates the in-game Debug tools; leave it false for normal play.\",\n" +
                    "  \"port\": " + Port + ",\n" +
                    "  \"allowCheats\": " + (AllowCheats ? "true" : "false") + ",\n" +
                    "  \"AIEnableFog\": " + (AIEnableFog ? "true" : "false") + ",\n" +
                    "  \"allowMap\": " + (AllowMap ? "true" : "false") + ",\n" +
            "  \"FixPlanScreenPriorityNRE\": " + (FixPlanScreenPriorityNRE ? "true" : "false") + ",\n" +
                    "  \"token\": \"" + Token + "\",\n" +
                    "  \"amountEpsilon\": " + AmountEpsilon.ToString(CultureInfo.InvariantCulture) + ",\n" +
                    "  \"alertStress\": " + AlertStress.ToString(CultureInfo.InvariantCulture) + ",\n" +
                    "  \"alertCaloriesKcal\": " + AlertCaloriesKcal.ToString(CultureInfo.InvariantCulture) + ",\n" +
                    "  \"alertBladder\": " + AlertBladder.ToString(CultureInfo.InvariantCulture) + "\n" +
                    "}\n";
                File.WriteAllText(ConfigPath, json, new System.Text.UTF8Encoding(false));
            }
            catch (Exception e)
            {
                Log.Warn("写 settings.json 失败（可用但不可持久化）: " + e.Message);
            }
        }
    }
}
