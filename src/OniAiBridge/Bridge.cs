using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

// ⚠️ 不要 using System 之后直接用 Action/DateTime：Assembly-CSharp 里有同名全局类型会遮蔽它们。
namespace OniAiBridge
{
    /// <summary>
    /// 本机 HTTP 服务（TcpListener 手写最小 HTTP/1.1 + SSE）。
    ///
    /// 铁律：**处理线程永不触碰 Unity 对象**。需要游戏数据就 MainThread.Invoke 回主线程，
    /// 拿回来的是已经算好的字符串 / 只读快照。
    /// </summary>
    internal static class Bridge
    {
        public const string ApiVersion = "1.1";
        public const string ModVersion = "0.115.0";
        public const string Stage = "M7.5";

        static BridgeSettings settings;
        static TcpListener listener;
        static Thread acceptThread;
        static long seq;
        static int boundPort;
        static string loopbackOnly = "true";

        public static BridgeSettings SettingsRef { get { return settings; } }
        public static bool Running { get { return listener != null; } }
        public static int Port { get { return boundPort; } }

        // 进程级信息：**不依赖主线程泵**。OnLoad（本身在主线程）缓存一次，主菜单下 /v1/meta 也能答。
        static string cachedBuildText;
        static string cachedUnityVersion;
        static string cachedBundleVersion;
        static volatile bool processInfoCached;

        /// <summary>只允许在主线程调用（幂等；失败留空，下次再试）。</summary>
        public static void CacheProcessInfoOnce()
        {
            if (processInfoCached) return;
            try { if (cachedUnityVersion == null) cachedUnityVersion = UnityEngine.Application.unityVersion; } catch { }
            try { if (cachedBundleVersion == null) cachedBundleVersion = UnityEngine.Application.version; } catch { }
            try { if (cachedBuildText == null) cachedBuildText = BuildWatermark.GetBuildText(); } catch { }
            if (cachedUnityVersion != null && cachedBundleVersion != null && cachedBuildText != null)
                processInfoCached = true;
        }

        public static void Start(BridgeSettings s)
        {
            settings = s;
            try
            {
                if (!TryBind(s.Port))
                {
                    Log.Warn("端口 " + s.Port + ".." + (s.Port + 9) + " 都被占用，HTTP 服务未启动");
                    return;
                }
                acceptThread = new Thread(AcceptLoop) { IsBackground = true, Name = "OniAiBridge-http" };
                acceptThread.Start();
                Log.Info("listening on http://127.0.0.1:" + boundPort + "/v1/meta  (stage " + Stage + ", apiVersion " + ApiVersion + ")");
                if (s.AllowCheats) Log.Warn("allowCheats=true —— 作弊/调试类命令将可用（会破坏存档一致性，仅限测试档）");
            }
            catch (System.Exception e) { Log.Warn("启动 HTTP 服务失败", e); }
        }

        static bool TryBind(int basePort)
        {
            for (int p = basePort; p < basePort + 10; p++)
            {
                if (p < 1024 || p > 65535) continue;
                try
                {
                    var l = new TcpListener(IPAddress.Loopback, p);   // 只绑 loopback
                    l.Start();
                    listener = l;
                    boundPort = p;
                    return true;
                }
                catch (SocketException) { }
            }
            return false;
        }

        static void AcceptLoop()
        {
            while (true)
            {
                TcpClient c = null;
                try
                {
                    c = listener.AcceptTcpClient();
                    var t = new Thread(() => { try { Serve(c); } catch { } finally { try { c.Close(); } catch { } } });
                    t.IsBackground = true;
                    t.Name = "OniAiBridge-conn";
                    t.Start();
                }
                catch (System.Exception e)
                {
                    if (!(e is System.ObjectDisposedException)) Log.Warn("accept 异常: " + e.Message);
                }
            }
        }

