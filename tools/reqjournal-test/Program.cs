// ReqJournal 的独立单测（不进仓库）：把 mod 里的 ReqJournal.cs 原文链进来，
// 用一个 Snap.Str 桩顶掉游戏侧依赖。验证的是"记账/回放/环形淘汰/查账"这套逻辑，不是 HTTP 层。
using System;
using System.Text;

namespace OniAiBridge
{
    // ---- 与 mod 里 Snap.Str 同语义的最小桩：JSON 字符串转义 ----
    internal static class Snap
    {
        public static string Str(string s)
        {
            if (s == null) return "null";
            var sb = new StringBuilder(s.Length + 2);
            sb.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default: sb.Append(c); break;
                }
            }
            sb.Append('"');
            return sb.ToString();
        }
    }

    internal static class Program
    {
        static int fails;
        static void Check(string name, bool ok, string detail = "")
        {
            Console.WriteLine((ok ? "PASS  " : "FAIL  ") + name + (detail.Length > 0 ? "  " + detail : ""));
            if (!ok) fails++;
        }

        static int Main()
        {
            // 1) 记账 → 回放
            ReqJournal.Begin("POST", "/v1/job", "id-1");
            ReqJournal.Record(200, "{\"ok\":true,\"data\":{\"applied\":true}}");
            int st; string body; bool missing;
            bool hit = ReqJournal.TryReplay("id-1", out st, out body, out missing);
            Check("replay hit", hit && st == 200 && !missing && body.Contains("applied"), "status=" + st);

            // 2) 再来一次回放：Replays 递增
            ReqJournal.TryReplay("id-1", out st, out body, out missing);
            string look = ReqJournal.LookupJson("id-1");
            Check("replays counted", look.Contains("\"replays\":2"), look.Substring(0, Math.Min(200, look.Length)));

            // 3) 503 不记账、不回放
            ReqJournal.Begin("POST", "/v1/job", "id-503");
            ReqJournal.Record(503, "{\"ok\":false}");
            Check("503 not recorded", !ReqJournal.TryReplay("id-503", out st, out body, out missing)
                  && ReqJournal.LookupJson("id-503").Contains("\"found\":false"));

            // 4) 超长 body：只记状态、回放时 bodyMissing
            var big = new string('x', ReqJournal.MaxBodyStored + 10);
            ReqJournal.Begin("POST", "/v1/shot", "id-big");
            ReqJournal.Record(200, big);
            bool hitBig = ReqJournal.TryReplay("id-big", out st, out body, out missing);
            Check("oversize body -> bodyMissing", hitBig && missing && body == null && st == 200);

            // 5) 环形淘汰：写满 2*Capacity 条，只留最后 Capacity 条
            for (int i = 0; i < ReqJournal.Capacity * 2; i++)
            {
                ReqJournal.Begin("GET", "/v1/state", "bulk-" + i);
                ReqJournal.Record(200, "{\"i\":" + i + "}");
            }
            Check("ring keeps last Capacity", !ReqJournal.TryReplay("bulk-0", out st, out body, out missing)
                  && ReqJournal.TryReplay("bulk-" + (ReqJournal.Capacity * 2 - 1), out st, out body, out missing));

            // 6) ListJson：升序、条数 <= limit、since 过滤
            string list = ReqJournal.ListJson(0, 1000);
            int n = CountOccurrences(list, "\"seq\":");
            Check("list returns Capacity entries", n == ReqJournal.Capacity, "n=" + n);
            string list2 = ReqJournal.ListJson(0, 5);
            Check("list limit honored", CountOccurrences(list2, "\"seq\":") == 5);
            int firstSeq = ExtractFirstSeq(list);
            int lastSeq = ExtractLastSeq(list);
            Check("list ascending & monotonic", firstSeq < lastSeq, "first=" + firstSeq + " last=" + lastSeq);
            string listSince = ReqJournal.ListJson(lastSeq - 3, 1000);
            Check("since filter", CountOccurrences(listSince, "\"seq\":") == 3,
                  "count=" + CountOccurrences(listSince, "\"seq\":"));

            // 7) Sanitize
            Check("sanitize strips", ReqJournal.Sanitize("ab\"c{}\n d-e.f:g_1") == "abcd-e.f:g_1",
                  ReqJournal.Sanitize("ab\"c{}\n d-e.f:g_1"));
            Check("sanitize empty -> null", ReqJournal.Sanitize("") == null && ReqJournal.Sanitize("!!!") == null);
            Check("sanitize caps 64", ReqJournal.Sanitize(new string('a', 200)).Length == 64);

            // 8) 没 reqId（Begin 传 null）⇒ 不记账
            ReqJournal.Begin("GET", "/", null);
            ReqJournal.Record(200, "{\"html\":1}");
            Check("no reqId -> no record", ReqJournal.ListJson(0, 1000).Contains("\"reqId\":null") == false);

            Console.WriteLine(fails == 0 ? "ALL PASS" : ("FAILED " + fails));
            return fails == 0 ? 0 : 1;
        }

        static int CountOccurrences(string hay, string needle)
        {
            int c = 0, i = 0;
            while ((i = hay.IndexOf(needle, i, StringComparison.Ordinal)) >= 0) { c++; i += needle.Length; }
            return c;
        }

        static int ExtractFirstSeq(string json)
        {
            int i = json.IndexOf("\"seq\":", StringComparison.Ordinal);
            if (i < 0) return -1;
            int s = i + 6, e = s;
            while (e < json.Length && (char.IsDigit(json[e]) || json[e] == '-')) e++;
            return int.Parse(json.Substring(s, e - s));
        }

        static int ExtractLastSeq(string json)
        {
            int i = json.LastIndexOf("\"seq\":", StringComparison.Ordinal);
            if (i < 0) return -1;
            int s = i + 6, e = s;
            while (e < json.Length && (char.IsDigit(json[e]) || json[e] == '-')) e++;
            return int.Parse(json.Substring(s, e - s));
        }
    }
}
