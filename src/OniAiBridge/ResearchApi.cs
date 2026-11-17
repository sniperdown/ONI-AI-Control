using System.Text;

// ⚠️ 不要 using System 之后直接用 Action/DateTime：Assembly-CSharp 里的全局 Action 枚举会遮蔽 System.Action。
namespace OniAiBridge
{
    /// <summary>
    /// M6.40：研究（只读 + 选目标 + 作弊直完成）。
    ///
    /// 为什么走 API 不走 UI：实测 `/v1/build-menu?screen=ResearchScreen` 只有 5 个窗口控件
    /// （CloseButton / ClearButton / ZoomIn / ZoomOut / 搜索框清空），**科技节点不是 KButton**，
    /// `/v1/ui/click` 点不到；`ManagementMenu` 里的「研究」页签在未激活屏幕上扫不到。
    ///
    /// 用的都是**公开成员**（成员表已核）：
    /// * `Research.Instance`（public static 字段）
    /// * `Research.GetTechInstance(string)` / `GetActiveResearch()` / `GetTargetResearch()`
    /// * `Research.SetActiveResearch(Tech, bool)`（public）
    /// * `TechInstance.IsComplete()` / `GetTotalPercentageComplete()` / `Purchased()`（public）
    /// * 非公开的只有 `Research.techs`（List&lt;TechInstance&gt;），用反射**只读**读取。
    /// </summary>
    internal static class ResearchApi
    {
        const System.Reflection.BindingFlags Flags =
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;

        static System.Collections.Generic.IList<TechInstance> AllTechs(Research r, out string how)
        {
            how = null;
            try
            {
                var f = typeof(Research).GetField("techs", Flags);
                if (f != null)
                {
                    var list = f.GetValue(r) as System.Collections.Generic.IList<TechInstance>;
                    if (list != null) { how = "Research.techs(反射只读)"; return list; }
                }
            }
            catch (System.Exception e) { how = "读 Research.techs 失败: " + e.GetType().Name; }
            return null;
        }

        static string TechId(Tech t) { try { return t != null ? t.Id : null; } catch { return null; } }
        static string TechName(Tech t)
        {
            try
            {
                if (t == null || t.Name == null) return null;
                try { return Catalog.PlainText(t.Name); } catch { }
                try { return t.Name.ToString(); } catch { }
            }
            catch { }
            return null;
        }

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
                    var r = Research.Instance;
                    if (r == null) { fail = "Research.Instance 为 null（没进游戏 / 还没初始化）"; return; }

                    string how;
                    var techs = AllTechs(r, out how);
                    if (techs == null) { fail = "读不到 Research.techs：" + (how ?? "字段不存在"); return; }

                    string activeId = null, targetId = null;
                    try { var a = r.GetActiveResearch(); if (a != null) activeId = TechId(a.tech); } catch { }
                    try { var t2 = r.GetTargetResearch(); if (t2 != null) targetId = TechId(t2.tech); } catch { }

                    var sb = new StringBuilder(4096);
                    sb.Append("{\"source\":").Append(Snap.Str("Research.Instance（" + (how ?? "") + "）"));
                    sb.Append(",\"techCount\":").Append(techs.Count);
                    sb.Append(",\"activeTechId\":").Append(Snap.Str(activeId));
                    sb.Append(",\"targetTechId\":").Append(Snap.Str(targetId));
                    sb.Append(",\"researchPointInventory\":{\"byType\":");
                    try
                    {
                        var d = r.globalPointInventory != null ? r.globalPointInventory.PointsByTypeID : null;
                        if (d == null) sb.Append("null");
                        else
                        {
                            sb.Append('{');
                            bool f1 = true;
                            foreach (var kv in d)
                            {
                                if (!f1) sb.Append(',');
                                f1 = false;
                                sb.Append(Snap.Str(kv.Key)).Append(':').Append(Snap.F(kv.Value));
                            }
                            sb.Append('}');
                        }
                    }
                    catch { sb.Append("null"); }
                    sb.Append('}');

                    sb.Append(",\"completed\":[");
                    int ec = 0;
                    for (int i = 0; i < techs.Count; i++)
                    {
                        var ti = techs[i];
                        if (ti == null) continue;
                        bool done = false;
                        try { done = ti.IsComplete(); } catch { }
                        if (!done) continue;
                        if (ec > 0) sb.Append(',');
                        ec++;
                        sb.Append("{\"techId\":").Append(Snap.Str(TechId(ti.tech)));
                        sb.Append(",\"name\":").Append(Snap.Str(TechName(ti.tech))).Append('}');
                    }
                    sb.Append("],\"completedCount\":").Append(ec);