        // ---------------- 请求解析与路由 ----------------
        static void Serve(TcpClient client)
        {
            client.ReceiveTimeout = 5000;
            client.SendTimeout = 30000;
            var st = client.GetStream();

            byte[] buf = new byte[16384];
            int got = st.Read(buf, 0, buf.Length);
            if (got <= 0) return;
            // ★ M6.20：请求一律按 UTF-8 解码（原来用 ASCII ⇒ 中文参数全变 ？）。
            string text = Encoding.UTF8.GetString(buf, 0, got);
            int hdrEnd = text.IndexOf("\r\n\r\n", System.StringComparison.Ordinal);
            string head = hdrEnd > 0 ? text.Substring(0, hdrEnd) : text;
            string body = hdrEnd > 0 ? text.Substring(hdrEnd + 4) : "";
            if (!string.IsNullOrEmpty(body))
            {
                string prev = body.Length > 80 ? body.Substring(0, 80) : body;
                Log.Info("[req] body(80)=" + prev.Replace("\\r", " ").Replace("\\n", " "));
            }

            string[] lines = head.Split(new[] { "\r\n" }, System.StringSplitOptions.None);
            if (lines.Length == 0) return;
            string[] reqLine = lines[0].Split(' ');
            if (reqLine.Length < 2) { WriteJson(client, 400, Error("BAD_REQUEST", "malformed request line")); return; }

            string method = reqLine[0].ToUpperInvariant();
            string rawPath = reqLine[1];
            string path = rawPath, query = "";
            int qi = rawPath.IndexOf('?');
            if (qi >= 0) { path = rawPath.Substring(0, qi); query = rawPath.Substring(qi + 1); }

            if (!Authorized(lines, query))
            {
                WriteJson(client, 401, Error("UNAUTHORIZED", "missing or wrong X-Oni-Token (see settings.json)"));
                return;
            }

            // ★ 0.96.1：**请求对账**。客户端可给 `X-Oni-Req-Id: <id>`（或 `?reqId=`）给这次请求起名字；
            //   同一个 id 的 POST 再来一次 ⇒ **直接回放上次那条响应，不再执行**。
            //   这一条是给"shell 只活一轮、curl 没有重试"的外部 agent 用的：超时后重发不会把活干两遍。
            string reqIdGiven = ReqJournal.Sanitize(HeaderValue(lines, "X-Oni-Req-Id") ?? QueryValue(query, "reqId"));
            if ((method == "POST" || method == "PUT") && reqIdGiven != null)
            {
                int rpStatus; string rpBody; bool rpMissing;
                if (ReqJournal.TryReplay(reqIdGiven, out rpStatus, out rpBody, out rpMissing))
                {
                    if (rpMissing || rpBody == null)
                    {
                        string note = "同一个 reqId 上次已执行（status " + rpStatus + "），但响应体超过 "
                                    + ReqJournal.MaxBodyStored + " 字节未存；这次**没有再次执行**。";
                        rpBody = (rpStatus >= 200 && rpStatus < 300)
                            ? Ok("{\"replayed\":true,\"bodyUnavailable\":true,\"originalStatus\":" + rpStatus
                                 + ",\"note\":" + Snap.Str(note) + "}")
                            : Error("REPLAY_BODY_UNAVAILABLE", note);
                    }
                    WriteJson(client, rpStatus, rpBody,
                        "X-Oni-Replayed: 1\r\nX-Oni-Req-Id: " + reqIdGiven + "\r\n", false);
                    Log.Info("[req] REPLAY reqId=" + reqIdGiven + " " + method + " " + path
                             + " status=" + rpStatus + "（未再次执行）");
                    return;
                }
            }
            string reqId = reqIdGiven ?? ReqJournal.NewId();
            ReqJournal.Begin(method, path, reqId);

            switch (path)
            {
                case "/":
                case "/index.html":
                    if (method != "GET") { WriteJson(client, 405, Error("METHOD_NOT_ALLOWED", "use GET")); return; }
                    WriteHtml(client, 200, WebUi.Html(settings != null ? settings.Token : ""));
                    return;
                case "/v1/settings":
                    if (method == "GET") { WriteJson(client, 200, Ok(SettingsApi.GetJson())); return; }
                    if (method == "PUT")
                    {
                        int st2; string c2, m2;
                        string data2 = SettingsApi.Put(body, out st2, out c2, out m2);
                        if (data2 == null) WriteJson(client, st2, Error(c2 ?? "ERROR", m2 ?? "unknown"));
                        else WriteJson(client, 200, Ok(data2));
                        return;
                    }
                    WriteJson(client, 405, Error("METHOD_NOT_ALLOWED", "use GET or PUT"));
                    return;
                case "/v1/meta": WriteJson(client, 200, Meta()); return;
                case "/v1/health": WriteJson(client, 200, Health()); return;
                case "/v1/journal":   // ★ 0.96.1：请求对账（只读；`?reqId=` 查单条 / `?since=&limit=` 列账目）
                    if (method != "GET") { WriteJson(client, 405, Error("METHOD_NOT_ALLOWED", "use GET")); return; }
                    HandleJournal(client, query);
                    return;
                case "/v1/debug/mods": HandleDebugMods(client); return;   // 诊断：不放进公共 /v1/meta
                case "/v1/catalog": HandleCatalog(client); return;   // M5.2 只读建筑目录
                case "/v1/move":
                    // 0.101.0：移动复制人到指定格（先问 Navigator.CanReach，再调游戏自己的移动入口）
                    if (method != "POST") { WriteJson(client, 405, Error("METHOD_NOT_ALLOWED", "use POST")); return; }
                    {
                        int stM; string cM, mM;
                        string dM = Move.Execute(body, out stM, out cM, out mM);
                        if (dM == null) WriteJson(client, stM, Error(cM ?? "ERROR", mM ?? "unknown"));
                        else WriteJson(client, 200, Ok(dM));
                        return;
                    }                case "/v1/reach":
                    // 0.100.0：可达性查询（下标记之前先问，别给够不到的格子下活）
                    if (method != "POST") { WriteJson(client, 405, Error("METHOD_NOT_ALLOWED", "use POST")); return; }
                    {
                        int stR; string cR, mR;
                        string dR = Reach.Query(body, out stR, out cR, out mR);
                        if (dR == null) WriteJson(client, stR, Error(cR ?? "ERROR", mR ?? "unknown"));
                        else WriteJson(client, 200, Ok(dR));
                        return;
                    }                case "/v1/wire":
                    // 0.98.0：批量铺线 + 显式设连接掩码（含 ForceRebuildNetworks）
                    if (method != "POST") { WriteJson(client, 405, Error("METHOD_NOT_ALLOWED", "use POST")); return; }
                    {
                        int stW; string cW, mW;
                        string dW = WireNet.Run(body, out stW, out cW, out mW);
                        if (dW == null) WriteJson(client, stW, Error(cW ?? "ERROR", mW ?? "unknown"));
                        else WriteJson(client, 200, Ok(dW));
                        return;
                    }                case "/v1/build-materials":
                    // 0.97.0：这个建筑能用哪些材料（穷举，含本档 0kg 的）+ 本档实况（只读、纯数据）
                    if (method != "GET") { WriteJson(client, 405, Error("METHOD_NOT_ALLOWED", "use GET")); return; }
                    {
                        int stBm; string cBm, mBm;
                        string dBm = BuildMaterials.Query(query, out stBm, out cBm, out mBm);
                        if (dBm == null) WriteJson(client, stBm, Error(cBm ?? "ERROR", mBm ?? "unknown"));
                        else WriteJson(client, 200, Ok(dBm));
                        return;
                    }                case "/v1/elements":
                    // 0.97.0：材料/元素性质（运行时遍历 ElementLoader，只读、纯数据）
                    if (method != "GET") { WriteJson(client, 405, Error("METHOD_NOT_ALLOWED", "use GET")); return; }
                    {
                        int stE; string cE, mE;
                        string dE = Elements.Query(query, out stE, out cE, out mE);
                        if (dE == null) WriteJson(client, stE, Error(cE ?? "ERROR", mE ?? "unknown"));
                        else WriteJson(client, 200, Ok(dE));
                        return;
                    }
                case "/v1/debug/vision-sample": HandleVisionSample(client, query); return;   // 一次性诊断（验完可删）
                case "/v1/ui/click":
                    // 0.108.0：body 里带 x/y 就走**坐标点击**（截图左上原点），否则走原来的路径点击
                    if (body != null && (body.IndexOf("\"fx\"", System.StringComparison.Ordinal) >= 0
                        || (body.IndexOf("\"x\"", System.StringComparison.Ordinal) >= 0 && body.IndexOf("\"y\"", System.StringComparison.Ordinal) >= 0)))
                    {
                        int stA; string cA, mA;
                        string dA = UiClickAt.Run(body, out stA, out cA, out mA);
                        if (dA == null) { WriteJson(client, stA, Error(cA ?? "ERROR", mA ?? "unknown")); return; }
                        WriteJson(client, stA, dA);
                        return;
                    }
                    if (method != "POST") { WriteJson(client, 405, Error("METHOD_NOT_ALLOWED", "use POST")); return; }
                    {
                        int stC; string cC, mC;
                        // ★ M6.84（0.93.0）：带 dryRun / name / index 的点击走新实现（path>name>index 三级定位 + dryRun + 回读）
                        if ((body ?? "").IndexOf("\"dryRun\"", System.StringComparison.Ordinal) >= 0
                            || (body ?? "").IndexOf("\"name\"", System.StringComparison.Ordinal) >= 0
                            || (body ?? "").IndexOf("\"index\"", System.StringComparison.Ordinal) >= 0)
                        {
                            int stU2; string cU2, mU2;
                            string dU2 = UiShot.Click(body, out stU2, out cU2, out mU2);
                            if (dU2 == null) { WriteJson(client, stU2, Error(cU2 ?? "ERROR", mU2 ?? "unknown")); return; }
                            WriteJson(client, 200, Ok(dU2));
                            return;
                        }
                        string dC = UiClick.Click(body, out stC, out cC, out mC);
                        if (dC == null)
                        {
                            string det = UiClick.LastDetailsHolder.Value;
                            if (!string.IsNullOrEmpty(det)) WriteJson(client, stC, ErrorDetails(cC ?? "ERROR", mC ?? "unknown", det));
                            else WriteJson(client, stC, Error(cC ?? "ERROR", mC ?? "unknown"));
                            return;
                        }
                        WriteJson(client, 200, Ok(dC));   // ★ 统一外壳（原来漏了 Ok，裸对象与其它 /v1/* 不一致）
                    }
                    return;
                case "/v1/dupes/spawn":
                    // 0.112.0：程序化生成复制人（MinionStartingStats -> Deliver）
                    if (method != "POST") { WriteJson(client, 405, Error("METHOD_NOT_ALLOWED", "use POST")); return; }
                    {
                        int stS; string cS, mS;
                        string dS = DupesSpawn.Execute(body, out stS, out cS, out mS);
                        if (dS == null) WriteJson(client, stS, Error(cS ?? "ERROR", mS ?? "unknown"));
                        else WriteJson(client, 200, Ok(dS));
                        return;
                    }                case "/v1/dupes":
                    if (method != "GET") { WriteJson(client, 405, Error("METHOD_NOT_ALLOWED", "use GET")); return; }
                    {
                        bool idleOnly = !string.IsNullOrEmpty(query) && (query.IndexOf("idle=1") >= 0 || query.IndexOf("idle=true", System.StringComparison.OrdinalIgnoreCase) >= 0);
                        int stD; string cD, mD;
                        string dD = DupesApi.List(idleOnly, out stD, out cD, out mD);
                        if (dD == null) { WriteJson(client, stD, Error(cD ?? "ERROR", mD ?? "unknown")); return; }
                        WriteJson(client, 200, Ok(dD));
                    }
                    return;
                case "/v1/alerts":
                    if (method != "GET") { WriteJson(client, 405, Error("METHOD_NOT_ALLOWED", "use GET")); return; }
                    {
                        int stA2; string cA2, mA2;
                        bool alDetail = !string.IsNullOrEmpty(query) && (query.IndexOf("detail=1") >= 0 || query.IndexOf("detail=true", System.StringComparison.OrdinalIgnoreCase) >= 0);
                        string dA2 = AlertsApi.List(alDetail, out stA2, out cA2, out mA2);
                        if (dA2 == null) { WriteJson(client, stA2, Error(cA2 ?? "ERROR", mA2 ?? "unknown")); return; }
                        WriteJson(client, 200, Ok(dA2));
                    }
                    return;
                case "/v1/shot/cameras":
                    if (method != "GET") { WriteJson(client, 405, Error("METHOD_NOT_ALLOWED", "use GET")); return; }
                    {
                        int stC3; string cC3, mC3;
                        string dC3 = UiShot.Cameras(out stC3, out cC3, out mC3);
                        if (dC3 == null) { WriteJson(client, stC3, Error(cC3 ?? "ERROR", mC3 ?? "unknown")); return; }
                        WriteJson(client, 200, Ok(dC3));
                    }
                    return;
                case "/v1/shot":
                    if (method != "GET") { WriteJson(client, 405, Error("METHOD_NOT_ALLOWED", "use GET")); return; }
                    {
                        int sw = 1600, sh = 0, sq = 80, scx = 0, scy = 0, scw = 0, sch = 0;
                        int sCenterX = -1, sCenterY = -1;
                        if (!string.IsNullOrEmpty(query))
                        {
                            foreach (string kv in query.Split('&'))
                            {
                                int e0 = kv.IndexOf('=');
                                if (e0 <= 0) continue;
                                string k0 = kv.Substring(0, e0), v0 = kv.Substring(e0 + 1);
                                int n0; if (!int.TryParse(v0, out n0)) continue;
                                if (k0 == "w") sw = n0; else if (k0 == "h") sh = n0; else if (k0 == "quality") sq = n0;
                                else if (k0 == "cx") scx = n0; else if (k0 == "cy") scy = n0;
                                else if (k0 == "cw") scw = n0; else if (k0 == "ch") sch = n0;
                                // ★ 0.95.0：按世界格坐标截图（相机临时移过去，渲染后还原）
                                else if (k0 == "centerX") sCenterX = n0; else if (k0 == "centerY") sCenterY = n0;
                            }
                        }
                        int stS; string cS, mS;
                        string dS = UiShot.Shot(sw, sh, sq, scx, scy, scw, sch, sCenterX, sCenterY, out stS, out cS, out mS);
                        if (dS == null) { WriteJson(client, stS, Error(cS ?? "ERROR", mS ?? "unknown")); return; }
                        WriteJson(client, 200, Ok(dS));
                    }
                    return;
                case "/v1/worlds":
                    if (method != "GET") { WriteJson(client, 405, Error("METHOD_NOT_ALLOWED", "use GET")); return; }
                    {
                        int stW; string cW, mW;
                        string dW = Worlds.List(out stW, out cW, out mW);
                        if (dW == null) { WriteJson(client, stW, Error(cW ?? "ERROR", mW ?? "unknown")); return; }
                        WriteJson(client, 200, Ok(dW));
                    }
                    return;
                case "/v1/area":
                    if (method != "GET") { WriteJson(client, 405, Error("METHOD_NOT_ALLOWED", "use GET")); return; }
                    {
                        int aW = 0, aCx = -1, aCy = -1, ax0 = 0, ay0 = 0, ax1 = 0, ay1 = 0, aMax = 4096;
                        bool useChunk = false, hasBox = false;
                        if (!string.IsNullOrEmpty(query))
                        {
                            foreach (string kv in query.Split('&'))
                            {
                                int e0 = kv.IndexOf('=' );
                                if (e0 <= 0) continue;
                                string k0 = kv.Substring(0, e0), v0 = kv.Substring(e0 + 1);
                                int n0; if (!int.TryParse(v0, out n0)) continue;
                                if (k0 == "worldId") aW = n0;
                                else if (k0 == "cx") { aCx = n0; useChunk = true; }
                                else if (k0 == "cy") { aCy = n0; useChunk = true; }
                                else if (k0 == "x0") { ax0 = n0; hasBox = true; }
                                else if (k0 == "y0") { ay0 = n0; hasBox = true; }
                                else if (k0 == "x1") { ax1 = n0; hasBox = true; }
                                else if (k0 == "y1") { ay1 = n0; hasBox = true; }
                                else if (k0 == "maxCells") aMax = n0;
                            }
                        }
                        if (!useChunk && !hasBox)
                        {
                            WriteJson(client, 400, Error("BAD_REQUEST", "需要块寻址 cx/cy，或框寻址 x0/y0/x1/y1"));
                            return;
                        }
                        if (useChunk && (aCx < 0 || aCy < 0))
                        {
                            WriteJson(client, 400, Error("BAD_REQUEST", "cx/cy 必须同时给出且 >= 0"));
                            return;
                        }
                        int stA; string cA, mA;
                        string dA = Area.Query(aW, useChunk, aCx, aCy, ax0, ay0, ax1, ay1, aMax, out stA, out cA, out mA);
                        if (dA == null) { WriteJson(client, stA, Error(cA ?? "ERROR", mA ?? "unknown")); return; }
                        WriteJson(client, 200, Ok(dA));
                    }
                    return;
                case "/v1/geysers":
                    if (method != "GET") { WriteJson(client, 405, Error("METHOD_NOT_ALLOWED", "use GET")); return; }
                    {
                        // ★ M6.66 实测结论：默认 false —— 被物块掩埋的泉**仍是 active 对象**，
                        //   includeInactive=true 只是多扫出 31 个模板再过滤掉（192ms vs 62ms，结果相同）。
                        int wId = -1; bool incInactive = false;
                        if (!string.IsNullOrEmpty(query) && (query.IndexOf("includeInactive=1") >= 0 || query.IndexOf("includeInactive=true", System.StringComparison.OrdinalIgnoreCase) >= 0)) incInactive = true;
                        if (!string.IsNullOrEmpty(query))
                        {
                            int wi = query.IndexOf("worldId=");
                            if (wi >= 0) { string s0 = query.Substring(wi + 8); int a0 = s0.IndexOf('&'); if (a0 >= 0) s0 = s0.Substring(0, a0); int.TryParse(s0, out wId); }
                        }
                        int stG; string cG, mG;
                        string dG = Geysers.List(wId, incInactive, out stG, out cG, out mG);
                        if (dG == null) { WriteJson(client, stG, Error(cG ?? "ERROR", mG ?? "unknown")); return; }
                        WriteJson(client, 200, Ok(dG));
                    }
                    return;
                case "/v1/tools":
                    if (method != "GET") { WriteJson(client, 405, Error("METHOD_NOT_ALLOWED", "use GET")); return; }
                    {
                        int stT; string cT, mT;
                        string dT = Tools.List(out stT, out cT, out mT);
                        if (dT == null) { WriteJson(client, stT, Error(cT ?? "ERROR", mT ?? "unknown")); return; }
                        WriteJson(client, 200, Ok(dT));
                    }
                    return;
                case "/v1/inventory":
                    if (method != "GET") { WriteJson(client, 405, Error("METHOD_NOT_ALLOWED", "use GET")); return; }
                    {
                        string tagI = null;
                        if (!string.IsNullOrEmpty(query))
                        {
                            int ii = query.IndexOf("tag=");
                            if (ii >= 0) { tagI = query.Substring(ii + 4); int ai = tagI.IndexOf('&'); if (ai >= 0) tagI = tagI.Substring(0, ai); }
                        }
                        int stI2; string cI2, mI2;
                        string dI2 = Inventory.Query(tagI, out stI2, out cI2, out mI2);
                        if (dI2 == null) { WriteJson(client, stI2, Error(cI2 ?? "ERROR", mI2 ?? "unknown")); return; }
                        WriteJson(client, 200, Ok(dI2));
                    }
                    return;
                case "/v1/materials":
                    if (method != "GET") { WriteJson(client, 405, Error("METHOD_NOT_ALLOWED", "use GET")); return; }
                    {
                        string tagF = null; double minKg = 0;
                        if (!string.IsNullOrEmpty(query))
                        {
                            int ti2 = query.IndexOf("tag=");
                            if (ti2 >= 0) { tagF = query.Substring(ti2 + 4); int a2 = tagF.IndexOf('&'); if (a2 >= 0) tagF = tagF.Substring(0, a2); }
                            int mi2 = query.IndexOf("minKg=");
                            if (mi2 >= 0) { string s2 = query.Substring(mi2 + 6); int a3 = s2.IndexOf('&'); if (a3 >= 0) s2 = s2.Substring(0, a3); double.TryParse(s2, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out minKg); }
                        }
                        int stM2; string cM2, mM2;
                        string dM2 = Materials.List(tagF, minKg, out stM2, out cM2, out mM2);
                        if (dM2 == null) { WriteJson(client, stM2, Error(cM2 ?? "ERROR", mM2 ?? "unknown")); return; }
                        WriteJson(client, 200, Ok(dM2));
                    }
                    return;
                case "/v1/research":
                    if (method != "GET") { WriteJson(client, 405, Error("METHOD_NOT_ALLOWED", "use GET")); return; }
                    {
                        int stR; string cR, mR;
                        string dR = ResearchApi.List(out stR, out cR, out mR);
                        if (dR == null) { WriteJson(client, stR, Error(cR ?? "ERROR", mR ?? "unknown")); return; }
                        WriteJson(client, 200, Ok(dR));
                    }
                    return;
                case "/v1/build-menu":
                    if (method != "GET") { WriteJson(client, 405, Error("METHOD_NOT_ALLOWED", "use GET")); return; }
                    {
                        string sF = null; int lim = 900;
                        if (!string.IsNullOrEmpty(query))
                        {
                            int si = query.IndexOf("screen=");
                            if (si >= 0) { sF = query.Substring(si + 7); int a0 = sF.IndexOf('&'); if (a0 >= 0) sF = sF.Substring(0, a0); }
                            int li = query.IndexOf("limit=");
                            if (li >= 0) { string s0 = query.Substring(li + 6); int a1 = s0.IndexOf('&'); if (a1 >= 0) s0 = s0.Substring(0, a1); int.TryParse(s0, out lim); }
                        }
                        int stB; string cB, mB;
                        string dB = BuildUi.Menu(sF, lim, out stB, out cB, out mB);
                        if (dB == null) { WriteJson(client, stB, Error(cB ?? "ERROR", mB ?? "unknown")); return; }
                        WriteJson(client, 200, Ok(dB));
                    }
                    return;
                case "/v1/building/select":
                    if (method != "POST") { WriteJson(client, 405, Error("METHOD_NOT_ALLOWED", "use POST")); return; }
                    {
                        int stS2; string cS2, mS2;
                        string dS2 = BuildUi.Select(body, out stS2, out cS2, out mS2);
                        if (dS2 == null) { WriteJson(client, stS2, Error(cS2 ?? "ERROR", mS2 ?? "unknown")); return; }
                        WriteJson(client, 200, dS2);
                    }
                    return;
                case "/v1/env":
                    if (method != "GET") { WriteJson(client, 405, Error("METHOD_NOT_ALLOWED", "use GET")); return; }
                    {
                        int ex = 0, ey = 0, er = 0, emax = 20000; bool eReg = false;
                        if (!string.IsNullOrEmpty(query))
                        {
                            foreach (string kv in query.Split('&'))
                            {
                                int e0 = kv.IndexOf('=' );
                                if (e0 <= 0) continue;
                                string k0 = kv.Substring(0, e0), v0 = kv.Substring(e0 + 1);
                                int n0; if (!int.TryParse(v0, out n0)) continue;
                                if (k0 == "x") { ex = n0; eReg = true; }
                                else if (k0 == "y") { ey = n0; eReg = true; }
                                else if (k0 == "r") { er = n0; eReg = true; }
                                else if (k0 == "maxCells") { emax = n0; }
                            }
                        }
                        int stE; string cE, mE;
                        bool eAround = query != null && query.IndexOf("around=dupes", System.StringComparison.OrdinalIgnoreCase) >= 0;
                        string dE = EnvQuery.Sample(ex, ey, eReg ? ex + er : 0, eReg ? ey + er : 0, eReg, emax,
                                                    eAround, er, out stE, out cE, out mE);
                        if (dE == null) { WriteJson(client, stE, Error(cE ?? "ERROR", mE ?? "unknown")); return; }
                        WriteJson(client, 200, Ok(dE));
                    }
                    return;
                case "/v1/ui/widgets":
                    if (method != "GET") { WriteJson(client, 405, Error("METHOD_NOT_ALLOWED", "use GET")); return; }
                    {
                        string sW = null; int limW = 400;
                        if (!string.IsNullOrEmpty(query))
                        {
                            int siW = query.IndexOf("screen=");
                            if (siW >= 0) { sW = query.Substring(siW + 7); int aW = sW.IndexOf('&'); if (aW >= 0) sW = sW.Substring(0, aW); }
                            int liW = query.IndexOf("limit=");
                            if (liW >= 0) { string s0W = query.Substring(liW + 6); int a1W = s0W.IndexOf('&'); if (a1W >= 0) s0W = s0W.Substring(0, a1W); int.TryParse(s0W, out limW); }
                        }
                        int stW; string cW, mW;
                        string dW = UiWidgets.List(sW, limW, out stW, out cW, out mW);
                        if (dW == null) { WriteJson(client, stW, Error(cW ?? "ERROR", mW ?? "unknown")); return; }
                        WriteJson(client, 200, Ok(dW));
                    }
                    return;
                case "/v1/ui":
                    if (method != "GET") { WriteJson(client, 405, Error("METHOD_NOT_ALLOWED", "use GET")); return; }
                    {
                        int stU; string cU, mU;
                        bool wLabels = false; int lLimit = 200;
                        // ★ 0.93.3：`clickablesLimit` 覆盖每个界面的 clickables 输出上限。
                        // ★ 0.93.4：`clickables` 的**默认输出模式改为 compact**（每条只写 kind/role/text/path），
                        //   显式 `?clickables=full` 取回全字段；两者与 `clickablesLimit` 正交（两种模式下都能限流）。
                        //   两个参数都按"写错不该让整次读取失败"处理 ⇒ 非法值一律回落默认（compact），不报错。
                        int cLimitReq = 0; bool cCompact = true;      // 默认 = compact
                        if (!string.IsNullOrEmpty(query))
                        {
                            int li0 = query.IndexOf("labelsLimit=");
                            if (li0 >= 0) { string s0 = query.Substring(li0 + 12); int amp0 = s0.IndexOf('&'); if (amp0 >= 0) s0 = s0.Substring(0, amp0); int.TryParse(s0, out lLimit); }
                            if (lLimit <= 0) lLimit = 200;
                            wLabels = query.Contains("labels=1");

                            int ci0 = query.IndexOf("clickablesLimit=", System.StringComparison.Ordinal);
                            if (ci0 >= 0) { string s1 = query.Substring(ci0 + 16); int amp1 = s1.IndexOf('&'); if (amp1 >= 0) s1 = s1.Substring(0, amp1); int.TryParse(s1, out cLimitReq); }
                            // 只取 `clickables=` 的值。`clickablesLimit=` 的 `s` 之后是 `L`，不含子串 `clickables=`，
                            // 因此不会与上限参数互相误命中。
                            int cm0 = query.IndexOf("clickables=", System.StringComparison.Ordinal);
                            if (cm0 >= 0)
                            {
                                string s2 = query.Substring(cm0 + 11);
                                int amp2 = s2.IndexOf('&');
                                if (amp2 >= 0) s2 = s2.Substring(0, amp2);
                                cCompact = UiQuery.ParseClickablesMode(s2);
                            }
                        }
                        string dU = UiQuery.List(wLabels, lLimit, cLimitReq, cCompact, out stU, out cU, out mU);
                        if (dU == null) { WriteJson(client, stU, Error(cU ?? "ERROR", mU ?? "unknown")); return; }
                        WriteJson(client, 200, Ok(dU));
                    }
                    return;
                case "/v1/ui/diag":
                    // 0.105.0：一轮判掉「界面是否开着」+「按钮对象叫什么」（主线程执行）
                    {
                        int stD; string cD, mD;
                        string dD = UiDiag.Run(out stD, out cD, out mD);
                        if (dD == null) { WriteJson(client, stD, Error(cD ?? "ERROR", mD ?? "unknown")); return; }
                        WriteJson(client, stD, dD);
                    }
                    return;                case "/v1/saves":
                    if (method != "GET") { WriteJson(client, 405, Error("METHOD_NOT_ALLOWED", "use GET")); return; }
                    {
                        int stS; string cS, mS;
                        string dS = SavesQuery.List(out stS, out cS, out mS);
                        if (dS == null) { WriteJson(client, stS, Error(cS ?? "ERROR", mS ?? "unknown")); return; }
                        WriteJson(client, 200, Ok(dS));
                    }
                    return;
                case "/v1/save/load":
                    if (method != "POST") { WriteJson(client, 405, Error("METHOD_NOT_ALLOWED", "use POST")); return; }
                    {
                        int stL; string cL, mL;
                        string dL = SavesQuery.Load(body, out stL, out cL, out mL);
                        if (dL == null) { WriteJson(client, stL, Error(cL ?? "ERROR", mL ?? "unknown")); return; }
                        WriteJson(client, stL, dL);   // 202 Accepted
                    }
                    return;
                case "/v1/buildings": HandleBuildings(client, query); return;   // M5.4 场上有哪些建筑（带迷雾过滤）
                case "/v1/map":
                    if (method != "GET") { WriteJson(client, 405, Error("METHOD_NOT_ALLOWED", "use GET")); return; }
                    HandleMap(client, query);   // M6.89 地图降采样（默认关，allowMap 开关）
                    return;
                case "/v1/marks":
                    if (method != "GET") { WriteJson(client, 405, Error("METHOD_NOT_ALLOWED", "use GET")); return; }
                    HandleMarks(client, query);
                    return;                case "/v1/job":
                    if (method != "POST") { WriteJson(client, 405, Error("METHOD_NOT_ALLOWED", "use POST")); return; }
                    HandleJob(client, body);
                    return;                case "/v1/door":
                    if (method != "POST") { WriteJson(client, 405, Error("METHOD_NOT_ALLOWED", "use POST")); return; }
                    HandleDoor(client, body);
                    return;                case "/v1/state": HandleState(client, query); return;
                case "/v1/commands": WriteJson(client, 200, Ok(Commands.CommandsJson())); return;
                case "/v1/events": HandleSse(client, lines, query); return;
                case "/v1/command":
                    if (method != "POST") { WriteJson(client, 405, Error("METHOD_NOT_ALLOWED", "use POST")); return; }
                    HandleCommand(client, body);
                    return;
                default:
                    WriteJson(client, 404, Error("NOT_FOUND", "no such endpoint: " + path));
                    return;
            }
        }

