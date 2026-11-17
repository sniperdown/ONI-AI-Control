using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;

namespace OniAiBridge
{
    /// <summary>
    /// SSE 事件中心。采样在主线程（由主线程泵每帧调 Tick，内部节流到每 1000 ms 一次），
    /// 广播给订阅者（每个订阅者一个队列 + 事件信号，由各自的 handler 线程写出）。
    ///
    /// 事件类型：
    ///  * snapshot —— 新连接时先发一次完整状态（等于 /v1/state 的 data）
    ///  * diff     —— 字段级变化（周期/暂停/倍速/任务/属性/资源/复制人增减）
    ///  * alert    —— 阈值告警集合变化（压力高 / 卡路里低 / 膀胱满）
    ///  * heartbeat—— 15 s 无事件时的保活
    /// 每个事件都有 `id:` = seq，可用 Last-Event-ID 或 ?since= 续传（保留最近 256 条）。
    /// </summary>
    internal static class EventHub
    {
        public const int SampleIntervalMs = 1000;
        const int RingSize = 256;
        const int MaxSubscribers = 8;

        internal sealed class Sub
        {
            public readonly ConcurrentQueue<string> Q = new ConcurrentQueue<string>();
            public readonly AutoResetEvent Signal = new AutoResetEvent(false);
        }

        static readonly List<Sub> subs = new List<Sub>();
        static readonly object gate = new object();
        static readonly Queue<KeyValuePair<long, string>> ring = new Queue<KeyValuePair<long, string>>();
        static readonly HashSet<string> lastAlerts = new HashSet<string>();
        static Snap last;
        static long nextSampleAtMs;
        static long eventSeq;

        public static long LastSeq { get { lock (gate) return eventSeq; } }
        public static int SubscriberCount { get { lock (gate) return subs.Count; } }

        public static Sub NewSub() { return new Sub(); }

        public static bool TryAdd(Sub s)
        {
            lock (gate)
            {
                if (subs.Count >= MaxSubscribers) return false;
                subs.Add(s);
                return true;
            }
        }

        public static void Remove(Sub s)
        {
            lock (gate) subs.Remove(s);
            try { s.Signal.Set(); } catch { }
        }

        /// <summary>主线程调用（主线程泵里）。节流采样 + 算 diff + 广播。</summary>
        public static void Tick()
        {
            if (!Gate.HasColony) return;      // 主菜单不采样：泵现在也在主菜单推进
            long now = System.Environment.TickCount;
            lock (gate)
            {
                if (now < nextSampleAtMs) return;
                nextSampleAtMs = now + SampleIntervalMs;
            }

            Snap cur;
            try { cur = State.CollectSnap(); }
            catch (System.Exception e) { Log.Warn("采样失败（跳过本轮）: " + e.Message); return; }

            Snap prev;
            lock (gate) { prev = last; last = cur; }

            BridgeSettings st = Bridge.SettingsRef;
            float eps = st != null ? st.AmountEpsilon : 0.1f;
            string diff = Snap.DiffJson(prev, cur, eps);
            if (diff != null) Publish("diff", diff);
            PublishAlerts(cur, st);
        }

        static void PublishAlerts(Snap cur, BridgeSettings st)
        {
            float stressHi = st != null ? st.AlertStress : 60f;
            float calLow = st != null ? st.AlertCaloriesKcal : 1500f;
            float bladderHi = st != null ? st.AlertBladder : 90f;

            var now = new HashSet<string>();
            foreach (var d in cur.Dupes)
            {
                if (!float.IsNaN(d.StressValue) && d.StressValue >= stressHi)
                    now.Add("stress_high:" + d.Key);
                if (!float.IsNaN(d.CaloriesKcal) && d.CaloriesKcal <= calLow)
                    now.Add("calories_low:" + d.Key);
                float b;
                if (d.Amounts.TryGetValue("Bladder", out b) && b >= bladderHi)
                    now.Add("bladder_full:" + d.Key);
            }

            List<string> added = null, removed = null;
            lock (gate)
            {
                foreach (var k in now) if (!lastAlerts.Contains(k)) { if (added == null) added = new List<string>(); added.Add(k); }
                foreach (var k in lastAlerts) if (!now.Contains(k)) { if (removed == null) removed = new List<string>(); removed.Add(k); }
                lastAlerts.Clear();
                foreach (var k in now) lastAlerts.Add(k);
            }
            if (added != null)
                foreach (var k in added) Publish("alert", "{\"kind\":\"added\",\"id\":" + Snap.Str(k) + "}");
            if (removed != null)
                foreach (var k in removed) Publish("alert", "{\"kind\":\"removed\",\"id\":" + Snap.Str(k) + "}");
        }

        public static void Publish(string evt, string json)
        {
            List<Sub> targets;
            lock (gate)
            {
                eventSeq++;
                string frame = "id: " + eventSeq + "\nevent: " + evt + "\ndata: " + json + "\n\n";
                ring.Enqueue(new KeyValuePair<long, string>(eventSeq, frame));
                while (ring.Count > RingSize) ring.Dequeue();
                targets = new List<Sub>(subs);
                foreach (var s in targets) s.Q.Enqueue(frame);
            }
            foreach (var s in targets) { try { s.Signal.Set(); } catch { } }
        }

        /// <summary>新连接：先给一条 snapshot；如果客户端带了 since，就先把漏掉的补上。</summary>
        public static string InitialFrames(long since)
        {
            var sb = new System.Text.StringBuilder(8192);
            lock (gate)
            {
                if (since > 0 && since < eventSeq)
                {
                    foreach (var kv in ring)
                        if (kv.Key > since) sb.Append(kv.Value);
                }
                Snap cur = last;
                if (cur != null)
                    sb.Append("id: ").Append(eventSeq).Append("\nevent: snapshot\ndata: ").Append(cur.ToJson()).Append("\n\n");
            }
            return sb.ToString();
        }

        public static string Heartbeat()
        {
            return "event: heartbeat\ndata: {\"seq\":" + LastSeq + "}\n\n";
        }
    }
}
