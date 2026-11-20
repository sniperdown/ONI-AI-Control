using System.Text;

// ⚠️ 不要 using System 之后直接用 Action/DateTime：Assembly-CSharp 里的全局 Action 枚举会遮蔽 System.Action。
namespace OniAiBridge
{
    /// <summary>
    /// M5.4：`GET /v1/buildings` —— 场上建筑清单（**第一个真正需要迷雾过滤的场景查询**）。
    ///
    /// 两条硬要求：
    /// * **在采集循环里逐项 `Vision.Allow(cell)`**，不是"先全查再 filter"（漏一个出口就泄露全图）；
    /// * **带上 `fogged` 与 `visibility.filteredCount`**，让人一眼看出"这 42 个是全部还是过滤后的"。
    ///
    /// M6.8a（0.38.1）：只加计时日志，不改行为。三段时间分开打：
    /// `loopMs`（遍历 + GetCell + Vision.Allow）/ `nameMs`（Catalog.PlainText）/ `jsonMs`（拼字符串）。
    ///
    /// **0.96.0：分页改成"对命中集做窗口"，并给出可翻页的总数。**
    /// 起因：实测大地图 `totalItems=72851`，而 `limit` 上限 2000 ⇒ 一页顶多 2000 条；旧实现里
    /// `totalItems` 只是"表里有多少个对象"（含被迷雾/坐标框/prefab 滤掉的），调用方**没有可翻页的总数**，
    /// 于是"取不全"既翻不动、也无法自证翻完了。这一版：
    /// * `total` = 通过 fog / bbox / prefabId 过滤后的条数 ⇒ 可翻页总数（`ceil(total/limit)` 页）；
    /// * `offset` 改为**只在命中集上计数**（旧实现先跳 offset 再判 prefab ⇒ 带 `prefabId` 翻页会既漏又重）；
    /// * `hasMore` / `nextOffset` 直接给下一页入口，`truncated` 保留（= `hasMore`）；
    /// * 单页只序列化窗口内的那 `limit` 条 ⇒ **7 万条不会一次性进内存**（只算总数，不攒 JSON）；
    /// * 计数硬等式（与 `tools/smoke.ps1` 同口径）：
    ///   `scanned = count + skipped + notEmittedDueToLimit + filteredByFog + filteredInvalid + filteredByBox + filteredByPrefab`，
    ///   且 `total = count + skipped + notEmittedDueToLimit`。
    ///
    /// **字段精简（同一版）**：每条的 `name`（本地化中文名）默认**不再返回**，要就显式 `?names=1`。
    /// 理由是 `Catalog.PlainText(def.Name)` 是整条链上最贵的一次调用（M6.8a 的 `nameMs` 就是为它加的计时），
    /// 而翻一页要全量扫一遍才能算 `total` ⇒ 旧实现等于每翻一页做 7 万次 LocText。
    /// 现在 `name` 只在**窗口内**且**显式要求**时才算；`prefabId → name` 的对照可以走 `/v1/catalog`（一次拿全表）。
    /// </summary>
    internal static class BuildingsQuery
    {
        public const int DefaultLimit = 500;
        public const int MaxLimit = 2000;

