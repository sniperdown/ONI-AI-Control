using System.Collections.Generic;
using System.Text;

namespace OniAiBridge
{
    /// <summary>
    /// 一次采集的结构化快照。**只在主线程构建**，之后给 HTTP 线程当不可变数据用（只读）。
    /// 有了结构化快照才能算 diff —— SSE 推送的基础。
    /// </summary>
    internal sealed class Snap
    {
        public string Ts;
        public int CycleRaw, Cycle, Frame, Speed, WorldId;
        public float TimeSinceStartOfCycle, TimePlayed;
        public bool IsNight, IsPaused;
        public string WorldName, WorldTypeKey;
        public bool IsStartWorld;
        public int WorldSizeX, WorldSizeY;               // 0,0 = 游戏没给
        public string ColonyError;

        public readonly List<DupeSnap> Dupes = new List<DupeSnap>();
        public int DupeCount;
        public readonly Dictionary<string, float> Elements = new Dictionary<string, float>();
        public int ElementCount;
        // ---- P2 诊断（M6.12）：让 count:0 能区分"真的没有"和"我们查错了范围" ----
        public int ElementTagsScanned;    // ElementLoader 里扫了多少个元素 tag
        public int ElementTagsFound;      // 真正有数（>0）的 tag 数（Storage 路专用）
        public int ElementPositiveTags;   // 其中累加后 > 0 的有几个
        public int ElementPositiveTagsRelated;   // 用 GetAmount(tag,true) 再数一遍（验证第二参语义）
        public int ElementWorldsScanned;  // 累加了几个世界
        public int ElementStoragesScanned;   // 备选路：扫了几个 Storage（让"备选路真的跑过"可自证）
        public int ElementTagsUnnamed;    // tag 名读不出来（被跳过）的个数
        public long ElapsedMs;                // 整个 snap 的采集耗时
        public string ElementScope;       // 本次统计范围说明
        public string ElementsError;

        public sealed class DupeSnap
        {
            public string Key;                            // diff 用的稳定键（dbId，重复则加序号）
            public string Name, DbId, Gender, ChoreText, ChoreId;
            public int Cell;
        public int X = -1;                 // 由 Grid.CellToXY 算，调用方不必自己换算
        public int Y = -1;
        public int WorldId = -1;
            public readonly Dictionary<string, float> Amounts = new Dictionary<string, float>();
            public float CaloriesKcal = float.NaN, TemperatureC = float.NaN, StressValue = float.NaN;
            public string AmountsError;
        }

