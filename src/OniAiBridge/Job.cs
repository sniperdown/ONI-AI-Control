using System;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using UnityEngine;

// ⚠️ 不要 using System 之后直接用 Action/DateTime：Assembly-CSharp 里的全局 Action 枚举会遮蔽 System.Action。
namespace OniAiBridge
{
    /// <summary>
    /// M6.2：`POST /v1/job` —— 统一的"下达工作指令"入口。
    /// 本类实现 `type=dig`（及 deconstruct/harvest/sweep/build/research）的**单格/区域**路径；
    /// **打标记那批**（`disinfect`/`empty_pipe`/`attack`/`capture`/`mop`）由 `Execute` 开头分派到
    /// `JobMarks.Execute`，在那边实现（含矩形语义）。
    ///
    /// 调研结论（docs/ACTION-SURVEY.md）：ONI 没有统一的"下命令"入口，但有统一**模式** ——
    /// 每种可标记对象上挂着组件、组件上有 public `Mark*` 方法。挖掘是例外：走静态捷径
    /// `DigTool.PlaceDig(cell, radius)`，它会实例化 `DigPlacer` 标记物、把 `Diggable` 挂在它身上、
    /// 并登记到该格的对象层（`ObjectLayer.DigPlacer`）。**`Diggable` 不在自然砖上、也不在建筑上。**
    ///
    /// 两条防御：
    /// * **短路**：该格已有挖标记 ⇒ 不调 `PlaceDig`（防标记物叠加），响应带 `shortCircuited: true`；
    /// * **计数**：调用前后各数一次该格对象数（`objectsBefore/objectsAfter/placedDelta`），
    ///   把"叠加"变成能被核对的数据，而不是靠肉眼看画面。
    ///
    /// 响应语义按 DESIGN §10.6q / §10.6s：
    /// * `accepted`：跨 type 的"受理"（门=已派活；挖=标记已落地）
    /// * `jobState`：**该 type 特有**的枚举（挖：`marked` / `completed`；门：`pending` / `done`）
    /// </summary>
    internal static class JobQuery
    {
        public const int MaxRadius = 2;

        // 已实现的 type（**只用于错误消息**；新增 type 时分派 if 与这里两处都要改）
        // ★ M6.90（0.93.6）：打标记那批（disinfect/empty_pipe/attack/capture/mop）从 M6.76 起就已接通
        //   —— 它们由 `Execute` **开头**的 `JobMarks.IsMarkType(t0)` 分派走，根本走不到下面的 if 链。
        //   此前这张表一直漏着，`capture` 还被错列进"未实现"，并在下面留了一段**永远不可达**的
        //   `if (t0 == "capture")` 提前 return（已删）。
        static readonly string[] ImplementedTypes = new string[] { "dig", "deconstruct", "harvest", "sweep", "build", "research",
            JobMarks.T_DISINFECT, JobMarks.T_EMPTY_PIPE, JobMarks.T_ATTACK, JobMarks.T_CAPTURE, JobMarks.T_MOP };
        // 已知但未实现的 type（同样只用于错误消息）—— 目前为空；保留数组是为了 BuildUnknownTypeMessage 的拼接逻辑
        static readonly string[] NotImplementedTypes = new string[0];

        static string BuildUnknownTypeMessage(string type)
        {
            var sb = new StringBuilder(128);
            sb.Append("type=").Append(type).Append(" 未知（支持：").Append(string.Join(" / ", ImplementedTypes));
            if (NotImplementedTypes.Length > 0)
            {
                sb.Append("；").Append(string.Join(" / ", NotImplementedTypes)).Append(" 暂未实现");
            }
            sb.Append("）");
            sb.Append("；界面工具（右下角那排，含消毒/攻击/捕获等）请看 GET /v1/tools —— 本接口只负责 job 类型");
            return sb.ToString();
        }
        public static string Execute(string body, out int status, out string code, out string message, out string details)
        {
            status = 200; code = null; message = null;
            details = null;

            string type = MatchString(body, "type");
            if (string.IsNullOrEmpty(type))
            {
                status = 400; code = "BAD_REQUEST";
                message = "需要 type（dig / deconstruct / harvest / sweep / build / research，或打标记的 disinfect / empty_pipe / attack / capture / mop）";
                return null;
            }
            string t0 = type.ToLowerInvariant();
            // 区域路径：带 x0/y0/x1/y1 的 dig（单格/radius 路径继续走下面**未改动**的老代码）
            // 区域路径：x0/y0/x1/y1（框）或 cx/cy（块）都走这里；单格/radius 走下面老代码
            // ★ M6.76（0.92.0）：打标记那批（消毒/清空管道/攻击/捕捉/拖地）
            if (JobMarks.IsMarkType(t0)) return JobMarks.Execute(t0, body, out status, out code, out message, out details);
            if (t0 == "build" && HasKey(body, "wirePath")) return BuildWirePath(body, out status, out code, out message, out details);
            if (t0 == "dig" && (HasKey(body, "x0") || HasKey(body, "cx"))) return DigRegionExecute(body, out status, out code, out message);
            // M6.13：对象级 type 的区域模式（**不照抄 dig**：格内有歧义就跳过并计数）
            if ((t0 == "deconstruct" || t0 == "harvest" || t0 == "sweep") && HasKey(body, "x0"))
                return RegionObjectExecute(t0, body, out status, out code, out message, out details);
            if (t0 == "research") return ResearchExecute(body, out status, out code, out message, out details);
            if (t0 == "build") return BuildExecute(body, out status, out code, out message, out details);
            if (t0 == "deconstruct") return DeconstructExecute(body, out status, out code, out message, out details);
            if (t0 == "harvest") return HarvestExecute(body, out status, out code, out message, out details);
            if (t0 == "sweep") return SweepExecute(body, out status, out code, out message, out details);
            if (t0 != "dig")
            {
                status = 400; code = "NOT_IMPLEMENTED";
                message = BuildUnknownTypeMessage(type);
                return null;
            }

            int x, y;
            if (!TryInt(body, "x", out x) || !TryInt(body, "y", out y))
            {
                status = 400; code = "BAD_REQUEST"; message = "需要 x / y（世界内格子坐标）";
                return null;
            }
            int worldId = -1; TryInt(body, "worldId", out worldId);

            int radius = 0;
            if (HasKey(body, "radius") && !TryInt(body, "radius", out radius))
            {
                status = 400; code = "BAD_REQUEST"; message = "radius 必须是整数";
                return null;
            }
            if (radius < 0 || radius > MaxRadius)
            {
                status = 400; code = "BAD_REQUEST";
                message = "radius 只允许 0.." + MaxRadius + "（怕一次标记太大片卡住主线程），收到 " + radius;
                return null;
            }

            int priority = -1;
            if (HasKey(body, "priority"))
            {
                if (!TryInt(body, "priority", out priority))
                {
                    status = 400; code = "BAD_REQUEST"; message = "priority 必须是整数";
                    return null;
                }
                if (priority < 1 || priority > 9)
                {
                    status = 400; code = "BAD_REQUEST"; message = "priority 只允许 1..9，收到 " + priority;
                    return null;
                }
            }

            if (!MainThread.Ready) { status = 503; code = "NOT_READY"; message = "main-thread pump is not running yet"; return null; }

            // ---- 全部游戏访问放在主线程 ----
            int cell = -1, foundWorld = -1;
            bool solidBefore = false, diggableBefore = false, shortCircuited = false, diggableAfter = false;
            int? objectsBefore = null, objectsAfter = null;   // null = 没拿到（不是 -1 哨兵）
            int objectsOkLayers = 0, objectsErrLayers = 0;
            string objectsErr = null;
            int digHitLayer = -1, digOkLayers = 0, digErrLayers = 0;
            string digErr = null;
            string failCode = null, failMsg = null, placedName = null;
            int placedInstance = -1;
            int? priorityApplied = null;                      // null = 没请求 / 拿不到
            bool? priorityMatch = null;
            bool notReadyDigTool = false;

            MainThread.Invoke(() =>
            {
                try
                {
                    if (DigTool.Instance == null)
                    {
                        notReadyDigTool = true;
                        return;
                    }
                    cell = Grid.XYToCell(x, y);
                    if (!Grid.IsValidCell(cell)) { failCode = "BAD_TARGET"; failMsg = "格子无效：" + x + "," + y + " ⇒ cell=" + cell; return; }

                    foundWorld = Grid.WorldIdx != null ? Grid.WorldIdx[cell] : -1;
                    if (worldId >= 0 && foundWorld != worldId)
                    {
                        failCode = "BAD_TARGET"; failMsg = "worldId 不符：请求 " + worldId + "，该格属于 " + foundWorld;
                        return;
                    }

                    solidBefore = Grid.IsSolidCell(cell);
                    if (!solidBefore)
                    {
                        failCode = "NOT_DIGGABLE"; failMsg = "该格没有实心物质可挖（Grid.IsSolidCell == false）";
                        return;
                    }

                    // ★ 建筑守卫：该格上有建筑 ⇒ 这是"挖"用错了工具（应该 deconstruct），属调用方的错 ⇒ 400
                    try
                    {
                        var atCell = Grid.Objects[cell, (int)ObjectLayer.Building];
                        if (atCell != null)
                        {
                            failCode = "NOT_DIGGABLE";
                            failMsg = "该格上有建筑（" + (atCell.name ?? "<unnamed>") + "），请用 deconstruct 而不是 dig";
                            objectsBefore = CountObjectsAt(cell, out objectsOkLayers, out objectsErrLayers, out objectsErr);
                            objectsAfter = objectsBefore;
                            return;
                        }
                    }
                    catch { /* 层索引取不到就跳过这层守卫，交给下面的 PlaceDig 判定 */ }

                    objectsBefore = CountObjectsAt(cell, out objectsOkLayers, out objectsErrLayers, out objectsErr);
                    // ★ 短路（防叠加）：该格已有挖标记 ⇒ 不调 PlaceDig
                    Diggable existing = FindDiggableAt(cell, out digHitLayer, out digOkLayers, out digErrLayers, out digErr);
                    diggableBefore = existing != null;
                    if (existing != null)
                    {
                        shortCircuited = true;
                        GameObject eg = null;
                        try { eg = existing.gameObject; } catch { }
                        placedName = eg != null ? eg.name : null;
                        try { placedInstance = eg != null ? eg.GetInstanceID() : -1; } catch { }
                        diggableAfter = true;
                        if (priority > 0)
                        {
                            priorityApplied = SetPriorityAndReadBack(eg, priority);
                            priorityMatch = priorityApplied.HasValue ? (bool?)(priorityApplied.Value == priority) : null;
                        }
                        objectsAfter = CountObjectsAt(cell, out objectsOkLayers, out objectsErrLayers, out objectsErr);
                        return;
                    }

                    GameObject placed = DigTool.PlaceDig(cell, radius);
                    if (placed == null)
                    {
                        // 这是"目标不可挖"的**正常拒绝**（建筑/已挖掉/地形不支持），不是我们代码出错 ⇒ 4xx
                        failCode = "NOT_DIGGABLE";
                        failMsg = "该格不可挖（可能是建筑/已被挖掉/地形不支持）；PlaceDig 返回 null";
                        objectsAfter = CountObjectsAt(cell, out objectsOkLayers, out objectsErrLayers, out objectsErr);
                        return;
                    }
                    placedName = placed.name;
                    try { placedInstance = placed.GetInstanceID(); } catch { }

                    Diggable dg = null;
                    try { dg = placed.GetComponent<Diggable>(); } catch { }
                    diggableAfter = dg != null;

                    if (priority > 0 && dg != null)
                    {
                        priorityApplied = SetPriorityAndReadBack(placed, priority);
                        priorityMatch = priorityApplied.HasValue ? (bool?)(priorityApplied.Value == priority) : null;
                    }

                    objectsAfter = CountObjectsAt(cell, out objectsOkLayers, out objectsErrLayers, out objectsErr);   // ★ 调用后再数一次 ⇒ placedDelta 可核对
                }
                catch (Exception e) { failCode = "INTERNAL"; failMsg = e.GetType().Name + ": " + e.Message; }
            }, 5000);

            if (notReadyDigTool)
            {
                status = 503; code = "NOT_READY"; message = "dig tool not initialised（DigTool.Instance == null）";
                Log.Info("[job] type=dig x=" + x + " y=" + y + " ⇒ 503 NOT_READY（DigTool.Instance == null）");
                return null;
            }
            if (failCode != null)
            {
                // 4xx = 调用方的错（改请求就行）；5xx = 我们的错（重试没用）。
                // 目前只有 INTERNAL 是真意外；NOT_DIGGABLE / BAD_TARGET 都是 4xx。
                status = failCode == "INTERNAL" ? 500 : 400;
                code = failCode; message = failMsg;
                Log.Info("[job] type=dig x=" + x + " y=" + y + " cell=" + cell + " ⇒ " + status + " " + failCode + ": " + failMsg);
                return null;
            }

            // 标记没落地 = 请求没成功 ⇒ 走 ok:false + error.code（**jobState 只表达"受理后的状态"**）
            if (!diggableAfter)
            {
                status = 500; code = "MARKER_NOT_PLACED";
                message = "PlaceDig 返回了对象但该对象上没有 Diggable 组件（cell=" + cell
                          + " objectsBefore=" + objectsBefore + " objectsAfter=" + objectsAfter + "）";
                Log.Warn("[job] type=dig x=" + x + " y=" + y + " cell=" + cell + " ⇒ 500 MARKER_NOT_PLACED"
                         + " objectsBefore=" + objectsBefore + " objectsAfter=" + objectsAfter
                         + " placed=" + placedName + "#" + placedInstance);
                return null;
            }

            bool accepted = true;
            string jobState = "marked";
            try { if (!Grid.IsSolidCell(cell)) jobState = "completed"; } catch { }

            int? placedDelta = (objectsBefore.HasValue && objectsAfter.HasValue)
                ? (int?)(objectsAfter.Value - objectsBefore.Value) : null;

            var sb = new StringBuilder(768);
            sb.Append("{\"type\":\"dig\"");
            sb.Append(",\"accepted\":").Append(accepted ? "true" : "false");
            sb.Append(",\"jobState\":").Append(Snap.Str(jobState));
            sb.Append(",\"target\":{\"worldId\":").Append(foundWorld);
            sb.Append(",\"x\":").Append(x).Append(",\"y\":").Append(y);
            sb.Append(",\"cell\":").Append(cell).Append('}');
            sb.Append(",\"radius\":").Append(radius);
            sb.Append(",\"markedObject\":{\"name\":").Append(Snap.Str(placedName));
            sb.Append(",\"instanceId\":").Append(placedInstance).Append('}');
            sb.Append(",\"diggableFound\":").Append(diggableAfter ? "true" : "false");
            sb.Append(",\"alreadyMarkedBefore\":").Append(diggableBefore ? "true" : "false");
            sb.Append(",\"shortCircuited\":").Append(shortCircuited ? "true" : "false");
            bool objectsAvailable = objectsBefore.HasValue && objectsAfter.HasValue;
            sb.Append(",\"objectsAvailable\":").Append(objectsAvailable ? "true" : "false");
            sb.Append(",\"objectsBefore\":").Append(NumOrNull(objectsBefore));
            sb.Append(",\"objectsAfter\":").Append(NumOrNull(objectsAfter));
            sb.Append(",\"placedDelta\":").Append(NumOrNull(placedDelta));
            sb.Append(",\"solidBefore\":").Append(solidBefore ? "true" : "false");
            sb.Append(",\"solidAfter\":").Append(Grid.IsSolidCell(cell) ? "true" : "false");
            sb.Append(",\"priorityRequested\":").Append(priority > 0 ? priority.ToString() : "null");
            sb.Append(",\"priorityApplied\":").Append(NumOrNull(priorityApplied));
            sb.Append(",\"priorityMatch\":").Append(priorityMatch.HasValue ? (priorityMatch.Value ? "true" : "false") : "null");
            // 部分成功：受理了，但某一步没成（目前只有"优先级没设上"这一种）⇒ 独立字段，不污染 jobState
            bool partial = priority > 0 && priorityMatch.HasValue && !priorityMatch.Value;
            sb.Append(",\"partial\":").Append(partial ? "true" : "false");
            if (partial)
                sb.Append(",\"partialReason\":").Append(Snap.Str("priority 未生效：请求 " + priority
                    + "，回读 " + (priorityApplied.HasValue ? priorityApplied.Value.ToString() : "<拿不到 Prioritizable>")));
            string note = shortCircuited
                ? "该格已有挖标记，已短路：PlaceDig 未被调用（标记物不会叠加）；priority 若给了则作用在已有标记上"
                : "标记已落地（Diggable 挂在 DigPlacer 上），等复制人去挖；挖完该格 solidAfter 会变 false、标记物消失";
            sb.Append(",\"note\":").Append(Snap.Str(note));
            sb.Append('}');

            Log.Info("[job] type=dig x=" + x + " y=" + y + " cell=" + cell + " world=" + foundWorld
                     + " radius=" + radius + " solidBefore=" + solidBefore
                     + " alreadyMarkedBefore=" + diggableBefore + " shortCircuited=" + shortCircuited
                     + " objectsBefore=" + (objectsBefore.HasValue ? objectsBefore.Value.ToString() : "<null>")
                     + " objectsAfter=" + (objectsAfter.HasValue ? objectsAfter.Value.ToString() : "<null>")
                     + " placedDelta=" + (placedDelta.HasValue ? placedDelta.Value.ToString() : "<null>")
                     + " placed=" + placedName + "#" + placedInstance
                     + " diggableFound=" + diggableAfter + " jobState=" + jobState
                     + " priority=" + (priority > 0
                        ? priority + "->" + (priorityApplied.HasValue ? priorityApplied.Value.ToString() : "<null>")
                          + "(match=" + (priorityMatch.HasValue ? priorityMatch.Value.ToString() : "<null>") + ")"
                        : "<none>")
                     + " partial=" + (priority > 0 && priorityMatch.HasValue && !priorityMatch.Value)
                     + " | diggableScan: hitLayer=" + digHitLayer + " okLayers=" + digOkLayers
                     + " errLayers=" + digErrLayers + " err=" + (digErr ?? "<none>")
                     + " | objectsScan: okLayers=" + objectsOkLayers + " errLayers=" + objectsErrLayers
                     + " err=" + (objectsErr ?? "<none>"));
            if (priority > 0 && priorityMatch.HasValue && !priorityMatch.Value)
                Log.Warn("[job] priority 回读不一致：期望 " + priority
                         + "，实际 " + (priorityApplied.HasValue ? priorityApplied.Value.ToString() : "<null>"));
            if (placedDelta.HasValue && placedDelta.Value > 1)
                Log.Warn("[job] placedDelta=" + placedDelta.Value + " > 1 ⇒ PlaceDig 一次调用在该格加了不止一个对象（需人工核对）");

            return sb.ToString();
        }

        /// <summary>某一格上的一个候选对象。</summary>
        internal sealed class CellObject
        {
            public int Layer;
            public System.Collections.Generic.List<int> Layers = new System.Collections.Generic.List<int>();   // 同一对象可能注册在多层
            public string PrefabId;
            public string FilterLayer;
            public bool Deconstructable;
            public GameObject Go;
            public Deconstructable Comp;
            public HarvestDesignatable Designatable;   // harvest 用：玩家"标记收获"的载体
            public Clearable Clear;                    // sweep 用：清扫标记的载体
            public Harvestable Harvest;                // harvest 用：chore 的持有者
        }

        /// <summary>枚举该格**所有层**上的对象（按层号升序）。判据：Grid.Objects[cell, layer]。</summary>
        /// <summary>
        /// 枚举该格的对象，**按 GameObject 的 instanceId 去重**。
        /// 背景（M6.18）：同一个对象会注册到多个 `ObjectLayer`（地板 = Building+FoundationTile，
        /// 电线 = Wire+WireTile，复制人 = Minion+Pickupables+Mover）。
        /// 按"层"计数会把 1 个对象数成 3 个 ⇒ **假歧义**。所以：
        ///   * 每个 instanceId 只出一条 `CellObject`；
        ///   * 它注册过的层都收进 `Layers`（`Layer` 保留最小的那个）；
        ///   * `layer` / `filterLayer` 的匹配都对 `Layers` 里**任意一层**生效。
        /// </summary>
        internal static System.Collections.Generic.List<CellObject> EnumerateCellObjects(int cell)
        {
            var list = new System.Collections.Generic.List<CellObject>();
            var byId = new System.Collections.Generic.Dictionary<int, CellObject>();
            int layers = (int)ObjectLayer.NumLayers;
            for (int layer = 0; layer < layers; layer++)
            {
                GameObject go = null;
                try { go = Grid.Objects[cell, layer]; } catch { continue; }
                if (go == null) continue;

                int id = 0;
                try { id = go.GetInstanceID(); } catch { }

                CellObject co;
                if (id != 0 && byId.TryGetValue(id, out co))
                {
                    // 同一个对象：只补层信息，不新增候选
                    if (!co.Layers.Contains(layer)) co.Layers.Add(layer);
                    continue;
                }

                co = new CellObject { Layer = layer, Go = go };
                co.Layers.Add(layer);
                try
                {
                    var b = go.GetComponent<Building>();
                    co.PrefabId = b != null && b.Def != null ? b.Def.PrefabID : null;
                }
                catch { }
                if (string.IsNullOrEmpty(co.PrefabId))
                {
                    try
                    {
                        var kp = go.GetComponent<KPrefabID>();
                        co.PrefabId = kp != null ? kp.PrefabTag.Name : go.name;
                    }
                    catch { co.PrefabId = go.name; }
                }
                try
                {
                    co.FilterLayer = DeconstructTool.Instance != null
                        ? DeconstructTool.Instance.GetFilterLayerFromObjectLayer((ObjectLayer)layer)
                        : ((ObjectLayer)layer).ToString();
                }
                catch { co.FilterLayer = ((ObjectLayer)layer).ToString(); }
                try { co.Comp = go.GetComponent<Deconstructable>(); } catch { }
                co.Deconstructable = co.Comp != null;
                try { co.Designatable = go.GetComponent<HarvestDesignatable>(); } catch { }
                try { co.Harvest = go.GetComponent<Harvestable>(); } catch { }
                try { co.Clear = go.GetComponent<Clearable>(); } catch { }

                if (id != 0) byId[id] = co;
                list.Add(co);
            }
            return list;
        }

