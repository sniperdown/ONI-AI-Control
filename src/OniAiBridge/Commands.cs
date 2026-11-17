using System.Collections.Generic;
using System.Text.RegularExpressions;

// ⚠️ 不要 using System 之后直接用 Action/DateTime：Assembly-CSharp 里有同名全局类型会遮蔽它们。
namespace OniAiBridge
{
    /// <summary>
    /// 命令层（M2 只做**普通操作**，不碰作弊/内置 Debug）。
    /// 每个 op 都：参数校验 → 主线程执行 → 明确错误码。绝不静默失败。
    /// </summary>
    internal static class Commands
    {
        public sealed class Def
        {
            public string Op;
            public string Summary;
            public string Args;
            public bool RequiresCheats;
            public bool Available;
            public string Note;
        }

        public static Def[] All()
        {
            return new Def[]
            {
                new Def { Op = "pause", Summary = "暂停游戏", Args = "{}", Available = true, Note = "执行后回读 IsPaused 校验" },
                new Def { Op = "resume", Summary = "继续游戏（取消暂停）", Args = "{}", Available = true, Note = "执行后回读 IsPaused 校验" },
                new Def { Op = "setSpeed", Summary = "设置倍速", Args = "{\"speed\":1|2|3}  // 1x/2x/3x", Available = true,
                          Note = "不改变暂停状态；内部 0 基，执行后回读 GetSpeed() 校验，没生效会回 409 NOT_APPLIED" },
                new Def { Op = "setChorePriority", Summary = "改某个复制人当前任务的优先级", Args = "{\"dupe\":\"dbId 或名字\",\"priority\":1..9,\"priorityClass\":\"idle|basic|high|personalNeeds|topPriority|compulsory\"}",
                          Available = true, Note = "priorityClass 可选，默认 high；复制人当前没有任务时返回 404 NOT_FOUND_TARGET" },
            };
        }

        public static string CommandsJson()
        {
            var sb = new System.Text.StringBuilder();
            sb.Append('[');
            Def[] all = All();
            for (int i = 0; i < all.Length; i++)
            {
                if (i > 0) sb.Append(',');
                var d = all[i];
                sb.Append("{\"op\":").Append(Snap.Str(d.Op));
                sb.Append(",\"summary\":").Append(Snap.Str(d.Summary));
                sb.Append(",\"args\":").Append(Snap.Str(d.Args));
                sb.Append(",\"requiresCheats\":").Append(d.RequiresCheats ? "true" : "false");
                sb.Append(",\"available\":").Append(d.Available ? "true" : "false");
                sb.Append(",\"available\":").Append(d.Available ? "true" : "false");
                sb.Append(",\"note\":").Append(Snap.Str(d.Note));
                sb.Append('}');
            }
            // 作弊 / 内置 Debug 调度：**永远列出来**（M4 要求），用 available + unavailableReason 表达现在能不能用
            string why = Gate.UnavailableReason();
            bool cheatOk = Gate.CheatsUnlocked;
            foreach (var d in Cheats.Defs)
            {
                bool isDebugOp = d[0] == "debug";
                bool avail = isDebugOp ? Gate.AllowCheats : cheatOk;
                sb.Append(",{\"op\":").Append(Snap.Str(d[0]));
                sb.Append(",\"summary\":").Append(Snap.Str(d[1]));
                sb.Append(",\"args\":").Append(Snap.Str(d[2]));
                sb.Append(",\"requiresCheats\":true");
                sb.Append(",\"available\":").Append(avail ? "true" : "false");
                sb.Append(",\"unavailableCode\":").Append(Snap.Str(avail ? null : (isDebugOp ? "ALLCHEATS_DISABLED" : Gate.UnavailableCode())));
                sb.Append(",\"unavailableReason\":").Append(Snap.Str(avail ? null : (isDebugOp ? Gate.UnavailableReason() : why)));
                sb.Append(",\"note\":").Append(Snap.Str(d[3]));
                sb.Append('}');
            }
            sb.Append(']');
            string __json = sb.ToString();
            JsonCheck.Validate(__json, "commands");   // 手写 JSON ⇒ 出门前必须能解析
            return __json;
        }