        // ---------------- 序列化 ----------------
        public string ToJson()
        {
            var sb = new StringBuilder(4096);
            sb.Append('{');
            sb.Append("\"elapsedMs\":").Append(ElapsedMs).Append(',');
            sb.Append("\"colony\":{\"ts\":").Append(Str(Ts));
            if (ColonyError != null) sb.Append(",\"error\":").Append(Str(ColonyError));
            sb.Append(",\"cycleRaw\":").Append(Cycle);
            sb.Append(",\"cycle\":").Append(Cycle + 1);
            sb.Append(",\"timeSinceStartOfCycle\":").Append(F(TimeSinceStartOfCycle));
            sb.Append(",\"timePlayed\":").Append(F(TimePlayed));
            sb.Append(",\"frame\":").Append(Frame);
            sb.Append(",\"isNight\":").Append(IsNight ? "true" : "false");
            sb.Append(",\"isPaused\":").Append(IsPaused ? "true" : "false");
            sb.Append(",\"speed\":").Append(Speed + 1);          // 对外语义 1|2|3（内部 0 基）
            sb.Append(",\"speedRaw\":").Append(Speed);
            sb.Append(",\"worldId\":").Append(WorldId);
            sb.Append(",\"world\":{\"name\":").Append(Str(WorldName));
            sb.Append(",\"typeKey\":").Append(Str(WorldTypeKey));
            sb.Append(",\"isStartWorld\":").Append(IsStartWorld ? "true" : "false");
            if (WorldSizeX != 0 || WorldSizeY != 0)
                sb.Append(",\"size\":[").Append(WorldSizeX).Append(',').Append(WorldSizeY).Append(']');
            else
                sb.Append(",\"size\":null,\"sizeNote\":\"WorldContainer.WorldSize 返回 0x0\"");
            sb.Append("}},");

            sb.Append("\"dupes\":{\"ts\":").Append(Str(Ts)).Append(",\"count\":").Append(DupeCount).Append(",\"items\":[");
            for (int i = 0; i < Dupes.Count; i++)
            {
                var d = Dupes[i];
                if (i > 0) sb.Append(',');
                sb.Append("{\"ts\":").Append(Str(Ts));
                sb.Append(",\"name\":").Append(Str(d.Name));
                sb.Append(",\"dbId\":").Append(Str(d.DbId));
                sb.Append(",\"gender\":").Append(Str(d.Gender));
                sb.Append(",\"cell\":").Append(d.Cell);
                sb.Append(",\"x\":").Append(d.X).Append(",\"y\":").Append(d.Y);
                sb.Append(",\"worldId\":").Append(d.WorldId);
                if (d.ChoreText == null && d.ChoreId == null) sb.Append(",\"chore\":null");
                else
                {
                    sb.Append(",\"chore\":{\"text\":").Append(Str(d.ChoreText));
                    sb.Append(",\"id\":").Append(Str(d.ChoreId)).Append('}');
                }
                sb.Append(",\"amounts\":{");
                bool f = true;
                foreach (var kv in d.Amounts)
                {
                    if (!f) sb.Append(',');
                    f = false;
                    sb.Append(Str(kv.Key)).Append(':').Append(F(kv.Value));
                }
                // 与 M1 一致：派生值就放在 amounts 里（不改位置，避免破坏已验证的客户端）
                if (!float.IsNaN(d.CaloriesKcal)) { if (!f) sb.Append(','); f = false; sb.Append("\"CaloriesKcal\":").Append(F(d.CaloriesKcal)); }
                if (!float.IsNaN(d.TemperatureC)) { if (!f) sb.Append(','); f = false; sb.Append("\"TemperatureC\":").Append(F(d.TemperatureC)); }
                sb.Append('}');
                if (d.AmountsError != null) sb.Append(",\"amountsError\":").Append(Str(d.AmountsError));
                sb.Append('}');
            }
            sb.Append("]},");

            sb.Append("\"elements\":{\"ts\":").Append(Str(Ts));
            if (ElementsError != null) sb.Append(",\"error\":").Append(Str(ElementsError));
            sb.Append(",\"diagnostics\":{\"tagsScanned\":").Append(ElementTagsScanned)
              .Append(",\"positiveTags\":").Append(ElementPositiveTags)
              .Append(",\"positiveTagsRelated\":").Append(ElementPositiveTagsRelated)
              .Append(",\"worldsScanned\":").Append(ElementWorldsScanned)
              .Append(",\"storagesScanned\":").Append(ElementStoragesScanned)
              .Append(",\"tagsUnnamed\":").Append(ElementTagsUnnamed)
              .Append(",\"scope\":").Append(Str(ElementScope)).Append("}");
            sb.Append(",\"count\":").Append(ElementCount).Append(",\"items\":{");
            bool f2 = true;
            foreach (var kv in Elements)
            {
                if (!f2) sb.Append(',');
                f2 = false;
                sb.Append(Str(kv.Key)).Append(':').Append(F(kv.Value));
            }
            sb.Append("},\"unit\":\"kg\"");
            sb.Append(",\"source\":\"WorldInventory.GetAmount(element.tag,false)\"");
            if (ElementCount > Elements.Count) sb.Append(",\"truncated\":true");
            sb.Append('}');
            sb.Append('}');
            return sb.ToString();
        }

