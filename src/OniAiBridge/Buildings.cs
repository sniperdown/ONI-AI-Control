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
    /// 字段刻意保持精简（字段越多，AI 读起来越贵）：prefabId / name / x / y / cell / worldId /
    /// operational / enabled。温度、材料等留到后续版本。
    ///
    /// M6.8a（0.38.1）：**只加计时日志，不改行为**。三段时间分开打：
    /// * `loopMs` —— 遍历 + `GetCell` + `Vision.Allow`
    /// * `nameMs` —— `Catalog.PlainText(def.Name)`（疑为"首轮贵"的元凶）
    /// * `jsonMs` —— 组装 JSON 字符串
    /// * 另加 `totalItems`（表里总条数，**截断时也能知道全量有多大**）
    /// </summary>
    internal static class BuildingsQuery
    {
        public const int DefaultLimit = 500;
        public const int MaxLimit = 2000;

        public static string Query(int offset, int limit, string prefabId,
                                   bool hasBox = false, int bx0 = 0, int by0 = 0, int bx1 = 0, int by1 = 0)
        {
            if (offset < 0) offset = 0;
            if (limit <= 0) limit = DefaultLimit;
            if (limit > MaxLimit) limit = MaxLimit;

            string wantPrefab = string.IsNullOrEmpty(prefabId) ? null : prefabId.Trim();
            // ★ bbox 过滤（M6.35）：顺序不敏感；x0==x1 && y0==y1 的单格查询也必须生效
            if (hasBox)
            {
                if (bx1 < bx0) { int t0 = bx0; bx0 = bx1; bx1 = t0; }
                if (by1 < by0) { int t1 = by0; by0 = by1; by1 = t1; }
            }
            int filteredByBox = 0;
            int filteredByPrefab = 0;   // prefabId 不匹配（P3）
            int notEmittedDueToLimit = 0;   // 扫到了但超出 limit 未输出
            bool fog = Vision.FogOn;
            int scanned = 0;         // 参与扫描的建筑数
            int filteredByFog = 0;   // 被迷雾挡掉（Grid.Visible==0）
            int filteredInvalid = 0; // 算不出有效格子（读不到 ⇒ 与迷雾无关）
            int skipped = 0;         // 因 offset 跳过的数
            int emitted = 0;         // 真正返回的条数（= count）
            bool truncated = false;

            // ---- M6.8a 计时（只观测，不影响行为）----
            var swTotal = System.Diagnostics.Stopwatch.StartNew();
            var swPhase = System.Diagnostics.Stopwatch.StartNew();
            long loopMs = 0, nameMs = 0, jsonMs = 0, visMs = 0;
            int total = 0;

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
                total = items.Count;
                Log.Info("[buildings] 开始扫描 totalItems=" + total + "（完工=" + completesCount + " 施工中=" + ucCount + "）"
                         + " offset=" + offset + " limit=" + limit
                         + " prefabId=[" + (wantPrefab ?? "<null>") + "]");
                for (int i = 0; i < total; i++)
                {
                    // limit 判定放在"扫描之前"，这样硬等式才精确：
                    // scanned = count + filteredByFog + filteredInvalid + skipped
            // 不再早退：`scanned` 必须是全量扫描数（原实现只报"本页处理了几条"，会误导）
            // 超 limit 的条目继续参与计数，但不进 JSON（计入 notEmittedDueToLimit）

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

                    if (skipped < offset) { skipped++; continue; }

                    BuildingDef def = null;
                    try { def = b.Def; } catch { }
                    string pid = null, name = null;
                    try { pid = def != null ? def.PrefabID : null; } catch { }
                    // ---- name 阶段计时 ----
                    swPhase.Restart();
                    try { name = def != null ? Catalog.PlainText(def.Name) : null; } catch { }
                    nameMs += swPhase.ElapsedMilliseconds;

                    if (wantPrefab != null && !string.Equals(pid, wantPrefab, System.StringComparison.OrdinalIgnoreCase))
                    { filteredByPrefab++; swPhase.Restart(); continue; }

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
            if (emitted >= limit) { notEmittedDueToLimit++; truncated = true; continue; }
                    if (emitted > 0) sb.Append(',');
                    emitted++;
                    sb.Append("{\"prefabId\":").Append(Snap.Str(pid));
                    sb.Append(",\"name\":").Append(Snap.Str(name));
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
            }

            sb.Append("],\"count\":").Append(emitted);
            sb.Append(",\"offset\":").Append(offset).Append(",\"limit\":").Append(limit);
            sb.Append(",\"scanned\":").Append(scanned);
            sb.Append(",\"totalItems\":").Append(total);   // M6.8a：**恒给**（截断时也知道全量多大）
            sb.Append(",\"truncated\":").Append(truncated ? "true" : "false");
            sb.Append(",\"elapsedMs\":").Append(swTotal.ElapsedMilliseconds);
            sb.Append(",\"timing\":{\"loopMs\":").Append(loopMs)
              .Append(",\"visMs\":").Append(visMs)
              .Append(",\"nameMs\":").Append(nameMs)
              .Append(",\"jsonMs\":").Append(jsonMs).Append('}');
            sb.Append(",\"fogged\":").Append(fog ? "true" : "false");
            sb.Append(",\"filteredByPrefab\":").Append(filteredByPrefab);
            sb.Append(",\"notEmittedDueToLimit\":").Append(notEmittedDueToLimit);
            sb.Append(",\"filteredByFog\":").Append(filteredByFog);
            sb.Append(",\"filteredInvalid\":").Append(filteredInvalid);
            sb.Append(",\"visibility\":{\"aiEnableFog\":").Append(fog ? "true" : "false");
            sb.Append(",\"filteredByPrefab\":").Append(filteredByPrefab);
            sb.Append(",\"filteredByFog\":").Append(filteredByFog);
            sb.Append(",\"filteredInvalid\":").Append(filteredInvalid);
            // 向后兼容：filteredCount = 两者之和（文档里写明它是"和"，不是纯粹的"被迷雾滤掉"）
            sb.Append(",\"filteredCount\":").Append(filteredByFog + filteredInvalid + filteredByPrefab);
            sb.Append(",\"rule\":").Append(Snap.Str(fog ? "Grid.Visible[cell] > 0" : "none (omniscient)"))
              .Append('}');
            sb.Append('}');

            Log.Info("[buildings] count=" + emitted + " scanned=" + scanned
                     + " totalItems=" + total
                     + " filteredByFog=" + filteredByFog + " filteredInvalid=" + filteredInvalid
                     + " skipped=" + skipped + " offset=" + offset + " limit=" + limit
                     + " truncated=" + truncated + " fogged=" + fog
                     + " | 计时 totalMs=" + swTotal.ElapsedMilliseconds
                     + " loopMs=" + loopMs + " visMs=" + visMs + " nameMs=" + nameMs + " jsonMs=" + jsonMs
                     + " prefabFilter=" + (wantPrefab ?? "<none>")
                     + " | 硬等式 " + (scanned == emitted + filteredByFog + filteredInvalid + filteredByPrefab + skipped + notEmittedDueToLimit ? "OK" : "FAIL"));
            return sb.ToString();
        }
    }
}
