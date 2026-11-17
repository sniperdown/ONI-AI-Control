using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

// ⚠️ 不要 using System 之后直接用 Action/DateTime：Assembly-CSharp 里的全局 Action 枚举会遮蔽 System.Action。
namespace OniAiBridge
{
    /// <summary>
    /// 网页版配置的接口层（M4.4）：
    ///  * `GET /v1/settings` —— 只读快照（**不含 token 内容**，只给长度）
    ///  * `PUT /v1/settings` —— **逐字段**更新：只改请求里出现的字段，其余保持原值
    ///    （整体覆盖会把没传的字段清空，这是必须避免的）
    ///
    /// 校验规则与游戏内设置页保持一致：数值 clamp 到合法范围，越界会回一个 `clamped` 列表。
    /// 写盘走 `BridgeSettings.SaveAndVerify`（写盘后重新读文件逐字段比对），失败则**回滚**。
    /// </summary>
    internal static class SettingsApi
    {
        // 字段范围（与 BridgeOptionsScreen.Fields 对齐；表驱动，避免两处不一致）
        struct Range { public string Key; public float Min; public float Max; public int Decimals; }

        static readonly Range[] Ranges =
        {
            new Range { Key = "alertStress",       Min = 0f,    Max = 100f,   Decimals = 0 },
            new Range { Key = "alertCaloriesKcal", Min = 100f,  Max = 10000f, Decimals = 0 },
            new Range { Key = "alertBladder",      Min = 0f,    Max = 100f,   Decimals = 0 },
            new Range { Key = "amountEpsilon",     Min = 0f,    Max = 10f,    Decimals = 2 },
        };

        const int PortMin = 1024, PortMax = 65535;

        /// <summary>GET 的 data 部分。</summary>
        public static string GetJson()
        {
            BridgeSettings s = Bridge.SettingsRef;
            if (s == null) return "null";

            var sb = new StringBuilder(320);
            sb.Append('{');
            sb.Append("\"mod\":").Append(Str(Bridge.ModVersion));
            sb.Append(",\"stage\":").Append(Str(Bridge.Stage));
            sb.Append(",\"apiVersion\":").Append(Str(Bridge.ApiVersion));
            sb.Append(",\"port\":").Append(Bridge.Port);
            sb.Append(",\"tokenLength\":").Append(s.Token == null ? 0 : s.Token.Length);
            sb.Append(",\"settings\":{");
            sb.Append("\"allowCheats\":").Append(s.AllowCheats ? "true" : "false");
            sb.Append(",\"alertStress\":").Append(Num(s.AlertStress, 1));
            sb.Append(",\"alertCaloriesKcal\":").Append(Num(s.AlertCaloriesKcal, 1));
            sb.Append(",\"alertBladder\":").Append(Num(s.AlertBladder, 1));
            sb.Append(",\"amountEpsilon\":").Append(Num(s.AmountEpsilon, 3));
            sb.Append(",\"port\":").Append(s.Port);
            sb.Append("},\"notes\":{");
            sb.Append("\"port\":").Append(Str("改端口需要重启 mod（当前 HTTP 监听仍绑在 " + Bridge.Port + "）"));
            sb.Append("}}");
            return sb.ToString();
        }