        // ---------------- diff ----------------
        /// <summary>返回 null 表示"没有值得推的变化"。输出是 JSON 数组。</summary>
        public static string DiffJson(Snap prev, Snap cur, float amountEpsilon)
        {
            var items = new List<string>();
            if (prev != null)
            {
                if (prev.Cycle != cur.Cycle) items.Add(Rec("path", "colony.cycle", prev.Cycle, cur.Cycle));
                if (prev.IsPaused != cur.IsPaused) items.Add(Rec("path", "colony.isPaused", prev.IsPaused ? 1 : 0, cur.IsPaused ? 1 : 0));
                if (prev.Speed != cur.Speed) items.Add(Rec("path", "colony.speed", prev.Speed + 1, cur.Speed + 1));
                if (System.Math.Abs(prev.TimeSinceStartOfCycle - cur.TimeSinceStartOfCycle) >= 1f)
                    items.Add(Rec("path", "colony.timeSinceStartOfCycle", prev.TimeSinceStartOfCycle, cur.TimeSinceStartOfCycle));

                // 生命周期
                var prevKeys = new Dictionary<string, DupeSnap>();
                foreach (var d in prev.Dupes) if (d.Key != null) prevKeys[d.Key] = d;
                foreach (var d in cur.Dupes)
                {
                    if (d.Key == null) continue;
                    DupeSnap old;
                    if (!prevKeys.ContainsKey(d.Key))
                    {
                        items.Add("{\"kind\":\"dupe.spawned\",\"id\":" + Str(d.Key) + ",\"name\":" + Str(d.Name) + "}");
                        continue;
                    }
                    old = prevKeys[d.Key];
                    prevKeys.Remove(d.Key);
                    if (old.ChoreText != d.ChoreText || old.ChoreId != d.ChoreId)
                        items.Add("{\"kind\":\"dupe.chore\",\"id\":" + Str(d.Key) + ",\"from\":" + Str(old.ChoreText) + ",\"to\":" + Str(d.ChoreText) + "}");
                    foreach (var kv in d.Amounts)
                    {
                        float o;
                        if (!old.Amounts.TryGetValue(kv.Key, out o)) { items.Add(Rec("path", "dupes." + d.Key + ".amounts." + kv.Key, null, kv.Value)); continue; }
                        if (System.Math.Abs(o - kv.Value) >= amountEpsilon)
                            items.Add(Rec("path", "dupes." + d.Key + ".amounts." + kv.Key, o, kv.Value));
                    }
                    if (old.Cell != d.Cell) items.Add(Rec("path", "dupes." + d.Key + ".cell", old.Cell, d.Cell));
                }
                foreach (var kv in prevKeys)
                    items.Add("{\"kind\":\"dupe.died\",\"id\":" + Str(kv.Key) + ",\"name\":" + Str(kv.Value.Name) + "}");

                // 资源
                foreach (var kv in cur.Elements)
                {
                    float o;
                    if (!prev.Elements.TryGetValue(kv.Key, out o)) { items.Add(Rec("path", "elements." + kv.Key, null, kv.Value)); continue; }
                    if (System.Math.Abs(o - kv.Value) >= 1f)
                        items.Add(Rec("path", "elements." + kv.Key, o, kv.Value));
                }
                foreach (var kv in prev.Elements)
                    if (!cur.Elements.ContainsKey(kv.Key))
                        items.Add(Rec("path", "elements." + kv.Key, kv.Value, null));
            }
            if (items.Count == 0) return null;
            var sb = new StringBuilder();
            sb.Append('[');
            for (int i = 0; i < items.Count; i++) { if (i > 0) sb.Append(','); sb.Append(items[i]); }
            sb.Append(']');
            return sb.ToString();
        }

        static string Rec(string key, string path, object from, object to)
        {
            return "{\"" + key + "\":" + Str(path) + ",\"from\":" + Num(from) + ",\"to\":" + Num(to) + "}";
        }

        static string Num(object o)
        {
            if (o == null) return "null";
            if (o is float) return F((float)o);
            return System.Convert.ToString(o, System.Globalization.CultureInfo.InvariantCulture);
        }

        public static string F(float v)
        {
            if (float.IsNaN(v) || float.IsInfinity(v)) return "null";
            return v.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
        }

        public static string D(double v)
        {
            if (double.IsNaN(v) || double.IsInfinity(v)) return "null";
            return v.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
        }
        public static string Str(string s) { return s == null ? "null" : "\"" + Bridge.Escape(s) + "\""; }
    }
}