        /// <summary>在主线程执行。返回结果 JSON（成功）或 null（失败，错误码/消息走 out 参数）。</summary>
        public static string Execute(string op, string body, out int status, out string errCode, out string errMsg)
        {
            status = 200; errCode = null; errMsg = null;
            if (string.IsNullOrEmpty(op)) { status = 400; errCode = "BAD_REQUEST"; errMsg = "missing \"op\""; return null; }

            switch (op)
            {
                case "pause":
                case "resume":
                {
                    var sc = SpeedControlScreen.Instance;
                    if (sc == null) { status = 503; errCode = "NOT_READY"; errMsg = "SpeedControlScreen.Instance == null"; return null; }
                    bool wantPaused = op == "pause";
                    bool before = sc.IsPaused;
                    if (before != wantPaused) sc.TogglePause(false);
                    bool back = sc.IsPaused;                      // 回读校验：没真生效就不许说 applied
                    if (back != wantPaused)
                    {
                        status = 409; errCode = "NOT_APPLIED";
                        errMsg = "requested isPaused=" + wantPaused + " but game reports " + back;
                        return null;
                    }
                    return "{\"op\":" + Snap.Str(op) + ",\"applied\":true,\"changed\":" + (before != back ? "true" : "false")
                           + ",\"isPaused\":" + (back ? "true" : "false") + "}";
                }
                case "setSpeed":
                {
                    int speed = GetInt(body, "speed", -1);
                    if (speed < 1 || speed > 3)
                    { status = 400; errCode = "INVALID_ARG"; errMsg = "\"speed\" must be 1..3 (1x/2x/3x)"; return null; }
                    var sc = SpeedControlScreen.Instance;
                    if (sc == null) { status = 503; errCode = "NOT_READY"; errMsg = "SpeedControlScreen.Instance == null"; return null; }
                    // 对内是 0 基：0=1x 1=2x 2=3x（IL：speed=value&3 后 switch 选 slow/medium/fast 按钮；
                    // 界面实测 speed:2 = 三倍速）。传 3 会落到 default 被当成 0，所以这里必须先转换再回读。
                    sc.SetSpeed(speed - 1);
                    int back = sc.GetSpeed() + 1;                 // 回读后换回对外语义
                    if (back != speed)
                    {
                        status = 409; errCode = "NOT_APPLIED";
                        errMsg = "requested speed " + speed + " but game reports " + back + " (raw " + sc.GetSpeed() + ")";
                        return null;
                    }
                    return "{\"op\":\"setSpeed\",\"applied\":true,\"speed\":" + back + ",\"speedRaw\":" + sc.GetSpeed()
                           + ",\"isPaused\":" + (sc.IsPaused ? "true" : "false") + "}";
                }
                case "setChorePriority":
                {
                    string who = GetStr(body, "dupe");
                    int pri = GetInt(body, "priority", -1);
                    if (string.IsNullOrEmpty(who)) { status = 400; errCode = "INVALID_ARG"; errMsg = "missing \"dupe\""; return null; }
                    if (pri < 1 || pri > 9) { status = 400; errCode = "INVALID_ARG"; errMsg = "\"priority\" must be 1..9"; return null; }
                    string clsName = GetStr(body, "priorityClass") ?? "high";
                    PriorityScreen.PriorityClass cls;
                    if (!TryParsePriorityClass(clsName, out cls))
                    { status = 400; errCode = "INVALID_ARG"; errMsg = "unknown priorityClass: " + clsName; return null; }

                    MinionIdentity target = FindMinion(who);
                    if (target == null) { status = 404; errCode = "NOT_FOUND_TARGET"; errMsg = "no live duplicant matches \"" + who + "\""; return null; }
                    var cd = target.GetComponent<ChoreDriver>();
                    Chore chore = cd != null ? cd.GetCurrentChore() : null;
                    if (chore == null) { status = 404; errCode = "NOT_FOUND_TARGET"; errMsg = "duplicant \"" + who + "\" has no current chore"; return null; }

                    chore.masterPriority = new PrioritySetting(cls, pri);
                    return "{\"op\":\"setChorePriority\",\"applied\":true,\"dupe\":" + Snap.Str(target.nameStringKey)
                           + ",\"chore\":" + Snap.Str(chore.GetReportName())
                           + ",\"priorityClass\":" + Snap.Str(clsName) + ",\"priority\":" + pri + "}";
                }
                default:
                    if (Cheats.IsCheatOp(op))
                        return Cheats.Execute(op, body, out status, out errCode, out errMsg);
                    status = 404; errCode = "NOT_FOUND"; errMsg = "unknown op: " + op; return null;
            }
        }

        static MinionIdentity FindMinion(string who)
        {
            var cmp = Components.LiveMinionIdentities;
            if (cmp == null) return null;
            foreach (MinionIdentity mi in cmp.Items)
            {
                if (mi == null) continue;
                string db = null, nm = null;
                try { db = mi.nameStringKey; } catch { }
                try { nm = mi.GetProperName(); } catch { }
                if (string.Equals(db, who, System.StringComparison.OrdinalIgnoreCase)) return mi;
                if (!string.IsNullOrEmpty(nm) && nm == who) return mi;
                if (!string.IsNullOrEmpty(nm) && nm.IndexOf(who, System.StringComparison.OrdinalIgnoreCase) >= 0) return mi;
            }
            return null;
        }

        static bool TryParsePriorityClass(string s, out PriorityScreen.PriorityClass cls)
        {
            switch ((s ?? "").Trim().ToLowerInvariant())
            {
                case "idle": cls = PriorityScreen.PriorityClass.idle; return true;
                case "basic": cls = PriorityScreen.PriorityClass.basic; return true;
                case "high": cls = PriorityScreen.PriorityClass.high; return true;
                case "personalneeds": cls = PriorityScreen.PriorityClass.personalNeeds; return true;
                case "toppriority": cls = PriorityScreen.PriorityClass.topPriority; return true;
                case "compulsory": cls = PriorityScreen.PriorityClass.compulsory; return true;
                default: cls = PriorityScreen.PriorityClass.basic; return false;
            }
        }

        // ---- 极简 JSON 取值（命令体很小，key 唯一，用正则足够；不引第三方库）----
        static string GetStr(string body, string key)
        {
            if (string.IsNullOrEmpty(body)) return null;
            var m = Regex.Match(body, "\"" + Regex.Escape(key) + "\"\\s*:\\s*\"([^\"]*)\"");
            return m.Success ? m.Groups[1].Value : null;
        }

        static int GetInt(string body, string key, int def)
        {
            if (string.IsNullOrEmpty(body)) return def;
            var m = Regex.Match(body, "\"" + Regex.Escape(key) + "\"\\s*:\\s*(-?\\d+)");
            int v;
            return m.Success && int.TryParse(m.Groups[1].Value, out v) ? v : def;
        }
    }
}
