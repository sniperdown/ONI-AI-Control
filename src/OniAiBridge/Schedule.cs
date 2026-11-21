using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Text;

namespace OniAiBridge
{
    /// <summary>
    /// `/v1/schedule` —— 作息表（日程）的**读取与修改**。0.116.0。
    ///
    /// 读取：`ScheduleManager.Instance` → `schedules` 字段 → 每张 `Schedule` 的 `name` / `blocks` / `assigned`。
    /// 修改（两条，都是玩家在"作息"界面里能做的事，所以**不挂作弊闸门**）：
    ///   * `assign`   —— 把某个复制人指派到某张作息表（`Schedule.Assign`）
    ///   * `set_block` —— 把某张表某小时的区块类型换掉（`Schedule.SetBlockGroup(hour, ScheduleGroup)`）
    ///
    /// 为什么大量用反射：`Assign` / `SetBlockGroup` 的参数类型在编译期不确定
    /// （`Schedulable` 还是 `Ref<Schedulable>`、`ScheduleGroup` 还是它的 Resource 包装），
    /// 与其猜错一次就崩，不如反射调用并把真实签名回显到响应里（`sig` 字段）——
    /// 这和 `type=destroy` 学 `GameUtil.DestroyCell` 是同一套做法。
    /// </summary>
    internal static class ScheduleApi
    {
        const BindingFlags InstAny = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

        /// <summary>极简 JSON 取字符串字段（与 Door.cs 里那份同形，各自保留一份避免跨文件可见性问题）。</summary>
        static string MatchString(string body, string key)
        {
            if (string.IsNullOrEmpty(body)) return null;
            string pat = "\"" + key + "\"";
            int k = body.IndexOf(pat, StringComparison.OrdinalIgnoreCase);
            if (k < 0) return null;
            int colon = body.IndexOf(':', k + pat.Length);
            if (colon < 0) return null;
            int q1 = body.IndexOf('"', colon + 1);
            if (q1 < 0) return null;
            int q2 = body.IndexOf('"', q1 + 1);
            if (q2 < 0) return null;
            return body.Substring(q1 + 1, q2 - q1 - 1);
        }

        // ---------------- 读取 ----------------

        internal static string Handle(string body, bool isPost, out int status, out string code, out string message)
        {
            status = 200; code = null; message = null;
            if (isPost) return Post(body, out status, out code, out message);
            return Get(out status, out code, out message);
        }

        static object Manager()
        {
            var t = typeof(ScheduleManager);
            var f = t.GetField("Instance", BindingFlags.Public | BindingFlags.Static);
            if (f != null) return f.GetValue(null);
            var p = t.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static);
            if (p != null) return p.GetValue(null);
            return null;
        }

        static IList Schedules(object mgr)
        {
            if (mgr == null) return null;
            var f = mgr.GetType().GetField("schedules", InstAny);
            return f == null ? null : f.GetValue(mgr) as IList;
        }

        static object BlockOf(object sched, int hour)
        {
            try
            {
                var m = sched.GetType().GetMethod("GetBlock", InstAny);
                if (m != null) return m.Invoke(sched, new object[] { hour });
            }
            catch { }
            return null;
        }

        static string Prop(object o, string name)
        {
            if (o == null) return null;
            try
            {
                var t = o.GetType();
                var p = t.GetProperty(name, InstAny);
                if (p != null) { var v = p.GetValue(o); return v == null ? null : v.ToString(); }
                var f = t.GetField(name, InstAny);
                if (f != null) { var v = f.GetValue(o); return v == null ? null : v.ToString(); }
            }
            catch { }
            return null;
        }

