using System.Collections.Generic;

// ⚠️ 不要 using System 之后直接用 Action/DateTime：Assembly-CSharp 里的全局 Action 枚举会遮蔽 System.Action。
namespace OniAiBridge
{
    /// <summary>
    /// 0.96.1：**请求对账（reqId）+ 写操作幂等重放** —— 给"外部 AI 靠一次 HTTP 调用干活"这个链路补两样东西。
    ///
    /// 为什么需要它（外部 agent = WorkBuddy 那轮验收暴露的形态）：
    /// * 它的 shell 只活一轮，`curl` 没有超时/重试；一次 `503 RETRY_LATER`（主线程忙 ⇒ **动作尚未执行**）
    ///   或一次连接超时，对它来说和"执行了但回执丢了"长得一模一样 ⇒ **不知道到底做没做**；
    /// * 反过来，如果它**重试**，写操作（`/v1/job` 打标记、`/v1/build`)会**真做第二次**；
    /// * 请求里没有任何 id ⇒ 回执、日志、游戏侧 Player.log 三者对不上号。
    ///
    /// 这一版给的最小闭环（不做队列、不做持久化，只在内存里留下最近的账）：
    /// 1. 请求可带 `X-Oni-Req-Id: <id>`（或 `?reqId=`）；服务端**原样回显**在响应体内（`reqId` 字段），
    ///    没带就自己生成一个（`srv-…`）；
    /// 2. 每个请求（除 `503`）完成后记一条账：`reqId / method / path / status / elapsedMs / at`；
    /// 3. **同一个 `reqId` 的 POST 再次到达 ⇒ 直接回放上次的响应体**（只读 `X-Oni-Replayed: 1`），
    ///    **不再执行一次** ⇒ 客户端"超时重试"变成安全的；
    /// 4. `GET /v1/journal?reqId=…` / `?since=…` 可以事后查账：**这次到底执行了没有、结果是什么**。
    ///
    /// 边界（写清楚，免得被当成持久化保证）：
    /// * 只存内存、只留最近 <see cref="Capacity"/> 条；**游戏重载 mod / 存档重载后清空**；
    /// * `503 RETRY_LATER` **不记账、不回放**（它的语义就是"动作尚未执行，请重试"）；
    /// * 响应体超过 <see cref="MaxBodyStored"/> 字节只记"已执行 + 状态码"，不存全文（回放时明确告知）。
    /// </summary>
    internal static class ReqJournal
    {
        /// <summary>环形账本的容量（条）。够覆盖一次"外部 agent 干一轮"的量级；超了最旧的被顶掉。</summary>
        public const int Capacity = 128;

        /// <summary>单条响应体最多存多少字节（超过就只记账不存全文）。</summary>
        public const int MaxBodyStored = 65536;

        internal sealed class Entry
        {
            public long Seq;
            public string ReqId;
            public string Method;
            public string Path;
            public int Status;
            public bool Ok;
            public string Body;          // 可能为 null（太大 / 未存）
            public bool BodyTruncated;
            public string At;            // ISO 8601 本地时间（带偏移）
            public long ElapsedMs;
            public int Replays;          // 这个 reqId 被回放过几次
        }

        static readonly object gate = new object();
        static readonly Entry[] ring = new Entry[Capacity];
        static readonly Dictionary<string, Entry> byId = new Dictionary<string, Entry>(System.StringComparer.Ordinal);
        static int ringNext;     // 下一个写入槽
        static int stored;       // 当前有效条数
        static long seq;         // 全局单调序号（since= 用）
        static long idSeq;

        // 一条连接一个线程（Bridge.AcceptLoop 每连接起一个 Thread）⇒ ThreadStatic 是安全的传递通道，
        // 不必把 reqId 顺着 60 多个 WriteJson 调用点一路加参数。
        [System.ThreadStatic] static string curId;
        [System.ThreadStatic] static string curMethod;
        [System.ThreadStatic] static string curPath;
        [System.ThreadStatic] static long curTicks;

