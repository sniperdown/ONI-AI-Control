using System.Text;

// ⚠️ 不要 using System 之后直接用 Action/DateTime：Assembly-CSharp 里的全局 Action 枚举会遮蔽 System.Action。
namespace OniAiBridge
{
    /// <summary>
    /// M6.64（**只读**）：`GET /v1/geysers` —— 间歇泉 / 火山清单。
    ///
    /// 为什么单开一个端点：**间歇泉不在 `Components.BuildingCompletes` 里**（实测全档 234 条建筑，
    /// `Group-Object prefabId` 里没有任何 `Geyser*` / `Volcano*`）⇒ `/v1/buildings` 永远读不到它们。
    /// 而且小僵实测：间歇泉**长在中子质上、被物块掩埋**，挖开才激活 ⇒ **埋在物块里时也必须能读到坐标**，
    /// 所以不能用"已激活/已挖开"作为枚举条件。
    ///
    /// 枚举方式：默认 `FindObjectsOfType<Geyser>(false)`（**只扫激活对象**）——
/// 实测依据（M6.66）：**被物块掩埋的泉仍然是 active 对象**，`false` 一样能拿到；
/// 而 `true` 会多扫出 31 个内存里的 prefab 模板（坐标全 0、不属于任何世界），再被过滤掉（192ms vs 62ms，结果相同）。
/// 需要扫未激活对象时用 `?includeInactive=1`（逃生口）。
    /// `Geyser` 类型已确证存在：`layout.exe types` 在 **Assembly-CSharp** 里列出 `Geyser` /
    /// `GeyserConfig` / `GeyserConfigurator` / `GeyserType` 等。
    ///
    /// 字段可得性（**如实区分**，取不到给 null）：
    /// * `worldId` / `x` / `y` / `cell` —— `Grid.PosToCell(transform.position)` + `Grid.WorldIdx`（实测可用）
    /// * `prefabId` —— `KPrefabID.PrefabTag.Name`（firstpass，public 字段）；退回 `gameObject.name`
    /// * `elementId` —— `Geyser.configuration.geyserType.element`（`SimHashes`）⇒ `.ToString()`
    /// * `buried` —— 用泉所在格是否为**固体**判断（`Grid.IsSolidCell(cell)`）；判据写进响应 note
    /// * `state` —— **没有把握**：`Geyser` 成员表里没有公开的 phase/state 字段，
    ///   这里用**反射**尝试 `smi.phase` / `state`，取不到就 `null` + `stateUnavailable` 说明原因（**不编**）
    /// * `nextEruptionCycle` —— **不提供**（拿不到"周期数"，只有秒数）；改为给
    ///   `remainingIdleTime` / `remainingEruptTime` / `remainingEruptPreTime` / `lifeTime`
    ///   （都是 `Geyser` 的 public 方法，调用失败则该字段为 null）
    /// </summary>
    internal static class Geysers
    {
        public static string List(int wantWorldId, bool includeInactive, out int status, out string code, out string message)
        {
            status = 200; code = null; message = null;
            if (!MainThread.Ready) { status = 503; code = "NOT_READY"; message = "main-thread pump is not running yet"; return null; }

            string data = null, fail = null;
            int okT; bool busy;
            bool ran = MainThread.InvokeAdaptive(() =>
            {
                try
                {
                    Geyser[] all = null;
                    try { all = includeInactive ? UnityEngine.Object.FindObjectsOfType<Geyser>(true) : UnityEngine.Object.FindObjectsOfType<Geyser>(false); }
                    catch (System.Exception e) { fail = "FindObjectsOfType<Geyser>(true) 失败：" + e.GetType().Name + ": " + e.Message; return; }
                    if (all == null) { fail = "FindObjectsOfType<Geyser> 返回 null"; return; }

                    var sb = new StringBuilder(4096);
                    sb.Append("{\"total\":").Append(all.Length);
                    sb.Append(",\"filter\":{\"worldId\":").Append(wantWorldId >= 0 ? wantWorldId.ToString() : "null").Append('}');
                    sb.Append(",\"geysers\":[");

                    int emitted = 0, filteredByWorld = 0, probeOk = 0, filteredTemplates = 0;
                    var stateErr = new System.Collections.Generic.List<string>();

                    for (int i = 0; i < all.Length; i++)
                    {
                        var g = all[i];
                        if (g == null) continue;

                        int cell = -1;
                        try { cell = Grid.PosToCell(g.transform.position); } catch { }
                        if (cell < 0 || !Grid.IsValidCell(cell)) continue;

                        int worldId = -1;
                        try { worldId = Grid.WorldIdx != null ? Grid.WorldIdx[cell] : -1; } catch { }

                        // ★ M6.65：排除**预制体模板**（FindObjectsOfType<Geyser>(true) 会扫到内存里的 prefab 原型：坐标全 0、不属于任何世界）
                        //   主判据 = 世界归属（模板 GetMyWorldId() 通常无效/255）；兜底 = 坐标全 0。两者都计入 filteredTemplates。
                        int myWorld = -999;
                        try { myWorld = g.GetMyWorldId(); } catch { myWorld = -999; }
                        if (myWorld < 0 || myWorld == 255) { filteredTemplates++; continue; }
                        if (wantWorldId >= 0 && worldId != wantWorldId) { filteredByWorld++; continue; }

                        int gx = -1, gy = -1;
                        try { Grid.CellToXY(cell, out gx, out gy); } catch { }
                        if (gx == 0 && gy == 0 && cell == 0 && worldId == 0) { filteredTemplates++; continue; }   // 兜底：坐标全 0 一律当模板

                        // prefabId：KPrefabID.PrefabTag（firstpass public 字段）
                        string prefabId = null;
                        try
                        {
                            var kp = g.gameObject.GetComponent<KPrefabID>();
                            if (kp != null && kp.PrefabTag.Name != null) prefabId = kp.PrefabTag.Name;
                        }
                        catch { }
                        if (string.IsNullOrEmpty(prefabId))
                        {
                            try { prefabId = g.gameObject.name; } catch { }
                        }

                        // elementId：Geyser.configuration.geyserType.element（SimHashes）
                        string elementId = null, geyserTypeId = null;
                        try
                        {
                            var cfg = g.configuration;
                            if (cfg != null)
                            {
                                try { var gt = cfg.geyserType; if (gt != null) { elementId = gt.element.ToString(); geyserTypeId = gt.id; } } catch { }
                            }
                        }
                        catch { }

                        // buried：泉所在格是固体 ⇒ 认为被埋
                        bool buried = false;
                        try { buried = Grid.IsSolidCell(cell); } catch { }

                        // 生命周期（都是 public 方法，逐个 try）
                        double? lifeTime = null, idleTime = null, eruptTime = null, eruptPreTime = null;
                        bool? shouldDormant = null;
                        try { lifeTime = g.GetCurrentLifeTime(); } catch { }
                        try { idleTime = g.RemainingIdleTime(); } catch { }
                        try { eruptTime = g.RemainingEruptTime(); } catch { }
                        try { eruptPreTime = g.RemainingEruptPreTime(); } catch { }
                        try { shouldDormant = g.ShouldGoDormant(); } catch { }

                        // state：反射尝试 smi.phase / state（**没有把握**，取不到就 null）
                        string state = null, stateSrc = null;
                        try
                        {
                            string via;
                            state = TryReadPhase(g, out via);
                            if (state != null) { stateSrc = via; probeOk++; }
                        }
                        catch (System.Exception exSt) { stateErr.Add(exSt.GetType().Name); }

                        if (emitted > 0) sb.Append(',');
                        emitted++;
                        sb.Append("{\"worldId\":").Append(worldId);
                        sb.Append(",\"x\":").Append(gx).Append(",\"y\":").Append(gy).Append(",\"cell\":").Append(cell);
                        sb.Append(",\"prefabId\":").Append(Snap.Str(prefabId));
                        sb.Append(",\"elementId\":").Append(Snap.Str(elementId));
                        sb.Append(",\"geyserTypeId\":").Append(Snap.Str(geyserTypeId));
                        sb.Append(",\"buried\":").Append(buried ? "true" : "false");
                        sb.Append(",\"state\":").Append(Snap.Str(state));
                        // ★ stateDerived：从剩余时间**推导**（不是游戏原始阶段值，原始值见 state）
                        string stateDerived = null;
                        try
                        {
                            if (eruptPreTime.HasValue && eruptPreTime.Value > 0) stateDerived = "preErupt";
                            else if (eruptTime.HasValue && eruptTime.Value > 0) stateDerived = "erupting";
                            else if (idleTime.HasValue && idleTime.Value > 0) stateDerived = (shouldDormant.HasValue && shouldDormant.Value) ? "dormant" : "idle";
                            else if (eruptPreTime.HasValue && eruptTime.HasValue && idleTime.HasValue) stateDerived = "unknown";   // 三个计时器全 0：尚未开始/未初始化（与"取不到"区分）
                        }
                        catch { }
                        sb.Append(",\"stateDerived\":").Append(Snap.Str(stateDerived));
                        if (state == null) sb.Append(",\"stateUnavailable\":").Append(Snap.Str("Geyser 成员表里没有公开的 phase/state 字段；反射 smi.phase/state 也没读到"));
                        else sb.Append(",\"stateSource\":").Append(Snap.Str(stateSrc));
                        sb.Append(",\"remainingIdleTime\":").Append(NumOrNull(idleTime));
                        sb.Append(",\"remainingEruptTime\":").Append(NumOrNull(eruptTime));
                        sb.Append(",\"remainingEruptPreTime\":").Append(NumOrNull(eruptPreTime));
                        sb.Append(",\"lifeTime\":").Append(NumOrNull(lifeTime));
                        sb.Append(",\"shouldGoDormant\":").Append(shouldDormant.HasValue ? (shouldDormant.Value ? "true" : "false") : "null");
                        sb.Append('}');
                    }

                    sb.Append(']');
                    // ★ 修：原来用 Insert 会多出一个 "{"（sb 本身已以 { 开头）⇒ 非法 JSON。
                    sb.Replace("{", "{\"count\":" + emitted + ",", 0, 1);   // 只替换第一个 {
                    sb.Append(",\"scanMode\":").Append(Snap.Str(includeInactive ? "FindObjectsOfType<Geyser>(true)" : "FindObjectsOfType<Geyser>(false)"));
                    sb.Append(",\"filteredByWorld\":").Append(filteredByWorld);
                    sb.Append(",\"filteredTemplates\":").Append(filteredTemplates);
                    sb.Append(",\"stateProbeOk\":").Append(probeOk);
                    sb.Append(",\"source\":").Append(Snap.Str("UnityEngine.Object.FindObjectsOfType<Geyser>(true)（含 inactive；间歇泉不在 BuildingCompletes 里）"));
                    sb.Append(",\"note\":").Append(Snap.Str(
                        "只读。`buried` 的判据 = 泉所在格是固体（Grid.IsSolidCell） —— 间歇泉长在中子质上、被物块掩埋时也算 buried:true；"
                        + "`state` 取不到时为 null（并带 stateUnavailable 说明原因），不编；"
                        + "`stateDerived` 是从 remainingEruptPreTime/RemainingEruptTime/RemainingIdleTime(+shouldGoDormant) **推导**的（preErupt/erupting/dormant/idle），不是游戏原始阶段值；"
                        + "不提供 nextEruptionCycle（拿不到周期数），只给剩余秒数与 shouldGoDormant"));
                    sb.Append('}');
                    data = sb.ToString();
                    Log.Info("[geysers] total=" + all.Length + " emitted=" + emitted + " filteredByWorld=" + filteredByWorld
                             + " stateProbeOk=" + probeOk + (stateErr.Count > 0 ? " stateErr=" + stateErr[0] : ""));
                }
                catch (System.Exception e) { fail = e.GetType().Name + ": " + e.Message; }
            }, 5000, 30000, out okT, out busy);

            if (!ran) { status = 503; code = "RETRY_LATER"; message = "main thread busy；请 1 秒后重试"; return null; }
            if (fail != null) { status = 500; code = "GEYSERS_UNAVAILABLE"; message = fail; return null; }
            if (data == null) { status = 500; code = "INTERNAL"; message = "geysers 采集返回空"; return null; }
            JsonCheck.Validate(data, "geysers");   // ★ 下发前自校验：坏 JSON 绝不发出去（0.88.0 那次就是靠人工才发现的）
            return data;
        }

