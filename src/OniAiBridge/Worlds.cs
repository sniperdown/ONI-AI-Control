using System.Text;

// ⚠️ 不要 using System 之后直接用 Action/DateTime：Assembly-CSharp 里的全局 Action 枚举会遮蔽 System.Action。
namespace OniAiBridge
{
    /// <summary>
    /// M6.67（**只读**）：`GET /v1/worlds` —— 世界表（每颗小行星一条）+ 区块行列数。
    ///
    /// 所有几何字段**运行时**从 `WorldContainer` 读（**不写死 240×380**）：
    /// * `WorldContainer.WorldOffset : Vector2I`（public 属性，firstpass 的 `Vector2I`，字段 `x`/`y`）
    /// * `WorldContainer.Width` / `.Height`（public 属性；兜底 `WorldSize : Vector2I`）
    /// * `WorldContainer.id`（public 字段）/ `.worldName`（public 字段）/ `.overrideName`（public 字段）
    /// * `ClusterManager.Instance.WorldContainers : List<WorldContainer>`（public 属性）
    /// * `ClusterManager.Instance.activeWorldId`（public 属性）
    /// 这些成员名**都经元数据核对**（见 docs/API.md §3.30 的"成员核对"表），不是猜的。
    /// </summary>
    internal static class Worlds
    {
        public static string List(out int status, out string code, out string message)
        {
            status = 200; code = null; message = null;
            if (!MainThread.Ready) { status = 503; code = "NOT_READY"; message = "main-thread pump is not running yet"; return null; }

            string data = null, fail = null;
            int okT; bool busy;
            bool ran = MainThread.InvokeAdaptive(() =>
            {
                try
                {
                    var cm = ClusterManager.Instance;
                    if (cm == null) { fail = "ClusterManager.Instance 为 null（没进游戏）"; return; }
                    var list = cm.WorldContainers;
                    if (list == null) { fail = "ClusterManager.WorldContainers 为 null"; return; }

                    int activeId = -1, activeId2 = -1;
                    try { activeId = cm.activeWorldId; } catch { }
                    // ★ M6.74 更正：`ClusterManager.Instance.activeWorldId` **是可信的**。
                    //   0.91.0 我曾以为"activeWorldId=7 但玩家在 w0 ⇒ 不可信" —— 前提错了：
                    //   小僵当时在看「筛土星」= MiniRegolithMoonlet = w7，activeWorldId=7 完全正确。
                    //   ⇒ isActive 只用 activeWorldId 判；摄像机仅附加信息。
                    int camWorld = -999;
                    try
                    {
                        var cam = UnityEngine.Camera.main;
                        if (cam != null)
                        {
                            int camCell = Grid.PosToCell(cam.transform.position);
                            if (Grid.IsValidCell(camCell) && Grid.WorldIdx != null) camWorld = Grid.WorldIdx[camCell];
                        }
                    }
                    catch { }
                    try { var aw = cm.activeWorld; if (aw != null) activeId2 = aw.id; } catch { }

                    // 每个世界的复制人数（按 dupes 的 worldId 现算，不依赖 WorldContainer 内部计数）
                    var dupeByWorld = new System.Collections.Generic.Dictionary<int, int>();

                    var sb = new StringBuilder(2048);
                    sb.Append("{\"chunkSize\":").Append(Chunks.ChunkSize);
                    sb.Append(",\"activeWorldId\":").Append(activeId);
                    sb.Append(",\"activeWorldIdFromContainer\":").Append(activeId2);   // ★ M6.73：交叉验证用
                    sb.Append(",\"cameraWorldId\":").Append(camWorld > -900 ? camWorld.ToString() : "null");   // 仅附加信息，不参与 isActive
                    sb.Append(",\"count\":").Append(list.Count);
                    sb.Append(",\"worlds\":[");

                    int emitted = 0;
                    for (int i = 0; i < list.Count; i++)
                    {
                        var wc = list[i];
                        if (wc == null) continue;

                        int wid = -1;
                        try { wid = wc.id; } catch { }
                        string name = null, display = null, typeKey = null;
                        try { name = wc.worldName; } catch { }
                        try { typeKey = wc.worldType; } catch { }
                        // ★ M6.73：displayName 的来历（实测 State.cs 里 colony.world.typeKey 就是 wc.worldType）
                        //   1) 优先 `overrideName`（玩家改过的名字）；
                        //   2) 否则用 `Strings.Get(worldType).String` 解析本地化名（worldType 本身就是
                        //      `STRINGS.WORLDS.<X>.NAME` 这种完整 key）；
                        //   3) 都拿不到 ⇒ null（**绝不拿内部名冒充**）。
                        try
                        {
                            if (!string.IsNullOrEmpty(wc.overrideName)) display = wc.overrideName;
                            else if (!string.IsNullOrEmpty(typeKey))
                            {
                                StringEntry se = Strings.Get(typeKey);
                                if (se != null && !string.IsNullOrEmpty(se.String)) display = se.String;
                                else
                                {
                                    // 有些世界 worldType 不带 .NAME 后缀，补一次
                                    string alt = typeKey.EndsWith(".NAME") ? typeKey : (typeKey + ".NAME");
                                    if (Strings.HasKey(alt)) { var se2 = Strings.Get(alt); if (se2 != null && !string.IsNullOrEmpty(se2.String)) display = se2.String; }
                                }
                            }
                        }
                        catch { }

                        int ox, oy, w, h;
                        string geoErr;
                        bool geoOk = Chunks.TryWorldBBox(wid, out ox, out oy, out w, out h, out geoErr);

                        // ★ M6.74：该世界在 Grid 里的真实 worldId（用世界中心格去问 Grid.WorldIdx）
                        int gridWorldId = -1;
                        if (geoOk)
                        {
                            try
                            {
                                int centerCell = Grid.XYToCell(ox + (w / 2), oy + (h / 2));
                                if (Grid.IsValidCell(centerCell) && Grid.WorldIdx != null) gridWorldId = Grid.WorldIdx[centerCell];
                            }
                            catch { }
                        }

                        int dupes = 0;
                        dupeByWorld.TryGetValue(wid, out dupes);

                        if (emitted > 0) sb.Append(',');
                        emitted++;
                        sb.Append("{\"worldId\":").Append(wid);
                        sb.Append(",\"gridWorldId\":").Append(gridWorldId);   // ★ Grid.WorldIdx 给的编号（isActive 实际比较对象）
                        sb.Append(",\"name\":").Append(Snap.Str(name));
                        sb.Append(",\"displayName\":").Append(Snap.Str(display));
                        sb.Append(",\"typeKey\":").Append(Snap.Str(typeKey));
                        sb.Append(",\"offsetX\":").Append(geoOk ? ox.ToString() : "null");
                        sb.Append(",\"offsetY\":").Append(geoOk ? oy.ToString() : "null");
                        sb.Append(",\"width\":").Append(geoOk ? w.ToString() : "null");
                        sb.Append(",\"height\":").Append(geoOk ? h.ToString() : "null");
                        sb.Append(",\"chunksX\":").Append(geoOk ? Chunks.Cols(w).ToString() : "null");
                        sb.Append(",\"chunksY\":").Append(geoOk ? Chunks.Rows(h).ToString() : "null");
                        // ★ M6.68：`isStub` —— 星际目标/飞船留下的**占位世界**（不是真小行星）
                        //   判据（写死，见 note）：name 为空 且 宽高都 <= 32（实测那 7 个都是 32×32、name=''）
                        bool isStub = string.IsNullOrEmpty(name) && geoOk && w <= 32 && h <= 32;
                        sb.Append(",\"isStub\":").Append(isStub ? "true" : "false");
                        sb.Append(",\"dupeCount\":").Append(dupes);          // 未实现：见 dupeCountUnavailable
                        sb.Append(",\"dupeCountUnavailable\":true");
                        bool isAct = (gridWorldId >= 0) ? (gridWorldId == activeId) : (wid == activeId);   // ★ M6.74：比 Grid 编号，不比容器 id
                        sb.Append(",\"isActive\":").Append(isAct ? "true" : "false");
                        if (!geoOk) sb.Append(",\"geometryError\":").Append(Snap.Str(geoErr));
                        sb.Append('}');
                    }

                    sb.Append(']');
                    sb.Append(",\"source\":").Append(Snap.Str(
                        "ClusterManager.WorldContainers + WorldContainer.{id,worldName,overrideName,WorldOffset,Width,Height}（全部 public，成员已核）"));
                    sb.Append(",\"note\":").Append(Snap.Str(
                        "只读。offsetX/offsetY 是该世界在整张 Grid 里的左下角；坐标一律全局网格坐标（与 dupes[].x/y 同一套）。"
                        + "chunkSize=" + Chunks.ChunkSize + "，列数=ceil(width/chunkSize)、行数=ceil(height/chunkSize)。"
                        + "`isStub:true` = 占位世界（判据：`name` 为空 **且** width<=32 **且** height<=32），星际目标/飞船留下的空壳，不是真小行星。"
                        + "`dupeCount` **未实现**（恒 0，`dupeCountUnavailable:true`）；复制人统计在 /v1/state，下一批次再接入。"
                        + "`displayName` 来历：优先 `overrideName`（玩家改名），否则 `Strings.Get(worldType).String` 解析本地化名；都拿不到 ⇒ `null`（**不拿内部名冒充**）。`typeKey` 原样给出（`STRINGS.WORLDS.…`），调用方可自行查表。"
                        + "`isActive` = **`gridWorldId == activeWorldId`**（`gridWorldId` = 该世界中心格在 `Grid.WorldIdx` 里的真实编号）；`WorldContainer.id` 与 Grid 编号未必同源，直接用 id 比会全 false（0.90.x 的错）。`cameraWorldId` **仅附加信息，不参与判定**。"));
                    sb.Append('}');
                    data = sb.ToString();
                    Log.Info("[worlds] count=" + emitted + " activeWorldId=" + activeId);
                }
                catch (System.Exception e) { fail = e.GetType().Name + ": " + e.Message; }
            }, 5000, 30000, out okT, out busy);

            if (!ran) { status = 503; code = "RETRY_LATER"; message = "main thread busy；请 1 秒后重试"; return null; }
            if (fail != null) { status = 500; code = "WORLDS_UNAVAILABLE"; message = fail; return null; }
            if (data == null) { status = 500; code = "INTERNAL"; message = "worlds 采集返回空"; return null; }
            JsonCheck.Validate(data, "worlds");
            return data;
        }

        /// <summary>供 `/v1/area` 与 `dig` 复用：某世界某块的全局 bbox。</summary>
        public static bool TryChunkBBox(int worldId, int cx, int cy, out int x0, out int y0, out int x1, out int y1, out string err)
        {
            return Chunks.TryChunkBBox(worldId, cx, cy, out x0, out y0, out x1, out y1, out err);
        }
    }
}
