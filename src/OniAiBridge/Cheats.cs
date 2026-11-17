using System.Collections.Generic;
using Klei.AI;

// ⚠️ 不要 using System 之后直接用 Action/DateTime：Assembly-CSharp 里有同名全局类型会遮蔽它们。
namespace OniAiBridge
{
    /// <summary>
    /// M3 作弊层：**只调度游戏内置的 Debug 工具**，不自己实现任何作弊逻辑。
    ///
    /// 好处：游戏更新时我们只依赖官方调试入口，不复制"瞬建=扣材料+加进度"这类私有业务，
    /// 因此不会被游戏改逻辑打死；行为也与玩家按调试快捷键完全一致（出问题可复现可对照）。
    ///
    /// 双闸门（缺一不可）：settings.allowCheats == true 且 DebugHandler.enabled == true。
    /// 每个 op 执行完都**回读**实际状态，不一致回 409 NOT_APPLIED，绝不假报成功。
    /// </summary>
    internal static class Cheats
    {
        // ---------- op 定义（供 /v1/commands 自描述）----------
        public static readonly string[][] Defs =
        {
            // op, summary, args, note
            new[] { "debug", "查/开/关游戏 Debug 模式", "{\"enable\":true|false} 或 {} 查询",
                    "其他作弊 op 的前置条件；开启后游戏内会出现调试功能。会改变游戏运行状态" },
            new[] { "setInstantBuild", "瞬建开关（游戏内置 DebugHandler.InstantBuildMode）", "{\"enable\":true|false}",
                    "会跳过建造材料与进度校验；**破坏存档一致性、不可撤销**" },
            new[] { "setInvincible", "无敌开关（DebugHandler.InvincibleMode）", "{\"enable\":true|false}",
                    "复制人不再受伤/窒息；**影响存档一致性**" },
            new[] { "revealFog", "开图（DebugHandler.RevealFogOfWar）", "{\"enable\":true|false}",
                    "直接揭开战争迷雾；**不可撤销**（探索状态已改变）" },
            new[] { "spawnElement", "在指定格刷元素（调度 DebugPaintElementScreen + DebugTool.DoReplaceSubstance）", "{\"element\":\"元素 tag 名，如 Water\",\"cell\":int,\"mass\":kg,\"temperature\":K}",
                    "直接改写模拟格子质量/温度；**破坏存档一致性、不可撤销**" },
            new[] { "teleport", "把复制人挪到指定格（Transform 定位 + Navigator.Stop）", "{\"dupe\":\"dbId 或名字\",\"cell\":int}",
                    "绕过寻路；可能被导航系统拉回，请用 /v1/state 复查 cell。**实验性**" },
        };

        /// <summary>需要 DebugHandler.enabled 的 op（`debug` 自己不需要，它负责开）。</summary>
        static bool NeedsDebugMode(string op) { return op != "debug"; }

        public static bool IsCheatOp(string op)
        {
            foreach (var d in Defs) if (d[0] == op) return true;
            return false;
        }

        /// <summary>在主线程执行。返回结果 JSON；失败时 result=null 且带 status/code/message。</summary>
        public static string Execute(string op, string body, out int status, out string code, out string msg)
        {
            status = 200; code = null; msg = null;

            // ---- 三段式闸门（M4）：allowCheats 且（沙盒 或 沙盒激活 或 debug）----
            // `debug` op 只需要 allowCheats（否则永远打不开 debug 模式）；其余作弊 op 需要完整闸门。
            if (!Gate.AllowCheats)
            {
                status = 403; code = "ALLCHEATS_DISABLED"; msg = Gate.UnavailableReason(); return null;
            }
            if (NeedsDebugMode(op) && !Gate.CheatsUnlocked)
            {
                status = 403; code = Gate.UnavailableCode(); msg = Gate.UnavailableReason(); return null;
            }

            switch (op)
            {
                case "debug": return OpDebug(body, out status, out code, out msg);
                case "setInstantBuild": return OpFlag(body, "setInstantBuild", InstantBuildGet, InstantBuildSet, out status, out code, out msg);
                case "setInvincible": return OpFlag(body, "setInvincible", InvincibleGet, InvincibleSet, out status, out code, out msg);
                case "revealFog": return OpFlag(body, "revealFog", RevealGet, RevealSet, out status, out code, out msg);
                case "spawnElement": return OpSpawnElement(body, out status, out code, out msg);
                case "teleport": return OpTeleport(body, out status, out code, out msg);
                default:
                    status = 404; code = "NOT_FOUND"; msg = "unknown cheat op: " + op; return null;
            }
        }