        static string Get(out int status, out string code, out string message)
        {
            status = 200; code = null; message = null;
            var mgr = Manager();
            if (mgr == null) { status = 503; code = "NO_COLONY"; message = "ScheduleManager.Instance 为 null（可能不在殖民地场景）"; return null; }

            var list = Schedules(mgr);
            if (list == null) { status = 500; code = "INTERNAL"; message = "读不到 schedules 字段"; return null; }

            int currentHour = -1;
            try { var mh = mgr.GetType().GetMethod("GetCurrentHour", InstAny); if (mh != null) currentHour = (int)mh.Invoke(mgr, null); } catch { }

            var sb = new StringBuilder(2048);
            sb.Append("{\"type\":\"schedule\"");
            sb.Append(",\"currentHour\":").Append(currentHour);
            sb.Append(",\"count\":").Append(list.Count);
            sb.Append(",\"schedules\":[");
            int n = 0;
            foreach (var s in list)
            {
                if (s == null) continue;
                if (n++ > 0) sb.Append(',');
                sb.Append("{\"name\":").Append(Snap.Str(Prop(s, "name")));
                sb.Append(",\"isDefaultForBionics\":").Append(string.Equals(Prop(s, "isDefaultForBionics"), "True", StringComparison.OrdinalIgnoreCase) ? "true" : "false");

                // blocks：每天 24 小时各是什么区块
                sb.Append(",\"blocks\":[");
                int bn = 0;
                for (int h = 0; h < 24; h++)
                {
                    var b = BlockOf(s, h);
                    if (b == null) continue;
                    if (bn++ > 0) sb.Append(',');
                    sb.Append("{\"hour\":").Append(h)
                      .Append(",\"group\":").Append(Snap.Str(Prop(b, "GroupId")))
                      .Append(",\"blockName\":").Append(Snap.Str(Prop(b, "name"))).Append('}');
                }
                sb.Append(']');

                // assigned：挂在这张表上的复制人
                sb.Append(",\"assigned\":[");
                var assigned = s.GetType().GetMethod("GetAssigned", InstAny)?.Invoke(s, null) as IEnumerable;
                int an = 0;
                if (assigned != null)
                {
                    foreach (var a in assigned)
                    {
                        if (a == null) continue;
                        string nm = null;
                        try
                        {
                            // GetAssigned() 返回的多半是 Ref<Schedulable> 或 Schedulable
                            object inner = a;
                            var vt = a.GetType();
                            var fRef = vt.GetField("target", InstAny) ?? vt.GetField("Target", InstAny);
                            if (fRef != null) inner = fRef.GetValue(a);
                            var mi = inner as MinionIdentity;
                            if (mi != null) nm = mi.GetProperName();
                            else
                            {
                                var go = inner as UnityEngine.Component;
                                if (go != null) { var mid = go.GetComponent<MinionIdentity>(); if (mid != null) nm = mid.GetProperName(); }
                            }
                        }
                        catch { }
                        if (nm == null) continue;
                        if (an++ > 0) sb.Append(',');
                        sb.Append(Snap.Str(nm));
                    }
                }
                sb.Append("]}");
            }
            sb.Append(']');
            sb.Append(",\"note\":").Append(Snap.Str(
                "blocks 是每天 24 小时各自的区块（group 是 ScheduleGroup 的 id，如 Recreation/Sleep/Work/Anything）；"
                + "assigned 是挂在这张表上的复制人名字。改法：POST {\"action\":\"assign\"|\"set_block\",...}"));
            sb.Append('}');
            return sb.ToString();
        }

        // ---------------- 修改 ----------------

        static string Post(string body, out int status, out string code, out string message)
        {
            status = 200; code = null; message = null;
            var mgr = Manager();
            if (mgr == null) { status = 503; code = "NO_COLONY"; message = "ScheduleManager.Instance 为 null"; return null; }

            string action = MatchString(body, "action");
            if (string.IsNullOrEmpty(action)) { status = 400; code = "BAD_REQUEST"; message = "需要 action（assign | set_block）"; return null; }
            string schedName = MatchString(body, "schedule");

            object sched = null;
            var list = Schedules(mgr);
            if (list != null)
            {
                foreach (var s in list)
                {
                    if (s == null) continue;
                    var nm = Prop(s, "name");
                    if (schedName == null || string.Equals(nm, schedName, StringComparison.OrdinalIgnoreCase)) { sched = s; break; }
                }
            }
            if (sched == null) { status = 404; code = "NOT_FOUND_SCHEDULE"; message = "找不到作息表：" + (schedName ?? "<未指定>"); return null; }

            if (string.Equals(action, "assign", StringComparison.OrdinalIgnoreCase)) return Assign(mgr, sched, body, out status, out code, out message);
            if (string.Equals(action, "set_block", StringComparison.OrdinalIgnoreCase)) return SetBlock(sched, body, out status, out code, out message);

            status = 400; code = "BAD_REQUEST"; message = "action 只能 assign | set_block，收到: " + action;
            return null;
        }

