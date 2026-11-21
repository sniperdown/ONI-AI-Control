using System;
using System.Reflection;
using System.Text;

namespace OniAiBridge
{
    /// <summary>
    /// `POST /v1/job {"type":"destroy","x":..,"y":..,"confirm":true}` —— 沙盒的「摧毁」。
    ///
    /// 与 `deconstruct` 的**本质区别**（别混用）：
    ///   * `deconstruct` = 派活给复制人拆（要人等、要路、材料退回）
    ///   * `destroy`    = **立即抹掉这一格**（沙盒专用、无任务、无小人、不回材料）
    ///
    /// 实现照抄游戏的沙盒摧毁工具（IL 实证）：`SandboxDestroyerTool.OnPaintCell` 的方法体只有一句
    /// `call GameUtil.DestroyCell`。所以这里直接反射调它，不驱动 UI 工具。
    ///
    /// `GameUtil.DestroyCell` 有 **4 个参数**（本次只确定第 1 个是 cell）。其余参数的**类型与名字**
    /// 由反射读出来写进响应（`sig` 字段），并按"看起来像特效/音效/弹窗的 bool"取 false、其余取默认值，
    /// 这样即使语义记错也只会影响表现层，不会误删别的格子。
    /// </summary>
    internal static class DestroyJob
    {
        internal static bool IsDestroyType(string t0) { return t0 == "destroy"; }