        /// <param name="offset">在**命中集**（通过 fog / bbox / prefabId 过滤后）里的起点；越界 ⇒ <c>count=0</c>、不报错。</param>
        /// <param name="limit">本页条数；≤0 ⇒ 默认 500，&gt;2000 ⇒ 2000。</param>
        /// <param name="prefabId">只留这个 prefab（大小写不敏感）；null/空 = 不过滤。</param>
        /// <param name="withNames">是否给窗口内每条计算本地化 <c>name</c>（默认 false，见类注释）。</param>
        public static string Query(int offset, int limit, string prefabId,
                                   bool hasBox = false, int bx0 = 0, int by0 = 0, int bx1 = 0, int by1 = 0,
                                   bool withNames = false)
        {
            if (offset < 0) offset = 0;
            if (limit <= 0) limit = DefaultLimit;
            if (limit > MaxLimit) limit = MaxLimit;
            // 窗口右端用 long：offset+limit 都是 int，极端值下会溢出成负数（那时 hasMore 会算反）
            long windowEnd = (long)offset + limit;

            string wantPrefab = string.IsNullOrEmpty(prefabId) ? null : prefabId.Trim();
            // ★ bbox 过滤（M6.35）：顺序不敏感；x0==x1 && y0==y1 的单格查询也必须生效
            if (hasBox)
            {
                if (bx1 < bx0) { int t0 = bx0; bx0 = bx1; bx1 = t0; }
                if (by1 < by0) { int t1 = by0; by0 = by1; by1 = t1; }
            }
            int filteredByBox = 0;          // 落在 bbox 之外
            int filteredByPrefab = 0;       // prefabId 不匹配（P3）
            int notEmittedDueToLimit = 0;   // 命中但落在本页窗口之后（= 还有下一页）
            bool fog = Vision.FogOn;
            int scanned = 0;         // 参与扫描的建筑数
            int filteredByFog = 0;   // 被迷雾挡掉（Grid.Visible==0）
            int filteredInvalid = 0; // 算不出有效格子（读不到 ⇒ 与迷雾无关）
            int skipped = 0;         // 命中但落在本页窗口之前（offset 跳过的）
            int emitted = 0;         // 真正返回的条数（= count）
            bool hasMore = false;    // 后面还有没返回的命中条目（= 旧 truncated）

            // ---- M6.8a 计时（只观测，不影响行为）----
            var swTotal = System.Diagnostics.Stopwatch.StartNew();
            var swPhase = System.Diagnostics.Stopwatch.StartNew();
            long loopMs = 0, nameMs = 0, jsonMs = 0, visMs = 0;
            int rawTotal = 0;   // 合并后表里的总条数（旧 `totalItems`；不是可翻页总数）
            int total = 0;      // ★ 命中总数（= 可翻页总数）

            var sb = new StringBuilder(16384);
            sb.Append("{\"buildings\":[");

            try
            {
                // 默认把"施工中"(BuildingUnderConstruction)也列进来。
                // Components.BuildingCompletes 只有【已完工】的建筑 —— 之前蓝图画在哪、有没有真加上，这里一律看不见，
                // 导致"建不出来"的判断其实是瞎的。
                // 注意：游戏里没有 Components.Buildings 这个容器（编译期确认过，CS0117），
                // 所以用"已完工列表 + FindObjectsOfType<BuildingUnderConstruction>()"两路合并。
                var merged = new System.Collections.Generic.List<Building>(1024);
                try
                {
                    var completes = Components.BuildingCompletes != null ? Components.BuildingCompletes.Items : null;
                    if (completes != null) for (int ci = 0; ci < completes.Count; ci++) if (completes[ci] != null) merged.Add(completes[ci]);
                }
                catch (System.Exception e) { Log.Warn("[buildings] 枚举已完工建筑失败: " + e.Message); }
                int completesCount = merged.Count;
                int ucCount = 0;
                try
                {
                    var ucs = UnityEngine.Object.FindObjectsOfType<BuildingUnderConstruction>();
                    if (ucs != null) for (int ui = 0; ui < ucs.Length; ui++) if (ucs[ui] != null) { merged.Add(ucs[ui]); ucCount++; }
                }
                catch (System.Exception e) { Log.Warn("[buildings] 枚举施工点失败: " + e.Message); }
                var items = merged;
                rawTotal = items.Count;
                Log.Info("[buildings] 开始扫描 totalItems=" + rawTotal + "（完工=" + completesCount + " 施工中=" + ucCount + "）"
                         + " offset=" + offset + " limit=" + limit + " names=" + (withNames ? "1" : "0")
                         + " prefabId=[" + (wantPrefab ?? "<null>") + "]");
                for (int i = 0; i < rawTotal; i++)
                {
                    // 条件顺序**有意如此**（便宜的在前，且决定总账）：无效格 → 迷雾 → bbox → prefab → 窗口。
                    // 计数硬等式靠"每条只落进一个桶"来保证，所以任何 continue 之前必须已经记了一笔。
                    Building b = items[i];
                    if (b == null) continue;
                    scanned++;

                    int cell = -1;
                    try { cell = b.GetCell(); } catch { cell = -1; }

                    // ★ 门禁在采集循环里，逐项判断（无效格也算"看不见"）
                    if (cell < 0 || !Grid.IsValidCell(cell)) { filteredInvalid++; continue; }   // 读不到
                    // ---- 视野阶段计时（M6.8b：单独拆出来，验"Grid.Visible 首轮初始化"这个假设）----
                    swPhase.Restart();
                    bool allowCell = Vision.Allow(cell);
                    visMs += swPhase.ElapsedMilliseconds;
                    if (!allowCell) { filteredByFog++; continue; }                               // 被迷雾挡掉

                    if (hasBox)
                    {
                        int cx, cy;
                        try { Grid.CellToXY(cell, out cx, out cy); } catch { filteredInvalid++; continue; }   // CellToXY 返回 void
                        if (cx < bx0 || cx > bx1 || cy < by0 || cy > by1) { filteredByBox++; continue; }
                    }

                    BuildingDef def = null;
                    try { def = b.Def; } catch { }
                    string pid = null;
                    try { pid = def != null ? def.PrefabID : null; } catch { }

                    // ★ 0.96.0：prefab 过滤提到 offset 之前。旧实现用 offset 跳过"还没判 prefab"的条目 ⇒
                    //   带 prefabId 翻页时会漏（跳过的不该占 offset）也会重（每次扫描顺序相同，但边界不一致）。
                    if (wantPrefab != null && !string.Equals(pid, wantPrefab, System.StringComparison.OrdinalIgnoreCase))
                    { filteredByPrefab++; continue; }

                    // ---- 到这里这条算"命中"：先数总数，再按窗口切片（7 万条也只序列化窗口内那些）----
                    total++;
                    if (total <= offset) { skipped++; continue; }        // 窗口之前
                    if (total > windowEnd) { notEmittedDueToLimit++; hasMore = true; continue; }   // 窗口之后

                    // ---- 只有在窗口内、且调用方要名字时，才付 LocText 的钱 ----
                    string name = null;
                    if (withNames)
                    {
                        swPhase.Restart();
                        try { name = def != null ? Catalog.PlainText(def.Name) : null; } catch { }
                        nameMs += swPhase.ElapsedMilliseconds;
                    }

                    int x = -1, y = -1, worldId = -1;
                    try { x = cell % Grid.WidthInCells; y = cell / Grid.WidthInCells; } catch { }
                    try { worldId = Grid.WorldIdx != null ? Grid.WorldIdx[cell] : -1; } catch { }

                    bool operational = false;
                    try
                    {
                        var op = b.GetComponent<Operational>();
                        operational = op != null && op.IsOperational;    // 游戏原生判定
                    }
                    catch { }

                    bool enabled = true;
                    bool hasEnabled = false;
                    try
                    {
                        var beb = b.GetComponent<BuildingEnabledButton>();
                        if (beb != null) { enabled = beb.IsEnabled; hasEnabled = true; }   // 游戏原生判定
                    }
                    catch { }

                    // ---- json 阶段计时 ----
                    swPhase.Restart();
                    if (emitted > 0) sb.Append(',');
                    emitted++;
                    sb.Append("{\"prefabId\":").Append(Snap.Str(pid));
                    if (withNames) sb.Append(",\"name\":").Append(Snap.Str(name));
                    sb.Append(",\"x\":").Append(x).Append(",\"y\":").Append(y);
                    sb.Append(",\"cell\":").Append(cell);
                    sb.Append(",\"worldId\":").Append(worldId);
                    // 施工点（蓝图）现在是默认列出的，必须能区分：true = 还没建完、在等材料/施工
                    bool underConstruction = false;
                    try { underConstruction = b is BuildingUnderConstruction; } catch { }
                    sb.Append(",\"underConstruction\":").Append(underConstruction ? "true" : "false");
                    sb.Append(",\"operational\":").Append(operational ? "true" : "false");
                    // 没有 BuildingEnabledButton 的建筑（如砖块、门的一部分）没有"启用/禁用"概念 ⇒ null，而不是假装 true
                    sb.Append(",\"enabled\":").Append(hasEnabled ? (enabled ? "true" : "false") : "null");
            // ★ 0.98.1：电力可观测性 —— 盲测/自测要判断"电到底通没通"，
            //   `operational` 只代表"建筑没坏且在运转"，**不含供电状态**（缺电时它照样 true）。
            //   所以把电池电量如实报出来：joules 会涨 = 真的在充电 = 电路是通的。
            {
                float? joules = null; float? cap = null;
                try
                {
                    var bat = b.GetComponent<Battery>();
                    if (bat != null)
                    {
                        // joulesAvailable / capacity 是 private 字段 ⇒ 反射读（只读，不改）
                        var fi = typeof(Battery).GetField("joulesAvailable", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public);
                        if (fi != null) { var v = fi.GetValue(bat); if (v is float) joules = (float)v; }
                        try { cap = bat.capacity; } catch { }
                    }
                }
                catch { }
                sb.Append(",\"joules\":").Append(joules.HasValue ? joules.Value.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) : "null");
                sb.Append(",\"joulesCapacity\":").Append(cap.HasValue ? cap.Value.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) : "null");
            }
                    // ★ 0.95.0：是否已被标记拆除（`Deconstructable.IsMarkedForDeconstruction()`，元数据实测存在；
                    //   同名字段 `isMarkedForDeconstruction : bool` 也在，但方法更稳）。没该组件 ⇒ false。
                    bool markedForDeconstruct = false;
                    try
                    {
                        var dc = b.GetComponent<Deconstructable>();
                        if (dc != null) markedForDeconstruct = dc.IsMarkedForDeconstruction();
                    }
                    catch { }
                    sb.Append(",\"markedForDeconstruct\":").Append(markedForDeconstruct ? "true" : "false");
                    sb.Append('}');
                    jsonMs += swPhase.ElapsedMilliseconds;
                }
                loopMs = swTotal.ElapsedMilliseconds - nameMs - jsonMs - visMs;
            }
            catch (System.Exception e)
            {
                Log.Warn("[buildings] 采集异常（已返回已采集部分）: " + e);
                hasMore = false;   // 异常时别诱导调用方继续翻（下一页可能是空的同一段）
            }

