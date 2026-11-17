using System.Text;

// ⚠️ 不要 using System 之后直接用 Action/DateTime：Assembly-CSharp 里的全局 Action 枚举会遮蔽 System.Action。
namespace OniAiBridge
{
    /// <summary>
    /// M6.55（**只读**）：`GET /v1/tools` —— 枚举引擎里的界面工具（`PlayerController.Instance.tools`）。
    ///
    /// 定位（小僵拍板）：**`id`（类名）就是主键**，稳定唯一，对 AI 完全可读；
    /// 不追求运行时解析中文名 —— 中英对照写在 `docs/API.md §3.28` 的**静态表**里，
    /// 由人工核对过一次即可（运行时解析 LocText/HoverText 反而会引入不确定）。
    ///
    /// 字段刻意精简：`id` / `active` / `enabled`。
    /// * `active` 读 nonpublic 字段 `PlayerController.activeTool`（反射只读）
    /// * `enabled` 只在工具自身有可读 `enabled` 属性时给 true/false，**否则 null**（不谎报）
    ///
    /// ⚠️ 这里是**引擎里的工具**（39 个，含沙盒/调试工具），
    /// **不等于玩家界面上看得到的按钮**（界面约 15 个）—— 别误读成"右下角有 39 个"。
    /// </summary>
    internal static class Tools
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
                    var pc = PlayerController.Instance;
                    if (pc == null) { fail = "PlayerController.Instance 为 null（没进游戏）"; return; }

                    InterfaceTool[] arr = null;
                    try { arr = pc.tools; } catch (System.Exception e) { fail = "读 PlayerController.tools 失败：" + e.GetType().Name; return; }
                    if (arr == null) { fail = "PlayerController.tools 为 null"; return; }

                    InterfaceTool active = null;
                    string activeErr = null;
                    try
                    {
                        var f = typeof(PlayerController).GetField("activeTool",
                            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                        if (f != null) active = f.GetValue(pc) as InterfaceTool;
                        else activeErr = "找不到字段 activeTool";
                    }
                    catch (System.Exception e) { activeErr = e.GetType().Name; }

                    var sb = new StringBuilder(2048);
                    sb.Append("{\"count\":").Append(arr.Length);
                    sb.Append(",\"activeToolId\":").Append(Snap.Str(active != null ? active.GetType().Name : null));
                    if (activeErr != null) sb.Append(",\"activeToolError\":").Append(Snap.Str(activeErr));
                    sb.Append(",\"source\":").Append(Snap.Str("PlayerController.Instance.tools（public 字段，游戏自己的工具集合）"));
                    sb.Append(",\"tools\":[");
                    int emitted = 0;
                    for (int i = 0; i < arr.Length; i++)
                    {
                        var t = arr[i];
                        if (t == null) continue;
                        if (emitted > 0) sb.Append(',');
                        emitted++;

                        string id = null;
                        try { id = t.GetType().Name; } catch { }

                        bool isActive = false;
                        try { isActive = (active == t); } catch { }

                        bool? enabled = null;   // 读不到就 null（不谎报 true）
                        try
                        {
                            var pi = t.GetType().GetProperty("enabled", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public);
                            if (pi != null && pi.PropertyType == typeof(bool)) enabled = (bool)pi.GetValue(t, null);
                        }
                        catch { }

                        sb.Append("{\"id\":").Append(Snap.Str(id));
                        sb.Append(",\"active\":").Append(isActive ? "true" : "false");
                        sb.Append(",\"enabled\":").Append(enabled.HasValue ? (enabled.Value ? "true" : "false") : "null");
                        sb.Append('}');
                    }
                    sb.Append("],\"emitted\":").Append(emitted);
                    sb.Append(",\"note\":").Append(Snap.Str(
                        "id 是游戏类名，稳定唯一；中文对照见 docs/API.md §3.28 的静态表。"
                        + "列的是**引擎里的工具**（含沙盒/调试工具），不等于玩家界面上看得到的按钮"));
                    sb.Append('}');
                    data = sb.ToString();
                    Log.Info("[tools] tools=" + arr.Length + " emitted=" + emitted + " active=" + (active != null ? active.GetType().Name : "<null>"));
                }
                catch (System.Exception e) { fail = e.GetType().Name + ": " + e.Message; }
            }, 5000, 30000, out okT, out busy);

            if (!ran) { status = 503; code = "RETRY_LATER"; message = "main thread busy；请 1 秒后重试"; return null; }
            if (fail != null) { status = 500; code = "TOOLS_UNAVAILABLE"; message = fail; return null; }
            if (data == null) { status = 500; code = "INTERNAL"; message = "tools 采集返回空"; return null; }
            return data;
        }
    }
}
