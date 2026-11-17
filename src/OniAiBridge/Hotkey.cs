using HarmonyLib;
using UnityEngine;

// ⚠️ 不要 using System 之后直接用 Action/DateTime：Assembly-CSharp 里的全局 Action 枚举会遮蔽 System.Action。
namespace OniAiBridge
{
    /// <summary>
    /// C2：组合键唤出设置页。**兜底可用**，不依赖 Mods 列表里有没有我们的条目。
    ///
    /// 依据（已核实）：Assembly-CSharp 引用 `UnityEngine.Input`（legacy），Managed 下没有
    /// `Unity.InputSystem` 程序集 ⇒ 用老 API 是对的；`Global.Update` 是每帧都在跑的实例方法
    /// （Global 是常驻单例，主菜单也存在），而 `Game.Update` 只在进了游戏后跑 ⇒ **两个都挂**，
    /// 保证"主菜单 + 游戏内"都能按出设置页。Open() 本身幂等，重复调用不会叠层。
    /// </summary>
    internal static class Hotkey
    {
        public const string Text = "Ctrl+Shift+O";

        static bool legacyInputBroken;
        static bool warned;

        public static void Poll()
        {
            if (legacyInputBroken) return;
            try
            {
                bool ctrl = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);
                bool shift = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
                if (!ctrl || !shift) return;
                if (!Input.GetKeyDown(KeyCode.O)) return;
                Log.Info("热键 " + Text + " 触发，打开设置页");
                BridgeOptionsScreen.Open();
            }
            catch (System.Exception e)
            {
                // Unity 6 若把 Active Input Handling 切成新 Input System，Input.GetKey 会抛异常。
                // 只记一次，然后彻底停用热键（不刷屏、不影响其它功能）。
                legacyInputBroken = true;
                if (!warned)
                {
                    warned = true;
                    Log.Warn("热键不可用（legacy Input 被禁用？）：" + e.GetType().Name + ": " + e.Message);
                }
            }
        }
    }

    /// <summary>Global 常驻单例，主菜单也在跑 ⇒ 热键在这里轮询。</summary>
    [HarmonyPatch(typeof(Global), "Update")]
    internal static class GlobalUpdatePatch
    {
        // 主菜单没有 Game.Update，所以泵也在这里推进：这样 /v1/state、/v1/debug/mods 在主菜单也能答
        static void Postfix() { MainThread.Pump(); Hotkey.Poll(); }
    }


}
