using HarmonyLib;

// ⚠️ 不要 using System 之后直接用 Action/DateTime：Assembly-CSharp 里的全局 Action 枚举会遮蔽 System.Action。
namespace OniAiBridge
{
    /// <summary>
    /// 诊断探针（M4.2m）：把 `MultiToggle` 的点击/状态推进过程**如实打出来**，用于定案
    /// "为什么 `NextState()` 没把状态翻过去"。
    ///
    /// 已排除的可能（IL 实证）：
    ///  * `get_CurrentState` 只有 7 字节：`ldarg.0; ldfld state; ret` ⇒ **它返回的就是索引 `state`**，不是 id；
    ///  * `ToggleState` 结构里**没有 `id` 字段**（字段是 Name/…/sprite/color/color_on_hover/use_color_on_hover/
    ///    use_rect_margins/rect_margins/additional_display_settings）⇒ "索引 vs id" 的假设可以直接排除；
    ///  * `NextState()` = `ChangeState((state + 1) % states.Length)`；`ChangeState(int)` 的守卫是
    ///    `if (!stateDirty && arg == state) return;` ⇒ 只要 arg≠state 就一定套用。
    ///
    /// 所以只剩两种可能，这组探针正好区分：
    ///  A. `NextState()` **根本没被调用**（我们的 `onClick` 抛异常把 `OnPointerClick` 后半段打断了，
    ///     或 `OnPointerClick` 提前 return）⇒ 看不到 `NextState 进入`；
    ///  B. `NextState()` 被调用了但状态没变 ⇒ 会看到"进入 state=1 / 返回 state=1"。
    ///
    /// ⚠️ 只打 `Log.Info`（不碰 Debug.LogError，避免被 ONI 记为 mod 出错）。
    /// </summary>
    internal static class ToggleProbe
    {
        public static string Describe(MultiToggle t)
        {
            if (t == null) return "<null>";
            try
            {
                int len = t.states != null ? t.states.Length : -1;
                return t.name + " state=" + t.CurrentState + " statesLen=" + len;
            }
            catch (System.Exception e) { return "<描述异常 " + e.GetType().Name + ">"; }
        }
    }

    [HarmonyPatch(typeof(MultiToggle), "NextState")]
    internal static class MultiToggleNextStateProbe
    {
        static void Prefix(MultiToggle __instance)
        {
            Log.Info("[probe] NextState 进入: " + ToggleProbe.Describe(__instance));
        }

        static void Postfix(MultiToggle __instance)
        {
            Log.Info("[probe] NextState 返回: " + ToggleProbe.Describe(__instance));
        }
    }

    [HarmonyPatch(typeof(MultiToggle), "OnPointerClick")]
    internal static class MultiTogglePointerClickProbe
    {
        static void Prefix(MultiToggle __instance)
        {
            Log.Info("[probe] OnPointerClick 进入: " + ToggleProbe.Describe(__instance));
        }

        static void Postfix(MultiToggle __instance)
        {
            Log.Info("[probe] OnPointerClick 返回: " + ToggleProbe.Describe(__instance));
        }
    }
}