        internal static string CurrentId { get { return curId; } }

        /// <summary>本线程当前请求的 reqId（没有则 <c>null</c>）。响应外壳用它回显。</summary>
        internal static string ReqIdJson()
        {
            string id = curId;
            return string.IsNullOrEmpty(id) ? ",\"reqId\":null" : ",\"reqId\":" + Snap.Str(id);
        }

        /// <summary>服务端生成的 id（调用方没给时用）。保证不会与客户端给的重复 ⇒ 永远不会被回放命中。</summary>
        public static string NewId()
        {
            long n = System.Threading.Interlocked.Increment(ref idSeq);
            return "srv-" + System.DateTime.UtcNow.Ticks.ToString("x") + "-" + n.ToString("x");
        }

        /// <summary>清洗客户端给的 id：只留 <c>[A-Za-z0-9._:-]</c>，最长 64；空 ⇒ null（当作没给）。</summary>
        public static string Sanitize(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return null;
            var sb = new System.Text.StringBuilder(64);
            for (int i = 0; i < raw.Length && sb.Length < 64; i++)
            {
                char ch = raw[i];
                bool ok = (ch >= 'a' && ch <= 'z') || (ch >= 'A' && ch <= 'Z') || (ch >= '0' && ch <= '9')
                          || ch == '.' || ch == '_' || ch == ':' || ch == '-';
                if (ok) sb.Append(ch);
            }
            return sb.Length == 0 ? null : sb.ToString();
        }

        internal static void Begin(string method, string path, string reqId)
        {
            curId = reqId;
            curMethod = method;
            curPath = path;
            curTicks = System.Diagnostics.Stopwatch.GetTimestamp();
        }

        internal static void Clear()
        {
            curId = null; curMethod = null; curPath = null; curTicks = 0;
        }

        /// <summary>
        /// 记一条账。<b>必须在把响应写到 socket 之前调用</b>：写失败说明"动作已经做了、回执没送到"，
        /// 这条账正好是重试时该回放的那一条；反过来先写后记，就会漏掉这一格。
        /// <c>503</c> 不记（语义是"尚未执行，请重试"）。
        /// </summary>
        internal static void Record(int status, string body)
        {
            string id = curId;
            if (string.IsNullOrEmpty(id)) return;         // 菜单页 / 401 等没有 reqId 的请求不记账
            if (status == 503) return;                    // 未执行 ⇒ 不记账、不回放

            long ms = 0;
            try { if (curTicks > 0) ms = (long)((System.Diagnostics.Stopwatch.GetTimestamp() - curTicks) * 1000.0 / System.Diagnostics.Stopwatch.Frequency); }
            catch { ms = 0; }

            var e = new Entry
            {
                ReqId = id,
                Method = curMethod,
                Path = curPath,
                Status = status,
                Ok = status >= 200 && status < 300,
                At = System.DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ssK"),
                ElapsedMs = ms,
                BodyTruncated = false,
                Replays = 0
            };
            if (body != null && body.Length <= MaxBodyStored) e.Body = body;
            else e.BodyTruncated = true;

            lock (gate)
            {
                Entry old = ring[ringNext];
                if (old != null)
                {
                    Entry prev;
                    if (byId.TryGetValue(old.ReqId, out prev) && ReferenceEquals(prev, old)) byId.Remove(old.ReqId);
                }
                e.Seq = ++seq;
                ring[ringNext] = e;
                ringNext = (ringNext + 1) % Capacity;
                if (stored < Capacity) stored++;
                byId[id] = e;      // 同 id 再来一次（真实重发）覆盖成最新一条
            }
        }