        // ---------- debug 开关 ----------
        static string OpDebug(string body, out int status, out string code, out string msg)
        {
            status = 200; code = null; msg = null;
            string en = GetBoolArg(body, "enable");
            if (en == null)
                return "{\"op\":\"debug\",\"enabled\":" + (DebugHandler.enabled ? "true" : "false") + ",\"queried\":true}";

            bool want = en == "true";
            if (DebugHandler.enabled != want) DebugHandler.SetDebugEnabled(want);
            bool back = DebugHandler.enabled;
            if (back != want)
            {
                status = 409; code = "NOT_APPLIED";
                msg = "requested DebugHandler.enabled=" + want + " but game reports " + back;
                return null;
            }
            return "{\"op\":\"debug\",\"enabled\":" + (back ? "true" : "false") + ",\"applied\":true}";
        }

        // ---------- 三个布尔开关（统一走回读） ----------
        static bool InstantBuildGet() { return DebugHandler.InstantBuildMode; }
        static void InstantBuildSet(bool v) { if (DebugHandler.InstantBuildMode != v) DebugHandler.ToggleInstantBuildMode(); }
        static bool InvincibleGet() { return DebugHandler.InvincibleMode; }
        static void InvincibleSet(bool v) { DebugHandler.InvincibleMode = v; }
        static bool RevealGet() { return DebugHandler.RevealFogOfWar; }
        static void RevealSet(bool v) { DebugHandler.RevealFogOfWar = v; }

        static string OpFlag(string body, string op, System.Func<bool> get, System.Action<bool> set,
                             out int status, out string code, out string msg)
        {
            status = 200; code = null; msg = null;
            string en = GetBoolArg(body, "enable");
            if (en == null) { status = 400; code = "INVALID_ARG"; msg = "\"enable\" must be true or false"; return null; }
            bool want = en == "true";
            set(want);
            bool back = get();                            // 回读校验
            if (back != want)
            {
                status = 409; code = "NOT_APPLIED";
                msg = "requested " + want + " but game reports " + back;
                return null;
            }
            return "{\"op\":" + Snap.Str(op) + ",\"applied\":true,\"value\":" + (back ? "true" : "false") + "}";
        }

        // ---------- 刷元素 ----------
        static string OpSpawnElement(string body, out int status, out string code, out string msg)
        {
            status = 200; code = null; msg = null;
            string elName = GetStrArg(body, "element");
            int cell = GetIntArg(body, "cell", -1);
            string massS = GetNumArg(body, "mass");
            string tempS = GetNumArg(body, "temperature");

            if (string.IsNullOrEmpty(elName)) { status = 400; code = "INVALID_ARG"; msg = "missing \"element\" (element tag name, e.g. Water)"; return null; }
            if (cell < 0 || !Grid.IsValidCell(cell))
            { status = 400; code = "INVALID_ARG"; msg = "\"cell\" out of range (valid 0.." + (Grid.CellCount - 1) + ")"; return null; }
            float mass, temp;
            if (massS == null || !float.TryParse(massS, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out mass) || mass <= 0f)
            { status = 400; code = "INVALID_ARG"; msg = "\"mass\" must be a positive number (kg)"; return null; }
            if (tempS == null || !float.TryParse(tempS, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out temp) || temp < 0f)
            { status = 400; code = "INVALID_ARG"; msg = "\"temperature\" must be a number >= 0 (Kelvin)"; return null; }

            Element el = FindElement(elName);
            if (el == null) { status = 404; code = "NOT_FOUND_TARGET"; msg = "no element with tag \"" + elName + "\""; return null; }

            var dbg = DebugPaintElementScreen.Instance;
            if (dbg == null)
            {
                status = 503; code = "NOT_READY";
                msg = "DebugPaintElementScreen.Instance == null —— 需要先在游戏里打开一次调试面板的元素绘制页（debug 模式 + 调试菜单）";
                return null;
            }

            // 走游戏自己的路径：填好调试面板的选择，再让它按当前设置刷这一格
            dbg.element = el.id;   // 实测：该字段是 SimHashes 枚举（不是 Tag）
            dbg.mass = mass;
            dbg.temperature = temp;
            DebugTool.DoReplaceSubstance(cell);

            // 回读：这一格的元素是否真的变成目标元素
            var after = Grid.Element[cell];
            string afterId = null;
            try { if (after != null) afterId = after.tag.Name; } catch { }
            if (!string.Equals(afterId, el.tag.Name, System.StringComparison.OrdinalIgnoreCase))
            {
                status = 409; code = "NOT_APPLIED";
                msg = "requested element " + el.tag.Name + " but cell " + cell + " reports " + (afterId ?? "?");
                return null;
            }
            float kg = 0f;
            try { kg = Grid.Mass[cell]; } catch { }
            return "{\"op\":\"spawnElement\",\"applied\":true,\"cell\":" + cell
                   + ",\"element\":" + Snap.Str(afterId)
                   + ",\"massAfter\":" + Snap.F(kg)
                   + ",\"massRequested\":" + Snap.F(mass) + "}";
        }

