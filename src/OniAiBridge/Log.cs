using System;

namespace OniAiBridge
{
    /// <summary>
    /// 极简日志：只走 Unity 的 Debug.Log（会进 Player.log），不额外写盘。
    /// 前缀统一 [OniAiBridge]，方便在 Player.log 里 grep。
    /// </summary>
    internal static class Log
    {
        const string Prefix = "[OniAiBridge] ";

        public static void Info(string msg)
        {
            try { UnityEngine.Debug.Log(Prefix + msg); } catch { }
        }

        /// <summary>
        /// 非致命问题一律用这个（内部 `Debug.LogWarning`）。
        /// ⚠️ **不要用 `Debug.LogError` 报非致命问题**：ONI 会据此把本 mod 记进"出错 mod"名单，
        /// 下次启动弹「游戏出现错误」屏，并**自动把 mod 在 mods.json 里改成 enabled=false**（M4.2k 踩过）。
        /// </summary>
        public static void Warn(string msg, Exception e = null)
        {
            try { UnityEngine.Debug.LogWarning(Prefix + msg + (e == null ? "" : "\n" + e)); } catch { }
        }

        /// <summary>
        /// 兼容旧调用点：**内部也走 `Debug.LogWarning`**，不会触发 ONI 的 mod 出错判定。
        /// </summary>
        public static void Error(string msg, Exception e = null)
        {
            try { UnityEngine.Debug.LogWarning(Prefix + "[error-as-warning] " + msg + (e == null ? "" : "\n" + e)); } catch { }
        }
    }
}