        /// <summary>
        /// 命中则回放上次那条响应体（<paramref name="body"/> 可能为 null ⇒ 只能给状态码）。
        /// 只对客户端**显式给过**的 reqId 生效（服务端生成的 id 不会重复，命中也无所谓）。
        /// </summary>
        public static bool TryReplay(string reqId, out int status, out string body, out bool bodyMissing)
        {
            status = 0; body = null; bodyMissing = false;
            if (string.IsNullOrEmpty(reqId)) return false;
            Entry e;
            lock (gate)
            {
                if (!byId.TryGetValue(reqId, out e) || e == null) return false;
                if (e.Status == 503) return false;        // 503 从未记账，这里再兜一层
                e.Replays++;
                status = e.Status;
                body = e.Body;
                bodyMissing = e.Body == null;
            }
            return true;
        }

        /// <summary>`GET /v1/journal?reqId=…` 的单条查账结果（带响应体）。</summary>
        public static string LookupJson(string reqId)
        {
            Entry e = null;
            lock (gate) { if (!string.IsNullOrEmpty(reqId)) byId.TryGetValue(reqId, out e); }
            if (e == null)
                return "{\"reqId\":" + (string.IsNullOrEmpty(reqId) ? "null" : Snap.Str(reqId))
                     + ",\"found\":false,\"note\":" + Snap.Str(
                         "本进程内存里没有这条 reqId 的账。可能是：①请求没到达服务端；②服务端回了 503（按约定不记账）；"
                         + "③账已被挤出（容量 " + Capacity + " 条）；④mod 或存档重载过（账本随进程清空）。"
                         + "**`found:false` 不等于「一定没执行」**，只等于「没有记录」。")
                     + "}";
            return "{\"reqId\":" + Snap.Str(e.ReqId) + ",\"found\":true,\"entry\":" + EntryJson(e, true) + "}";
        }

        /// <summary>`GET /v1/journal?since=N` 的列表（不含响应体，只给账目）。</summary>
        public static string ListJson(long sinceSeq, int limit)
        {
            if (limit <= 0) limit = 20;
            if (limit > Capacity) limit = Capacity;

            var sb = new System.Text.StringBuilder(4096);
            int n = 0;
            lock (gate)
            {
                sb.Append("{\"since\":").Append(sinceSeq).Append(",\"capacity\":").Append(Capacity)
                  .Append(",\"stored\":").Append(stored).Append(",\"entries\":[");
                // 按序号升序：从 ringNext 往后走一圈
                for (int k = 0; k < stored; k++)
                {
                    int idx = (ringNext - stored + k + Capacity * 2) % Capacity;
                    Entry e = ring[idx];
                    if (e == null || e.Seq <= sinceSeq) continue;
                    if (n >= limit) break;
                    if (n > 0) sb.Append(',');
                    sb.Append(EntryJson(e, false));
                    n++;
                }
                sb.Append("],\"count\":").Append(n).Append('}');
            }
            return sb.ToString();
        }

        static string EntryJson(Entry e, bool withBody)
        {
            var sb = new System.Text.StringBuilder(256);
            sb.Append("{\"seq\":").Append(e.Seq);
            sb.Append(",\"reqId\":").Append(Snap.Str(e.ReqId));
            sb.Append(",\"method\":").Append(Snap.Str(e.Method));
            sb.Append(",\"path\":").Append(Snap.Str(e.Path));
            sb.Append(",\"status\":").Append(e.Status);
            sb.Append(",\"ok\":").Append(e.Ok ? "true" : "false");
            sb.Append(",\"at\":").Append(Snap.Str(e.At));
            sb.Append(",\"elapsedMs\":").Append(e.ElapsedMs);
            sb.Append(",\"replays\":").Append(e.Replays);
            sb.Append(",\"bodyTruncated\":").Append(e.BodyTruncated ? "true" : "false");
            if (withBody) sb.Append(",\"body\":").Append(e.Body == null ? "null" : Snap.Str(e.Body));
            sb.Append('}');
            return sb.ToString();
        }
    }
}