        static string LayerEnumName(int layer) { try { return ((ObjectLayer)layer).ToString(); } catch { return layer.ToString(); } }

        /// <summary>给其他类（MarksQuery）复用的层名转换。</summary>
        internal static string LayerEnumName2(int layer) { return LayerEnumName(layer); }

        /// <summary>本地化层名：STRINGS.UI.TOOLS.FILTERLAYERS.&lt;FILTERLAYER&gt;.NAME；取不到返回 null（**不退回英文**）。</summary>
        static string FilterLayerZh(string filterLayer)
        {
            if (string.IsNullOrEmpty(filterLayer)) return null;
            try
            {
                var se = Strings.Get("STRINGS.UI.TOOLS.FILTERLAYERS." + filterLayer.ToUpperInvariant() + ".NAME");
                if (se != null && !string.IsNullOrEmpty(se.String))
                {
                    // 游戏的缺键占位符形如 "MISSING.STRINGS.UI.TOOLS.FILTERLAYERS.DEFAULT.NAME"
                    // ⇒ **不能把它当翻译交出去**，按规矩取不到就给 null（判据放宽，格式以后可能变）
                    string s = se.String;
                    if (s.StartsWith("MISSING.", System.StringComparison.OrdinalIgnoreCase)
                        || s.IndexOf(".MISSING.", System.StringComparison.OrdinalIgnoreCase) >= 0)
                        return null;
                    return s;
                }
            }
            catch { }
            return null;
        }

        static string CandidatesJson(System.Collections.Generic.List<CellObject> cands)
        {
            var sb = new StringBuilder(512);
            sb.Append("{\"candidates\":[");
            bool first = true;
            for (int i = 0; i < cands.Count; i++)
            {
                var c = cands[i];
                if (!first) sb.Append(',');
                first = false;
                sb.Append("{\"prefabId\":").Append(Snap.Str(c.PrefabId));
                sb.Append(",\"layer\":").Append(Snap.Str(LayerEnumName(c.Layer)));
                sb.Append(",\"filterLayer\":").Append(Snap.Str(c.FilterLayer));
                sb.Append(",\"filterLayerName\":").Append(Snap.Str(FilterLayerZh(c.FilterLayer)));
                sb.Append(",\"deconstructable\":").Append(c.Deconstructable ? "true" : "false");
                // 已排上队的候选也要标出来（标记 ≠ 拆掉，它仍在场上）
                bool already = false;
                try { already = c.Comp != null && c.Comp.IsMarkedForDeconstruction(); } catch { }
                sb.Append(",\"alreadyMarked\":").Append(already ? "true" : "false");
                sb.Append('}');
            }
            sb.Append("]}");
            return sb.ToString();
        }
        // ================= M6.13：对象级 type 的区域模式 =================
        //
        // 语义（用户定的）：
        //   * **格内歧义（≥2 个候选对象）⇒ 跳过 + 计数 `ambiguousSkipped`**，不整片失败；
        //   * 硬等式 `requested = applied + alreadyMarked + ambiguousSkipped + rejected`；
        //   * `ambiguousSkipped > 0` **不报 ok:false**（note 里说清缺了多少格）；
        //   * 整片 100% 歧义 ⇒ 直接 `400 NEED_DISAMBIGUATION`（先看再扫，别扫完才说）。
        static string RegionObjectExecute(string type, string body, out int status, out string code,
                                          out string message, out string details)
        {
            status = 200; code = null; message = null; details = null;


            int x0, y0, x1, y1;
            if (!TryInt(body, "x0", out x0) || !TryInt(body, "y0", out y0)
                || !TryInt(body, "x1", out x1) || !TryInt(body, "y1", out y1))
            { status = 400; code = "BAD_REQUEST"; message = "区域需要 x0/y0/x1/y1（整数）"; return null; }
            if (x1 < x0) { int t0 = x0; x0 = x1; x1 = t0; }
            if (y1 < y0) { int t1 = y0; y0 = y1; y1 = t1; }

            long cellsL = (long)(x1 - x0 + 1) * (long)(y1 - y0 + 1);
            if (cellsL > 5000)
            {
                status = 400; code = "REGION_TOO_LARGE";
                message = "区域太大：本请求含 " + cellsL + " 格，上限 5000（这条路跑在 Unity 主线程上）";
                return null;
            }
            int cells = (int)cellsL;

            if (!MainThread.Ready) { status = 503; code = "NOT_READY"; message = "main-thread pump is not running yet"; return null; }

            // M6.15：区域模式接受 layer / prefabId 作全局过滤器（用它消歧），priority 统一生效
            string gLayer = MatchString(body, "layer"), gPrefab = MatchString(body, "prefabId");
            int gPriority = -1;
            if (HasKey(body, "priority") && !TryInt(body, "priority", out gPriority))
            { status = 400; code = "BAD_REQUEST"; message = "priority 必须是整数"; return null; }
            if (gPriority > 0 && (gPriority < 1 || gPriority > 9))
            { status = 400; code = "BAD_REQUEST"; message = "priority 只允许 1..9，收到 " + gPriority; return null; }
            int priorityMismatch = 0;
            int applied = 0, alreadyMarked = 0, ambiguous = 0, rejected = 0;
            int rInvalid = 0, rNoTarget = 0, rNotRipe = 0, rReadback = 0;
            int bx0 = int.MaxValue, by0 = int.MaxValue, bx1 = int.MinValue, by1 = int.MinValue;
            var sw = System.Diagnostics.Stopwatch.StartNew();

            MainThread.Invoke(() =>
            {
                try
                {
                    for (int yy = y0; yy <= y1; yy++)
                    {
                        for (int xx = x0; xx <= x1; xx++)
                        {
                            try
                            {
                                int cell = Grid.XYToCell(xx, yy);
                                if (!Grid.IsValidCell(cell)) { rejected++; rInvalid++; continue; }

                                // ---- 该格属于这个 type 的候选 ----
                                var objs = EnumerateCellObjects(cell);
                                var cand = new System.Collections.Generic.List<CellObject>();
                                for (int i = 0; i < objs.Count; i++)
                                {
                                    var o = objs[i];
                                    // 全局过滤：先按 layer / prefabId 收窄，再判类型
                                    if (gLayer != null && !MatchLayer(o, gLayer)) continue;
                                    if (gPrefab != null && !MatchPrefab(o, gPrefab)) continue;
                                    if (type == "deconstruct" && o.Comp != null) cand.Add(o);
                                    else if (type == "harvest" && o.Designatable != null) cand.Add(o);
                                    else if (type == "sweep" && o.Clear != null) cand.Add(o);
                                }

                                if (cand.Count == 0) { rejected++; rNoTarget++; continue; }
                                if (cand.Count > 1) { ambiguous++; continue; }   // ★ 跳过并计数，不猜

                                var c = cand[0];
                                bool markedAlready = false, markedAfter = false;

                                if (type == "deconstruct")
                                {
                                    try { markedAlready = c.Comp.IsMarkedForDeconstruction(); } catch { }
                                    if (markedAlready) { alreadyMarked++; continue; }
                                    c.Comp.QueueDeconstruction(true);
                                    try { markedAfter = c.Comp.IsMarkedForDeconstruction(); } catch { }
                                }
                                else if (type == "harvest")
                                {
                                    bool ripe = false;
                                    try { ripe = c.Designatable.CanBeHarvested(); } catch { }
                                    if (!ripe) { rejected++; rNotRipe++; continue; }     // "还没熟"不等于失败，但确实没动
                                    try { markedAlready = c.Designatable.MarkedForHarvest; } catch { }
                                    if (markedAlready) { alreadyMarked++; continue; }
                                    c.Designatable.MarkForHarvest();
                                    try { markedAfter = c.Designatable.MarkedForHarvest; } catch { }
                                }
                                else // sweep
                                {
                                    string rdErr;
                                    bool? before = TryReadMarkedForClear(c.Clear, out rdErr);
                                    if (!before.HasValue) { rejected++; rReadback++; continue; }
                                    if (before.Value) { alreadyMarked++; continue; }
                                    c.Clear.MarkForClear(false, false);
                                    bool? after = TryReadMarkedForClear(c.Clear, out rdErr);
                                    markedAfter = after.HasValue && after.Value;
                                    if (!markedAfter) { rejected++; rReadback++; continue; }
                                }

                                if (!markedAfter) { rejected++; rReadback++; continue; }
                                applied++;
                                if (gPriority > 0)
                                {
                                    int? pa = SetPriorityAndReadBack(c.Go, gPriority);
                                    if (!pa.HasValue || pa.Value != gPriority) priorityMismatch++;
                                }
                                if (xx < bx0) bx0 = xx;
                                if (yy < by0) by0 = yy;
                                if (xx > bx1) bx1 = xx;
                                if (yy > by1) by1 = yy;
                            }
                            catch { rejected++; rReadback++; }   // 逐格独立兜住，不中断整批
                        }
                    }
                }
                catch (System.Exception e) { Log.Warn("[job] " + type + " 区域扫描异常（返回已统计部分）: " + e); }
            }, 30000);
            sw.Stop();

            // 整片 100% 歧义 ⇒ 先看再扫，直接让调用方点名，别"扫完 100 格才说一格没动"
            if (ambiguous == cells && cells > 0)
            {
                status = 400; code = "NEED_DISAMBIGUATION";
                message = "该区域 " + cells + " 格**全部**有多个候选对象，必须逐格点名（layer/prefabId）；"
                          + "本请求未改动任何格";
                Log.Info("[job] type=" + type + "(region) ⇒ 400 NEED_DISAMBIGUATION（" + cells + " 格全歧义）");
                return null;
            }

            int sum = applied + alreadyMarked + ambiguous + rejected;
            bool contig = true;
            if (applied > 0)
            {
                long barea = (long)(bx1 - bx0 + 1) * (long)(by1 - by0 + 1);
                contig = barea == applied;
            }
            if (sum != cells)
                Log.Warn("[job] " + type + " 区域计数不等：" + applied + "+" + alreadyMarked + "+" + ambiguous + "+" + rejected
                         + " = " + sum + " ≠ cells=" + cells);

            var sb = new StringBuilder(1024);
            sb.Append("{\"type\":").Append(Snap.Str(type));
            sb.Append(",\"requested\":{\"x0\":").Append(x0).Append(",\"y0\":").Append(y0)
              .Append(",\"x1\":").Append(x1).Append(",\"y1\":").Append(y1)
              .Append(",\"cells\":").Append(cells).Append('}');
            sb.Append(",\"applied\":{\"count\":").Append(applied);
            if (applied > 0)
                sb.Append(",\"bounds\":{\"x0\":").Append(bx0).Append(",\"y0\":").Append(by0)
                  .Append(",\"x1\":").Append(bx1).Append(",\"y1\":").Append(by1).Append('}');
            sb.Append(",\"isContiguous\":").Append(contig ? "true" : "false").Append('}');
            sb.Append(",\"alreadyMarked\":").Append(alreadyMarked);
            sb.Append(",\"ambiguousSkipped\":").Append(ambiguous);
            sb.Append(",\"filters\":{\"layer\":").Append(Snap.Str(gLayer))
              .Append(",\"prefabId\":").Append(Snap.Str(gPrefab))
              .Append(",\"priority\":").Append(gPriority > 0 ? gPriority.ToString() : "null").Append("}");
            sb.Append(",\"priorityMismatch\":").Append(priorityMismatch);
            sb.Append(",\"rejected\":{\"count\":").Append(rejected).Append(",\"byReason\":{");
            sb.Append("\"INVALID_CELL\":").Append(rInvalid);
            sb.Append(",\"NO_TARGET\":").Append(rNoTarget);
            sb.Append(",\"NOT_RIPE\":").Append(rNotRipe);
            sb.Append(",\"READBACK_FAILED\":").Append(rReadback).Append("}}");
            sb.Append(",\"accepted\":").Append((applied > 0 || alreadyMarked > 0) ? "true" : "false");
            sb.Append(",\"jobState\":").Append(Snap.Str((applied > 0 || alreadyMarked > 0) ? "marked" : "not-applied"));
            sb.Append(",\"elapsedMs\":").Append(sw.ElapsedMilliseconds);
            sb.Append(",\"note\":").Append(Snap.Str(
                "对象级区域模式：ambiguousSkipped = 该格有多个候选对象、已跳过（未改动），请逐格点名后再调用"
                + (ambiguous > 0 ? "；本次共跳过 " + ambiguous + " 格" : "")));
            sb.Append('}');

            Log.Info("[job] type=" + type + "(region) x0=" + x0 + " y0=" + y0 + " x1=" + x1 + " y1=" + y1
                     + " cells=" + cells + " applied=" + applied + " alreadyMarked=" + alreadyMarked
                     + " ambiguousSkipped=" + ambiguous + " rejected=" + rejected
                     + " (INVALID=" + rInvalid + " NO_TARGET=" + rNoTarget + " NOT_RIPE=" + rNotRipe
                     + " READBACK=" + rReadback + ")"
                     + " elapsedMs=" + sw.ElapsedMilliseconds
                     + " | 等式 " + (sum == cells ? "OK" : "FAIL(" + sum + "≠" + cells + ")"));
            return sb.ToString();
        }
        // ================= M6.5：dig 区域操作（带统计）=================
        //
        // 入参：{ "type":"dig", "x0":.., "y0":.., "x1":.., "y1":.., "priority":5, "listCells":true }
        // 语义（用户定的）：
        //   * `applied.count` = **本次真正新标记**的格数；
        //   * `alreadyMarked` = **本来就有标记**的格数，单独计数，**不视为错误**；
        //   * `rejected.byReason` 必须分类：OUT_OF_WORLD / HAS_BUILDING / NOT_DIGGABLE / READBACK_FAILED；
        //   * 硬等式：applied + alreadyMarked + rejected == requested.cells（日志自检）；
        //   * 区域上限 5000 格、逐格列表上限 2000 格（主线程操作，防止卡住整个游戏）。
        const int RegionMaxCells = 5000;
        const int RegionListMaxCells = 2000;