        // ---------------- /v1/debug/mods（诊断）----------------
        static void HandleDebugMods(TcpClient client)
        {
            if (!MainThread.Ready) { WriteJson(client, 503, Error("NOT_READY", "main-thread pump is not running yet")); return; }
            string data = null;
            int usedTimeout2; bool wasBusy2;
            bool ok = MainThread.InvokeAdaptive(
                () => { try { data = DebugMods.Collect(); } catch { data = null; } },
                3000, 30000, out usedTimeout2, out wasBusy2);
            if (!ok)
            {
                WriteJson(client, 503,
                    RetryLater("debug/mods", usedTimeout2, wasBusy2),
                    "Retry-After: 1\r\n");
                return;
            }
            if (data == null) { WriteJson(client, 500, Error("INTERNAL", "debug/mods returned null")); return; }
            WriteJson(client, 200, Ok(data));
        }

        // ---------------- /v1/marks（M6.4，单格查询）----------------
        static void HandleMarks(TcpClient client, string query)
        {
            int x = -1, y = -1, worldId = -1;
            bool hasX = false, hasY = false;
            if (!string.IsNullOrEmpty(query))
            {
                foreach (string kv in query.Split('&'))
                {
                    int e = kv.IndexOf('=');
                    if (e <= 0) continue;
                    string k = kv.Substring(0, e), v = kv.Substring(e + 1);
                    int n;
                    if (!int.TryParse(v, out n)) continue;
                    if (k == "x") { x = n; hasX = true; }
                    else if (k == "y") { y = n; hasY = true; }
                    else if (k == "worldId") worldId = n;
                }
            }
            if (!hasX || !hasY) { WriteJson(client, 400, Error("BAD_REQUEST", "需要 ?x=<int>&y=<int>（可选 worldId）")); return; }

            int st2; string c2, m2;
            string data = MarksQuery.Sample(x, y, worldId, out st2, out c2, out m2);
            if (data == null) { WriteJson(client, st2, Error(c2 ?? "ERROR", m2 ?? "unknown")); return; }
            WriteJson(client, 200, Ok(data));
        }
        // ---------------- /v1/job（M6.2；type 分派见 JobQuery.Execute，打标记那批在 JobMarks）----------------
        static void HandleJob(TcpClient client, string body)
        {
            int st2; string c2, m2;
            string det2;
            string data = JobQuery.Execute(body, out st2, out c2, out m2, out det2);
            if (data == null)
            {
                // 全程用返回值/out 参数，不读任何静态字段（DESIGN：隐式通道静默失败）
                WriteJson(client, st2, ErrorDetails(c2 ?? "ERROR", m2 ?? "unknown", det2));
                return;
            }
            WriteJson(client, 200, Ok(data));
        }
        // ---------------- /v1/door（M6.1，普通层 only）----------------
        static void HandleDoor(TcpClient client, string body)
        {
            int st2; string c2, m2;
            string data = DoorQuery.Execute(body, out st2, out c2, out m2);
            if (data == null) { WriteJson(client, st2, Error(c2 ?? "ERROR", m2 ?? "unknown")); return; }
            WriteJson(client, 200, Ok(data));
        }
        // ---------------- /v1/buildings（M5.4，带迷雾过滤）----------------
        static void HandleBuildings(TcpClient client, string query)
        {
            int offset = 0, limit = BuildingsQuery.DefaultLimit; string fPrefab = null;
            bool hasBox = false; int bx0 = 0, by0 = 0, bx1 = 0, by1 = 0;
            bool withNames = false;   // ★ 0.96.0：`?names=1` 才回本地化 name（默认不回，见 BuildingsQuery 类注释）
            if (!string.IsNullOrEmpty(query))
            {
                foreach (string kv in query.Split('&'))
                {
                    int e = kv.IndexOf('=');
                    if (e <= 0) continue;
                    string k = kv.Substring(0, e), v = kv.Substring(e + 1);
                    if (k == "prefabId") { fPrefab = v; continue; }   // 字符串参数：必须在数字解析之前取
                    int n;
                    if (!int.TryParse(v, out n)) continue;
                    if (k == "offset") offset = n; else if (k == "limit") limit = n;
                    else if (k == "names") withNames = (n != 0);
                    if (k == "x0") { bx0 = n; hasBox = true; }
                    else if (k == "y0") { by0 = n; hasBox = true; }
                    else if (k == "x1") { bx1 = n; hasBox = true; }
                    else if (k == "y1") { by1 = n; hasBox = true; }
                }
            }
            if (!MainThread.Ready) { WriteJson(client, 503, Error("NOT_READY", "main-thread pump is not running yet")); return; }

            string data = null;
            int usedTimeout; bool wasBusy;
            bool ok = MainThread.InvokeAdaptive(
                () => { try { data = BuildingsQuery.Query(offset, limit, fPrefab, hasBox, bx0, by0, bx1, by1, withNames); } catch { data = null; } },
                5000, 30000, out usedTimeout, out wasBusy);
            if (!ok)
            {
                // 语义纠正（M6.9）：这不是"查询超时"，而是"主线程还没轮到执行" ⇒ 调用方应重试
                int age = MainThread.LastPumpAgeMs;
                WriteJson(client, 503, RetryLater("buildings", usedTimeout, wasBusy), "Retry-After: 1\r\n");
                Log.Info("[buildings] RETRY_LATER：主线程忙 age=" + age + "ms queue=" + MainThread.QueueDepth
                         + " waited=" + usedTimeout + "ms");
                return;
            }
            if (data == null) { WriteJson(client, 500, Error("INTERNAL", "buildings query failed")); return; }
            Vision.LogMode("buildings");
            WriteJson(client, 200, Ok(data));
        }
        // ---------------- /v1/map（M6.89，默认关 + 迷雾过滤 + 64×64 硬上限）----------------
        /// <summary>
        /// 闸门顺序**故意如此**：`allowMap=false` 时先回 `403 MAP_DISABLED`，
        /// 连"参数对不对"都不判 —— 关掉的功能不该因为参数格式去教调用方怎么用。
        /// 参数解析/上限判定全在 `MapQuery`（本方法只做取值 + 包装，逻辑不进 Bridge.cs）。
        /// </summary>
        static void HandleMap(TcpClient client, string query)
        {
            BridgeSettings s = settings;
            if (s == null || !s.AllowMap)
            {
                WriteJson(client, 403, Error("MAP_DISABLED",
                    "allowMap is false —— /v1/map 默认关闭（DESIGN.md §2.3 第 4 条：地图不默认下发）。"
                    + "打开方式：settings.json 里 \"allowMap\": true，或游戏内设置页（Ctrl+Shift+O）勾上地图开关，然后重载 mod。"));
                return;
            }

            string bboxRaw = null, strideRaw = null;
            if (!string.IsNullOrEmpty(query))
            {
                foreach (string kv in query.Split('&'))
                {
                    int e = kv.IndexOf('=');
                    if (e <= 0) continue;
                    string k = kv.Substring(0, e), v = kv.Substring(e + 1);
                    if (k == "bbox") bboxRaw = v;        // 字符串参数：必须在数字解析之前取
                    else if (k == "stride") strideRaw = v;
                }
            }

            int st; string c, m;
            string data = MapQuery.Query(bboxRaw, strideRaw, out st, out c, out m);
            if (data == null) { WriteJson(client, st, Error(c ?? "ERROR", m ?? "unknown")); return; }
            Vision.LogMode("map");
            WriteJson(client, 200, Ok(data));
        }
        // ---------------- /v1/debug/vision-sample（一次性诊断，验完可删）----------------
        static void HandleVisionSample(TcpClient client, string query)
        {
            // 三种用法：?x=&y=（世界内坐标）｜?cell=<Grid 索引>｜什么都不给 ⇒ 自动用第一个活着的复制人所在格
            int x = -1, y = -1, cellIn = -1;
            bool auto = true;
            if (!string.IsNullOrEmpty(query))
            {
                foreach (string kv in query.Split('&'))
                {
                    int e = kv.IndexOf('=');
                    if (e <= 0) continue;
                    string k = kv.Substring(0, e), v = kv.Substring(e + 1);
                    int n;
                    if (!int.TryParse(v, out n)) continue;
                    if (k == "x") { x = n; auto = false; }
                    else if (k == "y") { y = n; auto = false; }
                    else if (k == "cell") { cellIn = n; auto = false; }
                }
            }
            if (!auto && cellIn < 0 && (x < 0 || y < 0))
            {
                WriteJson(client, 400, Error("BAD_REQUEST", "需要 ?x=<int>&y=<int> 或 ?cell=<int>，或干脆不给（自动取第一个复制人所在格）"));
                return;
            }
            if (!MainThread.Ready) { WriteJson(client, 503, Error("NOT_READY", "main-thread pump is not running yet")); return; }

            string data = null;
            bool ok = MainThread.InvokeAdaptive(
                () => { try { data = Vision.Sample(x, y, cellIn, auto); } catch { data = null; } },
                3000, 30000, out var visionT, out var visionB);
            if (!ok) { WriteJson(client, 503, RetryLater("debug/vision-sample", visionT, visionB), "Retry-After: 1\r\n"); return; }
            if (data == null) { WriteJson(client, 500, Error("INTERNAL", "vision sample failed")); return; }
            Log.Info("[vision-sample] " + data);
            WriteJson(client, 200, Ok(data));
        }
        // ---------------- /v1/catalog（M5.2，只读）----------------
        static void HandleCatalog(TcpClient client)
        {
            if (!MainThread.Ready)
            {
                WriteJson(client, 503, Error("NOT_READY", "main-thread pump is not running yet"));
                return;
            }
            string data = null;
            bool ok = MainThread.InvokeAdaptive(
                () => { try { data = Catalog.Build(); } catch { data = null; } },
                5000, 30000, out var catalogT, out var catalogB);
            if (!ok) { WriteJson(client, 503, RetryLater("catalog", catalogT, catalogB), "Retry-After: 1\r\n"); return; }
            if (data == null) { WriteJson(client, 500, Error("INTERNAL", "catalog build failed")); return; }
            WriteJson(client, 200, Ok(data));
        }
        // ---------------- /v1/state ----------------
        static void HandleState(TcpClient client, string query)
        {
            if (!MainThread.Ready)
            {
                WriteJson(client, 503, Error("NOT_READY", "main-thread pump is not running yet (Harmony patch inactive, or colony not loaded)"));
                return;
            }
            string data = null;
            string elsMode = null;
            if (!string.IsNullOrEmpty(query))
            {
                int emi = query.IndexOf("elementsMode=");
                if (emi >= 0)
                {
                    elsMode = query.Substring(emi + 13);
                    int amp = elsMode.IndexOf('&');
                    if (amp >= 0) elsMode = elsMode.Substring(0, amp);
                }
            }
            bool ok = MainThread.InvokeAdaptive(
                () => { try { data = State.CollectSnap(elsMode).ToJson(); } catch { data = null; } },
                3000, 30000, out var stateT, out var stateB);
            if (!ok) { WriteJson(client, 503, RetryLater("state", stateT, stateB), "Retry-After: 1\r\n"); return; }
            if (data == null) { WriteJson(client, 500, Error("INTERNAL", "state collection failed")); return; }
            Vision.LogMode("state");   // 断言：本次响应是在哪种视野模式下产出的
            WriteJson(client, 200, Ok(data));
        }

