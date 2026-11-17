using UnityEngine;
using System.Reflection;
using HarmonyLib;

// ⚠️ 不要 using System 之后直接用 Action/DateTime：Assembly-CSharp 里的全局 Action 枚举会遮蔽 System.Action。
namespace OniAiBridge
{
    internal enum SettingsUiKind
    {
        Unavailable = 0,
        ReflectionManageHook = 1,   // 方案 A：反射设 mod.on_managed / mod.manage_tooltip → 官方「管理」按钮
        HarmonyModsButton = 2,      // 方案 B：Harmony postfix ModsScreen.BuildDisplay → 自己加按钮
    }

    /// <summary>
    /// 设置页入口的统一抽象：两种实现同接口，取哪一个记录在 /v1/meta 的 settingsUi 里。
    ///
    /// 方案 A（优先）：ONI 的 ModsScreen.BuildDisplay 已经会渲染一个 "ManageButton"，
    ///   文案取 MODS::MANAGE(_LOCAL)、set_isInteractable = mod.is_managed、set_toolTip = mod.manage_tooltip、
    ///   点击挂 mod.on_managed（以上均在本轮 IL 里逐条核实）。
    ///   但这两个属性的 **setter 是 non-public** ⇒ 只能反射。
    ///   ⚠️ 注意 is_managed 的 IL 是 `manage_tooltip != null` ⇒ 两个必须一起设，只设 on_managed 按钮仍是灰的。
    /// 方案 B（兜底）：Harmony postfix 打进官方 Mods 屏，自己加一个按钮。
    /// 两条都失败 ⇒ 只保留 json 配置，并在 /v1/meta 里如实报 unavailable + 原因（不静默）。
    /// </summary>
    internal static class SettingsUi
    {
        public static SettingsUiKind Kind { get; private set; }
        public static string Detail { get; private set; }

        static bool harmonyTried;

        static SettingsUi()
        {
            Kind = SettingsUiKind.Unavailable;
            Detail = "not initialized";
        }

        public static bool IsAvailable { get { return Kind != SettingsUiKind.Unavailable; } }

        public static void Install(Harmony harmony, KMod.Mod mod)
        {
            string errA = null, errB = null;
            if (TryReflection(mod, out errA))
            {
                Kind = SettingsUiKind.ReflectionManageHook;
                Detail = "via KMod.Mod.on_managed + manage_tooltip (reflection)";
                Log.Info("设置页入口：官方「管理」按钮（方案 A 成功）");
                return;
            }
            Log.Warn("方案 A 失败，改走方案 B：" + errA);

            if (TryHarmony(harmony, out errB))
            {
                Kind = SettingsUiKind.HarmonyModsButton;
                Detail = "via Harmony postfix ModsScreen.BuildDisplay";
                Log.Info("设置页入口：自建按钮（方案 B 成功）");
                return;
            }
            Log.Warn("方案 B 也失败：" + errB);

            Kind = SettingsUiKind.Unavailable;
            Detail = "A: " + errA + " | B: " + errB;
            Log.Warn("设置页不可用（json 配置仍然有效）: " + Detail);
        }

        // ---------------- 方案 A ----------------
        static bool TryReflection(KMod.Mod mod, out string err)
        {
            err = null;
            if (mod == null) { err = "mod == null（UserMod2.mod 未赋值）"; return false; }
            try
            {
                const BindingFlags F = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                PropertyInfo pTooltip = typeof(KMod.Mod).GetProperty("manage_tooltip", F);
                PropertyInfo pManaged = typeof(KMod.Mod).GetProperty("on_managed", F);
                if (pTooltip == null) { err = "KMod.Mod.manage_tooltip 不存在（游戏版本变了）"; return false; }
                if (pManaged == null) { err = "KMod.Mod.on_managed 不存在（游戏版本变了）"; return false; }
                MethodInfo setTooltip = pTooltip.GetSetMethod(true);
                MethodInfo setManaged = pManaged.GetSetMethod(true);
                if (setTooltip == null) { err = "manage_tooltip 没有 setter（已改成只读）"; return false; }
                if (setManaged == null) { err = "on_managed 没有 setter（已改成只读）"; return false; }

                // 先设 tooltip（is_managed ⇔ tooltip != null），再设回调
                setTooltip.Invoke(mod, new object[] { new LocString("ONI AI Bridge", "ONI AI Bridge: 打开设置") });
                setManaged.Invoke(mod, new object[] { new System.Action(BridgeOptionsScreen.Open) });
                return true;
            }
            catch (System.Exception e)
            {
                err = (e.InnerException ?? e).GetType().Name + ": " + (e.InnerException ?? e).Message;
                return false;
            }
        }

        // ---------------- 方案 B ----------------
        static bool TryHarmony(Harmony harmony, out string err)
        {
            // M4.1c：B 路线**暂缓**。往 ModsScreen 里加按钮需要官方 prefab 的 KButton，
            // 自造 KButton 已实测在 KButton.UpdateColor 里 NRE（见 DESIGN §10.10）。
            // 现在有热键兜底（Ctrl+Shift+O），所以这里如实返回失败原因，不假装成功。
            err = "方案 B 暂缓：自造 KButton 会 NRE（需要官方 prefab），热键已覆盖入口需求";
            return false;
        }

        /// <summary>方案 B 的 Postfix 会调它。M4.1c 起 B 路线暂缓 ⇒ 这里是空实现，只记一次日志。</summary>
        public static void OnModsScreenBuilt(ModsScreen screen)
        {
            if (Kind != SettingsUiKind.HarmonyModsButton) return;
            Log.Warn("方案 B 已暂缓（自造 KButton 会 NRE）；请用热键 " + Hotkey.Text + " 打开设置页");
        }
    }
    /// <summary>
    /// 方案 B 的钩子。⚠️ 与 DGSM 相同的手法（它也 patch 这个方法，见 DESIGN §10.2），
    /// 但这里我们**只加一个按钮**，不做别的改动；方案 A 成功时 Postfix 直接返回。
    /// </summary>
    [HarmonyPatch(typeof(ModsScreen), "BuildDisplay")]
    internal static class ModsScreenBuildDisplayPatch
    {
        static void Postfix(ModsScreen __instance) { SettingsUi.OnModsScreenBuilt(__instance); }
    }
}