        static object FindDupeSchedulable(string name, out string dupeName)
        {
            dupeName = null;
            try
            {
                var ids = Components.LiveMinionIdentities;
                if (ids == null) return null;
                foreach (var mi in ids.Items)
                {
                    if (mi == null) continue;
                    var nm = mi.GetProperName();
                    if (!string.Equals(nm, name, StringComparison.OrdinalIgnoreCase)) continue;
                    dupeName = nm;
                    return mi.GetComponent<Schedulable>();
                }
            }
            catch { }
            return null;
        }

        static string Assign(object mgr, object sched, string body, out int status, out string code, out string message)
        {
            status = 200; code = null; message = null;
            string dupeName = MatchString(body, "dupe");
            if (string.IsNullOrEmpty(dupeName)) { status = 400; code = "BAD_REQUEST"; message = "需要 dupe（复制人名字）"; return null; }
            string realName;
            var sch = FindDupeSchedulable(dupeName, out realName);
            if (sch == null) { status = 404; code = "NOT_FOUND_DUPE"; message = "找不到复制人或它没有 Schedulable：" + dupeName; return null; }

            var assignM = sched.GetType().GetMethod("Assign", InstAny);
            var beforeAssigned = Prop(sched, "name");
            string sig = null; bool invoked = false; string err = null;
            try
            {
                var ps = assignM.GetParameters();
                sig = "Schedule.Assign(" + ps[0].ParameterType.Name + " " + ps[0].Name + ")";
                // 尝试直接传 Schedulable；若参数类型是 Ref<Schedulable> 需要包一层
                object arg = sch;
                var pt = ps[0].ParameterType;
                if (pt != sch.GetType() && pt.Name.StartsWith("Ref", StringComparison.Ordinal))
                {
                    var ctor = pt.GetConstructor(new[] { sch.GetType() });
                    if (ctor != null) arg = ctor.Invoke(new object[] { sch });
                }
                assignM.Invoke(sched, new object[] { arg });
                invoked = true;
            }
            catch (Exception e)
            {
                err = (e.InnerException != null ? e.InnerException.GetType().Name + ": " + e.InnerException.Message : e.GetType().Name + ": " + e.Message);
            }

            // 回读：这个复制人现在挂在哪张表上
            string nowSchedule = null;
            try { var g = sch.GetType().GetMethod("GetSchedule", InstAny); if (g != null) nowSchedule = Prop(g.Invoke(sch, null), "name"); } catch { }

            var sb = new StringBuilder(384);
            sb.Append("{\"type\":\"schedule\",\"action\":\"assign\"");
            sb.Append(",\"dupe\":").Append(Snap.Str(realName));
            sb.Append(",\"schedule\":").Append(Snap.Str(beforeAssigned));
            sb.Append(",\"sig\":").Append(sig == null ? "null" : Snap.Str(sig));
            sb.Append(",\"invoked\":").Append(invoked ? "true" : "false");
            sb.Append(",\"error\":").Append(err == null ? "null" : Snap.Str(err));
            sb.Append(",\"readback\":{\"dupeScheduleNow\":").Append(nowSchedule == null ? "null" : Snap.Str(nowSchedule)).Append('}');
            sb.Append(",\"ok\":").Append(invoked && string.Equals(nowSchedule, beforeAssigned) ? "true" : "false");
            sb.Append(",\"note\":").Append(Snap.Str("指派作息表；回读 dupeScheduleNow 必须等于目标表名才算成功"));
            sb.Append('}');
            return sb.ToString();
        }