        // ---------------- /v1/command ----------------
        static void HandleCommand(TcpClient client, string body)
        {
            if (!MainThread.Ready)
            {
                WriteJson(client, 503, Error("NOT_READY", "main-thread pump is not running yet"));
                return;
            }
            string op = ExtractOp(body);
            string result = null;
            int status = 200; string code = null, msg = null;
            bool ok = MainThread.InvokeAdaptive(
                () => { try { result = Commands.Execute(op, body, out status, out code, out msg); } catch { } },
                3000, 30000, out var commandT, out var commandB);
            if (!ok) { WriteJson(client, 503, RetryLater("command", commandT, commandB), "Retry-After: 1\r\n"); return; }
            if (result == null) { WriteJson(client, status, Error(code ?? "INTERNAL", msg ?? "command failed")); return; }
            WriteJson(client, 200, Ok(result));
        }

        static string ExtractOp(string body)
        {
            if (string.IsNullOrEmpty(body)) return null;
            var m = System.Text.RegularExpressions.Regex.Match(body, "\"op\"\\s*:\\s*\"([^\"]*)\"");
            return m.Success ? m.Groups[1].Value : null;
        }

        // ---------------- /v1/events (SSE) ----------------
        static void HandleSse(TcpClient client, string[] headerLines, string query)
        {
            long since = 0;
            // Last-Event-ID 头优先，其次 ?since=
            for (int i = 1; i < headerLines.Length; i++)
            {
                int c = headerLines[i].IndexOf(':');
                if (c <= 0) continue;
                if (headerLines[i].Substring(0, c).Trim().Equals("Last-Event-ID", System.StringComparison.OrdinalIgnoreCase))
                { long.TryParse(headerLines[i].Substring(c + 1).Trim(), out since); }
            }
            if (since == 0) since = ExtractLong(query, "since");

            var sub = EventHub.NewSub();
            if (!EventHub.TryAdd(sub)) { WriteJson(client, 503, Error("NOT_READY", "too many SSE subscribers (max 8)")); return; }
            try
            {
                var st = client.GetStream();
                WriteRaw(st,
                    "HTTP/1.1 200 OK\r\n" +
                    "Content-Type: text/event-stream; charset=utf-8\r\n" +
                    "Cache-Control: no-cache\r\n" +
                    "Connection: keep-alive\r\n" +
                    "X-Accel-Buffering: no\r\n\r\n");
                // 重连时先补发漏掉的事件，再给当前完整快照
                WriteRaw(st, EventHub.InitialFrames(since));
                st.Flush();

                while (true)
                {
                    if (sub.Signal.WaitOne(15000))
                    {
                        string frame;
                        while (sub.Q.TryDequeue(out frame)) WriteRaw(st, frame);
                    }
                    else WriteRaw(st, EventHub.Heartbeat());
                    st.Flush();
                    if (!IsConnected(client)) break;
                }
            }
            catch (System.Exception) { }
            finally { EventHub.Remove(sub); }
        }

