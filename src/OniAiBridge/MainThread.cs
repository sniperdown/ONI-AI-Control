using System.Collections.Concurrent;
using System.Reflection;
using System.Threading;
using HarmonyLib;

// ⚠️ 不能写 using System; 之后直接用 Action / DateTime ——
// Assembly-CSharp.dll（以及 firstpass）自己定义了全局类型 `Action` 和 `DateTime`，
// 直接把 System 的遮掉了（实测编译报 CS1660 / CS0117）。本文件所有 BCL 类型一律写全名。
namespace OniAiBridge
{
    /// <summary>
    /// 主线程泵。**整个 mod 的安全基石**：
    /// Unity/Mono 的对象只能在主线程访问；后台线程（HTTP）只投递委托，绝不直接读游戏对象。
    ///
    /// 实现：Harmony postfix 挂在 Game.Update（已核实：Game 里有个非公开实例方法 Update），
    /// 每帧把队列排干。Unity 的 Update 与游戏内暂停无关，所以暂停时也能响应。
    /// </summary>
    internal static class MainThread
    {
        static readonly ConcurrentQueue<System.Action> Queue = new ConcurrentQueue<System.Action>();
        const int MaxActionsPerFrame = 64;   // 防止某个客户端把帧率打穿

        /// <summary>泵是否已在跑（至少进过一次 Game.Update）。</summary>
        public static bool Ready { get; private set; }

        /// <summary>上次泵运行时的 TickCount（毫秒，回绕无妨，只看差值）。</summary>
        static int LastPumpTick;

        /// <summary>距上次泵运行过了多久（毫秒）。泵从未跑过返回 -1。</summary>
        public static int LastPumpAgeMs
        {
            get
            {
                if (LastPumpTick == 0) return -1;
                return System.Environment.TickCount - LastPumpTick;
            }
        }

        /// <summary>队列里还没执行的动作数（诊断用）。</summary>
        public static int QueueDepth { get { return Queue.Count; } }

        /// <summary>
        /// 主线程"忙"的判据：泵在跑，但距上次心跳 &gt; 1000ms。
        /// 典型场景 = 大存档加载完成后主线程仍在初始化/渲染，动作在队列里排不上。
        /// </summary>
        public static bool IsMainThreadBusy { get { int a = LastPumpAgeMs; return Ready && (a < 0 || a > 1000); } }

        public static void Pump()
        {
            Ready = true;
            LastPumpTick = System.Environment.TickCount;
            for (int i = 0; i < MaxActionsPerFrame; i++)
            {
                System.Action act;
                if (!Queue.TryDequeue(out act)) break;
                try { act(); }
                catch (System.Exception e) { Log.Warn("主线程任务抛异常（已吞掉，避免打断游戏）: " + e.Message); }
            }
            // 采样 + 广播（内部自己节流到每 1000 ms 一次，这里每帧调也便宜）
            try { EventHub.Tick(); } catch (System.Exception e) { Log.Warn("EventHub.Tick 异常: " + e.Message); }
        }

        /// <summary>把动作排到主线程下一帧执行（不等待）。已有主线程上下文时也安全。</summary>
        public static void Enqueue(System.Action act)
        {
            if (act == null) return;
            Queue.Enqueue(act);
        }

        /// <summary>
        /// 从任意线程同步调用：把 act 丢到主线程跑，最多等 timeoutMs。
        /// 返回 false = 泵没就绪或超时（调用方应回 null / NOT_READY，不要编数据）。
        /// </summary>
        public static bool Invoke(System.Action act, int timeoutMs)
        {
            if (!Ready) return false;
            using (var done = new ManualResetEventSlim(false))
            {
                Queue.Enqueue(() =>
                {
                    try { act(); }
                    finally { done.Set(); }
                });
                return done.Wait(timeoutMs);
            }
        }

        /// <summary>
        /// 按"主线程忙不忙"选超时：忙（刚加载存档）给 busyMs，稳定给 normalMs。
        /// 返回 false 时调用方应回 503 RETRY_LATER（是"还没排上队"，不是"查询失败"）。
        /// </summary>
        public static bool InvokeAdaptive(System.Action act, int normalMs, int busyMs, out int usedTimeoutMs, out bool wasBusy)
        {
            wasBusy = IsMainThreadBusy;
            usedTimeoutMs = wasBusy ? busyMs : normalMs;
            return Invoke(act, usedTimeoutMs);
        }

        public static void Install(Harmony harmony)
        {
            try
            {
                harmony.PatchAll(typeof(MainThread).Assembly);
                Log.Info("Harmony 补丁已应用（Game.Update postfix → 主线程泵）");
            }
            catch (System.Exception e)
            {
                // 软失败：HTTP 照样起，但 /v1/meta 会报 pumpReady=false，绝不假装正常。
                Log.Warn("Harmony PatchAll 失败：实时数据将不可用", e);
            }
        }
    }

    /// <summary>把 Game.Update 当每帧钩子用（Game 是全局命名空间里的类型）。</summary>
    [HarmonyPatch(typeof(Game), "Update")]
    internal static class GameUpdatePatch
    {
        static void Postfix() { MainThread.Pump(); }
    }
}