        // ---------- 传送复制人 ----------
        static string OpTeleport(string body, out int status, out string code, out string msg)
        {
            status = 200; code = null; msg = null;
            string who = GetStrArg(body, "dupe");
            int cell = GetIntArg(body, "cell", -1);
            if (string.IsNullOrEmpty(who)) { status = 400; code = "INVALID_ARG"; msg = "missing \"dupe\" (dbId or name)"; return null; }
            if (cell < 0 || !Grid.IsValidCell(cell))
            { status = 400; code = "INVALID_ARG"; msg = "\"cell\" out of range (valid 0.." + (Grid.CellCount - 1) + ")"; return null; }

            MinionIdentity mi = FindMinion(who);
            if (mi == null) { status = 404; code = "NOT_FOUND_TARGET"; msg = "no live duplicant matches \"" + who + "\""; return null; }

            int before = -1;
            try { before = Grid.PosToCell(mi.gameObject); } catch { }
            try
            {
                var nav = mi.GetComponent<Navigator>();
                if (nav != null) nav.Stop(false, false);          // 先停掉当前寻路，避免立刻被拉回
            }
            catch { }
            try { mi.transform.position = Grid.CellToPos(cell); }
            catch (System.Exception e) { status = 500; code = "INTERNAL"; msg = "transform move failed: " + e.Message; return null; }

            int after = -1;
            try { after = Grid.PosToCell(mi.gameObject); } catch { }
            if (after != cell)
            {
                status = 409; code = "NOT_APPLIED";
                msg = "requested cell " + cell + " but duplicant is at " + after;
                return null;
            }
            return "{\"op\":\"teleport\",\"applied\":true,\"dupe\":" + Snap.Str(mi.nameStringKey)
                   + ",\"fromCell\":" + before + ",\"cell\":" + after
                   + ",\"note\":\"Transform 定位+Navigator.Stop；导航系统之后可能把它拉回，请隔 1 秒用 /v1/state 复查\"}";
        }

        // ---------- 查找 ----------
        static Element FindElement(string tagName)
        {
            var els = ElementLoader.elements;
            if (els == null) return null;
            for (int i = 0; i < els.Count; i++)
            {
                var el = els[i];
                if (el == null) continue;
                string t = null;
                try { t = el.tag.Name; } catch { }
                if (string.Equals(t, tagName, System.StringComparison.OrdinalIgnoreCase)) return el;
            }
            return null;
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

        // ---------- 极简 JSON 取值 ----------
        static string GetBoolArg(string body, string key)
        {
            if (string.IsNullOrEmpty(body)) return null;
            var m = System.Text.RegularExpressions.Regex.Match(body, "\"" + System.Text.RegularExpressions.Regex.Escape(key) + "\"\\s*:\\s*(true|false)");
            return m.Success ? m.Groups[1].Value.ToLowerInvariant() : null;
        }

        static string GetStrArg(string body, string key)
        {
            if (string.IsNullOrEmpty(body)) return null;
            var m = System.Text.RegularExpressions.Regex.Match(body, "\"" + System.Text.RegularExpressions.Regex.Escape(key) + "\"\\s*:\\s*\"([^\"]*)\"");
            return m.Success ? m.Groups[1].Value : null;
        }

        static int GetIntArg(string body, string key, int def)
        {
            if (string.IsNullOrEmpty(body)) return def;
            var m = System.Text.RegularExpressions.Regex.Match(body, "\"" + System.Text.RegularExpressions.Regex.Escape(key) + "\"\\s*:\\s*(-?\\d+)");
            int v;
            return m.Success && int.TryParse(m.Groups[1].Value, out v) ? v : def;
        }

        static string GetNumArg(string body, string key)
        {
            if (string.IsNullOrEmpty(body)) return null;
            var m = System.Text.RegularExpressions.Regex.Match(body, "\"" + System.Text.RegularExpressions.Regex.Escape(key) + "\"\\s*:\\s*(-?[0-9]+(?:\\.[0-9]+)?(?:[eE][-+]?[0-9]+)?)");
            return m.Success ? m.Groups[1].Value : null;
        }
    }
}