        static bool IsConnected(TcpClient c)
        {
            try
            {
                var s = c.Client;
                return !(s.Poll(0, SelectMode.SelectRead) && s.Available == 0);
            }
            catch { return false; }
        }

        static long ExtractLong(string query, string key)
        {
            if (string.IsNullOrEmpty(query)) return 0;
            foreach (var kv in query.Split('&'))
            {
                int e = kv.IndexOf('=');
                if (e <= 0) continue;
                if (kv.Substring(0, e) != key) continue;
                long v;
                if (long.TryParse(kv.Substring(e + 1), out v)) return v;
            }
            return 0;
        }

        // ---------------- /v1/meta ----------------
        static string Meta()
        {
            // /v1/meta **永远能答**：
            //  * 进程级信息来自 OnLoad 缓存（不依赖泵）；
            //  * DebugHandler.enabled 是托管静态字段，任意线程读都安全；
            //  * colonyLoaded 需要游戏对象 ⇒ 只有泵在跑才可能知道；不知道就 false（不用 null）。
            bool pumpReady = MainThread.Ready;
            bool debugOn = false;
            try { debugOn = DebugHandler.enabled; } catch { debugOn = false; }

            bool colonyLoaded = false;
            if (pumpReady)
            {
                bool loaded = false;
                bool got = MainThread.Invoke(() =>
                {
                    try { loaded = GameClock.Instance != null; } catch { loaded = false; }
                    CacheProcessInfoOnce();
                }, 2000);
                if (got) colonyLoaded = loaded;
            }

            int? buildNum = ParseBuildNumber(cachedBuildText);
            var sb = new StringBuilder();
            sb.Append('{');
            sb.Append("\"apiVersion\":\"").Append(ApiVersion).Append("\",");
            sb.Append("\"ok\":true,\"seq\":").Append(NextSeq()).Append(',');
            sb.Append("\"serverTime\":\"").Append(Escape(System.DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ssK"))).Append("\",");
            // ★ 0.96.1：回显请求 id（对账用；`?reqId=` 或 `X-Oni-Req-Id`，没给就是服务端生成的）
            if (ReqJournal.CurrentId != null) sb.Append("\"reqId\":").Append(Snap.Str(ReqJournal.CurrentId)).Append(',');
            sb.Append("\"data\":{");
            sb.Append("\"mod\":{\"name\":\"ONI AI Bridge\",\"version\":\"").Append(ModVersion)
              .Append("\",\"staticID\":\"").Append(BridgeSettings.StaticId)
              .Append("\",\"stage\":\"").Append(Stage).Append('"')
              .Append(",\"compiledAgainstGameBuildText\":\"U59-744825-S\"")
              .Append(",\"compiledAgainstGameBuild\":744825}");
            sb.Append(',');
            sb.Append("\"game\":{");
            sb.Append("\"buildText\":").Append(cachedBuildText == null ? "null" : Snap.Str(cachedBuildText));
            sb.Append(",\"buildVersion\":").Append(buildNum.HasValue ? buildNum.Value.ToString(CultureInfo.InvariantCulture) : "null");
            sb.Append(",\"buildTextSource\":\"BuildWatermark.GetBuildText()\"");
            sb.Append(",\"unityVersion\":").Append(cachedUnityVersion == null ? "null" : Snap.Str(cachedUnityVersion));
            sb.Append(",\"unityBundleVersion\":").Append(cachedBundleVersion == null ? "null" : Snap.Str(cachedBundleVersion));
            sb.Append(",\"unityBundleVersionNote\":\"Unity bundleVersion is hardcoded to 1.0 in ONI and is NOT the game build; use buildVersion/buildText\"");
            sb.Append("},");
            sb.Append("\"server\":{\"port\":").Append(boundPort)
              .Append(",\"loopbackOnly\":").Append(loopbackOnly)
              .Append(",\"rateLimitPerMin\":0,\"sseSubscribers\":").Append(EventHub.SubscriberCount)
              .Append(",\"lastEventSeq\":").Append(EventHub.LastSeq).Append("},");
            sb.Append("\"pumpReady\":").Append(pumpReady ? "true" : "false").Append(',');
            sb.Append("\"colonyLoaded\":").Append(colonyLoaded ? "true" : "false").Append(',');
            // M4：闸门拆开报，外部可自己算；cheatsAllowed/debugModeOn 保留但已弃用
            sb.Append("\"gate\":{");
            sb.Append("\"allowCheats\":").Append(Gate.AllowCheats ? "true" : "false");
            sb.Append(",\"sandboxEnabled\":").Append(Gate.SandboxEnabled ? "true" : "false");
            sb.Append(",\"sandboxModeActive\":").Append(Gate.SandboxModeActive ? "true" : "false");
            sb.Append(",\"debugModeEnabled\":").Append(Gate.DebugModeEnabled ? "true" : "false");
            sb.Append(",\"cheatsUnlocked\":").Append(Gate.CheatsUnlocked ? "true" : "false");
            sb.Append(",\"unavailableCode\":").Append(Snap.Str(Gate.UnavailableCode()));
            sb.Append(",\"unavailableReason\":").Append(Snap.Str(Gate.UnavailableReason()));
            sb.Append("},");
            sb.Append("\"cheatsAllowed\":").Append(Gate.AllowCheats ? "true" : "false").Append(',');
            sb.Append("\"debugModeOn\":").Append(Gate.DebugModeEnabled ? "true" : "false").Append(',');
            {
                bool ibm = false;
                try { ibm = DebugHandler.InstantBuildMode; } catch { }
                sb.Append("\"instantBuildMode\":").Append(ibm ? "true" : "false").Append(',');
            }
            sb.Append("\"settingsUi\":{\"available\":").Append(SettingsUi.IsAvailable ? "true" : "false");
            sb.Append(",\"kind\":").Append(Snap.Str(SettingsUi.Kind.ToString()));
            sb.Append(",\"detail\":").Append(Snap.Str(SettingsUi.Detail));
            sb.Append(",\"hotkey\":").Append(Snap.Str(Hotkey.Text));   // C2：Ctrl+Shift+O 随时唤出
            sb.Append("},");
            sb.Append("\"capabilities\":{\"state\":true,\"events\":true,\"commands\":true,\"settingsUi\":").Append(SettingsUi.IsAvailable ? "true" : "false");
            // M6.89：`/v1/map` 已实现，这里必须**如实上报 allowMap**（原来是硬编码 false = 谎言）。
            // 注意 capabilities 是"端点存在 + 当前闸门允许"，`map:false` 表示 allowMap 关着，端点本身存在。
            sb.Append(",\"map\":").Append((settings != null && settings.AllowMap) ? "true" : "false");
            sb.Append(",\"catalog\":true,\"elements\":true}");
            sb.Append(",\"aiEnableFog\":").Append(Vision.FogOn ? "true" : "false");
            sb.Append(",\"allowMap\":").Append((settings != null && settings.AllowMap) ? "true" : "false");
            sb.Append(",\"mapLimits\":{\"maxSide\":").Append(MapQuery.MaxSide)
              .Append(",\"maxCells\":").Append(MapQuery.MaxCells)
              .Append(",\"channels\":[\"element\",\"temperature\",\"mass\"]}");
            sb.Append(",\"vision\":").Append(Snap.Str(Vision.ModeName));
            // ★ 0.95.1：阻塞型对话框检测 —— **白名单**，只认"真的挡住输入、必须点掉才能继续"的那类。
            //   判据是**具体类名**（`KModalScreen` 的子类太多，`ResearchScreen` / `SkillsScreen` / `StarmapScreen`
            //   这些常驻界面同样是 KModalScreen 子类，但不算阻塞 ⇒ 0.95.0 报出来是误报）。
            //   宁可漏报不要误报：误报会让 AI 一直以为有东西挡着。
            //   下列名字来自元数据（直接继承 KModalScreen 的类型里，带 confirm/cancel 或"强制确认"语义的那些）。
            {
                string blocking = null;
                bool bRan = MainThread.Ready && MainThread.InvokeAdaptive(() =>
                {
                    try
                    {
                        var allScreens = UnityEngine.Object.FindObjectsOfType<KScreen>();
                        var names = new System.Collections.Generic.List<string>();
                        if (allScreens != null)
                        {
                            for (int i = 0; i < allScreens.Length; i++)
                            {
                                var s = allScreens[i];
                                if (s == null) continue;
                                bool act = false;
                                try { act = s.IsActive(); } catch { }
                                if (!act) continue;
                                string tn = null;
                                try { tn = s.GetType().Name; } catch { }
                                if (tn == null) continue;
                                if (System.Array.IndexOf(BlockingScreenNames, tn) < 0) continue;
                                names.Add(Snap.Str(tn));
                            }
                        }
                        blocking = "{\"present\":" + (names.Count > 0 ? "true" : "false")
                                 + ",\"count\":" + names.Count
                                 + ",\"screens\":[" + string.Join(",", names.ToArray()) + "]"
                                 + ",\"whitelist\":[" + JoinQuoted(BlockingScreenNames) + "]"
                                 + ",\"note\":" + Snap.Str("present:true = 有确认/终止类模态框挡着，AI 此时不应假设可以操作世界。"
                                     + "**只报告不自动点掉**（自动确认可能误触危险操作）；要清掉请自己点，"
                                     + "例如 POST /v1/ui/click {\"text\":\"好的\",\"screen\":\"ConfirmDialogScreen\"}。"
                                     + "判据是白名单里的具体类名；ResearchScreen / SkillsScreen / StarmapScreen 这类常驻面板**不算**阻塞。")
                                 + "}";
                    }
                    catch { }
                }, 1500, 8000, out _, out _);
                sb.Append(",\"blockingDialog\":").Append(string.IsNullOrEmpty(blocking) ? "null" : blocking);
            }
            sb.Append("},");
            sb.Append("\"error\":null}");
            string __json = sb.ToString();
            JsonCheck.Validate(__json, "meta");   // 手写 JSON ⇒ 出门前必须能解析
            return __json;
        }

        /// <summary>
        /// 阻塞型对话框白名单（0.95.1）。只收"真的挡住输入、必须点掉才能继续"的具体类，
        /// 全部来自元数据（直接继承 `KModalScreen` 且带 confirm/cancel/强制确认语义）。
        /// **刻意不含** ResearchScreen / SkillsScreen / StarmapScreen / LoadScreen 这类常驻面板 ——
        /// 它们也是 KModalScreen 子类，但开着不代表"被挡住"。
        /// </summary>
        static readonly string[] BlockingScreenNames = {
            "ConfirmDialogScreen",        // 通用确认框（confirmButton / cancelButton）
            "BarterConfirmationScreen",   // 交易确认
            "GameOverScreen",             // 全灭
            "VictoryScreen",              // 胜利结算
            "DLCBetaMessageScreen",       // DLC 测试期提示
            "OldVersionMessageScreen",    // 旧版本提示
            "DemoOverScreen",             // 试玩结束
            "PatchNotesScreen"            // 更新后补丁说明（要按 OK 才继续）
        };

        static string JoinQuoted(string[] arr)
        {
            if (arr == null || arr.Length == 0) return "";
            var parts = new string[arr.Length];
            for (int i = 0; i < arr.Length; i++) parts[i] = Snap.Str(arr[i]);
            return string.Join(",", parts);
        }

        /// <summary>从 "U59-744825-S" 里取 build 数字（最长的一段连续数字）。</summary>
        static int? ParseBuildNumber(string buildText)
        {
            if (string.IsNullOrEmpty(buildText)) return null;
            int bestStart = -1, bestLen = 0, i = 0;
            while (i < buildText.Length)
            {
                if (buildText[i] >= '0' && buildText[i] <= '9')
                {
                    int s = i;
                    while (i < buildText.Length && buildText[i] >= '0' && buildText[i] <= '9') i++;
                    if (i - s > bestLen) { bestStart = s; bestLen = i - s; }
                }
                else i++;
            }
            if (bestStart < 0) return null;
            int val;
            if (int.TryParse(buildText.Substring(bestStart, bestLen), NumberStyles.Integer, CultureInfo.InvariantCulture, out val)) return val;
            return null;
        }

        static string Health()
        {
            string json = "{\"apiVersion\":\"" + ApiVersion + "\",\"ok\":true,\"seq\":" + NextSeq()
                 + ReqJournal.ReqIdJson()                 // ★ 0.96.1
                 + ",\"data\":{\"alive\":true,\"pumpReady\":" + (MainThread.Ready ? "true" : "false")
                 + ",\"stage\":\"" + Stage + "\"},\"error\":null}";
            JsonCheck.Validate(json, "health");   // 手写 JSON ⇒ 出门前必须能解析
            return json;
        }

        static string Ok(string data)
        {
            return "{\"apiVersion\":\"" + ApiVersion + "\",\"ok\":true,\"seq\":" + NextSeq()
                 + ",\"serverTime\":\"" + Escape(System.DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ssK"))
                 + "\"" + ReqJournal.ReqIdJson()      // ★ 0.96.1：回显请求 id（对账用）
                 + ",\"data\":" + data + ",\"error\":null}";
        }

        /// <summary>取某个请求头的值（大小写不敏感）；没有 ⇒ <c>null</c>。0.96.1 起 `X-Oni-Req-Id` 用它。</summary>
        static string HeaderValue(string[] headerLines, string name)
        {
            if (headerLines == null) return null;
            for (int i = 1; i < headerLines.Length; i++)
            {
                int c = headerLines[i].IndexOf(':');
                if (c <= 0) continue;
                if (!headerLines[i].Substring(0, c).Trim().Equals(name, System.StringComparison.OrdinalIgnoreCase)) continue;
                return headerLines[i].Substring(c + 1).Trim();
            }
            return null;
        }

        /// <summary>取 query 里某个参数的原值（不做 URL 解码：reqId 的字符集被 <see cref="ReqJournal.Sanitize"/> 限死，不需要解码）。</summary>
        static string QueryValue(string query, string key)
        {
            if (string.IsNullOrEmpty(query) || string.IsNullOrEmpty(key)) return null;
            foreach (string kv in query.Split('&'))
            {
                int e = kv.IndexOf('=');
                if (e <= 0) continue;
                if (kv.Substring(0, e) != key) continue;
                return kv.Substring(e + 1);
            }
            return null;
        }

        /// <summary>0.96.1：`GET /v1/journal` —— 请求对账（只读）。<c>?reqId=</c> 查单条（带响应体），<c>?since=&amp;limit=</c> 列账目。</summary>
        static void HandleJournal(TcpClient client, string query)
        {
            string data = null;
            try
            {
                string rid = ReqJournal.Sanitize(QueryValue(query, "reqId"));
                if (!string.IsNullOrEmpty(rid)) data = ReqJournal.LookupJson(rid);
                else
                {
                    long since = 0; int limit = 20;
                    string sSince = QueryValue(query, "since"), sLimit = QueryValue(query, "limit");
                    if (sSince != null) long.TryParse(sSince, out since);
                    if (sLimit != null) int.TryParse(sLimit, out limit);
                    if (since < 0) since = 0;
                    if (limit <= 0) limit = 20;
                    data = ReqJournal.ListJson(since, limit);
                }
                JsonCheck.Validate(data, "journal");   // 手写 JSON ⇒ 出门前必须能解析
            }
            catch (System.Exception e)
            {
                WriteJson(client, 500, Error("JOURNAL_FAILED", e.GetType().Name + ": " + e.Message));
                return;
            }
            WriteJson(client, 200, Ok(data));
        }

        static bool Authorized(string[] headerLines, string query)
        {
            if (settings == null || string.IsNullOrEmpty(settings.Token)) return false;
            for (int i = 1; i < headerLines.Length; i++)
            {
                int c = headerLines[i].IndexOf(':');
                if (c <= 0) continue;
                if (!headerLines[i].Substring(0, c).Trim().Equals("X-Oni-Token", System.StringComparison.OrdinalIgnoreCase)) continue;
                if (headerLines[i].Substring(c + 1).Trim() == settings.Token) return true;
            }
            if (!string.IsNullOrEmpty(query))
                foreach (string kv in query.Split('&'))
                {
                    int e = kv.IndexOf('=');
                    if (e <= 0) continue;
                    if (kv.Substring(0, e) == "token" && kv.Substring(e + 1) == settings.Token) return true;
                }
            return false;
        }

        /// <summary>
        /// `503 RETRY_LATER` 的统一构造：**message 给人看，details 给程序看**。
        /// 调用方不该靠正则解析中文 message 判断"是不是刚忙过" —— 那是把展示文本当接口用。
        /// </summary>
        internal static string RetryLater(string where, int waitedMs, bool wasBusy)
        {
            int age = MainThread.LastPumpAgeMs;
            int q = MainThread.QueueDepth;
            string det = "{\"where\":" + Snap.Str(where)
                       + ",\"pumpAgeMs\":" + age + ",\"queueDepth\":" + q
                       + ",\"waitedMs\":" + waitedMs
                       + ",\"wasBusy\":" + (wasBusy ? "true" : "false") + "}";
            return ErrorDetails("RETRY_LATER",
                "main thread busy（距上次心跳 " + age + " ms，队列 " + q + " 项）"
                + "，动作尚未执行；请 1 秒后重试（本次已等待 " + waitedMs + " ms"
                + (wasBusy ? "，存档加载中已用长超时" : "") + "）", det);
        }

        /// <summary>
        /// 带 details 的错误（例如 409 的 candidates）。
        /// `details` 字段**永远存在**（兼容旧客户端），但**没有内容时是 `null` 而不是 `{}`**
        /// —— `{}` 会让人以为"查了但没有"，`null` 才是"没有这个信息"（同 null vs unavailable 那条规矩）。
        /// </summary>
        internal static string ErrorDetails(string code, string message, string detailsJson)
        {
            string d = string.IsNullOrEmpty(detailsJson) ? "null" : detailsJson;
            return "{\"apiVersion\":\"" + ApiVersion + "\",\"ok\":false,\"seq\":" + NextSeq()
                 + ReqJournal.ReqIdJson()             // ★ 0.96.1
                 + ",\"data\":null,\"error\":{\"code\":\"" + Escape(code) + "\",\"message\":\"" + Escape(message)
                 + "\",\"details\":" + d + "}}";
        }

        internal static string Error(string code, string message)
        {
            return "{\"apiVersion\":\"" + ApiVersion + "\",\"ok\":false,\"seq\":" + NextSeq()
                 + ReqJournal.ReqIdJson()             // ★ 0.96.1
                 + ",\"data\":null,\"error\":{\"code\":\"" + Escape(code) + "\",\"message\":\"" + Escape(message) + "\",\"details\":null}}";
        }

        static long NextSeq() { return Interlocked.Increment(ref seq); }

        internal static void WriteJson(TcpClient client, int status, string body)
        {
            WriteJson(client, status, body, null);
        }

        internal static void WriteJson(TcpClient client, int status, string body, string extraHeaders)
        {
            WriteJson(client, status, body, extraHeaders, true);
        }

        /// <summary>
        /// 发响应。<paramref name="record"/>=true 时**先记账、再写 socket**（0.96.1）：
        /// 写 socket 失败正是"动作已执行、回执没送到"那种情况，那条账就是重试时该回放的东西；
        /// 反过来先写后记就会漏掉这一格。回放路径自己传 false（否则会把 replay 记成一次新执行）。
        /// </summary>
        internal static void WriteJson(TcpClient client, int status, string body, string extraHeaders, bool record)
        {
            if (record) { try { ReqJournal.Record(status, body); } catch { } }
            byte[] payload = Encoding.UTF8.GetBytes(body);
            string reason = status == 200 ? "OK" : status == 400 ? "Bad Request" : status == 401 ? "Unauthorized"
                          : status == 404 ? "Not Found" : status == 405 ? "Method Not Allowed"
                          : status == 503 ? "Service Unavailable" : status == 504 ? "Gateway Timeout" : "Error";
            string rid = ReqJournal.CurrentId;
            string head =
                "HTTP/1.1 " + status + " " + reason + "\r\n" +
                "Content-Type: application/json; charset=utf-8\r\n" +
                "Content-Length: " + payload.Length + "\r\n" +
                "Cache-Control: no-store\r\n" +
                (string.IsNullOrEmpty(rid) ? "" : "X-Oni-Req-Id: " + rid + "\r\n") +
                (string.IsNullOrEmpty(extraHeaders) ? "" : extraHeaders) +
                "Connection: close\r\n\r\n";
            var st = client.GetStream();
            byte[] hb = Encoding.ASCII.GetBytes(head);
            st.Write(hb, 0, hb.Length);
            st.Write(payload, 0, payload.Length);
            st.Flush();
        }

        internal static void WriteHtml(TcpClient client, int status, string html)
        {
            byte[] payload = Encoding.UTF8.GetBytes(html ?? "");
            string reason = status == 200 ? "OK" : status == 404 ? "Not Found" : status == 405 ? "Method Not Allowed" : "Error";
            string head =
                "HTTP/1.1 " + status + " " + reason + "\r\n" +
                "Content-Type: text/html; charset=utf-8\r\n" +
                "Content-Length: " + payload.Length + "\r\n" +
                "Cache-Control: no-store\r\n" +
                "Connection: close\r\n\r\n";
            var st = client.GetStream();
            byte[] hb = Encoding.ASCII.GetBytes(head);
            st.Write(hb, 0, hb.Length);
            st.Write(payload, 0, payload.Length);
            st.Flush();
        }

        static void WriteRaw(NetworkStream st, string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            byte[] b = Encoding.UTF8.GetBytes(text);
            st.Write(b, 0, b.Length);
        }

        internal static string Escape(string s)
        {
            if (s == null) return "";
            var sb = new StringBuilder(s.Length + 8);
            foreach (char ch in s)
            {
                switch (ch)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (ch < 0x20) sb.Append("\\u").Append(((int)ch).ToString("x4", CultureInfo.InvariantCulture));
                        else sb.Append(ch);
                        break;
                }
            }
            return sb.ToString();      // 注意：Escape 只是转义，产物不是 JSON，别在这里校验
        }
    }
}