        /// <summary>PUT 的 data 部分；失败时返回 null 并给出 status/code/message。</summary>
        public static string Put(string body, out int status, out string code, out string message)
        {
            status = 200; code = null; message = null;

            BridgeSettings s = Bridge.SettingsRef;
            if (s == null) { status = 503; code = "NOT_READY"; message = "settings 还没加载完"; return null; }
            if (string.IsNullOrEmpty(body)) { status = 400; code = "BAD_REQUEST"; message = "空请求体"; return null; }

            // token 明确禁止改（安全）
            if (Regex.IsMatch(body, "\"token\"\\s*:"))
            {
                status = 400; code = "FIELD_READONLY";
                message = "token 不允许通过接口修改（安全考虑）；请直接编辑 settings.json 后重启 mod";
                return null;
            }

            // 先备份原值，失败可回滚
            bool oldCheat = s.AllowCheats;
            float oldStress = s.AlertStress, oldCal = s.AlertCaloriesKcal, oldBlad = s.AlertBladder, oldEps = s.AmountEpsilon;
            int oldPort = s.Port;

            var applied = new StringBuilder();
            var clamped = new StringBuilder();
            var notes = new StringBuilder();

            float v;
            if (TryReadFloat(body, "alertStress", out v)) ApplyNum(Ranges[0], ref s.AlertStress, v, applied, clamped, ref status, ref code, ref message);
            if (status != 200) { Revert(s, oldCheat, oldStress, oldCal, oldBlad, oldEps, oldPort); return null; }
            if (TryReadFloat(body, "alertCaloriesKcal", out v)) ApplyNum(Ranges[1], ref s.AlertCaloriesKcal, v, applied, clamped, ref status, ref code, ref message);
            if (status != 200) { Revert(s, oldCheat, oldStress, oldCal, oldBlad, oldEps, oldPort); return null; }
            if (TryReadFloat(body, "alertBladder", out v)) ApplyNum(Ranges[2], ref s.AlertBladder, v, applied, clamped, ref status, ref code, ref message);
            if (status != 200) { Revert(s, oldCheat, oldStress, oldCal, oldBlad, oldEps, oldPort); return null; }
            if (TryReadFloat(body, "amountEpsilon", out v)) ApplyNum(Ranges[3], ref s.AmountEpsilon, v, applied, clamped, ref status, ref code, ref message);
            if (status != 200) { Revert(s, oldCheat, oldStress, oldCal, oldBlad, oldEps, oldPort); return null; }

            var mCheat = Regex.Match(body, "\"allowCheats\"\\s*:\\s*(true|false)", RegexOptions.IgnoreCase);
            if (mCheat.Success)
            {
                s.AllowCheats = mCheat.Groups[1].Value.Equals("true", System.StringComparison.OrdinalIgnoreCase);
                AppendPair(applied, "allowCheats", s.AllowCheats ? "true" : "false");
            }

            var mPort = Regex.Match(body, "\"port\"\\s*:\\s*(\\d+)");
            if (mPort.Success)
            {
                int p;
                if (!int.TryParse(mPort.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out p)
                    || p < PortMin || p > PortMax)
                {
                    status = 400; code = "INVALID_FIELD";
                    message = "port 必须是 " + PortMin + "–" + PortMax + " 之间的整数";
                    Revert(s, oldCheat, oldStress, oldCal, oldBlad, oldEps, oldPort);
                    return null;
                }
                s.Port = p;
                AppendPair(applied, "port", p.ToString(CultureInfo.InvariantCulture));
                AppendPair(notes, "port", "改端口需要重启 mod 才生效（当前监听仍在 " + Bridge.Port + "）");
            }

            if (applied.Length == 0)
            {
                status = 400; code = "NO_FIELDS";
                message = "没有可识别的字段（允许：allowCheats/alertStress/alertCaloriesKcal/alertBladder/amountEpsilon/port）";
                Revert(s, oldCheat, oldStress, oldCal, oldBlad, oldEps, oldPort);
                return null;
            }

            string err;
            bool ok = s.SaveAndVerify(out err);
            if (!ok)
            {
                Revert(s, oldCheat, oldStress, oldCal, oldBlad, oldEps, oldPort);
                status = 500; code = "WRITE_FAILED"; message = "写盘失败，已回滚：" + err;
                return null;
            }

            Log.Info("[settings-api] PUT 已应用: " + applied);
            var sb = new StringBuilder(256);
            sb.Append("{\"applied\":{").Append(applied).Append('}');
            sb.Append(",\"clamped\":[").Append(clamped).Append(']');
            sb.Append(",\"notes\":{").Append(notes).Append('}');
            sb.Append(",\"settings\":").Append(GetJson()).Append('}');
            return sb.ToString();
        }

        static void ApplyNum(Range r, ref float target, float wanted, StringBuilder applied, StringBuilder clamped,
                             ref int status, ref string code, ref string message)
        {
            float v = wanted;
            bool wasClamped = false;
            if (v < r.Min) { v = r.Min; wasClamped = true; }
            if (v > r.Max) { v = r.Max; wasClamped = true; }
            v = (float)System.Math.Round(v, r.Decimals);
            target = v;
            AppendPair(applied, r.Key, Num(v, r.Decimals));
            if (wasClamped)
            {
                if (clamped.Length > 0) clamped.Append(',');
                clamped.Append(Str(r.Key));
            }
        }

        static void Revert(BridgeSettings s, bool cheat, float stress, float cal, float blad, float eps, int port)
        {
            try
            {
                s.AllowCheats = cheat; s.AlertStress = stress; s.AlertCaloriesKcal = cal;
                s.AlertBladder = blad; s.AmountEpsilon = eps; s.Port = port;
            }
            catch { }
        }

        static bool TryReadFloat(string body, string key, out float v)
        {
            v = 0f;
            var m = Regex.Match(body, "\"" + key + "\"\\s*:\\s*(-?[0-9]+(?:\\.[0-9]+)?)");
            if (!m.Success) return false;
            return float.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out v);
        }

        static void AppendPair(StringBuilder sb, string k, string v)
        {
            if (sb.Length > 0) sb.Append(',');
            sb.Append(Str(k)).Append(':').Append(v.StartsWith("\"") ? v : Str(v));
        }

        static string Str(string s)
        {
            if (s == null) return "null";
            var sb = new StringBuilder(s.Length + 8);
            sb.Append('"');
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '"' || c == '\\') sb.Append('\\').Append(c);
                else if (c == '\n') sb.Append("\\n");
                else if (c == '\r') sb.Append("\\r");
                else if (c == '\t') sb.Append("\\t");
                else sb.Append(c);
            }
            sb.Append('"');
            return sb.ToString();
        }

        static string Num(float v, int decimals)
        {
            return v.ToString("F" + decimals, CultureInfo.InvariantCulture);
        }
    }
}