        internal static string Execute(string body, out int status, out string code, out string message, out string details)
        {
            status = 200; code = null; message = null; details = null;

            if (!Gate.CheatsUnlocked)
            {
                status = 403; code = Gate.UnavailableCode();
                message = Gate.UnavailableReason() + "（/v1/job type=destroy 属于沙盒作弊能力：它立即抹掉一格，不产生任何施工任务）";
                return null;
            }

            int x, y;
            if (!JobQuery.TryInt(body, "x", out x) || !JobQuery.TryInt(body, "y", out y))
            {
                status = 400; code = "BAD_REQUEST"; message = "需要 x / y（世界内格子坐标）";
                return null;
            }
            int cell = Grid.XYToCell(x, y);
            if (!Grid.IsValidCell(cell))
            {
                status = 400; code = "OUT_OF_WORLD"; message = "格子不在世界内: (" + x + "," + y + ")";
                return null;
            }

            // 记录摧毁前的事实，供核对
            string beforeElement = null;
            double beforeMass = 0;
            string beforeBuilding = null;
            try { var el = Grid.Element[cell]; beforeElement = el == null ? null : el.id.ToString(); } catch { }
            try { beforeMass = Grid.Mass[cell]; } catch { }
            try { var b = JobQuery.FindBuildingAt(cell); if (b != null) beforeBuilding = b.name; } catch { }

            var sig = new StringBuilder(120);
            bool invoked = false;
            string err = null;
            try
            {
                var t = typeof(GameUtil);
                MethodInfo mi = null;
                foreach (var cand in t.GetMethods(BindingFlags.Public | BindingFlags.Static))
                {
                    if (cand.Name == "DestroyCell") { mi = cand; break; }
                }
                if (mi == null) { err = "找不到 GameUtil.DestroyCell"; }
                else
                {
                    var ps = mi.GetParameters();
                    var argv = new object[ps.Length];
                    sig.Append('(');
                    for (int i = 0; i < ps.Length; i++)
                    {
                        if (i > 0) sig.Append(", ");
                        sig.Append(ps[i].ParameterType.Name).Append(' ').Append(ps[i].Name);
                        string pn = (ps[i].Name ?? string.Empty).ToLowerInvariant();
                        if (i == 0) argv[i] = cell;
                        else if (ps[i].ParameterType == typeof(bool))
                        {
                            // ★ 实测（0.114.2）：沙盒摧毁工具传的是 true/true（IL 里两个 ldc.i4.1）。
                            //   `deleteMinions`=true 才会顺手清掉该格上的小动物；
                            //   `destroyNaturalBackwall`=true 才会连"天然物块"一起抹掉 ——
                            //   传 false 时对天然泥土无效（实测 after.solid 仍为 true）。
                            argv[i] = true;
                        }
                        else if (ps[i].Name != null && ps[i].Name.ToLowerInvariant().Contains("eventsource"))
                            argv[i] = SandboxEventSource();   // 必须是 CellEventLogger.Instance.SandBoxTool
                        else argv[i] = DefaultOf(ps[i].ParameterType);
                    }
                    sig.Append(')');
                    mi.Invoke(null, argv);
                    invoked = true;
                }
            }
            catch (Exception e)
            {
                err = (e.InnerException != null ? e.InnerException.GetType().Name + ": " + e.InnerException.Message
                                               : e.GetType().Name + ": " + e.Message);
            }

            bool stillSolid = false, hasBuilding = false;
            try { stillSolid = Grid.Solid[cell]; } catch { }
            try { hasBuilding = JobQuery.FindBuildingAt(cell) != null; } catch { }

            // ★ 与 cancel 同一条规矩：`DestroyCell` 的清理也是**帧末生效**，
            //   同一帧回读必然看到旧内容（实测 Dirt 那格当帧仍 solid=true，两秒后才是 Vacuum）。
            //   ⇒ 不能谎报 cleared=true，也不该谎报 false；如实给 pendingFrame。
            bool pendingFrame = invoked;
            bool clearedNow = !stillSolid && !hasBuilding;

            var sb = new StringBuilder(512);
            sb.Append("{\"type\":\"destroy\"");
            sb.Append(",\"target\":{\"x\":").Append(x).Append(",\"y\":").Append(y).Append(",\"cell\":").Append(cell).Append('}');
            sb.Append(",\"sig\":").Append(Snap.Str(sig.Length > 0 ? "GameUtil.DestroyCell" + sig.ToString() : null));
            sb.Append(",\"invoked\":").Append(invoked ? "true" : "false");
            sb.Append(",\"error\":").Append(err == null ? "null" : Snap.Str(err));
            sb.Append(",\"before\":{\"element\":").Append(beforeElement == null ? "null" : Snap.Str(beforeElement))
              .Append(",\"massKg\":").Append(beforeMass.ToString("0.###"))
              .Append(",\"building\":").Append(beforeBuilding == null ? "null" : Snap.Str(beforeBuilding)).Append('}');
            sb.Append(",\"after\":{\"solid\":").Append(stillSolid ? "true" : "false")
              .Append(",\"building\":").Append(hasBuilding ? "true" : "false").Append('}');
            sb.Append(",\"cleared\":").Append(pendingFrame ? "null" : (clearedNow ? "true" : "false"));
            sb.Append(",\"pendingFrame\":").Append(pendingFrame ? "true" : "false");
            sb.Append(",\"jobState\":").Append(Snap.Str(
                !invoked ? "failed" : (pendingFrame ? "destroy-scheduled" : (clearedNow ? "destroyed" : "still-there"))));
            sb.Append(",\"note\":").Append(Snap.Str(
                "沙盒作弊：立即抹掉该格（等价玩家的「摧毁」工具），不产生施工任务、小人不会来、材料不退回。"
                + "要正常拆建筑请用 type=deconstruct（派活、需要人等）。sig 是反射读到的真实签名。"));
            sb.Append('}');

            Log.Info("[job] type=destroy x=" + x + " y=" + y + " cell=" + cell
                     + " invoked=" + invoked + " err=" + (err ?? "<none>")
                     + " before=" + (beforeBuilding ?? beforeElement) + " solidAfter=" + stillSolid);
            return sb.ToString();
        }

        /// <summary>
        /// 取 `CellEventLogger.Instance.SandBoxTool` —— 摧毁工具传给 `DestroyCell` 的事件来源。
        /// 全用反射：这两个成员在 mod 编译期不确定是字段还是属性。
        /// </summary>
        static object SandboxEventSource()
        {
            try
            {
                var t = typeof(CellEventLogger);
                object inst = null;
                var f = t.GetField("Instance", BindingFlags.Public | BindingFlags.Static);
                if (f != null) inst = f.GetValue(null);
                if (inst == null)
                {
                    var p = t.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static);
                    if (p != null) inst = p.GetValue(null);
                }
                if (inst == null) return null;
                var sp = inst.GetType().GetProperty("SandBoxTool");
                if (sp != null) return sp.GetValue(inst);
                var sf = inst.GetType().GetField("SandBoxTool");
                if (sf != null) return sf.GetValue(inst);
            }
            catch (Exception e) { Log.Info("[destroy] SandboxEventSource 失败: " + e.GetType().Name); }
            return null;
        }

        static object DefaultOf(Type t)
        {
            if (t == typeof(bool)) return false;
            if (t == typeof(int)) return 0;
            if (t == typeof(float)) return 0f;
            if (t == typeof(double)) return 0d;
            return t.IsValueType ? Activator.CreateInstance(t) : null;
        }
    }
}