        static string NumOrNull(double? v)
        {
            if (!v.HasValue || double.IsNaN(v.Value) || double.IsInfinity(v.Value)) return "null";
            return v.Value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// 反射尝试读 "喷发阶段"。**已知不可靠**：`Geyser` 成员表里没有公开 phase/state；
        /// 这里只做"有就读、没有就 null"，绝不猜。
        /// </summary>
        static string TryReadPhase(Geyser g, out string via)
        {
            via = null;
            const System.Reflection.BindingFlags F =
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;

            object smi = null;
            var fSmi = typeof(Geyser).GetField("smi", F);
            if (fSmi != null) { try { smi = fSmi.GetValue(g); } catch { } }
            if (smi == null)
            {
                var pSmi = typeof(Geyser).GetProperty("smi", F);
                if (pSmi != null) { try { smi = pSmi.GetValue(g, null); } catch { } }
            }
            if (smi == null) return null;

            var tSmi = smi.GetType();
            var fPhase = tSmi.GetField("phase", F);
            if (fPhase != null)
            {
                try { var v = fPhase.GetValue(smi); if (v != null) { via = "smi.phase"; return v.ToString(); } } catch { }
            }
            var pPhase = tSmi.GetProperty("phase", F);
            if (pPhase != null)
            {
                try { var v = pPhase.GetValue(smi, null); if (v != null) { via = "smi.phase(prop)"; return v.ToString(); } } catch { }
            }
            var fState = tSmi.GetField("state", F);
            if (fState != null)
            {
                try { var v = fState.GetValue(smi); if (v != null) { via = "smi.state"; return v.ToString(); } } catch { }
            }
            return null;
        }
    }
}
