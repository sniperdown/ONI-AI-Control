using HarmonyLib;

// ⚠️ 不要 using System 之后直接用 Action/DateTime：Assembly-CSharp 里的全局 Action 枚举会遮蔽 System.Action。
namespace OniAiBridge
{
    /// <summary>
    /// M6.64：`PlanScreen.GetBuildingPriority()` 的 **Finalizer** 兜底（修正了签名错误）。
    ///
    /// ⚠️ 0.85.0/0.86.0 的写法是 `static bool Finalizer(...)` —— **那不是合法的 Harmony Finalizer**：
    /// Finalizer 的返回类型必须是 `System.Exception`（返回 null = 异常已处理/没有异常；
    /// 返回某个异常 = 把它抛出）。返回 bool 时 Harmony 会把这个值当作"要抛出的异常"处理，
    /// `true` 不是 Exception ⇒ 未定义行为（实测：玩家手动建造后游戏整体卡死，主线程不再写日志）。
    ///
    /// 正确行为：
    /// * `__exception == null`（正常路径）⇒ `return null`，**不碰 `__result`**，玩家零影响；
    /// * 有异常且开关打开 ⇒ 兜底 `PrioritySetting`（全反射构造，因为编译期无法按名引用 PriorityClass），
    ///   然后 `return null` 表示"异常已处理"；
    /// * 有异常但开关关闭 ⇒ `return __exception`，原样向上抛（等于没打补丁）。
    ///
    /// 开关：`settings.json` 的 `FixPlanScreenPriorityNRE`（默认 true）。
    /// </summary>
    [HarmonyPatch(typeof(PlanScreen), "GetBuildingPriority")]
    internal static class PlanScreen_GetBuildingPriority_Patch
    {
        // Harmony Finalizer 的正确签名：返回 System.Exception（null = 已处理）
        static System.Exception Finalizer(System.Exception __exception, ref PrioritySetting __result)
        {
            if (__exception == null) return null;      // 正常路径：原样，不动结果

            bool enabled = true;
            try { var st = BridgeSettings.Load(); enabled = st == null || st.FixPlanScreenPriorityNRE; } catch { }

            Log.Warn("[build] PlanScreen.GetBuildingPriority 抛 " + __exception.GetType().Name
                     + "（建造已成功，仅后处理设优先度失败）；FixPlanScreenPriorityNRE=" + enabled);

            if (!enabled) return __exception;           // 关闭开关 ⇒ 不吞，原样上抛

            try
            {
                var asm = typeof(PrioritySetting).Assembly;
                System.Type tpc = null;
                var types = asm.GetTypes();
                for (int i = 0; i < types.Length; i++)
                {
                    if (types[i] != null && types[i].Name == "PriorityClass") { tpc = types[i]; break; }
                }
                if (tpc != null)
                {
                    var basicF = tpc.GetField("basic", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
                    var ctor = typeof(PrioritySetting).GetConstructor(new System.Type[] { tpc, typeof(int) });
                    if (basicF != null && ctor != null)
                    {
                        var basic = basicF.GetValue(null);
                        __result = (PrioritySetting)ctor.Invoke(new object[] { basic, 5 });
                        return null;                    // 已处理
                    }
                }
            }
            catch (System.Exception ex2)
            {
                Log.Warn("[build] 构造兜底 PrioritySetting 失败：" + ex2.GetType().Name + " ⇒ 用 default");
            }

            __result = default(PrioritySetting);
            return null;                                // 已处理
        }
    }
}