            sb.Append("],\"count\":").Append(emitted);
            sb.Append(",\"offset\":").Append(offset).Append(",\"limit\":").Append(limit);
            // ★ 0.96.0：可翻页总数 + 下一页入口。`total` 只数命中（fog/bbox/prefab 之后的），
            //   与 `totalItems`（表里原有对象数）不是一回事，两个都给，别混用。
            sb.Append(",\"total\":").Append(total);
            sb.Append(",\"hasMore\":").Append(hasMore ? "true" : "false");
            sb.Append(",\"nextOffset\":").Append(hasMore ? offset + emitted : -1);   // 没有下一页给 -1（省得调用方判 null）
            sb.Append(",\"scanned\":").Append(scanned);
            sb.Append(",\"skipped\":").Append(skipped);
            sb.Append(",\"totalItems\":").Append(rawTotal);   // M6.8a：表里总条数（**不是**可翻页总数）
            sb.Append(",\"truncated\":").Append(hasMore ? "true" : "false");   // 向后兼容：与 hasMore 同值
            sb.Append(",\"elapsedMs\":").Append(swTotal.ElapsedMilliseconds);
            sb.Append(",\"timing\":{\"loopMs\":").Append(loopMs)
              .Append(",\"visMs\":").Append(visMs)
              .Append(",\"nameMs\":").Append(nameMs)
              .Append(",\"jsonMs\":").Append(jsonMs).Append('}');
            sb.Append(",\"fogged\":").Append(fog ? "true" : "false");
            sb.Append(",\"namesIncluded\":").Append(withNames ? "true" : "false");
            sb.Append(",\"filteredByPrefab\":").Append(filteredByPrefab);
            sb.Append(",\"filteredByBox\":").Append(filteredByBox);
            sb.Append(",\"notEmittedDueToLimit\":").Append(notEmittedDueToLimit);
            sb.Append(",\"filteredByFog\":").Append(filteredByFog);
            sb.Append(",\"filteredInvalid\":").Append(filteredInvalid);
            sb.Append(",\"visibility\":{\"aiEnableFog\":").Append(fog ? "true" : "false");
            sb.Append(",\"filteredByPrefab\":").Append(filteredByPrefab);
            sb.Append(",\"filteredByBox\":").Append(filteredByBox);
            sb.Append(",\"filteredByFog\":").Append(filteredByFog);
            sb.Append(",\"filteredInvalid\":").Append(filteredInvalid);
            // 向后兼容：filteredCount = 三者之和（文档里写明它是"和"，不是纯粹的"被迷雾滤掉"）
            sb.Append(",\"filteredCount\":").Append(filteredByFog + filteredInvalid + filteredByPrefab);
            sb.Append(",\"rule\":").Append(Snap.Str(fog ? "Grid.Visible[cell] > 0" : "none (omniscient)"))
              .Append('}');
            sb.Append('}');

            Log.Info("[buildings] count=" + emitted + " total=" + total + " offset=" + offset + " limit=" + limit
                     + " hasMore=" + hasMore + " scanned=" + scanned + " totalItems=" + rawTotal
                     + " filteredByFog=" + filteredByFog + " filteredInvalid=" + filteredInvalid
                     + " filteredByBox=" + filteredByBox + " filteredByPrefab=" + filteredByPrefab
                     + " skipped=" + skipped + " notEmittedDueToLimit=" + notEmittedDueToLimit
                     + " names=" + (withNames ? "1" : "0") + " fogged=" + fog
                     + " | 计时 totalMs=" + swTotal.ElapsedMilliseconds
                     + " loopMs=" + loopMs + " visMs=" + visMs + " nameMs=" + nameMs + " jsonMs=" + jsonMs
                     + " prefabFilter=" + (wantPrefab ?? "<none>")
                     + " | 硬等式 " + (scanned == emitted + skipped + notEmittedDueToLimit + filteredByFog
                                       + filteredInvalid + filteredByBox + filteredByPrefab ? "OK" : "FAIL"));
            return sb.ToString();
        }
    }
}
