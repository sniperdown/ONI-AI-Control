// ⚠️ 不要 using System 之后直接用 Action/DateTime：Assembly-CSharp 里的全局 Action 枚举会遮蔽 System.Action。
namespace OniAiBridge
{
    /// <summary>
    /// 作弊闸门（M4 三段式）。判断入口全部来自反编译核实：
    ///  * `SaveGame.Instance.sandboxEnabled`  —— public 字段，**存档级**"这个档是沙盒档"
    ///  * `Game.Instance.SandboxModeActive`   —— public 属性，运行时沙盒工具激活态
    ///  * `DebugHandler.enabled`              —— 游戏 Debug 模式
    /// 条件：`allowCheats == true` **且**（沙盒 或 沙盒激活态 或 debug 模式）。
    /// 这两个条件必须能区分开，所以提供两个不同的错误码。
    /// </summary>
    internal static class Gate
    {
        public static bool AllowCheats
        {
            get { BridgeSettings st = Bridge.SettingsRef; return st != null && st.AllowCheats; }
        }

        /// <summary>存档级沙盒标记（public 字段，不抛异常）。</summary>
        public static bool SandboxEnabled
        {
            get
            {
                try { SaveGame sg = SaveGame.Instance; return sg != null && sg.sandboxEnabled; }
                catch { return false; }
            }
        }

        /// <summary>运行时沙盒工具激活态（Game 上的 public 属性）。</summary>
        public static bool SandboxModeActive
        {
            get
            {
                try { Game g = Game.Instance; return g != null && g.SandboxModeActive; }
                catch { return false; }
            }
        }

        /// <summary>是否已载入殖民地（主菜单为 false）。主线程读。</summary>
        public static bool HasColony
        {
            get { try { return GameClock.Instance != null; } catch { return false; } }
        }

        public static bool DebugModeEnabled
        {
            get { try { return DebugHandler.enabled; } catch { return false; } }
        }

        public static bool SandboxOrDebug { get { return SandboxEnabled || SandboxModeActive || DebugModeEnabled; } }

        /// <summary>作弊类 op 的总闸门。</summary>
        public static bool CheatsUnlocked { get { return AllowCheats && SandboxOrDebug; } }

        /// <summary>null = 可用；否则返回错误码。</summary>
        public static string UnavailableCode()
        {
            if (!AllowCheats) return "ALLCHEATS_DISABLED";
            if (!SandboxOrDebug) return "SANDBOX_OR_DEBUG_REQUIRED";
            return null;
        }

        /// <summary>人类看得懂的一句话（直接给客户端显示）。</summary>
        public static string UnavailableReason()
        {
            if (!AllowCheats)
                return "allcheats 未启用：请把 settings.json 的 allowCheats 改成 true（或游戏内 mod 设置页勾选）";
            if (!SandboxOrDebug)
                return "需要在沙盒模式或 debug 模式下使用：当前存档不是沙盒档，且游戏 Debug 模式未开启";
            return null;
        }
    }
}
