using System.Collections.Generic;
using System.Reflection;
using System.Text;

// ⚠️ 不要 using System 之后直接用 Action/DateTime：Assembly-CSharp 里的全局 Action 枚举会遮蔽 System.Action。
namespace OniAiBridge
{
    /// <summary>
    /// 诊断端点 `GET /v1/debug/mods`（**不放进公共 /v1/meta，避免弄脏 API**）。
    ///
    /// 为什么需要它：M4.1 出现"反射设 on_managed 没抛异常、但 Mods 列表里根本看不到我们的条目"。
    /// "没抛异常"不等于"生效"——这类反射/私有 API 操作必须配一个可观测的探头。
    /// 这里把 manager 里每一条 mod 的原生状态、以及游戏自己的 `ModsScreen.ShouldDisplayMod(mod)`
    /// 的返回值都吐出来，一眼就能看出我们的条目在不在、被哪个条件挡了。
    /// </summary>
    internal static class DebugMods
    {
        public static string Collect()
        {
            var sb = new StringBuilder(8192);
            try
            {
                Global g = Global.Instance;
                KMod.Manager mgr = g != null ? g.modManager : null;
                if (mgr == null || mgr.mods == null)
                {
                    return "{\"error\":\"Global.Instance / modManager / mods 不可用（可能还没初始化）\"}";
                }

                // ShouldDisplayMod 是 ModsScreen 的私有实例方法 ⇒ 需要一个活着的 ModsScreen 实例
                MethodInfo shouldDisplay = typeof(ModsScreen).GetMethod("ShouldDisplayMod",
                    BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                ModsScreen screen = null;
                try { screen = UnityEngine.Object.FindObjectOfType<ModsScreen>(); } catch { }
                string reflectNote = null;
                if (shouldDisplay == null) reflectNote = "找不到 ModsScreen.ShouldDisplayMod 方法";
                else if (screen == null) reflectNote = "当前没有活着的 ModsScreen 实例（不在模组界面）⇒ shouldDisplayMod 报 null";

                int shown = 0, unknown = 0;
                var items = new StringBuilder(4096);
                for (int i = 0; i < mgr.mods.Count; i++)
                {
                    KMod.Mod m = mgr.mods[i];
                    if (m == null) continue;
                    if (i > 0) items.Append(',');
                    items.Append('{');
                    items.Append("\"index\":").Append(i);
                    items.Append(",\"title\":").Append(Snap.Str(SafeStr(m.title)));
                    items.Append(",\"staticID\":").Append(Snap.Str(SafeStr(m.staticID)));
                    items.Append(",\"platform\":").Append(Snap.Str(SafeEnum(m.label.distribution_platform)));
                    items.Append(",\"status\":").Append(Snap.Str(SafeEnum(m.status)));
                    items.Append(",\"availableContent\":").Append(Snap.Str(SafeEnum(m.available_content)));
                    items.Append(",\"isLocal\":").Append(SafeBool(m.IsLocal));
                    items.Append(",\"hasOnlyTranslationContent\":").Append(SafeBool(InvokeBool(m.HasOnlyTranslationContent)));
                    items.Append(",\"onManaged\":").Append(m.on_managed != null ? "true" : "false");
                    items.Append(",\"manageTooltip\":").Append(m.manage_tooltip != null ? "true" : "false");

                    string sd;
                    if (shouldDisplay == null || screen == null) { sd = "null"; unknown++; }
                    else
                    {
                        try
                        {
                            object r = shouldDisplay.Invoke(screen, new object[] { m });
                            bool b = r is bool && (bool)r;
                            sd = b ? "true" : "false";
                            if (b) shown++;
                        }
                        catch (System.Exception) { sd = "null"; unknown++; }
                    }
                    items.Append(",\"shouldDisplayMod\":").Append(sd);
                    items.Append('}');
                }

                sb.Append("{\"managerModsCount\":").Append(mgr.mods.Count);
                sb.Append(",\"shouldDisplayTrue\":").Append(shown);
                sb.Append(",\"shouldDisplayUnknown\":").Append(unknown);
                sb.Append(",\"note\":").Append(Snap.Str(reflectNote));
                sb.Append(",\"platforms\":[");
                if (mgr.distribution_platforms != null)
                {
                    for (int i = 0; i < mgr.distribution_platforms.Count; i++)
                    {
                        if (i > 0) sb.Append(',');
                        var p = mgr.distribution_platforms[i];
                        sb.Append("{\"type\":").Append(Snap.Str(p == null ? "null" : p.GetType().Name));
                        if (p is KMod.Local) sb.Append(",\"folder\":").Append(Snap.Str(((KMod.Local)p).folder));
                        sb.Append('}');
                    }
                }
                sb.Append("],");
                sb.Append("\"mods\":[").Append(items).Append("]}");
            }
            catch (System.Exception e)
            {
                sb.Clear();
                sb.Append("{\"error\":").Append(Snap.Str(e.GetType().Name + ": " + e.Message)).Append('}');
            }
            string __json = sb.ToString();
            JsonCheck.Validate(__json, "debug-mods");   // 手写 JSON ⇒ 出门前必须能解析
            return __json;
        }

        static string SafeStr(string s) { return s; }

        static string SafeEnum(object o)
        {
            try { return o == null ? null : o.ToString(); } catch { return null; }
        }

        static string SafeBool(bool b) { return b ? "true" : "false"; }

        static bool InvokeBool(System.Func<bool> f)
        {
            try { return f(); } catch { return false; }
        }
    }
}