        static string DigRegionExecute(string body, out int status, out string code, out string message)
        {
            status = 200; code = null; message = null;

            int x0 = 0, y0 = 0, x1 = 0, y1 = 0;
            int worldIdReq = 0; TryInt(body, "worldId", out worldIdReq);

            // ★ M6.67：块寻址 cx/cy（块规则见 docs/API.md §3.31）
            int cx = -1, cy = -1;
            bool useChunk = TryInt(body, "cx", out cx) && TryInt(body, "cy", out cy);
            if (useChunk)
            {
                string cerr0;
                int chx0, chy0, chx1, chy1;
                if (!Chunks.TryChunkBBox(worldIdReq, cx, cy, out chx0, out chy0, out chx1, out chy1, out cerr0))
                {
                    status = 400; code = "BAD_CHUNK"; message = cerr0;
                    return null;
                }
                x0 = chx0; y0 = chy0; x1 = chx1; y1 = chy1;
            }
            else
            {
                if (!TryInt(body, "x0", out x0) || !TryInt(body, "y0", out y0)
                 || !TryInt(body, "x1", out x1) || !TryInt(body, "y1", out y1))
                {
                    status = 400; code = "BAD_REQUEST"; message = "区域需要 x0/y0/x1/y1（整数），或者块寻址 cx/cy + worldId";
                    return null;
                }
            }
            if (x1 < x0) { int tmp = x0; x0 = x1; x1 = tmp; }      // 允许反向拖拽
            if (y1 < y0) { int tmp = y0; y0 = y1; y1 = tmp; }

            // ★ 防手滑：区域/块挖必须显式 confirm:true（单格路径不受影响）
            bool digConfirm = false;
            MatchBool(body, "confirm", out digConfirm);
            if (!digConfirm)
            {
                long nGuarded = (long)(x1 - x0 + 1) * (long)(y1 - y0 + 1);
                status = 400; code = "CONFIRM_REQUIRED";
                message = "区域/块挖会改存档（本请求 " + nGuarded + " 格），必须显式 confirm:true";
                return null;
            }

            long cellsL = (long)(x1 - x0 + 1) * (long)(y1 - y0 + 1);
            if (cellsL > RegionMaxCells)
            {
                status = 400; code = "REGION_TOO_LARGE";
                message = "区域太大：本请求含 " + cellsL + " 格，上限 " + RegionMaxCells
                          + "（这条路跑在 Unity 主线程上，会卡住整个游戏）";
                return null;
            }
            int cells = (int)cellsL;

            bool listCells = System.Text.RegularExpressions.Regex.IsMatch(body ?? "", "\"listCells\"\\s*:\\s*true",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (listCells && cells > RegionListMaxCells)
            {
                status = 400; code = "TOO_MANY_CELLS";
                message = "listCells=true 时区域不能超过 " + RegionListMaxCells + " 格（本请求 " + cells + " 格）";
                return null;
            }

            int priority = -1;
            if (HasKey(body, "priority"))
            {
                if (!TryInt(body, "priority", out priority))
                { status = 400; code = "BAD_REQUEST"; message = "priority 必须是整数"; return null; }
                if (priority < 1 || priority > 9)
                { status = 400; code = "BAD_REQUEST"; message = "priority 只允许 1..9，收到 " + priority; return null; }
            }
            int worldId = -1; TryInt(body, "worldId", out worldId);

            if (!MainThread.Ready) { status = 503; code = "NOT_READY"; message = "main-thread pump is not running yet"; return null; }

            int applied = 0, alreadyMarked = 0, rejected = 0;
        int markedStillPresent = -1;   // ★ 请求末尾复核：bbox 内现在还剩多少个挖标记（-1 = 没算）
            int okOutOfWorld = 0, okHasBuilding = 0, okNotDiggable = 0, okReadbackFailed = 0;
            int priorityMismatch = 0;
            int bx0 = int.MaxValue, by0 = int.MaxValue, bx1 = int.MinValue, by1 = int.MinValue;
            var failSamples = new StringBuilder();
            int failSampleCount = 0;
            bool digToolMissing = false;
            var listedCells = listCells ? new StringBuilder() : null;
            int listed = 0;
            var sw = System.Diagnostics.Stopwatch.StartNew();

            MainThread.Invoke(() =>
            {
                try
                {
                    if (DigTool.Instance == null) { digToolMissing = true; return; }
                    int width = Grid.WidthInCells;

                    for (int yy = y0; yy <= y1; yy++)
                    {
                        for (int xx = x0; xx <= x1; xx++)
                        {
                            // ★ 逐格独立 try/catch：一格失败不能中断整批
                            string reason = null;
                            int cell = -1;
                            try
                            {
                                cell = Grid.XYToCell(xx, yy);
                                if (!Grid.IsValidCell(cell)) { reason = "OUT_OF_WORLD"; }
                                else
                                {
                                    int w = Grid.WorldIdx != null ? Grid.WorldIdx[cell] : -1;
                                    if (worldId >= 0 && w != worldId) { reason = "OUT_OF_WORLD"; }
                                    // 有建筑 ⇒ 该用 deconstruct
                                    else if (Grid.Objects[cell, (int)ObjectLayer.Building] != null) { reason = "HAS_BUILDING"; }
                                    else if (!CellRules.IsSolidForDig(cell)) { reason = "NOT_DIGGABLE"; }   // ★ 实心判据与 area 同源（CellRules）
                                    else
                                    {
                                        int hit, okc, errc; string errTxt;
                    bool existing = CellRules.IsMarkedForDig(cell);   // ★ 与 area 同源（全层搜 Diggable，同 /v1/marks）
                    if (existing) { alreadyMarked++; if (listCells) AppendCellListed(listedCells, ref listed, xx, yy, "alreadyMarked"); continue; }

                                        GameObject placed = DigTool.PlaceDig(cell, 0);
                                        if (placed == null) { reason = "NOT_DIGGABLE"; }
                                        else
                                        {
                                            Diggable dg = null;
                                            try { dg = placed.GetComponent<Diggable>(); } catch { }
                                            if (dg == null) { reason = "READBACK_FAILED"; }
                                            else
                                            {
                                                applied++;
                                                if (xx < bx0) bx0 = xx;
                                                if (yy < by0) by0 = yy;
                                                if (xx > bx1) bx1 = xx;
                                                if (yy > by1) by1 = yy;
                                                if (priority > 0)
                                                {
                                                    int? pa = SetPriorityAndReadBack(placed, priority);
                                                    if (!pa.HasValue || pa.Value != priority) priorityMismatch++;
                                                }
                                                if (listCells) AppendCellListed(listedCells, ref listed, xx, yy, "marked");
                                                continue;
                                            }
                                        }
                                    }
                                }
                            }
                            catch (System.Exception e)
                            {
                                reason = "READBACK_FAILED";
                                if (failSampleCount < 5)
                                {
                                    failSamples.Append(" (").Append(xx).Append(",").Append(yy).Append(")=")
                                               .Append(reason).Append("[").Append(e.GetType().Name).Append("]");
                                    failSampleCount++;
                                }
                                rejected++;
                                Increment(ref okReadbackFailed);
                                continue;
                            }

                            // 走到这里 = 被拒
                            rejected++;
                            if (reason == "OUT_OF_WORLD") okOutOfWorld++;
                            else if (reason == "HAS_BUILDING") okHasBuilding++;
                            else if (reason == "READBACK_FAILED") okReadbackFailed++;
                            else okNotDiggable++;
                            if (failSampleCount < 5)
                            {
                                failSamples.Append(" (").Append(xx).Append(",").Append(yy).Append(")=").Append(reason);
                                failSampleCount++;
                            }
                            if (listCells) AppendCellListed(listedCells, ref listed, xx, yy, reason ?? "rejected");
                        }
                    }
                }
                catch (System.Exception e)
                {
                    Log.Warn("[job] dig 区域扫描异常（已返回已统计部分）: " + e);
                }
        // ★ M6.69：**请求末尾复核** —— 不依赖"当时 PlaceDig 返回非 null"，直接在同一个请求里重数一遍
        try
        {
            int still = 0;
            for (int yy = y0; yy <= y1; yy++)
            {
                for (int xx = x0; xx <= x1; xx++)
                {
                    int cc = Grid.XYToCell(xx, yy);
                    if (!Grid.IsValidCell(cc)) continue;
                    if (CellRules.IsMarkedForDig(cc)) still++;
                }
            }
            markedStillPresent = still;
        }
        catch { markedStillPresent = -1; }

            }, 30000);
            sw.Stop();

            if (digToolMissing)
            {
                status = 503; code = "NOT_READY"; message = "dig tool not initialised（DigTool.Instance == null）";
                Log.Info("[job] type=dig(region) ⇒ 503 NOT_READY（DigTool.Instance == null）");
                return null;
            }

            bool contiguous = true;
            if (applied > 0)
            {
                long barea = (long)(bx1 - bx0 + 1) * (long)(by1 - by0 + 1);
                contiguous = barea == applied;
            }

            // 硬等式自检：applied + alreadyMarked + rejected == cells
            int sum = applied + alreadyMarked + rejected;
            if (sum != cells)
                Log.Warn("[job] dig 区域计数不等：" + applied + "(applied) + " + alreadyMarked + "(already) + "
                         + rejected + "(rejected) = " + sum + " ≠ cells=" + cells);

            var sb = new StringBuilder(1024);
            sb.Append("{\"type\":\"dig\"");
            sb.Append(",\"requested\":{\"x0\":").Append(x0).Append(",\"y0\":").Append(y0);
            sb.Append(",\"x1\":").Append(x1).Append(",\"y1\":").Append(y1);
            sb.Append(",\"cells\":").Append(cells);
            sb.Append(",\"worldId\":").Append(worldId).Append('}');
            sb.Append(",\"applied\":{\"count\":").Append(applied);
            if (applied > 0)
            {
                sb.Append(",\"bounds\":{\"x0\":").Append(bx0).Append(",\"y0\":").Append(by0);
                sb.Append(",\"x1\":").Append(bx1).Append(",\"y1\":").Append(by1).Append('}');
            }
            else { sb.Append(",\"bounds\":null"); }   // ★ M6.68：无标记时给 null（以前是空串/缺字段）
            sb.Append(",\"isContiguous\":").Append(contiguous ? "true" : "false");
            sb.Append('}');
            sb.Append(",\"alreadyMarked\":").Append(alreadyMarked);
        // ★ M6.67：补 _约定别名_（lead 指定命名），旧字段保持不变
        sb.Append(",\"marked\":").Append(applied);
        sb.Append(",\"skipped\":").Append(rejected);
        sb.Append(",\"cellsScanned\":").Append(cells);
        sb.Append(",\"markedStillPresent\":").Append(markedStillPresent);
        sb.Append(",\"markedVanished\":").Append(markedStillPresent >= 0 ? (applied - markedStillPresent).ToString() : "null");
        if (useChunk)
        {
            sb.Append(",\"chunk\":{\"cx\":").Append(cx).Append(",\"cy\":").Append(cy)
              .Append(",\"w\":").Append(x1 - x0 + 1).Append(",\"h\":").Append(y1 - y0 + 1).Append('}')
              .Append(",\"chunkSize\":").Append(Chunks.ChunkSize);
        }
        else sb.Append(",\"chunk\":null");
            sb.Append(",\"rejected\":{\"count\":").Append(rejected).Append(",\"byReason\":{");
            sb.Append("\"OUT_OF_WORLD\":").Append(okOutOfWorld);
            sb.Append(",\"HAS_BUILDING\":").Append(okHasBuilding);
            sb.Append(",\"NOT_DIGGABLE\":").Append(okNotDiggable);
            sb.Append(",\"READBACK_FAILED\":").Append(okReadbackFailed).Append("}}");
            sb.Append(",\"accepted\":").Append((applied > 0 || alreadyMarked > 0) ? "true" : "false");
            sb.Append(",\"jobState\":").Append(Snap.Str(applied > 0 || alreadyMarked > 0 ? "marked" : "not-applied"));
            sb.Append(",\"priorityRequested\":").Append(priority > 0 ? priority.ToString() : "null");
            sb.Append(",\"priorityMismatch\":").Append(priorityMismatch);
            sb.Append(",\"partial\":").Append(priorityMismatch > 0 ? "true" : "false");
            sb.Append(",\"elapsedMs\":").Append(sw.ElapsedMilliseconds);
            sb.Append(",\"sourceAgnostic\":true");
            if (listCells)
            {
                sb.Append(",\"cellsListed\":[").Append(listedCells).Append(']');
            }
            sb.Append(",\"note\":").Append(Snap.Str(
                "applied=本次新标记，alreadyMarked=本来就有（不算失败），rejected=按原因分类；"
                + "isContiguous=false 表示实际区域不连续（bounds 会偏大）"));
            sb.Append('}');

            Log.Info("[job] type=dig(region) x0=" + x0 + " y0=" + y0 + " x1=" + x1 + " y1=" + y1
                     + " cells=" + cells + " applied=" + applied + " alreadyMarked=" + alreadyMarked
                     + " rejected=" + rejected + " (OUT_OF_WORLD=" + okOutOfWorld + " HAS_BUILDING=" + okHasBuilding
                     + " NOT_DIGGABLE=" + okNotDiggable + " READBACK_FAILED=" + okReadbackFailed + ")"
                     + " contiguous=" + contiguous + " priorityMismatch=" + priorityMismatch
                     + " elapsedMs=" + sw.ElapsedMilliseconds
                     + " | 等式 " + (sum == cells ? "OK" : "FAIL(" + sum + "≠" + cells + ")")
                     + " | 前几个失败格:" + (failSamples.Length > 0 ? failSamples.ToString() : " <none>"));

            return sb.ToString();
        }

        static void Increment(ref int v) { v++; }

        static void AppendCellListed(StringBuilder sb, ref int listed, int x, int y, string state)
        {
            if (listed > 0) sb.Append(',');
            sb.Append("{\"x\":").Append(x).Append(",\"y\":").Append(y)
              .Append(",\"state\":").Append(Snap.Str(state)).Append('}');
            listed++;
        }
        static bool MatchLayerEnum(CellObject c, string want)
        {
            if (string.IsNullOrEmpty(want)) return false;
            for (int i = 0; i < c.Layers.Count; i++)
            {
                if (string.Equals(LayerEnumName(c.Layers[i]), want.Trim(), System.StringComparison.OrdinalIgnoreCase)) return true;
            }
            // 兼容 c.Layer（最小层）
            return string.Equals(LayerEnumName(c.Layer), want.Trim(), System.StringComparison.OrdinalIgnoreCase);
        }
        static bool MatchFilterLayer(CellObject c, string want)
        {
            if (string.IsNullOrEmpty(want)) return false;
            if (string.Equals(c.FilterLayer, want.Trim(), System.StringComparison.OrdinalIgnoreCase)) return true;
            // filterLayer 是"该对象最小层"的映射；若对象跨层，接受任意一层的映射名
            for (int i = 0; i < c.Layers.Count; i++)
            {
                string fl = null;
                try
                {
                    fl = DeconstructTool.Instance != null
                        ? DeconstructTool.Instance.GetFilterLayerFromObjectLayer((ObjectLayer)c.Layers[i])
                        : LayerEnumName(c.Layers[i]);
                }
                catch { fl = LayerEnumName(c.Layers[i]); }
                if (string.Equals(fl, want.Trim(), System.StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }
        static bool MatchLayer(CellObject c, string want)
        {
            return MatchLayerEnum(c, want) || MatchFilterLayer(c, want);
        }
        static bool MatchPrefab(CellObject c, string want)
        {
            if (string.IsNullOrEmpty(want)) return false;
            return string.Equals(c.PrefabId, want.Trim(), System.StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>反射把对象的所有实例字段读成 "name=value; " 文本（诊断用，值截断）。</summary>

        /// <summary>把 Tag 列表拼成 "A,B" 文本（诊断用）。</summary>
        static string TagsToText(System.Collections.Generic.IList<Tag> tags)
        {
            if (tags == null) return null;
            var sb = new StringBuilder(128);
            for (int i = 0; i < tags.Count; i++)
            {
                if (i > 0) sb.Append(',');
                try { sb.Append(tags[i].Name); } catch { sb.Append("<err>"); }
            }
            return sb.ToString();
        }
        static string ReflectDump(object obj)
        {
            if (obj == null) return "<obj null>";
            var sb = new StringBuilder(512);
            try
            {
                var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
                var fields = obj.GetType().GetFields(flags);
                for (int i = 0; i < fields.Length && i < 60; i++)
                {
                    string v = null;
                    try { var val = fields[i].GetValue(obj); v = val == null ? "null" : val.ToString(); } catch { v = "<err>"; }
                    if (v != null && v.Length > 60) v = v.Substring(0, 60) + "…";
                    sb.Append(fields[i].Name).Append('=').Append(v).Append("; ");
                }
            }
            catch (System.Exception e) { sb.Append("<dump ex: ").Append(e.GetType().Name).Append(">"); }
            return sb.ToString();
        }
        // ================= M6.10：research（让某台研究站去研究）=================
        //
        // 入口：`ResearchCenter.CreateChore() : Chore`（**public**；IL 已核：`new Chore(Db.Get().ChoreTypes.Research, …)` 并存进字段）
        // 语义：玩家在游戏里没有"标记研究"这个动作 —— 研究是"当前选中的科技 + 研究站"共同驱动的。
        //       本 type 的语义是「**让这台研究站开始干活**」，不是"选科技"。
        // 失败真话：没有 ResearchCenter ⇒ `NOT_RESEARCH_CENTER`；CreateChore 返回 null ⇒ `READBACK_FAILED`。
        static string ResearchExecute(string body, out int status, out string code, out string message, out string details)
        {
            status = 200; code = null; message = null; details = null;

            // ★ M6.41：有 techId ⇒ 走研究目标分支，**完全不看 x/y**（否则会被坐标校验挡住）
            {
                string techIdReq0 = MatchString(body, "techId");
                if (!string.IsNullOrEmpty(techIdReq0))
                {
                    bool instReq0 = false; MatchBool(body, "instant", out instReq0);
                    if (instReq0) return ResearchApi.InstantComplete(techIdReq0, out status, out code, out message);
                    string rdata0 = ResearchApi.SetTarget(techIdReq0, out status, out code, out message);
                    details = null;
                    return rdata0;
                }
            }

            int x, y, worldId = -1;
            if (!TryInt(body, "x", out x) || !TryInt(body, "y", out y))
            { status = 400; code = "BAD_REQUEST"; message = "需要 x / y（世界内格子坐标）"; return null; }
            TryInt(body, "worldId", out worldId);
            if (!MainThread.Ready) { status = 503; code = "NOT_READY"; message = "main-thread pump is not running yet"; return null; }

            int cell = -1, foundWorld = -1;
            string prefabId = null, failCode = null, failMsg = null;
            bool choreCreated = false;
            int? objects = null; int okLayers = 0, errLayers = 0; string scanErr = null;

            MainThread.Invoke(() =>
            {
                try
                {
                    cell = Grid.XYToCell(x, y);
                    if (!Grid.IsValidCell(cell)) { failCode = "BAD_TARGET"; failMsg = "格子无效：" + x + "," + y; return; }
                    foundWorld = Grid.WorldIdx != null ? Grid.WorldIdx[cell] : -1;
                    if (worldId >= 0 && foundWorld != worldId)
                    { failCode = "BAD_TARGET"; failMsg = "worldId 不符：请求 " + worldId + "，该格属于 " + foundWorld; return; }

                    objects = CountObjectsAt(cell, out okLayers, out errLayers, out scanErr);

                    var b = FindResearchCenterAt(cell);   // ★ 修：按组件找（同格可能有 Battery/Wire 占先）
                    var rc = b != null ? b.GetComponent<ResearchCenter>() : null;
                    if (rc == null)
                    {
                        failCode = "NOT_RESEARCH_CENTER";
                        failMsg = b == null ? "该格上没有建筑" : "该格的建筑不是研究站（无 ResearchCenter）";
                        return;
                    }
                    try { prefabId = b.Def != null ? b.Def.PrefabID : null; } catch { }

                    // ⚠️ ResearchCenter.CreateChore() 实测 **不可访问**（CS0122：protected/private），
                    //    元数据 dump 显示 public 是假象（与 Harvestable.chore 同一类）。
                    //    ⇒ 不硬做（不用反射去碰一个"内部构造 chore"的方法），如实报未实现。
                    failCode = "NOT_IMPLEMENTED";
                    failMsg = "ResearchCenter.CreateChore() 不是 public（编译报 CS0122；元数据 dump 显示 public 不可信）"
                              + " ⇒ research 暂不实现。需要的话只能用反射，风险未评估";
                }
                catch (System.Exception e) { failCode = "INTERNAL"; failMsg = e.GetType().Name + ": " + e.Message; }
            }, 5000);

            if (failCode != null)
            {
                status = failCode == "INTERNAL" ? 500 : 400;
                code = failCode; message = failMsg;
                Log.Info("[job] type=research x=" + x + " y=" + y + " cell=" + cell + " ⇒ " + status + " " + failCode + ": " + failMsg);
                return null;
            }

            var sb = new StringBuilder(512);
            sb.Append("{\"type\":\"research\"");
            sb.Append(",\"accepted\":").Append(choreCreated ? "true" : "false");
            sb.Append(",\"jobState\":").Append(Snap.Str(choreCreated ? "chore-created" : "not-applied"));
            sb.Append(",\"target\":{\"worldId\":").Append(foundWorld).Append(",\"x\":").Append(x)
              .Append(",\"y\":").Append(y).Append(",\"cell\":").Append(cell)
              .Append(",\"prefabId\":").Append(Snap.Str(prefabId)).Append('}');
            sb.Append(",\"choreCreated\":").Append(choreCreated ? "true" : "false");
            sb.Append(",\"objectsAtCell\":").Append(objects.HasValue ? objects.Value.ToString() : "null");
            sb.Append(",\"note\":").Append(Snap.Str(
                "调用了 ResearchCenter.CreateChore()（public，IL 已核会 new Chore(ChoreTypes.Research)）；"
                + "choreCreated 取自它的返回值，未在游戏里观察小人是否领活"));
            sb.Append('}');

            Log.Info("[job] type=research x=" + x + " y=" + y + " cell=" + cell + " prefab=" + (prefabId ?? "<null>")
                     + " choreCreated=" + choreCreated + " objects=" + (objects.HasValue ? objects.Value.ToString() : "<null>"));
            return sb.ToString();
        }

        // ================= M6.11：build（**第一步只做校验**）=================
        //
        // 入口（已核 public）：
        //   `BuildingDef.IsValidPlaceLocation(GameObject, int cell, Orientation, ref string reason)`
        //   `BuildingDef.Build(int cell, Orientation, Storage, IList<Tag> materials, float, string, bool, float)`
        // 本版只实现 `validateOnly`（**不改游戏状态**）：
        //   * `valid:true/false` + **原样返回游戏给的 reason**（`ref string`）
        //   * `confirm:true` ⇒ **真放置**：走 `BuildTool.Activate(def, def.DefaultElements())` + `OnLeftClickDown(Grid.CellToPos(cell))`
        //     （玩家同一条建造路径；校验未过则拒绝放置，报 `BUILD_INVALID`）
        // ⚠️ 校验本身不改存档；误报"能建"的代价比误建小得多。
        /// <summary>
        /// 这个 prefab 是不是"可与建筑共格"的管线/电线类。
        ///
        /// ★ 0.97.3 修 bug1：ONI 的标准接法是"电线/管道直接铺在机器所在格内"
        ///   （Wire 与 Building 各占不同 ObjectLayer，同格共存是常态）。
        ///   原来的占用预检扫**所有层**找 Building/BuildingUnderConstruction ⇒
        ///   在机器格放 Wire 永远 409 CELL_OCCUPIED ⇒ 电网建不起来 ⇒ 氧气扩散器/打浆机/
        ///   充电器/制作台全不可用（早期电力-氧气-食物循环被整个堵死）。
        ///   ⇒ 管线/电线类**跳过占用预检**，交给游戏自己的 IsValidPlaceLocation 判。
        /// </summary>
        static bool IsUtilityLayerPrefab(BuildingDef def)
        {
            if (def == null) return false;
            try
            {
                ObjectLayer ol = def.ObjectLayer;
                return ol == ObjectLayer.Wire
                    || ol == ObjectLayer.LogicWire
                    || ol == ObjectLayer.GasConduit
                    || ol == ObjectLayer.LiquidConduit
                    || ol == ObjectLayer.SolidConduit
                    || ol == ObjectLayer.TravelTube;
            }
            catch { return false; }
        }
        /// <summary>
        /// `build` + `wirePath`：沿路径逐格建线，**建完立刻按游戏配方双向连线**
        /// （`UtilityBuildTool.ApplyPathToConduitSystem` 的 IL 配方，见 `WireOps`）。
        /// 走这条而不是"先建后补 /v1/wire"，是因为分两步时游戏可能已经把孤岛网络算过一轮。
        /// 单格路径不连线（与游戏一致）。
        /// </summary>
        static string BuildWirePath(string body, out int status, out string code, out string message, out string details)
        {
            status = 200; code = null; message = null; details = null;

            string prefabId = MatchString(body, "prefabId");
            if (string.IsNullOrEmpty(prefabId)) prefabId = "Wire";

            string matsText = null;
            {
                int mi = body.IndexOf("\"materials\"", System.StringComparison.Ordinal);
                if (mi >= 0)
                {
                    int lb = body.IndexOf('[', mi);
                    if (lb >= 0)
                    {
                        int depth = 0;
                        for (int i = lb; i < body.Length; i++)
                        {
                            if (body[i] == '[') depth++;
                            else if (body[i] == ']') { depth--; if (depth == 0) { matsText = body.Substring(lb, i - lb + 1); break; } }
                        }
                    }
                }
            }

            // 解析 wirePath: [[x,y],[x,y],...]
            var pts = new System.Collections.Generic.List<int[]>();
            {
                int k = body.IndexOf("\"wirePath\"", System.StringComparison.Ordinal);
                if (k < 0) { status = 400; code = "BAD_REQUEST"; message = "缺 wirePath"; return null; }
                int lb = body.IndexOf('[', k);
                int depth = 0, end = -1;
                for (int i = lb; i < body.Length; i++)
                {
                    if (body[i] == '[') depth++;
                    else if (body[i] == ']') { depth--; if (depth == 0) { end = i; break; } }
                }
                if (end < 0) { status = 400; code = "BAD_REQUEST"; message = "wirePath 括号不匹配"; return null; }
                string arr = body.Substring(lb, end - lb + 1);
                int p = 0;
                while (p < arr.Length)
                {
                    int s = arr.IndexOf('[', p);
                    if (s < 0) break;
                    int e = arr.IndexOf(']', s);
                    if (e < 0) break;
                    string inner = arr.Substring(s + 1, e - s - 1);
                    var parts = inner.Split(',');
                    if (parts.Length >= 2)
                    {
                        int x, y;
                        if (int.TryParse(parts[0].Trim(), out x) && int.TryParse(parts[1].Trim(), out y))
                            pts.Add(new int[] { x, y });
                    }
                    p = e + 1;
                }
            }
            if (pts.Count == 0) { status = 400; code = "BAD_REQUEST"; message = "wirePath 为空"; return null; }

            var cells = new System.Collections.Generic.List<int>(pts.Count);
            var sb = new System.Text.StringBuilder(1024);
            sb.Append("{\"type\":\"build\",\"wirePath\":true,\"prefabId\":").Append(Snap.Str(prefabId));
            sb.Append(",\"cells\":[");
            int okN = 0, failN = 0;
            for (int i = 0; i < pts.Count; i++)
            {
                int x = pts[i][0], y = pts[i][1];
                string sub = "{\"type\":\"build\",\"prefabId\":" + Snap.Str(prefabId) + ",\"x\":" + x + ",\"y\":" + y + ",\"confirm\":true";
                if (matsText != null) sub += ",\"materials\":" + matsText;
                sub += "}";

                int st2; string c2, m2, d2;
                string one = BuildExecute(sub, out st2, out c2, out m2, out d2);
                int cc = Grid.XYToCell(x, y);
                bool placed = one != null && one.IndexOf("\"placed\":true", System.StringComparison.Ordinal) >= 0;
                if (placed) { okN++; cells.Add(cc); } else failN++;
                if (i > 0) sb.Append(',');
                sb.Append("{\"x\":").Append(x).Append(",\"y\":").Append(y)
                  .Append(",\"ok\":").Append(placed ? "true" : "false");
                if (!placed) sb.Append(",\"error\":").Append(Snap.Str((c2 ?? "ERROR") + ": " + (m2 ?? "")));
                sb.Append('}');
            }
            sb.Append(']');

            // 建完立刻连线（同一请求内，最接近玩家拖拽的时序）
            string pathDiag = null;
            if (cells.Count >= 2)
            {
                int okR; bool busyR; string diag = null;
                bool ran = MainThread.InvokeAdaptive(() => { diag = WireOps.ConnectPath(cells); }, 3000, 15000, out okR, out busyR);
                pathDiag = ran ? diag : "MAIN_THREAD_BUSY";
            }
            else pathDiag = "SINGLE_CELL(单格不连线，与游戏一致)";

            sb.Append(",\"placedOk\":").Append(okN).Append(",\"placedFailed\":").Append(failN);
            sb.Append(",\"pathDiag\":").Append(Snap.Str(pathDiag));
            sb.Append(",\"note\":").Append(Snap.Str("沿 wirePath 逐格建线并立刻双向连线（照抄 UtilityBuildTool.ApplyPathToConduitSystem）；单格不连"));
            sb.Append('}');
            JsonCheck.Validate(sb.ToString(), "build-wirepath");
            return sb.ToString();
        }
        static string BuildExecute(string body, out int status, out string code, out string message, out string details)
        {
            status = 200; code = null; message = null; details = null;

            int x, y, worldId = -1;
            string prefabId = MatchString(body, "prefabId");
            Log.Info("[build] 参数 prefabId=[" + (prefabId ?? "<null>") + "] validateOnly=[" + MatchString(body, "validateOnly") + "] confirm=[" + MatchString(body, "confirm") + "] orientation=[" + (MatchString(body, "orientation") ?? "<null>") + "]");
            bool validateOnly = false, confirm = false, forcePlace = false;
            string orientationReq = MatchString(body, "orientation");
            string placeMode = MatchString(body, "placeMode");
            string setFields = MatchString(body, "setFields");   // 逗号分隔：lastDragCell / downPos / lastCell（按需逐步加，便于单独验证）
            // materials: JSON 数组，如 ["Algae"] —— 显式指定建造材料（不用 def.DefaultElements() 猜）
            var matNames = new System.Collections.Generic.List<string>();
            {
                var mm = System.Text.RegularExpressions.Regex.Match(body ?? "", "\"materials\"\\s*:\\s*\\[([^\\]]*)\\]");
                if (mm.Success)
                {
                    foreach (System.Text.RegularExpressions.Match q in System.Text.RegularExpressions.Regex.Matches(mm.Groups[1].Value, "\"([^\"]+)\""))
                        matNames.Add(q.Groups[1].Value);
                }
            }
            if (string.IsNullOrEmpty(placeMode)) placeMode = "trybuild";   // 默认 trybuild（真建造：反射 BuildTool.TryBuild）
            // ★ 未知值必须报错：绝不静默回落（0.52.0 那次就是这么骗过我们的）
            if (placeMode != "trybuild" && placeMode != "instant" && placeMode != "defbuild" && placeMode != "dragtool")
            {
                status = 400; code = "BAD_REQUEST";
                message = "unknown placeMode: " + placeMode + "（合法值：trybuild / instant / defbuild / dragtool）";
                Log.Info("[job] type=build 未知 placeMode=" + placeMode);
                return null;
            }
            Orientation orientation = Orientation.Neutral;   // 默认不分朝向
            if (!string.IsNullOrEmpty(orientationReq))
            {
                switch (orientationReq.Trim().ToLowerInvariant())
                {
                    case "neutral": orientation = Orientation.Neutral; break;
                    case "r90": orientation = Orientation.R90; break;
                    case "r180": orientation = Orientation.R180; break;
                    case "r270": orientation = Orientation.R270; break;
                    case "fliph": orientation = Orientation.FlipH; break;
                    case "flipv": orientation = Orientation.FlipV; break;
                    default:
                        status = 400; code = "BAD_REQUEST";
                        message = "orientation 只允许 Neutral/R90/R180/R270/FlipH/FlipV，收到 " + orientationReq;
                        return null;
                }
            }
            try
            {
                bool voFound = MatchBool(body, "validateOnly", out validateOnly);
                bool cfFound = MatchBool(body, "confirm", out confirm);
            }
            catch { }

            if (string.IsNullOrEmpty(prefabId))
            { status = 400; code = "BAD_REQUEST"; message = "需要 prefabId（例如 \"Tile\"）"; return null; }
            if (!TryInt(body, "x", out x) || !TryInt(body, "y", out y))
            { status = 400; code = "BAD_REQUEST"; message = "需要 x / y（世界内格子坐标）"; return null; }
            TryInt(body, "worldId", out worldId);

            if (!validateOnly && !confirm)
            {
                status = 400; code = "CONFIRM_REQUIRED";
                message = "真建造会改存档，必须显式 \"confirm\": true；只想问能不能建请用 \"validateOnly\": true";
                return null;
            }
            if (!MainThread.Ready) { status = 503; code = "NOT_READY"; message = "main-thread pump is not running yet"; return null; }

            int cell = -1, foundWorld = -1;
            string occupiedPrefab = null; bool occupiedUnderConstruction = false; int occupiedCell = -1;
            bool found = false, valid = false, buildable = true;
            int footprintW = 1, footprintH = 1;   // 建筑占地尺寸（WidthInCells × HeightInCells）；多格建筑总是输出
            string gameReason = null, defName = null, failCode = null, failMsg = null, buildBlockedReason = null, placementMissing = null;

            MainThread.Invoke(() =>
            {
                try
                {
                    cell = Grid.XYToCell(x, y);
                    if (!Grid.IsValidCell(cell)) { failCode = "BAD_TARGET"; failMsg = "格子无效：" + x + "," + y; return; }
                    foundWorld = Grid.WorldIdx != null ? Grid.WorldIdx[cell] : -1;
                    if (worldId >= 0 && foundWorld != worldId)
                    { failCode = "BAD_TARGET"; failMsg = "worldId 不符：请求 " + worldId + "，该格属于 " + foundWorld; return; }

                    BuildingDef def = null;
                    try { def = Assets.GetBuildingDef(prefabId); } catch { }
                    if (def == null)
                    {
                        failCode = "UNKNOWN_PREFAB";
                        failMsg = "没有这个建筑 prefabId：" + prefabId + "（可查 GET /v1/catalog）";
                        return;
                    }
                    found = true;
                    try { defName = Catalog.PlainText(def.Name); } catch { }   // 剥掉 <link="TILE"> 这类游戏富文本

                    GameObject preview = null;
                    try { preview = def.BuildingPreview; } catch { }
                    if (preview == null)
                    {
                        failCode = "VALIDATE_UNAVAILABLE";
                        failMsg = "该 BuildingDef 没有 BuildingPreview，无法用 IsValidPlaceLocation 校验（不猜）";
                        return;
                    }
                    string reason = null;
                    // ★ M6.58：占地占用判定提到 IsValidPlaceLocation **之前**
                    //   否则游戏会先报 BUILD_INVALID「必须建在未占用的空间中」，调用方拿不到更精确的 CELL_OCCUPIED。
                    //   顺序：CELL_OCCUPIED → IsValidPlaceLocation → 材料（只报数）→ TryBuild
                    {
                        int dwP = 1, dhP = 1;
                        try { dwP = def.WidthInCells; dhP = def.HeightInCells; } catch { }
                        if (dwP <= 0) dwP = 1; if (dhP <= 0) dhP = 1;
                        footprintW = dwP; footprintH = dhP;
                        int lnP = (int)ObjectLayer.NumLayers;
                        for (int oy = 0; oy < dhP && occupiedCell < 0; oy++)
                        {
                            for (int ox = 0; ox < dwP && occupiedCell < 0; ox++)
                            {
                                int ccP = Grid.XYToCell(x + ox, y + oy);
                                if (!Grid.IsValidCell(ccP)) continue;
                                for (int ly = 0; ly < lnP; ly++)
                                {
                                    UnityEngine.GameObject gP = null;
                                    try { gP = Grid.Objects[ccP, ly]; } catch { continue; }
                                    if (gP == null) continue;
                                    try
                                    {
                                        var ucP = gP.GetComponent<BuildingUnderConstruction>();
                                        var bcP = gP.GetComponent<Building>();
                                        if (ucP == null && bcP == null) continue;
                                        occupiedCell = ccP;
                                        if (ucP != null) { occupiedUnderConstruction = true; try { occupiedPrefab = ucP.Def != null ? ucP.Def.PrefabID : null; } catch { } }
                                        else { try { occupiedPrefab = bcP.Def != null ? bcP.Def.PrefabID : null; } catch { } }
                                        break;
                                    }
                                    catch { }
                                }
                            }
                        }
                        // ★ 0.97.3：管线/电线类允许与建筑共格（ONI 标准接法），跳过占用判定
                        if (occupiedCell >= 0 && IsUtilityLayerPrefab(def))
                        {
                            occupiedCell = -1; occupiedPrefab = null; occupiedUnderConstruction = false;
                        }
                        if (occupiedCell >= 0)
                        {
                            failCode = "CELL_OCCUPIED";
                            failMsg = "目标格已被占用：cell=" + occupiedCell + "，prefab=" + (occupiedPrefab ?? "<unknown>")
                                    + (occupiedUnderConstruction ? "（施工中/蓝图）" : "（已完工）") + " ⇒ 不在已有建筑/蓝图上重复建造";
                            return;
                        }
                    }

                    valid = def.IsValidPlaceLocation(preview, cell, orientation, out reason);

                    // ★ 0.97.3 修 bug3（口径：**不拒、只报**）
                    //   `IsValidPlaceLocation` 只管"蓝图放不放得下"；"放下去建不建得成"另有一说。
                    //   盲测实录：人力发电机放在 CO₂ 格、下方无地面 ⇒ 游戏画面明写「无效的建造位置」
                    //   红叹号，而 build 回 placed:true ⇒ AI 以为成功再也不管它。
                    //   但游戏本身允许"先画草图后补条件"（实心岩石上放蓝图、挖通再建是自带功能），
                    //   拒掉会让 AI **没法规划** ⇒ 只放 + 报诊断（buildable / placementMissing / reason）。
                    try { buildable = def.IsValidBuildLocation(preview, Grid.CellToPos(cell), orientation, false); }
                    catch { buildable = true; }   // 取不到就不猜（按可建处理）
                    if (!buildable)
                    {
                        try
                        {
                            int solidInFoot = 0;
                            int dwn = 1, dhn = 1;
                            try { dwn = def.WidthInCells; dhn = def.HeightInCells; } catch { }
                            if (dwn <= 0) dwn = 1; if (dhn <= 0) dhn = 1;
                            for (int oy2 = 0; oy2 < dhn && oy2 < 4; oy2++)
                                for (int ox2 = 0; ox2 < dwn && ox2 < 4; ox2++)
                                {
                                    int cc2 = Grid.XYToCell(x + ox2, y + oy2);
                                    if (Grid.IsValidCell(cc2) && Grid.IsSolidCell(cc2)) solidInFoot++;
                                }
                            bool needsFoundation = false;
                            try { needsFoundation = def.ContinuouslyCheckFoundation; } catch { }
                            if (solidInFoot > 0)
                            {
                                placementMissing = "solid_cells";
                                buildBlockedReason = "占地位有 " + solidInFoot + " 格实心方块 ⇒ 需要先挖开这些格子";
                            }
                            else if (needsFoundation)
                            {
                                placementMissing = "ground_support";
                                buildBlockedReason = "该建筑需要下方实心地面支撑，现在下方是空的（悬空）";
                            }
                            else
                            {
                                placementMissing = "unknown";
                                buildBlockedReason = "游戏判定 IsValidBuildLocation=false（具体缺什么未能归类）";
                            }
                        }
                        catch { placementMissing = "unknown"; }
                    }
                    gameReason = reason;                     // ★ 原样返回游戏自己给的理由

            // ★ M6.47（据实测更正）：游戏自己的 `IsValidPlaceLocation` **已经校验整个占地**
            //   （实测：第二格有砖时它自己返回 valid:false + "必须建在未占用的空间中"），
            //   所以我们**不再补一遍占用检查**（补了也永远跑不到，是死代码）。
            //   这里只读出占地尺寸，供调用方理解"为什么 1x2 建不出来"。
            int dw = 1, dh = 1;
            try { dw = def.WidthInCells; dh = def.HeightInCells; } catch { }
            if (dw <= 0) dw = 1; if (dh <= 0) dh = 1;
            footprintW = dw; footprintH = dh;
                }
                catch (System.Exception e) { failCode = "INTERNAL"; failMsg = e.GetType().Name + ": " + e.Message; }
            }, 5000);

            if (failCode != null)
            {
                status = (failCode == "INTERNAL" || failCode == "VALIDATE_UNAVAILABLE") ? 500 : 400;
                if (failCode == "CELL_OCCUPIED") { status = 409; details = "{\"cell\":" + occupiedCell + ",\"prefabId\":" + Snap.Str(occupiedPrefab ?? "") + ",\"underConstruction\":" + (occupiedUnderConstruction ? "true" : "false") + "}"; }
                code = failCode; message = failMsg;
                Log.Info("[job] type=build x=" + x + " y=" + y + " prefab=" + (prefabId ?? "<null>")
                         + " ⇒ " + status + " " + failCode + ": " + failMsg);
                return null;
            }

            if (!validateOnly)
            {
                // ================= M6.29：放置（两条路，默认走"真建造"）=================
                //   默认 placeMode="trybuild"：反射调 BuildTool.TryBuild(cell)（protected/private，用户已批准用反射）
                //        —— IL 已核：BuildTool.OnDragTool → TryBuild 就是玩家放置时真正跑的方法
                //        ⇒ 会走真实建造流程：扣材料 + 产生建造 chore + 在建状态
                //   placeMode="dragtool"：反射调 BuildTool.OnDragTool(cell, 0)（备选）
                //   placeMode="defbuild"：BuildingDef.Build 直造（**作弊**：不扣材料、不产生 chore）⇒ 需作弊闸门
                // ⚠️ 校验未过 ⇒ 一律不放置
                if (!valid)
                {
                    status = 400; code = "BUILD_INVALID";
                    message = "校验未通过，拒绝放置" + (string.IsNullOrEmpty(gameReason) ? "" : "：" + gameReason);
                    Log.Info("[job] type=build confirm=true 但 valid=false ⇒ 拒绝放置");
                    return null;
                }

                bool placed = false, underConstruction = false, activated = false, methodInvoked = false;
                string pFail = null, pFailCode = null, placedPrefab = null;
                string invokeDetail = null;
                string fieldDump = null;
                string fieldsSetText = null, fieldsMissingText = null;
                string pStack = null;   // 反射调用的异常堆栈（截断）
                string invokeExc = null;   // 反射调用抛的异常（**不立刻判失败**：先回读，真有对象就算成功+warning）
                string matsDiag = null, matsUsedText = null;
                string instantDiag = null;   // InstantBuildMode 真实前后状态（自证）
                int foundCell = -1, foundX = -1, foundY = -1, searchRadius = 0;   // 多格建筑的真实锚点（可能不在请求格）
                string gateTrace = null;   // 我们复刻的 TryBuild 前置判据（不是游戏内部返回值，见 note）
                bool toolRestored = false; string restoreErr = null;   // 工具是否已还原（防"挂起状态"导致玩家点击崩溃）
                bool visMoved = false, visHadOriginal = false; int visFrom = -1, visTo = -1;
                UnityEngine.Vector3 visOriginalPos = default(UnityEngine.Vector3);
                double needKg = -1, haveKg = -1, groundKg = -1, totalKg = -1; string haveText = null, matCatText2 = null;
                int okB; bool busyB;

                // ---- defbuild 是作弊路径：先过闸门 ----
                if ((placeMode == "defbuild" || placeMode == "instant") && !Gate.CheatsUnlocked)
                {
                    status = 403; code = Gate.UnavailableCode();
                    message = "placeMode=" + placeMode + " 是作弊操作（直接生成/瞬间建成：不扣材料、不产生建造任务）⇒ " + Gate.UnavailableReason();
                    Log.Info("[job] type=build placeMode=" + placeMode + " ⇒ 403 " + code);
                    return null;
                }

                MainThread.InvokeAdaptive(() =>
                {
                    try
                    {
                        BuildingDef def2 = null;
                        try { def2 = Assets.GetBuildingDef(prefabId); } catch { }
                        if (def2 == null) { pFailCode = "UNKNOWN_PREFAB"; pFail = "没有这个 prefabId：" + prefabId; return; }

                        // ★ M6.51：目标格（及占地）上已有建筑/蓝图 ⇒ 直接 409 CELL_OCCUPIED
                        //   实测：往已有蓝图格发 trybuild ⇒ TryBuild 内部 NRE（PlanScreen 相关）；
                        //   被 catch 住不会崩游戏，但只报 REFLECT_INVOKE_FAILED 对调用方没信息量。
                        {
                            int dwPre = 1, dhPre = 1;
                            try { dwPre = def2.WidthInCells; dhPre = def2.HeightInCells; } catch { }
                            if (dwPre <= 0) dwPre = 1; if (dhPre <= 0) dhPre = 1;
                            int lnPre = (int)ObjectLayer.NumLayers;
                            for (int oy = 0; oy < dhPre && occupiedCell < 0; oy++)
                            {
                                for (int ox = 0; ox < dwPre && occupiedCell < 0; ox++)
                                {
                                    int ccPre = Grid.XYToCell(x + ox, y + oy);
                                    if (!Grid.IsValidCell(ccPre)) continue;
                                    for (int ly = 0; ly < lnPre; ly++)
                                    {
                                        UnityEngine.GameObject gPre = null;
                                        try { gPre = Grid.Objects[ccPre, ly]; } catch { continue; }
                                        if (gPre == null) continue;
                                        try
                                        {
                                            var ucPre = gPre.GetComponent<BuildingUnderConstruction>();
                                            var bcPre = gPre.GetComponent<Building>();
                                            if (ucPre == null && bcPre == null) continue;
                                            occupiedCell = ccPre;
                                            if (ucPre != null)
                                            {
                                                occupiedUnderConstruction = true;
                                                try { occupiedPrefab = ucPre.Def != null ? ucPre.Def.PrefabID : null; } catch { }
                                            }
                                            else
                                            {
                                                try { occupiedPrefab = bcPre.Def != null ? bcPre.Def.PrefabID : null; } catch { }
                                            }
                                            break;
                                        }
                                        catch { }
                                    }
                                }
                            }
                            // ★ 0.97.3：同上，管线/电线允许共格
                            if (occupiedCell >= 0 && IsUtilityLayerPrefab(def2))
                            {
                                occupiedCell = -1; occupiedPrefab = null; occupiedUnderConstruction = false;
                            }
                            if (occupiedCell >= 0)
                            {
                                pFailCode = "CELL_OCCUPIED";
                                pFail = "目标格已被占用：cell=" + occupiedCell
                                      + "，prefab=" + (occupiedPrefab ?? "<unknown>")
                                      + (occupiedUnderConstruction ? "（施工中/蓝图）" : "（已完工）")
                                      + " ⇒ 不在已有建筑/蓝图上重复建造";
                                return;
                            }
                        }

                        if (placeMode == "defbuild")
                        {
                            var mats2 = def2.DefaultElements();
                            var go2 = def2.Build(cell, Orientation.Neutral, null, mats2, 293.15f, null, false, 0f);
                            if (go2 == null) { pFailCode = "DEFBUILD_RETURNED_NULL"; pFail = "BuildingDef.Build 返回 null"; return; }
                        }
                        else
                        {
                            var tool = BuildTool.Instance;
                            if (tool == null) { pFailCode = "BUILD_TOOL_UNAVAILABLE"; pFail = "BuildTool.Instance 为 null"; return; }

                            // 1) 先选中该建筑（没有当前 def，TryBuild 大概率什么都不做）
                            // 材料：优先调用方指定的 materials，否则退回 def.DefaultElements()
                            var defMats = def2.DefaultElements();
                            string defMatsText = TagsToText(defMats);
                            string matCatText = null;
                            try
                            {
                                var mc = def2.MaterialCategory;
                                if (mc != null) matCatText = string.Join(",", mc);
                            }
                            catch { }

                            var mats = new System.Collections.Generic.List<Tag>();
                            string matsSource;
                            if (matNames.Count > 0)
                            {
                                matsSource = "request.materials";
                                for (int mi2 = 0; mi2 < matNames.Count; mi2++)
                                {
                                    try { mats.Add(TagManager.Create(matNames[mi2])); }
                                    catch { matsDiag = (matsDiag ?? "") + "创建 Tag 失败:" + matNames[mi2] + " "; }
                                }
                            }
                            else
                            {
                                matsSource = "def.DefaultElements()";
                                if (defMats != null) for (int mi3 = 0; mi3 < defMats.Count; mi3++) mats.Add(defMats[mi3]);
                            }
                            matsUsedText = TagsToText(mats);
                            matsDiag = "materialsSource=" + matsSource
                                     + "; used=[" + (matsUsedText ?? "") + "]"
                                     + "; defaultElements=[" + (defMatsText ?? "") + "]"
                                     + "; materialCategory=[" + (matCatText ?? "<null>") + "]"
                                     + (matsDiag != null ? "; " + matsDiag : "");
                            tool.Activate(def2, mats);

                            activated = true;

                            // ★ M6.62：**把建造预览挪到目标格**（1x2 的真正死因：预览停在别处 ⇒ 多格占地算不出来 ⇒ 静默不建）
                            //   1x1 不受影响（单格路径直接用传入的 cell），所以这个 bug 长期只对多格显现。
                            //   挪过之后在 finally 里**恢复原位**（不把玩家的预览丢在别处）。
                            if (placeMode != "defbuild")
                            {
                                try
                                {
                                    var fVisM = typeof(InterfaceTool).GetField("visualizer", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                                    var visM = fVisM != null ? fVisM.GetValue(tool) as UnityEngine.GameObject : null;
                                    if (visM != null && visM.transform != null)
                                    {
                                        visOriginalPos = visM.transform.position;
                                        visHadOriginal = true;
                                        try { visFrom = Grid.PosToCell(visM.transform.position); } catch { visFrom = -1; }
                                        if (visFrom != cell)
                                        {
                                            visM.transform.position = Grid.CellToPos(cell);
                                            visMoved = true;
                                            try { visTo = Grid.PosToCell(visM.transform.position); } catch { visTo = -1; }
                                        }
                                    }
                                }
                                catch (System.Exception exVis) { gateTrace = (gateTrace ?? "") + "; visMoveErr=" + exVis.GetType().Name; }
                            }

                            // ★ M6.31：按需设置拖拽状态字段（用户要求一个一个来，避免"改多个变量分不清"）
                            if (placeMode == "instant" && string.IsNullOrEmpty(setFields)) setFields = "visualizerPos";   // instant 也需要预览就位
                            if (!string.IsNullOrEmpty(setFields))
                            {
                                var fs = setFields.Split(new[] { ',' }, System.StringSplitOptions.RemoveEmptyEntries);
                                var setSb = new StringBuilder(128);
                                var missSb = new StringBuilder(128);
                                var positions = Grid.CellToPos(cell);
                                for (int fi = 0; fi < fs.Length; fi++)
                                {
                                    string fn = fs[fi].Trim();
                                    if (fn.Length == 0) continue;

                                    // ---- 特殊 token（IL 已核：TryBuild 用 visualizer 的位置判断目标格）----
                                    if (fn == "visualizerPos")
                                    {
                                        try
                                        {
                                            var vf = typeof(InterfaceTool).GetField("visualizer", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                                            var vis = vf != null ? vf.GetValue(tool) as UnityEngine.GameObject : null;
                                            if (vis != null && vis.transform != null)
                                            {
                                                vis.transform.position = Grid.CellToPos(cell);
                                                setSb.Append("visualizerPos ");
                                            }
                                            else missSb.Append("visualizerPos(visualizer null) ");
                                        }
                                        catch (System.Exception ex) { missSb.Append("visualizerPos(").Append(ex.GetType().Name).Append(") "); }
                                        continue;
                                    }
                                    if (fn == "dragCellPrev" || fn == "resetDrag")
                                    {
                                        try
                                        {
                                            var df = typeof(BuildTool).GetField("lastDragCell", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                                            if (df != null)
                                            {
                                                df.SetValue(tool, fn == "resetDrag" ? (object)(-1) : (object)(cell - 1));
                                                setSb.Append(fn).Append(" ");
                                            }
                                            else missSb.Append(fn).Append("(field null) ");
                                        }
                                        catch (System.Exception ex) { missSb.Append(fn).Append("(").Append(ex.GetType().Name).Append(") "); }
                                        continue;
                                    }
                                    var ff = typeof(BuildTool).GetField(fn,
                                        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                                    if (ff == null) { missSb.Append(fn).Append(" "); continue; }
                                    try
                                    {
                                        object val;
                                        if (ff.FieldType == typeof(int)) val = cell;
                                        else if (ff.FieldType == typeof(UnityEngine.Vector3)) val = positions;
                                        else { missSb.Append(fn).Append("(type ").Append(ff.FieldType.Name).Append(") "); continue; }
                                        ff.SetValue(tool, val);
                                        setSb.Append(fn).Append(" ");
                                    }
                                    catch (System.Exception ex) { missSb.Append(fn).Append("(").Append(ex.GetType().Name).Append(") "); }
                                }
                                fieldsSetText = setSb.ToString().Trim();
                                fieldsMissingText = missSb.ToString().Trim();
                            }

                            // ★ 诊断（M6.30）：把 BuildTool 的实例字段读出来，看 TryBuild 的前置哪个是 null
                            try { fieldDump = ReflectDump(tool); } catch { fieldDump = "<dump failed>"; }

                            // 2) 反射调用（用户已批准；GetMethod 返回 null 必须明确报错，不静默）

                            // ★ 复刻 TryBuild 开头三条判据（IL: visualizer null / 去重 / visualizer 位置 == cell）
                            try
                            {
                                bool visNull = false; bool visPosMatches = false; bool dedupHit = false;
                                int visCell = -1;
                                var vf2 = typeof(InterfaceTool).GetField("visualizer", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                                var vis2 = vf2 != null ? vf2.GetValue(tool) as UnityEngine.GameObject : null;
                                visNull = vis2 == null;
                                if (vis2 != null && vis2.transform != null)
                                {
                                    visCell = Grid.PosToCell(vis2.transform.position);
                                    visPosMatches = (visCell == cell);
                                }
                                int lastDrag = -1;
                                var df2 = typeof(BuildTool).GetField("lastDragCell", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                                if (df2 != null) { try { lastDrag = (int)df2.GetValue(tool); } catch { } }
                                dedupHit = (lastDrag == cell);
                                bool defNull = false;
                                var dfDef = typeof(BuildTool).GetField("def", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                                if (dfDef != null) { try { defNull = dfDef.GetValue(tool) == null; } catch { } }
                                bool instNow = false;
                                try { instNow = DebugHandler.InstantBuildMode; } catch { }
                                gateTrace = "visualizerNull=" + visNull
                                           + "; visualizerCell=" + visCell
                                           + "; visPosMatches=" + visPosMatches
                                           + "; lastDragCell=" + lastDrag + "; dedupHit=" + dedupHit
                                           + "; defNull=" + defNull
                                           + "; instantBuild(设之前)=" + instNow
                                           + "; placeMode=" + placeMode;

                                // ★ M6.59：把"到底哪个对象是 null"直接报出来（纯读取，不改行为）
                                try
                                {
                                    var ps = PlanScreen.Instance;
                                    gateTrace += "; planScreenInstanceNull=" + (ps == null ? "true" : "false");
                                    if (ps != null)
                                    {
                                        try { gateTrace += "; planScreenInstanceId=" + ps.GetInstanceID(); } catch { gateTrace += "; planScreenInstanceId=<err>"; }
                                        try
                                        {
                                            var prio = ps.GetBuildingPriority();
                                            gateTrace += "; planScreenPriorityProbe=OK(" + (prio.ToString()) + ")";
                                        }
                                        catch (System.Exception exPri)
                                        {
                                            string st0 = "";
                                            try { var s2 = exPri.StackTrace; if (!string.IsNullOrEmpty(s2)) { var ls = s2.Split((char)10); st0 = ls[0].Trim(); if (ls.Length > 1) st0 += " | " + ls[1].Trim(); } } catch { }
                                            gateTrace += "; planScreenPriorityProbe=THREW:" + exPri.GetType().Name + "@" + st0;
                                        }
                                    }
                                    else gateTrace += "; planScreenPriorityProbe=SKIPPED(instance null)";

                                    // 你怀疑的两个"异常值"字段（DragTool 上的）
                                    try
                                    {
                                        var fAV = typeof(DragTool).GetField("areaVisualizer", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                                        var av = fAV != null ? fAV.GetValue(tool) as UnityEngine.GameObject : null;
                                        gateTrace += "; areaVisualizerNull=" + (av == null ? "true" : "false");
                                    }
                                    catch { }
                                    try
                                    {
                                        var fAVS = typeof(DragTool).GetField("areaVisualizerSpriteRenderer", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                                        var avs = fAVS != null ? fAVS.GetValue(tool) : null;
                                        gateTrace += "; areaVisualizerSpriteRendererNull=" + (avs == null ? "true" : "false");
                                    }
                                    catch { gateTrace += "; areaVisualizerSpriteRendererNull=<err>"; }
                                    try
                                    {
                                        var fAVT = typeof(DragTool).GetField("areaVisualizerText", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                                        var avt = fAVT != null ? fAVT.GetValue(tool) : null;
                                        gateTrace += "; areaVisualizerText=" + (avt != null ? avt.ToString() : "<null>");
                                    }
                                    catch { }
                                    gateTrace += "; postProcessOffset=+0x4d";
                                    gateTrace += "; visualizerMoved=" + visMoved + "; visualizerFrom=" + visFrom + "; visualizerTo=" + visTo;

                                    // ★ M6.60：最后一个布尔值 —— PriorityScreen.Instance 是否 null
                                    try
                                    {
                                        // PriorityScreen **没有 static Instance** —— 实例挂在 ToolMenu 上（ToolMenu.PriorityScreen，public 属性）
                                        PriorityScreen prs = null;
                                        try
                                        {
                                            var tm = ToolMenu.Instance;
                                            gateTrace += "; toolMenuInstanceNull=" + (tm == null ? "true" : "false");
                                            if (tm != null)
                                            {
                                                prs = tm.PriorityScreen;
                                                gateTrace += "; priorityScreenNull=" + (prs == null ? "true" : "false");
                                            }
                                        }
                                        catch (System.Exception exTM) { gateTrace += "; priorityScreenProbeErr=" + exTM.GetType().Name; }
                                        if (prs != null)
                                        {
                                            try { gateTrace += "; priorityScreenInstanceId=" + prs.GetInstanceID(); } catch { }
                                            try
                                            {
                                                var fLast = typeof(PriorityScreen).GetField("lastSelectedPriority", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                                                var last = fLast != null ? fLast.GetValue(prs) : null;
                                                gateTrace += "; priorityScreenLastSelected=" + (last != null ? last.ToString() : "<null>");
                                            }
                                            catch { }
                                            try
                                            {
                                                var fPri = typeof(PriorityScreen).GetField("priority", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                                                var pr = fPri != null ? fPri.GetValue(prs) : null;
                                                gateTrace += "; priorityScreenPriority=" + (pr != null ? pr.ToString() : "<null>");
                                            }
                                            catch { }
                                            try
                                            {
                                                var lsp = prs.GetLastSelectedPriority();
                                                gateTrace += "; priorityScreenGetLastSelected=OK";
                                            }
                                            catch (System.Exception exG)
                                            {
                                                string st1 = "";
                                                try { var s3 = exG.StackTrace; if (!string.IsNullOrEmpty(s3)) { var ls2 = s3.Split((char)10); st1 = ls2[0].Trim(); } } catch { }
                                                gateTrace += "; priorityScreenGetLastSelected=THREW:" + exG.GetType().Name + "@" + st1;
                                            }
                                        }
                                    }
                                    catch (System.Exception exPS) { gateTrace += "; priorityScreenProbeFailed=" + exPS.GetType().Name; }
                                }
                                catch (System.Exception exProbe) { gateTrace += "; probeFailed=" + exProbe.GetType().Name; }
                            }
                            catch (System.Exception exT) { gateTrace = "<trace failed: " + exT.GetType().Name + ">"; }
                            string methodName = (placeMode == "dragtool") ? "OnDragTool" : "TryBuild";
                            var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public;
                            System.Reflection.MethodInfo mi = null;
                            if (methodName == "TryBuild")
                            {
                                mi = typeof(BuildTool).GetMethod("TryBuild", flags, null, new System.Type[] { typeof(int) }, null);
                            }
                            else
                            {
                                mi = typeof(BuildTool).GetMethod("OnDragTool", flags, null, new System.Type[] { typeof(int), typeof(int) }, null);
                            }
                            if (mi == null)
                            {
                                pFailCode = "REFLECT_METHOD_NOT_FOUND";
                                pFail = "反射找不到 BuildTool." + methodName + "（BindingFlags=" + flags + "，已查 int / int,int 两种签名）";
                                return;
                            }
                            try
                            {
                                if (placeMode == "instant")
                                {
                                    bool oldInstant = false;
                                    try { oldInstant = DebugHandler.InstantBuildMode; } catch { }
                                    try { DebugHandler.InstantBuildMode = true; }
                                    catch (System.Exception ex0) { pFailCode = "INSTANT_TOGGLE_FAILED"; pFail = "无法设置 DebugHandler.InstantBuildMode：" + ex0.GetType().Name + ": " + ex0.Message; return; }
                                    try { instantDiag = "before=" + oldInstant + "; setToTrueReadBack=" + DebugHandler.InstantBuildMode; } catch { }
                                    // gateTrace 必须在【设成 true 之后、Invoke 之前】重新算一次，否则读到的是设之前的值
                                    gateTrace = (gateTrace ?? "") + "; instantBuildAtInvoke=" + DebugHandler.InstantBuildMode;
                                    try
                                    {
                                        if (methodName == "TryBuild") mi.Invoke(tool, new object[] { cell });
                                        else mi.Invoke(tool, new object[] { cell, 0 });
                                        methodInvoked = true;
                                        gateTrace = (gateTrace ?? "") + "; instantBuildAfterInvoke=" + DebugHandler.InstantBuildMode;
                                        {
                                            bool anyAtCell = false;
                                            int lc = (int)ObjectLayer.NumLayers;
                                            for (int lx = 0; lx < lc; lx++)
                                            {
                                                UnityEngine.GameObject gx = null;
                                                try { gx = Grid.Objects[cell, lx]; } catch { continue; }
                                                if (gx == null) continue;
                                                try { var gb = gx.GetComponent<Building>(); var gc = gx.GetComponent<BuildingUnderConstruction>(); string pid = gb != null && gb.Def != null ? gb.Def.PrefabID : (gc != null && gc.Def != null ? gc.Def.PrefabID : null); if (pid != null && string.Equals(pid, prefabId, System.StringComparison.OrdinalIgnoreCase)) { anyAtCell = true; break; } } catch { }
                                            }
                                            gateTrace = (gateTrace ?? "") + "; builtItemInTrueWindow=" + anyAtCell;
                                        }
                                    }
                                    finally { try { DebugHandler.InstantBuildMode = oldInstant; } catch { } try { instantDiag = (instantDiag ?? "") + "; restoredTo=" + DebugHandler.InstantBuildMode; } catch { } }
                                }
                                else
                                {
                                    if (methodName == "TryBuild") mi.Invoke(tool, new object[] { cell });
                                    else mi.Invoke(tool, new object[] { cell, 0 });
                                    methodInvoked = true;
                                }
                            }
                            catch (System.Exception ex)
                            {
                                var inner = ex.InnerException ?? ex;
                                invokeExc = methodName + " 调用抛异常：" + inner.GetType().Name + ": " + inner.Message;
                                // ★ 不立刻 return：先回读，若对象已建出来 ⇒ 那是"后处理炸了但建造成功"，按成功返回 + warning
                                try
                                {
                                    string st = inner.ToString();
                                    if (st != null && st.Length > 3000) st = st.Substring(0, 3000) + "…";
                                    pStack = st;
                                }
                                catch { }
                            }
                        }


                        // ★ 材料需求/持有统计（把"材料不足"从"其它原因"里分出来）
                        try
                        {
                            BuildingDef defM = null;
                            try { defM = Assets.GetBuildingDef(prefabId); } catch { }
                            if (defM != null)
                            {
                                try { if (defM.Mass != null && defM.Mass.Length > 0) needKg = defM.Mass[0]; } catch { }
                                if (matNames.Count > 0)
                                {
                                    double sum = 0;
                                    var haveSb = new StringBuilder(128);
                                    var buildsM = Components.BuildingCompletes != null ? Components.BuildingCompletes.Items : null;
                                    for (int ti = 0; ti < matNames.Count; ti++)
                                    {
                                        Tag tg = default(Tag);
                                        bool okTag = false;
                                        try { tg = TagManager.Create(matNames[ti]); okTag = true; } catch { }
                                        if (!okTag) continue;
                                        double perTag = 0;
                                        if (buildsM != null)
                                        {
                                            for (int bi = 0; bi < buildsM.Count; bi++)
                                            {
                                                var bb = buildsM[bi];
                                                if (bb == null) continue;
                                                UnityEngine.Component[] cs = null;
                                                try { cs = bb.GetComponentsInChildren<Storage>(); } catch { }
                                                if (cs == null) continue;
                                                for (int ci = 0; ci < cs.Length; ci++)
                                                {
                                                    var st2 = cs[ci] as Storage;
                                                    if (st2 == null) continue;
                                                    float amt = 0f;
                                                    try { amt = st2.GetAmountAvailable(tg); } catch { }
                                                    perTag += amt;
                                                }
                                            }
                                        }
                                        sum += perTag;
                                        if (haveSb.Length > 0) haveSb.Append(",");
                                        haveSb.Append(matNames[ti]).Append("=").Append(perTag.ToString("0.###"));
                                    }
                                    haveKg = sum;

                                    // ★ 地上散落（小人能捡起来用的）—— 与 /v1/inventory 同口径
                                    double groundSum = 0;
                                    try
                                    {
                                        var picks = Components.Pickupables != null ? Components.Pickupables.Items : null;
                                        if (picks != null)
                                        {
                                            for (int pi = 0; pi < picks.Count; pi++)
                                            {
                                                var pp = picks[pi];
                                                if (pp == null) continue;
                                                bool inStore = false;
                                                try { inStore = pp.storage != null; } catch { }
                                                if (inStore) continue;
                                                UnityEngine.GameObject pgo = null;
                                                try { pgo = pp.gameObject; } catch { }
                                                if (pgo == null) continue;
                                                PrimaryElement ppe = null;
                                                try { ppe = pgo.GetComponent<PrimaryElement>(); } catch { }
                                                if (ppe == null) continue;
                                                string ptname = null;
                                                try { ptname = ppe.ElementID.ToString(); } catch { }
                                                if (string.IsNullOrEmpty(ptname)) continue;
                                                bool hit = false;
                                                for (int ti2 = 0; ti2 < matNames.Count; ti2++)
                                                {
                                                    if (string.Equals(ptname, matNames[ti2], System.StringComparison.OrdinalIgnoreCase)) { hit = true; break; }
                                                }
                                                if (!hit) continue;
                                                float pm = 0f;
                                                try { pm = ppe.Mass; } catch { }
                                                if (pm > 0) groundSum += pm;
                                            }
                                        }
                                    }
                                    catch { }
                                    groundKg = groundSum;
                                    totalKg = sum + groundSum;
                                    haveText = haveSb.ToString();
                                }
                            }
                        }
                        catch { }
                        // 3) 回读：该格是否出现"在建/已建成"
                        gateTrace = (gateTrace ?? "") + "; methodInvoked=" + methodInvoked;
                        // ★ 多格建筑（1x2 等）的真实锚点可能与请求格不同（实测 AlgaeHabitat 1x2 会 NOT_PLACED）
                        //   ⇒ 在请求格周围 ±2 的范围内扫，记录真实命中的格；命中即算 placed
                        // ★ 修误报（0.68.1）：邻域扫描必须**按 prefabId 过滤**，否则会把邻近的别的建筑当成新建的
                        //   （实测：AlgaeHabitat 1x2 没建出来，却把 (110,218) 原有的 Ladder 当成了命中）
                        //   半径收到 ±1；并且优先用"占地集合包含请求格"判定，其次才用 ±1 邻域。
                        searchRadius = 1;
                        string wantPrefabId = prefabId;
                        int layers = (int)ObjectLayer.NumLayers;
                        for (int dy = -searchRadius; dy <= searchRadius && !placed; dy++)
                        {
                            for (int dx = -searchRadius; dx <= searchRadius && !placed; dx++)
                            {
                                int cx2 = x + dx, cy2 = y + dy;
                                int c2 = Grid.XYToCell(cx2, cy2);
                                if (!Grid.IsValidCell(c2)) continue;
                                for (int layer = 0; layer < layers; layer++)
                                {
                                    UnityEngine.GameObject go = null;
                                    try { go = Grid.Objects[c2, layer]; } catch { continue; }
                                    if (go == null) continue;
                                    try
                                    {
                                        BuildingDef candDef = null;
                                        bool underC = false;
                                        var uc = go.GetComponent<BuildingUnderConstruction>();
                                        if (uc != null) { candDef = uc.Def; underC = true; }
                                        else
                                        {
                                            var bc = go.GetComponent<Building>();
                                            if (bc != null) candDef = bc.Def;
                                        }
                                        if (candDef == null) continue;

                                        string candId = null;
                                        try { candId = candDef.PrefabID; } catch { }
                                        if (!string.Equals(candId, wantPrefabId, System.StringComparison.OrdinalIgnoreCase)) continue;   // ★ prefab 过滤

                                        // 占地集合包含请求格 ⇒ 这才是"真的建在请求格上"
                                        bool covers = false;
                                        try
                                        {
                                            var bcov = go.GetComponent<BuildingComplete>();
                                            if (bcov != null) covers = bcov.PlacementCellsContainCell(cell);
                                        }
                                        catch { }
                                        if (!covers && (dx != 0 || dy != 0)) continue;   // 不在占地里、又不是请求格本身 ⇒ 不算

                                        placed = true; underConstruction = underC;
                                        foundCell = c2; foundX = cx2; foundY = cy2;
                                        try { placedPrefab = candId; } catch { }
                                        break;
                                    }
                                    catch { }
                                }
                            }
                        }
                        gateTrace = (gateTrace ?? "") + "; builtItemAtCell=" + placed
                                  + "; foundCell=" + foundCell + "; searchRadius=" + searchRadius
                                  + "; prefabFilter=" + (wantPrefabId ?? "<null>");
                        if (!placed && invokeExc != null) { pFailCode = "REFLECT_INVOKE_FAILED"; pFail = invokeExc; }
                    }
                    catch (System.Exception e) { pFailCode = "PLACE_FAILED"; pFail = e.GetType().Name + ": " + e.Message; }
                    finally
                    {
                        try
                        {
                        if (visMoved && visHadOriginal)
                        {
                            try
                            {
                                var fVisR = typeof(InterfaceTool).GetField("visualizer", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                                var visR = fVisR != null ? fVisR.GetValue(BuildTool.Instance) as UnityEngine.GameObject : null;
                                if (visR != null && visR.transform != null) visR.transform.position = visOriginalPos;
                            }
                            catch { }
                        }
                            var btRestore = BuildTool.Instance;
                            if (btRestore != null) btRestore.Deactivate();
                            toolRestored = true;
                        }
                        catch (System.Exception exR) { toolRestored = false; restoreErr = exR.GetType().Name + ": " + exR.Message; }
                    }
                }, 5000, 30000, out okB, out busyB);

                if (pFailCode != null)
                {
                    status = 400;
                    if (pFailCode == "CELL_OCCUPIED") { status = 409; details = "{\"cell\":" + occupiedCell + ",\"prefabId\":" + Snap.Str(occupiedPrefab ?? "") + ",\"underConstruction\":" + (occupiedUnderConstruction ? "true" : "false") + "}"; }
                    if (pFailCode == "PLACE_FAILED" || pFailCode == "REFLECT_INVOKE_FAILED" || pFailCode == "DEFBUILD_RETURNED_NULL") status = 500;
                    code = pFailCode; message = pFail;
                    if (pStack != null || fieldsSetText != null || fieldDump != null)
                    {
                        details = "{\"exceptionStack\":" + Snap.Str(pStack ?? "<none>")
                                + ",\"fieldsSet\":" + Snap.Str(fieldsSetText ?? "<none>")
                                + ",\"buildToolFields\":" + Snap.Str(fieldDump ?? "<none>")
                                + ",\"materialsDiag\":" + Snap.Str(matsDiag ?? "<none>")
                                + ",\"instantBuildDiag\":" + Snap.Str(instantDiag ?? "<none>")
                                + ",\"gateTrace\":" + Snap.Str(gateTrace ?? "<none>") + "}";
                    }
                    Log.Info("[job] type=build 放置失败：" + pFailCode + " " + pFail);
                    return null;
                }
                if (!placed)
                {
                    // ★ 材料不足要报准（否则 NOT_PLACED 会误导成"机制不通"）
                    // ★ M6.56（小僵拍板）：材料不足**不得阻塞蓝图**（玩家没材料也能画蓝图）。
                    //   只有 defbuild（直接生成实物）才保留材料校验；instant 是作弊、trybuild 是画蓝图 ⇒ 都不拦。
                    if (placeMode == "defbuild" && needKg > 0 && totalKg >= 0 && totalKg + 0.0001 < needKg)
                    {
                        status = 409; code = "INSUFFICIENT_MATERIAL";
                        string catText = null;
                        try { var mc2 = Assets.GetBuildingDef(prefabId) != null ? Assets.GetBuildingDef(prefabId).MaterialCategory : null; if (mc2 != null) catText = string.Join(",", mc2); } catch { }
                        message = "材料不足（仅 defbuild 拦）：需要 " + needKg.ToString("0.###") + "kg（类别 " + (catText ?? "?") + "）"
                                  + "；箱里 " + haveKg.ToString("0.###") + "kg / 地上 " + (groundKg >= 0 ? groundKg.ToString("0.###") : "?") + "kg / 合计 " + (totalKg >= 0 ? totalKg.ToString("0.###") : "?") + "kg";
                        details = "{\"need\":[{\"category\":" + Snap.Str(catText ?? "?") + ",\"kg\":" + needKg.ToString("0.###") + "}]"
                        + ",\"inStorage\":" + haveKg.ToString("0.###") + ",\"onGround\":" + (groundKg >= 0 ? groundKg.ToString("0.###") : "null") + ",\"total\":" + (totalKg >= 0 ? totalKg.ToString("0.###") : "null")
                        + ",\"hint\":" + Snap.Str("需要 " + needKg.ToString("0.###") + "kg " + (catText ?? "材料") + "；箱里 " + haveKg.ToString("0.###") + "kg，地上 " + (groundKg >= 0 ? groundKg.ToString("0.###") : "?") + "kg，合计 " + (totalKg >= 0 ? totalKg.ToString("0.###") : "?") + "kg")
                                + ",\"gateTrace\":" + Snap.Str(gateTrace ?? "<none>")
                                + ",\"toolRestored\":" + (toolRestored ? "true" : "false") + "}";
                        Log.Info("[job] type=build INSUFFICIENT_MATERIAL need=" + needKg + " have=" + haveKg + " tags=[" + (haveText ?? "") + "]");
                        return null;
                    }
                    status = 500; code = "NOT_PLACED";
                    details = "{\"buildToolFields\":" + Snap.Str(fieldDump ?? "<null>")
                        + ",\"fieldsSet\":" + Snap.Str(fieldsSetText ?? "<none>")
                        + ",\"fieldsMissing\":" + Snap.Str(fieldsMissingText ?? "<none>") 
                        + ",\"instantBuildDiag\":" + Snap.Str(instantDiag ?? "<none>")
                        + ",\"gateTrace\":" + Snap.Str(gateTrace ?? "<none>")
                        + ",\"materialsDiag\":" + Snap.Str(matsDiag ?? "<none>")
                        + ",\"toolRestored\":" + (toolRestored ? "true" : "false")
                        + ",\"restoreErr\":" + Snap.Str(restoreErr) + "}";
                    message = "调用完成但请求格 ±" + searchRadius + " 范围内没有在建对象（placeMode=" + placeMode
                              + "，activated=" + activated + "，methodInvoked=" + methodInvoked + "）";
                    Log.Info("[job] type=build NOT_PLACED cell=" + cell + " prefab=" + prefabId
                             + " placeMode=" + placeMode + " activated=" + activated + " invoked=" + methodInvoked);
                    return null;
                }

                bool cheatPath = (placeMode == "defbuild" || placeMode == "instant");
                var sbp = new StringBuilder(640);
                sbp.Append("{\"type\":\"build\"");
                sbp.Append(",\"placed\":true");
                if (invokeExc != null) { sbp.Append(",\"warning\":").Append(Snap.Str(invokeExc)).Append(",\"warningKind\":").Append(Snap.Str("POST_PROCESS_FAILED")); }
                sbp.Append(",\"spawned\":").Append(cheatPath ? "true" : "false");
                sbp.Append(",\"jobState\":").Append(Snap.Str(placeMode == "defbuild" ? "cheat-spawned" : (placeMode == "instant" ? "cheat-instant" : (underConstruction ? "under-construction" : "completed"))));
                sbp.Append(",\"materialsConsumed\":").Append(cheatPath ? "false" : "true");
                sbp.Append(",\"target\":{\"x\":").Append(x).Append(",\"y\":").Append(y)
                   .Append(",\"cell\":").Append(cell)
                   .Append(",\"prefabId\":").Append(Snap.Str(placedPrefab ?? prefabId)).Append('}');
                sbp.Append(",\"footprint\":{\"w\":").Append(footprintW).Append(",\"h\":").Append(footprintH).Append('}');
                // ★ M6.56：材料块只报数、不拦（小僵拍板：没材料也能画蓝图）
                {
                    string catText3 = null;
                    try { var mc3 = Assets.GetBuildingDef(prefabId); if (mc3 != null && mc3.MaterialCategory != null) catText3 = string.Join(",", mc3.MaterialCategory); } catch { }
                    double total3 = totalKg >= 0 ? totalKg : (haveKg + (groundKg > 0 ? groundKg : 0));
                    bool enough3 = (needKg > 0 && total3 + 0.0001 >= needKg);
                    sbp.Append(",\"materials\":{\"need\":[{\"category\":").Append(Snap.Str(catText3 ?? "?"))
                       .Append(",\"kg\":").Append(needKg > 0 ? needKg.ToString("0.###") : "null").Append("}]");
                    sbp.Append(",\"inStorage\":").Append(haveKg >= 0 ? haveKg.ToString("0.###") : "null");
                    sbp.Append(",\"onGround\":").Append(groundKg >= 0 ? groundKg.ToString("0.###") : "null");
                    sbp.Append(",\"total\":").Append(total3.ToString("0.###"));
                    sbp.Append(",\"enough\":").Append(enough3 ? "true" : "false");
                    sbp.Append(",\"used\":").Append(Snap.Str(matsUsedText));
                    sbp.Append(",\"hint\":").Append(Snap.Str(
                        "箱里 " + haveKg.ToString("0.###") + "kg，地上 " + (groundKg >= 0 ? groundKg.ToString("0.###") : "?") + "kg，合计 " + total3.ToString("0.###") + "kg（enough 仅供参考，不参与放行）"));
                    sbp.Append("}");
                    // ★ 0.97.1：材料清单别只在"想查才查"的地方 —— 就放必经之路上。
                    //   两种情况附：①没传 materials（走 DefaultElements 默认项，最容易翻车）②传了但不够。
                    //   只给 tag/available/enough/unlocked（**不带 props**，要性质去 /v1/elements）。
                    bool wantCandidates = (matNames.Count == 0) || (!enough3 && needKg > 0);
                    if (wantCandidates)
                    {
                        try
                        {
                            BuildingDef defC = null; try { defC = Assets.GetBuildingDef(prefabId); } catch { }
                            int cTotal, cEnough;
                            string cand = BuildMaterials.LightList(defC, needKg, out cTotal, out cEnough);
                            if (cand != null)
                            {
                                sbp.Append(",\"candidates\":").Append(cand);
                                sbp.Append(",\"candidatesMeta\":{\"total\":").Append(cTotal).Append(",\"enough\":").Append(cEnough)
                                   .Append(",\"reason\":").Append(Snap.Str(matNames.Count == 0
                                       ? "没传 materials，用的是 DefaultElements() 默认项；以下是这个建筑的全部合法材料"
                                       : "传了 materials 但本档不够；以下是这个建筑的全部合法材料"))
                                   .Append(",\"note\":").Append(Snap.Str("备选清单，不是推荐；排序为够的在前。discovered=该资源是否已进入本档的『资源发现』账本（真正获得过：收进世界库存/被建筑消耗/模板发放）；false 表示本档还没获得过这种资源，不等于没研究解锁（研究看 /v1/research）"))
                                   .Append("}");
                            }
                        }
                        catch { }
                    }
                }
                sbp.Append(",\"underConstruction\":").Append(underConstruction ? "true" : "false");
                sbp.Append(",\"buildable\":").Append(buildable ? "true" : "false");
                if (!buildable) sbp.Append(",\"placementMissing\":").Append(Snap.Str(placementMissing ?? "unknown"));
                if (!string.IsNullOrEmpty(buildBlockedReason)) sbp.Append(",\"buildBlockedReason\":").Append(Snap.Str(buildBlockedReason));
                sbp.Append(",\"requestedCell\":").Append(cell);
                sbp.Append(",\"foundCell\":").Append(foundCell);
                sbp.Append(",\"foundX\":").Append(foundX).Append(",\"foundY\":").Append(foundY);
                sbp.Append(",\"searchRadius\":").Append(searchRadius);
                sbp.Append(",\"placeMode\":").Append(Snap.Str(placeMode));
                sbp.Append(",\"activated\":").Append(activated ? "true" : "false");
                sbp.Append(",\"methodInvoked\":").Append(methodInvoked ? "true" : "false");
                sbp.Append(",\"fieldsSet\":").Append(Snap.Str(fieldsSetText));
                sbp.Append(",\"materialsUsed\":").Append(Snap.Str(matsUsedText));
                sbp.Append(",\"materialsDiag\":").Append(Snap.Str(matsDiag));
                sbp.Append(",\"instantBuildDiag\":").Append(Snap.Str(instantDiag));
                sbp.Append(",\"gateTrace\":").Append(Snap.Str(gateTrace));
                sbp.Append(",\"toolRestored\":").Append(toolRestored ? "true" : "false");
                sbp.Append(",\"fieldsMissing\":").Append(Snap.Str(fieldsMissingText));
                sbp.Append(",\"method\":").Append(Snap.Str(
                    placeMode == "defbuild" ? "BuildingDef.Build（作弊直造）"
                    : ("BuildTool.Activate + 反射调用 BuildTool." + (placeMode == "dragtool" ? "OnDragTool(cell,0)" : "TryBuild(cell)"))));
                sbp.Append(",\"note\":").Append(Snap.Str(cheatPath
                    ? "作弊路径：直接生成建筑对象，不消耗材料、不产生建造任务、不需要小人施工"
                    : "走游戏内部放置方法（反射）；若 materialsConsumed=true 且 underConstruction=true ⇒ 是真实建造流程"));
                sbp.Append('}');
                Log.Info("[job] type=build 已放置：cell=" + cell + " prefab=" + (placedPrefab ?? prefabId)
                         + " placeMode=" + placeMode + " underConstruction=" + underConstruction);
                return sbp.ToString();
            }

            var sb = new StringBuilder(512);
            sb.Append("{\"type\":\"build\"");
            sb.Append(",\"validateOnly\":true");
            sb.Append(",\"orientation\":").Append(Snap.Str(orientationReq == null ? "Neutral(default)" : orientationReq));
            sb.Append(",\"valid\":").Append(valid ? "true" : "false");
            sb.Append(",\"footprint\":{\"w\":").Append(footprintW).Append(",\"h\":").Append(footprintH).Append('}');
            sb.Append(",\"reason\":").Append(Snap.Str(valid ? null : gameReason));   // ★ 游戏原话
            sb.Append(",\"target\":{\"worldId\":").Append(foundWorld).Append(",\"x\":").Append(x)
              .Append(",\"y\":").Append(y).Append(",\"cell\":").Append(cell)
              .Append(",\"prefabId\":").Append(Snap.Str(prefabId))
              .Append(",\"name\":").Append(Snap.Str(defName)).Append('}');
            sb.Append(",\"applied\":false");
            sb.Append(",\"note\":").Append(Snap.Str(
                "只校验、不改存档（applied:false）。reason 为 IsValidPlaceLocation 的 ref string 原文；"
                + "真建造未实现，需 confirm:true 且后续版本支持"));
            sb.Append('}');

            Log.Info("[job] type=build(validateOnly) x=" + x + " y=" + y + " cell=" + cell
                     + " prefab=" + (prefabId ?? "<null>") + " valid=" + valid
                     + " reason=" + (gameReason ?? "<null>"));
            return sb.ToString();
        }
        // ================= M6.3：deconstruct =================
        //
        // 入口：`Deconstructable.QueueDeconstruction(bool)`（public；true=标记，false=取消）
        // 回读：`IsMarkedForDeconstruction()`（public）
        // jobState：`marked`（已标记拆除）/ `done`（该建筑已不在场上）
        // ⚠️ 与 dig 不同：这里**没有"新建对象"**，所以 placedDelta 只会是 0；
        //    我们仍然给出 objectsBefore/After，作为"这一步没有副作用"的证明。
        static string DeconstructExecute(string body, out int status, out string code, out string message, out string details)
        {
            status = 200; code = null; message = null; details = null;

            int x, y, worldId = -1, priority = -1;
            if (!TryInt(body, "x", out x) || !TryInt(body, "y", out y))
            {
                status = 400; code = "BAD_REQUEST"; message = "需要 x / y（世界内格子坐标）"; return null;
            }
            TryInt(body, "worldId", out worldId);
            if (HasKey(body, "priority") && !TryInt(body, "priority", out priority))
            {
                status = 400; code = "BAD_REQUEST"; message = "priority 必须是整数"; return null;
            }
            if (priority > 0 && (priority < 1 || priority > 9))
            {
                status = 400; code = "BAD_REQUEST"; message = "priority 只允许 1..9，收到 " + priority; return null;
            }
            if (!MainThread.Ready) { status = 503; code = "NOT_READY"; message = "main-thread pump is not running yet"; return null; }

            int cell = -1, foundWorld = -1;
            int? objectsBefore = null, objectsAfter = null;
            int okLayers = 0, errLayers = 0; string scanErr = null;
            bool alreadyMarked = false, shortCircuited = false, markedAfter = false;
            string prefabId = null, failCode = null, failMsg = null;
            string reqLayer = MatchString(body, "layer"), reqPrefabId = MatchString(body, "prefabId");
            string objLayerName = null, filterLayerName = null, filterLayerZh = null;
            string detLocal = null;      // out 参数不能在 lambda 里赋值 ⇒ 用局部变量中转
            int? priorityApplied = null; bool? priorityMatch = null;

            MainThread.Invoke(() =>
            {
                try
                {
                    cell = Grid.XYToCell(x, y);
                    if (!Grid.IsValidCell(cell)) { failCode = "BAD_TARGET"; failMsg = "格子无效：" + x + "," + y; return; }
                    foundWorld = Grid.WorldIdx != null ? Grid.WorldIdx[cell] : -1;
                    if (worldId >= 0 && foundWorld != worldId)
                    {
                        failCode = "BAD_TARGET"; failMsg = "worldId 不符：请求 " + worldId + "，该格属于 " + foundWorld; return;
                    }

                    objectsBefore = CountObjectsAt(cell, out okLayers, out errLayers, out scanErr);

                    // ★ 一格可有多个对象 ⇒ 必须"点名"，不许替调用方挑
                    var cands = EnumerateCellObjects(cell);
                    var dcands = new System.Collections.Generic.List<CellObject>();
                    for (int i = 0; i < cands.Count; i++) if (cands[i].Deconstructable) dcands.Add(cands[i]);

                    CellObject pick = null;
                    bool wantLayer = !string.IsNullOrEmpty(reqLayer);
                    bool wantPrefab = !string.IsNullOrEmpty(reqPrefabId);

                    if (wantLayer || wantPrefab)
                    {
                        // 1) 两个都给：必须同时满足
                        if (wantLayer && wantPrefab)
                        {
                            var both = new System.Collections.Generic.List<CellObject>();
                            for (int i = 0; i < dcands.Count; i++)
                                if (MatchLayer(dcands[i], reqLayer) && MatchPrefab(dcands[i], reqPrefabId)) both.Add(dcands[i]);
                            if (both.Count == 0) { failCode = "NOT_FOUND_TARGET"; failMsg = "该格没有同时满足 layer=" + reqLayer + " 与 prefabId=" + reqPrefabId + " 的可拆对象"; return; }
                            if (both.Count > 1) { failCode = "AMBIGUOUS_TARGET"; failMsg = "同时满足 layer+prefabId 的对象有 " + both.Count + " 个，请改用 layer 精确点名"; detLocal = CandidatesJson(both); return; }
                            pick = both[0];
                        }
                        // 2) 只给 layer：先按 ObjectLayer 枚举名精确匹配，再按 filterLayer 名匹配；多命中 ⇒ 409
                        else if (wantLayer)
                        {
                            var byEnum = new System.Collections.Generic.List<CellObject>();
                            for (int i = 0; i < dcands.Count; i++) if (MatchLayerEnum(dcands[i], reqLayer)) byEnum.Add(dcands[i]);
                            if (byEnum.Count == 1) pick = byEnum[0];
                            else if (byEnum.Count > 1) { failCode = "AMBIGUOUS_TARGET"; failMsg = "layer=" + reqLayer + " 命中 " + byEnum.Count + " 个对象，请改用 filterLayer 或 prefabId"; detLocal = CandidatesJson(byEnum); return; }
                            else
                            {
                                var byFilter = new System.Collections.Generic.List<CellObject>();
                                for (int i = 0; i < dcands.Count; i++) if (MatchFilterLayer(dcands[i], reqLayer)) byFilter.Add(dcands[i]);
                                if (byFilter.Count == 1) pick = byFilter[0];
                                else if (byFilter.Count > 1) { failCode = "AMBIGUOUS_TARGET"; failMsg = "filterLayer=" + reqLayer + " 对应多个对象层，请改用 ObjectLayer 枚举名（如 \"Wire\"）"; detLocal = CandidatesJson(byFilter); return; }
                                else { failCode = "NOT_FOUND_TARGET"; failMsg = "该格没有 layer=" + reqLayer + " 的可拆对象"; return; }
                            }
                        }
                        // 3) 只给 prefabId：多命中 ⇒ 409
                        else
                        {
                            var byPrefab = new System.Collections.Generic.List<CellObject>();
                            for (int i = 0; i < dcands.Count; i++) if (MatchPrefab(dcands[i], reqPrefabId)) byPrefab.Add(dcands[i]);
                            if (byPrefab.Count == 1) pick = byPrefab[0];
                            else if (byPrefab.Count > 1) { failCode = "AMBIGUOUS_TARGET"; failMsg = "prefabId=" + reqPrefabId + " 命中 " + byPrefab.Count + " 个对象，请改用 layer"; detLocal = CandidatesJson(byPrefab); return; }
                            else { failCode = "NOT_FOUND_TARGET"; failMsg = "该格没有 prefabId=" + reqPrefabId + " 的可拆对象"; return; }
                        }
                    }
                    else
                    {
                        // 4) 没点名：唯一可拆才动手；多个 ⇒ 409 + 候选；零个 ⇒ 400
                        if (dcands.Count == 0)
                        {
                            failCode = "NOT_DECONSTRUCTABLE";
                            failMsg = cands.Count == 0 ? "该格上没有任何对象" : "该格有 " + cands.Count + " 个对象，但都不可拆";
                            objectsAfter = objectsBefore;
                            return;
                        }
                        if (dcands.Count > 1)
                        {
                            failCode = "AMBIGUOUS_TARGET";
                            failMsg = "该格有 " + dcands.Count + " 个可拆对象，必须点名（layer 或 prefabId 或两者），不替你挑";
                            detLocal = CandidatesJson(dcands);
                            return;
                        }
                        pick = dcands[0];
                    }

                    Deconstructable d = pick.Comp;
                    prefabId = pick.PrefabId;
                    try { objLayerName = LayerEnumName(pick.Layer); filterLayerName = pick.FilterLayer; filterLayerZh = FilterLayerZh(pick.FilterLayer); } catch { }

                    alreadyMarked = d.IsMarkedForDeconstruction();
                    if (alreadyMarked)
                    {
                        shortCircuited = true;           // 已标记 ⇒ 短路，不重复下发
                        markedAfter = true;
                    }
                    else
                    {
                        d.QueueDeconstruction(true);     // true = 标记拆除
                        markedAfter = d.IsMarkedForDeconstruction();   // ★ 回读
                    }

                    if (priority > 0)
                    {
                        priorityApplied = SetPriorityAndReadBack(pick.Go, priority);
                        priorityMatch = priorityApplied.HasValue ? (bool?)(priorityApplied.Value == priority) : null;
                    }

                    objectsAfter = CountObjectsAt(cell, out okLayers, out errLayers, out scanErr);
                }
                catch (System.Exception e) { failCode = "INTERNAL"; failMsg = e.GetType().Name + ": " + e.Message; }
            }, 5000);

            details = detLocal;          // lambda 里算好的候选，在这里交给 out 参数

            if (failCode != null)
            {
                status = failCode == "INTERNAL" ? 500 : 400;
                code = failCode; message = failMsg;
                Log.Info("[job] type=deconstruct x=" + x + " y=" + y + " cell=" + cell + " ⇒ " + status + " " + failCode + ": " + failMsg);
                return null;
            }

            string jobState = markedAfter ? "marked" : "failed_should_not_happen";
            if (markedAfter && FindBuildingAt(cell) == null) jobState = "done";   // 建筑已不在场上（罕见）

            return BuildJobJson("deconstruct", foundWorld, x, y, cell, prefabId,
                markedAfter, jobState, alreadyMarked, shortCircuited,
                objectsBefore, objectsAfter, okLayers, errLayers, scanErr,
                priority, priorityApplied, priorityMatch,
                "已标记拆除（Deconstructable.QueueDeconstruction(true) + IsMarkedForDeconstruction 回读）；等复制人去拆");
        }

        // ================= M6.7：harvest（改为"点名 + 真派活"）=================
        //
        // 语义纠正（M6.7）：之前调 `Harvestable.SetCanBeHarvested(true)` = **硬说它熟了**，不会派活（错的）。
        // 正确入口：`HarvestDesignatable.MarkForHarvest()`（**public 方法**，IL 里会 `Harvestable.OnMarkedForHarvest()`
        // → `new Chore(ChoreTypes.Harvest, …)`）。属性 `MarkedForHarvest` 的 setter 只有 8 字节 = 纯字段写入，**不派活**。
        //
        // 错误码分两种（用户要求机器可判断）：
        //   * 没有 `HarvestDesignatable` ⇒ `400 NOT_HARVESTABLE`（你找错对象了）
        //   * 有但 `!CanBeHarvested()`  ⇒ `400 NOT_RIPE`        （再等等）
        //
        // 回读两个量：`MarkedForHarvest`（字段）+ **`Harvestable.chore != null`**（派活的硬证据）。
        static string HarvestCandidatesJson(System.Collections.Generic.List<CellObject> cands)
        {
            var sb = new StringBuilder(512);
            sb.Append("{\"candidates\":[");
            bool first = true;
            for (int i = 0; i < cands.Count; i++)
            {
                var c = cands[i];
                if (!first) sb.Append(',');
                first = false;
                bool marked = false, ready = false, choreCreated = false;
                try { marked = c.Designatable != null && c.Designatable.MarkedForHarvest; } catch { }
                try { ready = c.Designatable != null && c.Designatable.CanBeHarvested(); } catch { }
                try { choreCreated = c.Harvest != null && c.Harvest.HasChore(); } catch { }
                sb.Append("{\"prefabId\":").Append(Snap.Str(c.PrefabId));
                sb.Append(",\"layer\":").Append(Snap.Str(LayerEnumName(c.Layer)));
                sb.Append(",\"filterLayer\":").Append(Snap.Str(c.FilterLayer));
                sb.Append(",\"filterLayerName\":").Append(Snap.Str(FilterLayerZh(c.FilterLayer)));
                sb.Append(",\"markedForHarvest\":").Append(marked ? "true" : "false");
                sb.Append(",\"readyToHarvest\":").Append(ready ? "true" : "false");
                sb.Append(",\"choreCreated\":").Append(choreCreated ? "true" : "false");
                sb.Append('}');
            }
            sb.Append("]}");
            return sb.ToString();
        }

        static string HarvestExecute(string body, out int status, out string code, out string message, out string details)
        {
            status = 200; code = null; message = null; details = null;

            int x, y, worldId = -1, priority = -1;
            if (!TryInt(body, "x", out x) || !TryInt(body, "y", out y))
            {
                status = 400; code = "BAD_REQUEST"; message = "需要 x / y（世界内格子坐标）"; return null;
            }
            TryInt(body, "worldId", out worldId);
            if (HasKey(body, "priority") && !TryInt(body, "priority", out priority))
            {
                status = 400; code = "BAD_REQUEST"; message = "priority 必须是整数"; return null;
            }
            if (priority > 0 && (priority < 1 || priority > 9))
            {
                status = 400; code = "BAD_REQUEST"; message = "priority 只允许 1..9，收到 " + priority; return null;
            }
            string reqLayer = MatchString(body, "layer"), reqPrefabId = MatchString(body, "prefabId");

            if (!MainThread.Ready) { status = 503; code = "NOT_READY"; message = "main-thread pump is not running yet"; return null; }

            int cell = -1, foundWorld = -1;
            int? objectsBefore = null, objectsAfter = null;
            int okLayers = 0, errLayers = 0; string scanErr = null;
            bool alreadyMarked = false, shortCircuited = false, markedAfter = false, choreCreated = false, readyNow = false;
            bool hasHarvestable = false;
            string prefabId = null, failCode = null, failMsg = null, detLocal = null;
            string objLayerName = null, filterLayerName = null, filterLayerZh = null;
            int? priorityApplied = null; bool? priorityMatch = null;

            MainThread.Invoke(() =>
            {
                try
                {
                    cell = Grid.XYToCell(x, y);
                    if (!Grid.IsValidCell(cell)) { failCode = "BAD_TARGET"; failMsg = "格子无效：" + x + "," + y; return; }
                    foundWorld = Grid.WorldIdx != null ? Grid.WorldIdx[cell] : -1;
                    if (worldId >= 0 && foundWorld != worldId)
                    {
                        failCode = "BAD_TARGET"; failMsg = "worldId 不符：请求 " + worldId + "，该格属于 " + foundWorld; return;
                    }

                    objectsBefore = CountObjectsAt(cell, out okLayers, out errLayers, out scanErr);

                    // ★ 一格可有多个对象（植物占多格、相邻植物可能重叠）⇒ 必须点名
                    var cands = EnumerateCellObjects(cell);
                    var hcands = new System.Collections.Generic.List<CellObject>();
                    for (int i = 0; i < cands.Count; i++) if (cands[i].Designatable != null) hcands.Add(cands[i]);

                    if (hcands.Count == 0)
                    {
                        failCode = "NOT_HARVESTABLE";
                        failMsg = cands.Count == 0 ? "该格上没有任何对象" : "该格有 " + cands.Count + " 个对象，但都不可收获（无 HarvestDesignatable）";
                        objectsAfter = objectsBefore;
                        return;
                    }

                    CellObject pick = null;
                    bool wantLayer = !string.IsNullOrEmpty(reqLayer);
                    bool wantPrefab = !string.IsNullOrEmpty(reqPrefabId);

                    if (wantLayer && wantPrefab)
                    {
                        var both = new System.Collections.Generic.List<CellObject>();
                        for (int i = 0; i < hcands.Count; i++)
                            if (MatchLayer(hcands[i], reqLayer) && MatchPrefab(hcands[i], reqPrefabId)) both.Add(hcands[i]);
                        if (both.Count == 0) { failCode = "NOT_FOUND_TARGET"; failMsg = "该格没有同时满足 layer=" + reqLayer + " 与 prefabId=" + reqPrefabId + " 的可收获对象"; return; }
                        if (both.Count > 1) { failCode = "AMBIGUOUS_TARGET"; failMsg = "同时满足 layer+prefabId 的可收获对象有 " + both.Count + " 个"; detLocal = HarvestCandidatesJson(both); return; }
                        pick = both[0];
                    }
                    else if (wantLayer)
                    {
                        var byEnum = new System.Collections.Generic.List<CellObject>();
                        for (int i = 0; i < hcands.Count; i++) if (MatchLayerEnum(hcands[i], reqLayer)) byEnum.Add(hcands[i]);
                        if (byEnum.Count == 1) pick = byEnum[0];
                        else if (byEnum.Count > 1) { failCode = "AMBIGUOUS_TARGET"; failMsg = "layer=" + reqLayer + " 命中 " + byEnum.Count + " 个可收获对象"; detLocal = HarvestCandidatesJson(byEnum); return; }
                        else
                        {
                            var byFilter = new System.Collections.Generic.List<CellObject>();
                            for (int i = 0; i < hcands.Count; i++) if (MatchFilterLayer(hcands[i], reqLayer)) byFilter.Add(hcands[i]);
                            if (byFilter.Count == 1) pick = byFilter[0];
                            else if (byFilter.Count > 1) { failCode = "AMBIGUOUS_TARGET"; failMsg = "filterLayer=" + reqLayer + " 对应多个对象层，请改用 ObjectLayer 枚举名"; detLocal = HarvestCandidatesJson(byFilter); return; }
                            else { failCode = "NOT_FOUND_TARGET"; failMsg = "该格没有 layer=" + reqLayer + " 的可收获对象"; return; }
                        }
                    }
                    else if (wantPrefab)
                    {
                        var byPrefab = new System.Collections.Generic.List<CellObject>();
                        for (int i = 0; i < hcands.Count; i++) if (MatchPrefab(hcands[i], reqPrefabId)) byPrefab.Add(hcands[i]);
                        if (byPrefab.Count == 1) pick = byPrefab[0];
                        else if (byPrefab.Count > 1) { failCode = "AMBIGUOUS_TARGET"; failMsg = "prefabId=" + reqPrefabId + " 命中 " + byPrefab.Count + " 个可收获对象，请改用 layer"; detLocal = HarvestCandidatesJson(byPrefab); return; }
                        else { failCode = "NOT_FOUND_TARGET"; failMsg = "该格没有 prefabId=" + reqPrefabId + " 的可收获对象"; return; }
                    }
                    else
                    {
                        if (hcands.Count > 1)
                        {
                            failCode = "AMBIGUOUS_TARGET";
                            failMsg = "该格有 " + hcands.Count + " 个可收获对象，必须点名（layer 或 prefabId 或两者）";
                            detLocal = HarvestCandidatesJson(hcands);
                            return;
                        }
                        pick = hcands[0];
                    }

                    prefabId = pick.PrefabId;
                    objLayerName = LayerEnumName(pick.Layer);
                    filterLayerName = pick.FilterLayer;
                    filterLayerZh = FilterLayerZh(pick.FilterLayer);

                    var d = pick.Designatable;
                    // ★ 未成熟 ⇒ 单独的错误码（"再等等"，不是"找错对象"）
                    readyNow = d.CanBeHarvested();
                    if (!readyNow)
                    {
                        // 即便"不熟"，也把本格全部可收获对象带回去 —— 调用方要据此挑别的（可能是另一株熟的）
                        int ripeCount = 0;
                        for (int k = 0; k < hcands.Count; k++)
                        {
                            try { if (hcands[k].Designatable != null && hcands[k].Designatable.CanBeHarvested()) ripeCount++; } catch { }
                        }
                        failCode = "NOT_RIPE";
                        failMsg = "该对象尚未成熟（CanBeHarvested() == false）；本格共 " + hcands.Count
                                  + " 个可收获对象，其中 " + ripeCount + " 个已成熟。候选见 details.candidates（每项带 readyToHarvest）";
                        detLocal = HarvestCandidatesJson(hcands);
                        objectsAfter = objectsBefore;
                        return;
                    }

                    alreadyMarked = d.MarkedForHarvest;
                    hasHarvestable = pick.Harvest != null;              // 事实报告：有没有 Harvestable（决定标记能否派活）
                    choreCreated = pick.Harvest != null && pick.Harvest.HasChore();
                    if (alreadyMarked && choreCreated)
                    {
                        shortCircuited = true;
                        markedAfter = true;
                    }
                    else
                    {
                        d.MarkForHarvest();                       // ★ public 方法，含 chore 创建（不用反射）
                        markedAfter = d.MarkedForHarvest;           // 回读 1：标记
                        choreCreated = pick.Harvest != null && pick.Harvest.HasChore();   // 回读 2：chore 真的有了
                    }

                    if (priority > 0)
                    {
                        priorityApplied = SetPriorityAndReadBack(pick.Go, priority);
                        priorityMatch = priorityApplied.HasValue ? (bool?)(priorityApplied.Value == priority) : null;
                    }

                    objectsAfter = CountObjectsAt(cell, out okLayers, out errLayers, out scanErr);
                }
                catch (System.Exception e) { failCode = "INTERNAL"; failMsg = e.GetType().Name + ": " + e.Message; }
            }, 5000);

            details = detLocal;

            if (failCode != null)
            {
                status = failCode == "INTERNAL" ? 500 : 400;
                code = failCode; message = failMsg;
                Log.Info("[job] type=harvest x=" + x + " y=" + y + " cell=" + cell + " ⇒ " + status + " " + failCode + ": " + failMsg);
                return null;
            }

            var sb = new StringBuilder(768);
            sb.Append("{\"type\":\"harvest\"");
            sb.Append(",\"accepted\":").Append(markedAfter ? "true" : "false");
            sb.Append(",\"jobState\":").Append(Snap.Str(markedAfter ? "marked" : "not-applied"));
            sb.Append(",\"target\":{\"worldId\":").Append(foundWorld);
            sb.Append(",\"x\":").Append(x).Append(",\"y\":").Append(y);
            sb.Append(",\"cell\":").Append(cell);
            sb.Append(",\"prefabId\":").Append(Snap.Str(prefabId));
            sb.Append(",\"layer\":").Append(Snap.Str(objLayerName));
            sb.Append(",\"filterLayer\":").Append(Snap.Str(filterLayerName));
            sb.Append(",\"filterLayerName\":").Append(Snap.Str(filterLayerZh)).Append('}');
            sb.Append(",\"markedForHarvest\":").Append(markedAfter ? "true" : "false");
            sb.Append(",\"readyToHarvest\":").Append(readyNow ? "true" : "false");
            sb.Append(",\"choreCreated\":").Append(choreCreated ? "true" : "false");
            sb.Append(",\"hasHarvestable\":").Append(hasHarvestable ? "true" : "false");
            if (!hasHarvestable)
                sb.Append(",\"choreUnavailableReason\":").Append(Snap.Str("该对象没有 Harvestable 组件（只有 HarvestDesignatable）⇒ MarkForHarvest 只置标记，不会产生 chore"));
            sb.Append(",\"alreadyMarkedBefore\":").Append(alreadyMarked ? "true" : "false");
            sb.Append(",\"shortCircuited\":").Append(shortCircuited ? "true" : "false");
            bool avail = objectsBefore.HasValue && objectsAfter.HasValue;
            sb.Append(",\"objectsAvailable\":").Append(avail ? "true" : "false");
            sb.Append(",\"objectsBefore\":").Append(NumOrNull(objectsBefore));
            sb.Append(",\"objectsAfter\":").Append(NumOrNull(objectsAfter));
            sb.Append(",\"placedDelta\":").Append(NumOrNull(avail ? (int?)(objectsAfter.Value - objectsBefore.Value) : null));
            sb.Append(",\"priorityRequested\":").Append(priority > 0 ? priority.ToString() : "null");
            sb.Append(",\"priorityApplied\":").Append(NumOrNull(priorityApplied));
            sb.Append(",\"priorityMatch\":").Append(priorityMatch.HasValue ? (priorityMatch.Value ? "true" : "false") : "null");
            bool partial = priority > 0 && priorityMatch.HasValue && !priorityMatch.Value;
            sb.Append(",\"partial\":").Append(partial ? "true" : "false");
            sb.Append(",\"note\":").Append(Snap.Str(choreCreated
                ? "已标记收获且 chore 已创建（Harvestable.chore != null）—— 这是'真派活'的硬证据"
                : "已标记收获，但 choreCreated=false ⇒ 可能没有真的派活，需要排查"));
            sb.Append('}');

            Log.Info("[job] type=harvest x=" + x + " y=" + y + " cell=" + cell + " prefab=" + (prefabId ?? "<null>")
                     + " layer=" + (objLayerName ?? "<null>") + " filterLayer=" + (filterLayerName ?? "<null>")
                     + " ready=" + readyNow + " marked=" + markedAfter + " choreCreated=" + choreCreated
                     + " alreadyMarkedBefore=" + alreadyMarked + " shortCircuited=" + shortCircuited
                     + " objectsBefore=" + (objectsBefore.HasValue ? objectsBefore.Value.ToString() : "<null>")
                     + " objectsAfter=" + (objectsAfter.HasValue ? objectsAfter.Value.ToString() : "<null>")
                     + " priority=" + (priority > 0
                        ? priority + "->" + (priorityApplied.HasValue ? priorityApplied.Value.ToString() : "<null>")
                          + "(match=" + (priorityMatch.HasValue ? priorityMatch.Value.ToString() : "<null>") + ")"
                        : "<none>")
                     + " | objectsScan: okLayers=" + okLayers + " errLayers=" + errLayers + " err=" + (scanErr ?? "<none>"));
            if (markedAfter && !choreCreated)
                Log.Warn("[job] harvest: marked=true 但 choreCreated=false ⇒ MarkForHarvest() 可能没派活（推断可能有误）");
            return sb.ToString();
        }
        // ================= M6.3：sweep（**未实机验证**）=================
        //
        // 入口：`Clearable.MarkForClear(false, false)`（参数取自 `ClearTool.OnDragTool`，玩家拖拽那条路）
        // 回读：`Clearable.isMarkedForClear` 是 **nonpub 字段**（没有 public 读入口）⇒ 只能反射。
        // jobState：`marked`（标记即生效，无 pending 态）。
        /// <summary>sweep 的候选列表：prefabId / layer / filterLayer / instanceId / alreadyMarked（已排队的也要看得见）。</summary>
        internal static string SweepCandidatesJson(System.Collections.Generic.List<CellObject> cands)
        {
            var sb = new StringBuilder(384);
            sb.Append("{\"candidates\":[");
            for (int ci = 0; ci < cands.Count; ci++)
            {
                if (ci > 0) sb.Append(",");
                var c = cands[ci];
                bool marked = false; string rdErr;
                bool? m = TryReadMarkedForClear(c.Clear, out rdErr);
                marked = m.HasValue && m.Value;
                sb.Append("{\"prefabId\":").Append(Snap.Str(c.PrefabId));
                sb.Append(",\"layer\":").Append(Snap.Str(LayerEnumName(c.Layer)));
                sb.Append(",\"filterLayer\":").Append(Snap.Str(c.FilterLayer));
                sb.Append(",\"filterLayerName\":").Append(Snap.Str(FilterLayerZh(c.FilterLayer)));
                int iid = -1; try { iid = c.Go != null ? c.Go.GetInstanceID() : -1; } catch { }
                sb.Append(",\"instanceId\":").Append(iid);
                bool ic = false; try { ic = c.Clear != null && c.Clear.isClearable; } catch { }
                sb.Append(",\"isClearable\":").Append(ic ? "true" : "false");
                sb.Append(",\"alreadyMarked\":").Append(marked ? "true" : "false");
                sb.Append("}");
            }
            sb.Append("]}");
            return sb.ToString();
        }

        static System.Reflection.FieldInfo _markedForClearField;

        internal static bool? TryReadMarkedForClear(Clearable c, out string err)
        {
            err = null;
            try
            {
                if (_markedForClearField == null)
                {
                    _markedForClearField = typeof(Clearable).GetField("isMarkedForClear",
                        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                }
                if (_markedForClearField == null) { err = "field-not-found: Clearable.isMarkedForClear 不存在（字段名/游戏更新？）"; return null; }
                return (bool)_markedForClearField.GetValue(c);
            }
            catch (System.Exception e) { err = "reflection-error: " + e.GetType().Name + ": " + e.Message; return null; }
        }

        internal static string SweepExecute(string body, out int status, out string code, out string message, out string details)
        {
            status = 200; code = null; message = null; details = null;
            string detLocal = null;

            int x, y, worldId = -1, priority = -1;
            if (!TryInt(body, "x", out x) || !TryInt(body, "y", out y))
            {
                status = 400; code = "BAD_REQUEST"; message = "需要 x / y（世界内格子坐标）"; return null;
            }
            TryInt(body, "worldId", out worldId);
            if (HasKey(body, "priority") && !TryInt(body, "priority", out priority))
            { status = 400; code = "BAD_REQUEST"; message = "priority 必须是整数"; return null; }
            if (priority > 0 && (priority < 1 || priority > 9))
            { status = 400; code = "BAD_REQUEST"; message = "priority 只允许 1..9，收到 " + priority; return null; }
            if (!MainThread.Ready) { status = 503; code = "NOT_READY"; message = "main-thread pump is not running yet"; return null; }

            int cell = -1, foundWorld = -1;
            int? objectsBefore = null, objectsAfter = null;
            int okLayers = 0, errLayers = 0; string scanErr = null;
            bool alreadyMarked = false, shortCircuited = false, markedAfter = false;
            string prefabId = null, failCode = null, failMsg = null, readErr = null;
            int? priorityApplied = null; bool? priorityMatch = null;

            MainThread.Invoke(() =>
            {
                try
                {
                    cell = Grid.XYToCell(x, y);
                    if (!Grid.IsValidCell(cell)) { failCode = "BAD_TARGET"; failMsg = "格子无效：" + x + "," + y; return; }
                    foundWorld = Grid.WorldIdx != null ? Grid.WorldIdx[cell] : -1;
                    if (worldId >= 0 && foundWorld != worldId)
                    { failCode = "BAD_TARGET"; failMsg = "worldId 不符：请求 " + worldId + "，该格属于 " + foundWorld; return; }

                    objectsBefore = CountObjectsAt(cell, out okLayers, out errLayers, out scanErr);

                    // ★ M6.14：一格可有多堆碎屑 ⇒ 先枚举、再判歧义（与 deconstruct/harvest 同一规格）
                    var objs = EnumerateCellObjects(cell);
                    var ccands = new System.Collections.Generic.List<CellObject>();
                    // 收集该格所有带 Clearable 的对象（**不按 isClearable 过滤**：复制人可清扫是游戏机制）；isClearable 作为候选字段返回
                    string reqLayer = MatchString(body, "layer"), reqPrefabId = MatchString(body, "prefabId");
                    for (int i = 0; i < objs.Count; i++)
                    {
                        var o = objs[i];
                        if (o.Clear == null) continue;
                        // ⚠️ 不用 isClearable 过滤（复制人可清扫是游戏机制，过滤会把用户能做的事关掉）；
                        //    改为把它作为候选字段返回，让调用方自己决定。
                        if (reqLayer != null && !MatchLayer(o, reqLayer)) continue;
                        if (reqPrefabId != null && !MatchPrefab(o, reqPrefabId)) continue;
                        ccands.Add(o);
                    }

                    if (ccands.Count == 0)
                    {
                        failCode = "NOT_CLEARABLE";
                        failMsg = objs.Count == 0 ? "该格上没有任何对象"
                                  : ("该格有 " + objs.Count + " 个对象，但没有匹配的可清扫对象（可能是 layer/prefabId 不匹配，或对象不可清扫）");
                        return;
                    }
                    if (ccands.Count > 1)
                    {
                        // 与 deconstruct 同规格：不替你挑（"只清一堆却报 ok" 是最坏的形态）
                        failCode = "AMBIGUOUS_TARGET";
                        failMsg = "该格有 " + ccands.Count + " 个可清扫对象，必须点名（layer 或 prefabId 或两者），不替你挑"
                                  + "（M6.14 新增歧义检查：以前会静默只清一堆并返回 ok）";
                        detLocal = SweepCandidatesJson(ccands);
                        return;
                    }
                    Clearable cl = ccands[0].Clear;
                    prefabId = ccands[0].PrefabId;
                    GameObject go = null;
                    try { go = cl.gameObject; } catch { }
                    try { var kp = go != null ? go.GetComponent<KPrefabID>() : null; prefabId = kp != null ? kp.PrefabTag.Name : (go != null ? go.name : null); } catch { }

                    bool? before = TryReadMarkedForClear(cl, out readErr);
                    if (before == null)
                    {
                        failCode = "READBACK_UNAVAILABLE";
                        failMsg = "读不到 Clearable.isMarkedForClear（反射失败）：" + readErr;
                        objectsAfter = objectsBefore;
                        return;
                    }
                    alreadyMarked = before.Value;
                    if (alreadyMarked)
                    {
                        shortCircuited = true;
                        markedAfter = true;
                    }
                    else
                    {
                        cl.MarkForClear(false, false);          // 参数照抄 ClearTool.OnDragTool
                        bool? after = TryReadMarkedForClear(cl, out readErr);
                        if (after == null)
                        {
                            failCode = "READBACK_UNAVAILABLE";
                            failMsg = "下发后读不到 isMarkedForClear：" + readErr;
                            objectsAfter = CountObjectsAt(cell, out okLayers, out errLayers, out scanErr);
                            return;
                        }
                        markedAfter = after.Value;
                        if (!markedAfter)
                        {
                            // 前后都是 false ⇒ 没生效，别报成功
                            failCode = "READBACK_FAILED";
                            failMsg = "调用 MarkForClear 后 isMarkedForClear 仍为 false（未生效）";
                        }
                    }

                    if (priority > 0 && go != null)
                    {
                        priorityApplied = SetPriorityAndReadBack(go, priority);
                        priorityMatch = priorityApplied.HasValue ? (bool?)(priorityApplied.Value == priority) : null;
                    }

                    objectsAfter = CountObjectsAt(cell, out okLayers, out errLayers, out scanErr);
                }
                catch (System.Exception e) { failCode = "INTERNAL"; failMsg = e.GetType().Name + ": " + e.Message; }
            }, 5000);

            if (failCode != null)
            {
                status = (failCode == "INTERNAL" || failCode == "READBACK_UNAVAILABLE" || failCode == "READBACK_FAILED") ? 500 : 400;
                code = failCode; message = failMsg; details = detLocal;
                Log.Info("[job] type=sweep x=" + x + " y=" + y + " cell=" + cell + " ⇒ " + status + " " + failCode + ": " + failMsg);
                return null;
            }

            string json = BuildJobJson("sweep", foundWorld, x, y, cell, prefabId,
                markedAfter, markedAfter ? "marked" : "not-applied", alreadyMarked, shortCircuited,
                objectsBefore, objectsAfter, okLayers, errLayers, scanErr,
                priority, priorityApplied, priorityMatch,
                "已标记清扫（MarkForClear(false,false)，参数取自 ClearTool.OnDragTool）；等复制人去扫。**本 type 未实机验证**");
            json = json.Substring(0, json.Length - 1)
                 + ",\"verified\":false"
                 + ",\"note\":\"sweep 参数取自 ClearTool.OnDragTool，未经实机验证\"}";
            return json;
        }

        internal static Clearable FindClearableAt(int cell)
        {
            try
            {
                if (cell < 0 || !Grid.IsValidCell(cell)) return null;
                int layers = (int)ObjectLayer.NumLayers;
                for (int layer = 0; layer < layers; layer++)
                {
                    GameObject go = null;
                    try { go = Grid.Objects[cell, layer]; } catch { continue; }
                    if (go == null) continue;
                    var cl = go.GetComponent<Clearable>();
                    if (cl != null) return cl;
                }
            }
            catch { }
            return null;
        }
        // ================= 共用助手 =================
        internal static BuildingComplete FindBuildingAt(int cell)
        {
            try
            {
                var items = Components.BuildingCompletes != null ? Components.BuildingCompletes.Items : null;
                if (items == null) return null;
                for (int i = 0; i < items.Count; i++)
                {
                    var b = items[i];
                    if (b == null) continue;
                    bool contains = false;
                    try { contains = b.PlacementCellsContainCell(cell); } catch { continue; }
                    if (contains) return b;
                }
            }
            catch { }
            return null;
        }

        /// <summary>
        /// 该格上**带 ResearchCenter 组件**的建筑（按组件找，而不是"层里第一个对象"）。
        /// 实测教训：同一格可能先有 Battery / Wire 占位 ⇒ 按层取第一个会误报 NOT_RESEARCH_CENTER。
        /// </summary>
        static BuildingComplete FindResearchCenterAt(int cell)
        {
            var items = Components.BuildingCompletes != null ? Components.BuildingCompletes.Items : null;
            if (items == null) return null;
            for (int i = 0; i < items.Count; i++)
            {
                var b = items[i];
                if (b == null) continue;
                bool contains = false;
                try { contains = b.PlacementCellsContainCell(cell); } catch { continue; }
                if (!contains) continue;
                try { if (b.GetComponent<ResearchCenter>() != null) return b; } catch { }
            }
            return null;
        }

        static string BuildJobJson(string type, int worldId, int x, int y, int cell, string prefabId,
            bool accepted, string jobState, bool alreadyMarked, bool shortCircuited,
            int? objectsBefore, int? objectsAfter, int okLayers, int errLayers, string scanErr,
            int priority, int? priorityApplied, bool? priorityMatch, string note)
        {
            int? delta = (objectsBefore.HasValue && objectsAfter.HasValue)
                ? (int?)(objectsAfter.Value - objectsBefore.Value) : null;
            bool partial = priority > 0 && priorityMatch.HasValue && !priorityMatch.Value;

            var sb = new StringBuilder(640);
            sb.Append("{\"type\":").Append(Snap.Str(type));
            sb.Append(",\"accepted\":").Append(accepted ? "true" : "false");
            sb.Append(",\"jobState\":").Append(Snap.Str(jobState));
            sb.Append(",\"target\":{\"worldId\":").Append(worldId);
            sb.Append(",\"x\":").Append(x).Append(",\"y\":").Append(y);
            sb.Append(",\"cell\":").Append(cell);
            sb.Append(",\"prefabId\":").Append(Snap.Str(prefabId)).Append('}');
            sb.Append(",\"alreadyMarkedBefore\":").Append(alreadyMarked ? "true" : "false");
            sb.Append(",\"shortCircuited\":").Append(shortCircuited ? "true" : "false");
            bool avail = objectsBefore.HasValue && objectsAfter.HasValue;
            sb.Append(",\"objectsAvailable\":").Append(avail ? "true" : "false");
            sb.Append(",\"objectsBefore\":").Append(NumOrNull(objectsBefore));
            sb.Append(",\"objectsAfter\":").Append(NumOrNull(objectsAfter));
            sb.Append(",\"placedDelta\":").Append(NumOrNull(delta));
            sb.Append(",\"priorityRequested\":").Append(priority > 0 ? priority.ToString() : "null");
            sb.Append(",\"priorityApplied\":").Append(NumOrNull(priorityApplied));
            sb.Append(",\"priorityMatch\":").Append(priorityMatch.HasValue ? (priorityMatch.Value ? "true" : "false") : "null");
            sb.Append(",\"partial\":").Append(partial ? "true" : "false");
            if (partial)
                sb.Append(",\"partialReason\":").Append(Snap.Str("priority 未生效：请求 " + priority
                    + "，回读 " + (priorityApplied.HasValue ? priorityApplied.Value.ToString() : "<拿不到 Prioritizable>")));
            sb.Append(",\"note\":").Append(Snap.Str(note));
            sb.Append('}');

            Log.Info("[job] type=" + type + " world=" + worldId + " x=" + x + " y=" + y + " cell=" + cell
                     + " prefab=" + (prefabId ?? "<null>")
                     + " accepted=" + accepted + " jobState=" + jobState
                     + " alreadyMarkedBefore=" + alreadyMarked + " shortCircuited=" + shortCircuited
                     + " objectsBefore=" + (objectsBefore.HasValue ? objectsBefore.Value.ToString() : "<null>")
                     + " objectsAfter=" + (objectsAfter.HasValue ? objectsAfter.Value.ToString() : "<null>")
                     + " placedDelta=" + (delta.HasValue ? delta.Value.ToString() : "<null>")
                     + " priority=" + (priority > 0
                        ? priority + "->" + (priorityApplied.HasValue ? priorityApplied.Value.ToString() : "<null>")
                          + "(match=" + (priorityMatch.HasValue ? priorityMatch.Value.ToString() : "<null>") + ")"
                        : "<none>")
                     + " partial=" + partial
                     + " | objectsScan: okLayers=" + okLayers + " errLayers=" + errLayers
                     + " err=" + (scanErr ?? "<none>"));
            return sb.ToString();
        }
        /// <summary>设置优先级并**回读比对**（不信"调用了就成功"）。返回回读到的值（拿不到返回 -1）。</summary>
        static int? SetPriorityAndReadBack(GameObject go, int priority)
        {
            try
            {
                if (go == null) return null;
                var pr = go.GetComponent<Prioritizable>();
                if (pr == null) return null;
                pr.SetMasterPriority(new PrioritySetting(PriorityScreen.PriorityClass.basic, priority));
                var got = pr.GetMasterPriority();
                return got.priority_value;
            }
            catch { return null; }
        }

        /// <summary>
        /// 该格上的对象总数（**所有层**，不只 DigPlacer 层）。
        /// 用途：把"标记物叠加"变成响应里能核对的数字（placedDelta）。
        /// 走 `Grid.Objects[cell, layer]`（public 索引器），层数取 `ObjectLayer.NumLayers`，**不硬编码层号**。
        /// </summary>
        internal static int? CountObjectsAt(int cell, out int okLayers, out int errLayers, out string errText)
        {
            okLayers = 0; errLayers = 0; errText = null;
            try
            {
                if (cell < 0 || !Grid.IsValidCell(cell)) { errText = "invalid-cell"; return null; }
                int n = 0;
                int layers = (int)ObjectLayer.NumLayers;
                for (int layer = 0; layer < layers; layer++)
                {
                    // 每层单独兜住：某一层抛异常不能毁掉整轮统计，而且要把层号记下来
                    try
                    {
                        if (Grid.Objects[cell, layer] != null) n++;
                        okLayers++;
                    }
                    catch (System.Exception e)
                    {
                        errLayers++;
                        if (errText == null) errText = "layer=" + layer + " " + e.GetType().Name + ": " + e.Message;
                    }
                }
                return n;
            }
            catch (System.Exception e) { errText = "outer " + e.GetType().Name + ": " + e.Message; return null; }
        }

        /// <summary>该格上是否已有 Diggable（即已标过挖）。优先直查 `ObjectLayer.DigPlacer` 层，再逐层兜底。</summary>
        internal static Diggable FindDiggableAt(int cell, out int hitLayer, out int okLayers, out int errLayers, out string errText)
        {
            hitLayer = -1; okLayers = 0; errLayers = 0; errText = null;
            try
            {
                if (cell < 0 || !Grid.IsValidCell(cell)) { errText = "invalid-cell"; return null; }

                // 1) 先直查挖标记层（与 PlaceDig 内部同一访问方式）
                int digLayer = (int)ObjectLayer.DigPlacer;
                try
                {
                    var atDig = Grid.Objects[cell, digLayer];
                    okLayers++;
                    if (atDig != null)
                    {
                        var dg0 = atDig.GetComponent<Diggable>();
                        if (dg0 != null) { hitLayer = digLayer; return dg0; }
                    }
                }
                catch (System.Exception e)
                {
                    errLayers++;
                    errText = "digLayer=" + digLayer + " " + e.GetType().Name + ": " + e.Message;
                }

                // 2) 兜底逐层扫（每层单独兜住，记第一个抛异常的层号）
                int layers = (int)ObjectLayer.NumLayers;
                for (int layer = 0; layer < layers; layer++)
                {
                    if (layer == digLayer) continue;   // 上面查过了
                    try
                    {
                        var go = Grid.Objects[cell, layer];
                        okLayers++;
                        if (go == null) continue;
                        var dg = go.GetComponent<Diggable>();
                        if (dg != null) { hitLayer = layer; return dg; }
                    }
                    catch (System.Exception e)
                    {
                        errLayers++;
                        if (errText == null) errText = "layer=" + layer + " " + e.GetType().Name + ": " + e.Message;
                    }
                }
            }
            catch (System.Exception e) { errText = "outer " + e.GetType().Name + ": " + e.Message; }
            return null;
        }

        /// <summary>int? → JSON：有值写数字，没拿到写 null（不写 -1 哨兵）。</summary>
        static string NumOrNull(int? v)
        {
            return v.HasValue ? v.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) : "null";
        }

        internal static bool HasKey(string body, string key)
        {
            return !string.IsNullOrEmpty(body) && Regex.IsMatch(body, "\"" + key + "\"\\s*:");
        }

        internal static bool TryInt(string body, string key, out int v)
        {
            v = 0;
            if (string.IsNullOrEmpty(body)) return false;
            var m = Regex.Match(body, "\"" + key + "\"\\s*:\\s*(-?[0-9]+)");
            if (!m.Success) return false;
            return int.TryParse(m.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out v);
        }

        /// <summary>
        /// 读 JSON 里的布尔值（`"key":true` 或 `"key":"true"` 都接受）。
        /// ⚠️ 不能用 MatchString —— 它只匹配带引号的值，布尔字面量永远读不到（0.40.3 修）。
        /// </summary>
        internal static bool MatchBool(string body, string key, out bool val)
        {
            val = false;
            if (string.IsNullOrEmpty(body)) return false;
            var m = Regex.Match(body, "\"" + key + "\"\\s*:\\s*(true|false|\"true\"|\"false\")", RegexOptions.IgnoreCase);
            if (!m.Success) return false;
            val = m.Groups[1].Value.Trim('\"').Equals("true", System.StringComparison.OrdinalIgnoreCase);
            return true;
        }

        static string MatchString(string body, string key)
        {
            if (string.IsNullOrEmpty(body)) return null;
            var m = Regex.Match(body, "\"" + key + "\"\\s*:\\s*\"([^\"]*)\"");
            return m.Success ? m.Groups[1].Value : null;
        }
    }
}