                    sb.Append(",\"available\":[");
                    int ea = 0;
                    for (int i = 0; i < techs.Count; i++)
                    {
                        var ti = techs[i];
                        if (ti == null) continue;
                        bool done = false; float pct = 0f;
                        try { done = ti.IsComplete(); } catch { }
                        if (done) continue;
                        try { pct = ti.GetTotalPercentageComplete(); } catch { }
                        bool researched = false;
                        try { if (ti.tech != null) researched = r.IsBeingResearched(ti.tech); } catch { }
                        if (ea > 0) sb.Append(',');
                        ea++;
                        sb.Append("{\"techId\":").Append(Snap.Str(TechId(ti.tech)));
                        sb.Append(",\"name\":").Append(Snap.Str(TechName(ti.tech)));
                        sb.Append(",\"progressPct\":").Append(Snap.F(pct));
                        sb.Append(",\"isActive\":").Append(researched ? "true" : "false");
                        sb.Append('}');
                    }
                    sb.Append("],\"availableCount\":").Append(ea);
                    sb.Append(",\"note\":").Append(Snap.Str(
                        "只读：不改变任何研究状态。`isActive:true` 表示游戏当前正在研究它；"
                        + "科技节点不是 KButton（实测 ResearchScreen 只有 5 个窗口控件），所以 UI 点击不可行"));
                    sb.Append('}');
                    data = sb.ToString();
                }
                catch (System.Exception e) { fail = e.GetType().Name + ": " + e.Message; }
            }, 5000, 30000, out okT, out busy);

            if (!ran) { status = 503; code = "RETRY_LATER"; message = "main thread busy；请 1 秒后重试"; return null; }
            if (fail != null) { status = 500; code = "RESEARCH_UNAVAILABLE"; message = fail; return null; }
            if (data == null) { status = 500; code = "INTERNAL"; message = "research 采集返回空"; return null; }
            return data;
        }

        /// <summary>选研究方向（玩家行为：只是"设定当前研究目标"，不需要作弊闸门）。</summary>
        public static string SetTarget(string techId, out int status, out string code, out string message)
        {
            status = 200; code = null; message = null;
            if (string.IsNullOrEmpty(techId)) { status = 400; code = "BAD_REQUEST"; message = "需要 techId（可先 GET /v1/research 看 available）"; return null; }
            if (!MainThread.Ready) { status = 503; code = "NOT_READY"; message = "main-thread pump is not running yet"; return null; }

            string data = null, fail = null, failCode = null;
            int okT; bool busy;
            bool ran = MainThread.InvokeAdaptive(() =>
            {
                try
                {
                    var r = Research.Instance;
                    if (r == null) { failCode = "RESEARCH_UNAVAILABLE"; fail = "Research.Instance 为 null"; return; }

                    TechInstance ti = null;
                    try { ti = r.GetTechInstance(techId); } catch { }
                    if (ti == null)
                    {
                        string how;
                        var all = AllTechs(r, out how);
                        if (all != null)
                        {
                            for (int i = 0; i < all.Count; i++)
                            {
                                var x = all[i];
                                if (x == null || x.tech == null) continue;
                                if (string.Equals(TechId(x.tech), techId, System.StringComparison.OrdinalIgnoreCase)) { ti = x; break; }
                            }
                        }
                    }
                    if (ti == null || ti.tech == null) { failCode = "UNKNOWN_TECH"; fail = "没有这个 techId：" + techId; return; }

                    bool wasComplete = false;
                    try { wasComplete = ti.IsComplete(); } catch { }
                    if (wasComplete) { failCode = "ALREADY_COMPLETE"; fail = "该科技已完成：" + techId; return; }

                    r.SetActiveResearch(ti.tech, true);

                    string nowActive = null;
                    try { var a = r.GetActiveResearch(); if (a != null) nowActive = TechId(a.tech); } catch { }

                    var sb = new StringBuilder(384);
                    sb.Append("{\"type\":\"research\"");
                    sb.Append(",\"techId\":").Append(Snap.Str(techId));
                    sb.Append(",\"name\":").Append(Snap.Str(TechName(ti.tech)));
                    sb.Append(",\"jobState\":").Append(Snap.Str("target-set"));
                    sb.Append(",\"activeTechIdAfter\":").Append(Snap.Str(nowActive));
                    sb.Append(",\"applied\":").Append(string.Equals(nowActive, techId, System.StringComparison.OrdinalIgnoreCase) ? "true" : "false");
                    sb.Append(",\"method\":").Append(Snap.Str("Research.SetActiveResearch(Tech, true)（public）"));
                    sb.Append(",\"note\":").Append(Snap.Str("玩家等价：只设定研究目标，之后小人会去研究站工作；不修改完成状态"));
                    sb.Append('}');
                    data = sb.ToString();
                    Log.Info("[research] 设定目标 techId=" + techId + " → 当前=" + (nowActive ?? "<null>"));
                }
                catch (System.Exception e) { failCode = "REFLECT_INVOKE_FAILED"; fail = e.GetType().Name + ": " + e.Message; }
            }, 5000, 30000, out okT, out busy);

            if (!ran) { status = 503; code = "RETRY_LATER"; message = "main thread busy；请 1 秒后重试"; return null; }
            if (failCode != null)
            {
                status = (failCode == "UNKNOWN_TECH" || failCode == "ALREADY_COMPLETE") ? 400 : 500;
                code = failCode; message = fail;
                return null;
            }
            return data;
        }

        /// <summary>作弊：直接完成该科技（`TechInstance.Purchased()`，public）。</summary>
        public static string InstantComplete(string techId, out int status, out string code, out string message)
        {
            status = 200; code = null; message = null;
            if (string.IsNullOrEmpty(techId)) { status = 400; code = "BAD_REQUEST"; message = "需要 techId"; return null; }
            if (!Gate.CheatsUnlocked) { status = 403; code = Gate.UnavailableCode(); message = "research instant=true 是作弊操作 ⇒ " + Gate.UnavailableReason(); return null; }
            if (!MainThread.Ready) { status = 503; code = "NOT_READY"; message = "main-thread pump is not running yet"; return null; }

            string data = null, fail = null, failCode = null;
            int okT; bool busy;
            bool ran = MainThread.InvokeAdaptive(() =>
            {
                try
                {
                    var r = Research.Instance;
                    if (r == null) { failCode = "RESEARCH_UNAVAILABLE"; fail = "Research.Instance 为 null"; return; }
                    var ti = r.GetTechInstance(techId);
                    if (ti == null) { failCode = "UNKNOWN_TECH"; fail = "没有这个 techId：" + techId; return; }

                    bool before = false;
                    try { before = ti.IsComplete(); } catch { }
                    if (!before) ti.Purchased();
                    bool after = false;
                    try { after = ti.IsComplete(); } catch { }

                    var sb = new StringBuilder(384);
                    sb.Append("{\"type\":\"research\"");
                    sb.Append(",\"techId\":").Append(Snap.Str(techId));
                    sb.Append(",\"name\":").Append(Snap.Str(TechName(ti.tech)));
                    sb.Append(",\"jobState\":").Append(Snap.Str("cheat-research"));
                    sb.Append(",\"spawned\":false");
                    sb.Append(",\"materialsConsumed\":false");
                    sb.Append(",\"wasCompleteBefore\":").Append(before ? "true" : "false");
                    sb.Append(",\"isCompleteAfter\":").Append(after ? "true" : "false");
                    sb.Append(",\"method\":").Append(Snap.Str("TechInstance.Purchased()（public）"));
                    sb.Append(",\"note\":").Append(Snap.Str("作弊路径：直接标记该科技完成，不消耗研究点数、不需要小人研究"));
                    sb.Append('}');
                    data = sb.ToString();
                    Log.Info("[research] 作弊完成 techId=" + techId + " before=" + before + " after=" + after);
                }
                catch (System.Exception e) { failCode = "REFLECT_INVOKE_FAILED"; fail = e.GetType().Name + ": " + e.Message; }
            }, 5000, 30000, out okT, out busy);

            if (!ran) { status = 503; code = "RETRY_LATER"; message = "main thread busy；请 1 秒后重试"; return null; }
            if (failCode != null)
            {
                status = (failCode == "UNKNOWN_TECH") ? 400 : 500;
                code = failCode; message = fail;
                return null;
            }
            return data;
        }
    }
}