        static string SetBlock(object sched, string body, out int status, out string code, out string message)
        {
            status = 200; code = null; message = null;
            int hour;
            if (!JobQuery.TryInt(body, "hour", out hour) || hour < 0 || hour > 23)
            {
                status = 400; code = "BAD_REQUEST"; message = "需要 hour（0..23）";
                return null;
            }
            string group = MatchString(body, "group");
            if (string.IsNullOrEmpty(group)) { status = 400; code = "BAD_REQUEST"; message = "需要 group（ScheduleGroup id，如 Sleep / Recreation / Work / Anything）"; return null; }

            // ScheduleGroup 是 Resource：从 Db 里按 id 找
            object grp = FindGroup(group);
            string sig = null; bool invoked = false; string err = null;
            if (grp == null) err = "找不到 ScheduleGroup: " + group;
            else
            {
                try
                {
                    var m = sched.GetType().GetMethod("SetBlockGroup", InstAny);
                    var ps = m.GetParameters();
                    sig = "Schedule.SetBlockGroup(" + ps[0].ParameterType.Name + " " + ps[0].Name + ", " + ps[1].ParameterType.Name + " " + ps[1].Name + ")";
                    m.Invoke(sched, new object[] { hour, grp });
                    invoked = true;
                }
                catch (Exception e)
                {
                    err = (e.InnerException != null ? e.InnerException.GetType().Name + ": " + e.InnerException.Message : e.GetType().Name + ": " + e.Message);
                }
            }

            string groupAfter = null, nameAfter = null;
            try { var b = BlockOf(sched, hour); groupAfter = Prop(b, "GroupId"); nameAfter = Prop(b, "name"); } catch { }

            var sb = new StringBuilder(384);
            sb.Append("{\"type\":\"schedule\",\"action\":\"set_block\"");
            sb.Append(",\"schedule\":").Append(Snap.Str(Prop(sched, "name")));
            sb.Append(",\"hour\":").Append(hour);
            sb.Append(",\"groupRequested\":").Append(Snap.Str(group));
            sb.Append(",\"sig\":").Append(sig == null ? "null" : Snap.Str(sig));
            sb.Append(",\"invoked\":").Append(invoked ? "true" : "false");
            sb.Append(",\"error\":").Append(err == null ? "null" : Snap.Str(err));
            sb.Append(",\"readback\":{\"group\":").Append(groupAfter == null ? "null" : Snap.Str(groupAfter))
              .Append(",\"blockName\":").Append(nameAfter == null ? "null" : Snap.Str(nameAfter)).Append('}');
            sb.Append(",\"ok\":").Append(invoked && string.Equals(groupAfter, group, StringComparison.OrdinalIgnoreCase) ? "true" : "false");
            sb.Append(",\"note\":").Append(Snap.Str("改某个小时的区块类型；readback.group 必须等于请求值才算成功"));
            sb.Append('}');
            return sb.ToString();
        }

        /// <summary>从 `Db.Get().ScheduleGroups` 里按 id 取 ScheduleGroup（全反射，兼容资源集的不同 API）。</summary>
        static object FindGroup(string id)
        {
            try
            {
                object db = null;
                var dbT = typeof(Db);
                var gm = dbT.GetMethod("Get", BindingFlags.Public | BindingFlags.Static);
                if (gm != null) db = gm.Invoke(null, null);
                if (db == null) return null;

                object set = null;
                var pf = db.GetType().GetProperty("ScheduleGroups", InstAny) ?? (object)db.GetType().GetField("ScheduleGroups", InstAny);
                if (pf is PropertyInfo pi) set = pi.GetValue(db);
                else if (pf is FieldInfo fi) set = fi.GetValue(db);
                if (set == null) return null;

                // 资源集常见形态：TryGet(string) / Get(string) / 实现 IEnumerable 且元素有 Id
                foreach (var mn in new[] { "TryGet", "Get" })
                {
                    var m = set.GetType().GetMethod(mn, new[] { typeof(string) });
                    if (m != null) { var v = m.Invoke(set, new object[] { id }); if (v != null) return v; }
                }
                var en = set as IEnumerable;
                if (en != null)
                {
                    foreach (var it in en)
                    {
                        if (it == null) continue;
                        var itId = Prop(it, "Id") ?? Prop(it, "id");
                        if (string.Equals(itId, id, StringComparison.OrdinalIgnoreCase)) return it;
                    }
                }
            }
            catch (Exception e) { Log.Info("[schedule] FindGroup 失败: " + e.GetType().Name); }
            return null;
        }
    }
}
