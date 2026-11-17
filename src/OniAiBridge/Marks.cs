using System.Text;

// ⚠️ 不要 using System 之后直接用 Action/DateTime：Assembly-CSharp 里的全局 Action 枚举会遮蔽 System.Action。
namespace OniAiBridge
{
    /// <summary>
    /// M6.4：`GET /v1/marks?x=&amp;y=` —— 查**某一格上所有标记类组件的状态**（dig / deconstruct / clear / harvest）。
    ///
    /// 语义（DESIGN §3.12 字段契约）：
    /// * `marked: true/false` = **查到了**（有/没有标记）；
    /// * `marked: null` + `unavailable: "<原因>"` = **查不到**（例如 `Clearable.isMarkedForClear` 是 nonpub，只能反射）。
    ///   ⇒ 两者**绝不混用**：`false` 是"查了没有"，`null` 是"没查到"。
    ///
    /// ⚠️ 已知局限：游戏的标记组件里**没有任何"谁标的"字段**（`MarkedBy`/`MarkSource` 全 DLL 0 次命中）
    ///   ⇒ 本端点**无法区分"AI 标的"与"玩家手标的"**。`instanceId` 只说明是哪个对象。
    /// </summary>
    internal static class MarksQuery
    {
        public static string Sample(int x, int y, int worldId, out int status, out string code, out string message)
        {
            status = 200; code = null; message = null;

            if (!MainThread.Ready) { status = 503; code = "NOT_READY"; message = "main-thread pump is not running yet"; return null; }

            int cell = -1, foundWorld = -1;
            int? objects = null; int okLayers = 0, errLayers = 0; string scanErr = null;
            bool digMarked = false, deconMarked = false, harvestMarked = false;
            bool harvestReady = false, harvestChore = false, harvestHasHv = false;
            int harvestCount = 0;
            string harvestLayer = null;
            bool? clearMarked = null; string clearErr = null;
            string digName = null, digId = null, deconName = null, harvestName = null, clearName = null;
            string failCode = null, failMsg = null;

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

                    objects = JobQuery.CountObjectsAt(cell, out okLayers, out errLayers, out scanErr);

                    // dig：复用 FindDiggableAt（D 层优先 + 逐层兜底 + 诊断）
                    int hitLayer, digOk, digErrN; string digErr;
                    var dg = JobQuery.FindDiggableAt(cell, out hitLayer, out digOk, out digErrN, out digErr);
                    if (dg != null)
                    {
                        digMarked = true;
                        try
                        {
                            var go = dg.gameObject;
                            digName = go != null ? go.name : null;
                            digId = go != null ? go.GetInstanceID().ToString() : null;
                        }
                        catch { }
                    }
                    if (digErr != null && scanErr == null) scanErr = "dig: " + digErr;

                    // deconstruct：public API（IsMarkedForDeconstruction）
                    var b = JobQuery.FindBuildingAt(cell);
                    if (b != null)
                    {
                        var d = b.GetComponent<Deconstructable>();
                        if (d != null)
                        {
                            deconMarked = d.IsMarkedForDeconstruction();
                            try { deconName = b.Def != null ? b.Def.PrefabID : b.name; } catch { }
                        }
                    }

                    // harvest：**必须与 /v1/job 读同一个字段**（M6.9b 修正）
                    // 之前这里读的是 `Harvestable.CanBeHarvested`（成熟度），却当成"已标记"返回
                    //   ⇒ 与 /v1/job 的 `HarvestDesignatable.MarkedForHarvest` 结论相反（实测 (162,190) VineMother）。
                    // 现在：枚举该格所有带 `HarvestDesignatable` 的对象，逐个读 MarkedForHarvest / CanBeHarvested / HasChore。
                    {
                        var objs = JobQuery.EnumerateCellObjects(cell);
                        for (int i = 0; i < objs.Count; i++)
                        {
                            var co = objs[i];
                            if (co.Designatable == null) continue;
                            harvestCount++;
                            try { if (co.Designatable.MarkedForHarvest) harvestMarked = true; } catch { }
                            try { if (co.Designatable.CanBeHarvested()) harvestReady = true; } catch { }
                            try { if (co.Harvest != null) { harvestHasHv = true; if (co.Harvest.HasChore()) harvestChore = true; } } catch { }
                            if (harvestName == null)
                            {
                                harvestName = co.PrefabId;
                                harvestLayer = JobQuery.LayerEnumName2(co.Layer);
                            }
                        }
                    }

                    // clear：**没有 public 读入口** ⇒ 反射；读不到就给 null + 原因（不假装 false）
                    var cl = JobQuery.FindClearableAt(cell);
                    if (cl != null)
                    {
                        clearMarked = JobQuery.TryReadMarkedForClear(cl, out clearErr);
                        try { clearName = cl.gameObject != null ? cl.gameObject.name : null; } catch { }
                    }
                }
                catch (System.Exception e) { failCode = "INTERNAL"; failMsg = e.GetType().Name + ": " + e.Message; }
            }, 5000);

            if (failCode != null)
            {
                status = failCode == "INTERNAL" ? 500 : 400;
                code = failCode; message = failMsg;
                Log.Info("[marks] x=" + x + " y=" + y + " ⇒ " + status + " " + failCode + ": " + failMsg);
                return null;
            }

            var sb = new StringBuilder(768);
            sb.Append("{\"target\":{\"worldId\":").Append(foundWorld);
            sb.Append(",\"x\":").Append(x).Append(",\"y\":").Append(y);
            sb.Append(",\"cell\":").Append(cell).Append('}');

            sb.Append(",\"dig\":{\"marked\":").Append(digMarked ? "true" : "false");
            sb.Append(",\"objectName\":").Append(Snap.Str(digName));
            sb.Append(",\"instanceId\":").Append(digId ?? "null").Append('}');

            sb.Append(",\"deconstruct\":{\"marked\":").Append(deconMarked ? "true" : "false");
            sb.Append(",\"objectName\":").Append(Snap.Str(deconName)).Append('}');

            sb.Append(",\"clear\":{\"marked\":").Append(clearMarked.HasValue ? (clearMarked.Value ? "true" : "false") : "null");
            sb.Append(",\"objectName\":").Append(Snap.Str(clearName));
            if (!clearMarked.HasValue && clearErr != null)
                sb.Append(",\"unavailable\":").Append(Snap.Str(clearErr));
            sb.Append('}');

            sb.Append(",\"harvest\":{\"marked\":").Append(harvestMarked ? "true" : "false");
            sb.Append(",\"objectName\":").Append(Snap.Str(harvestName));
            sb.Append(",\"layer\":").Append(Snap.Str(harvestLayer));
            sb.Append(",\"readyToHarvest\":").Append(harvestReady ? "true" : "false");
            sb.Append(",\"choreCreated\":").Append(harvestChore ? "true" : "false");
            sb.Append(",\"hasHarvestable\":").Append(harvestHasHv ? "true" : "false");
            sb.Append(",\"count\":").Append(harvestCount).Append('}');

            sb.Append(",\"objectsAtCell\":").Append(objects.HasValue ? objects.Value.ToString() : "null");
            sb.Append(",\"scan\":{\"okLayers\":").Append(okLayers);
            sb.Append(",\"errLayers\":").Append(errLayers);
            sb.Append(",\"err\":").Append(Snap.Str(scanErr)).Append('}');
            sb.Append(",\"sourceAgnostic\":true");
            sb.Append(",\"note\":").Append(Snap.Str("标记不记录来源：无法区分 AI 标的与玩家手标的"));
            sb.Append('}');

            Log.Info("[marks] x=" + x + " y=" + y + " cell=" + cell + " world=" + foundWorld
                     + " dig=" + digMarked + "(" + (digName ?? "<null>") + "#" + (digId ?? "<null>") + ")"
                     + " deconstruct=" + deconMarked + " clear=" + (clearMarked.HasValue ? clearMarked.Value.ToString() : "<null>")
                     + " harvest=" + harvestMarked + " objects=" + (objects.HasValue ? objects.Value.ToString() : "<null>")
                     + " okLayers=" + okLayers + " errLayers=" + errLayers + " err=" + (scanErr ?? "<none>"));
            return sb.ToString();
        }
    }
}