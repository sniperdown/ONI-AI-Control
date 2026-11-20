using System.Collections.Generic;
using System.Text;

namespace OniAiBridge
{
    /// <summary>
    /// `POST /v1/dupes/spawn` —— 程序化生成一个复制人（0.112.0）。
    ///
    /// **IL 实证的调用链**（由只读勘察确认，不是猜的）：
    ///   1) `MinionStartingStats..ctor(bool is_starter_minion, string guaranteedAptitudeID,
    ///                                string guaranteedTraitID, bool isDebugMinion)` —— 里面选姓名/特质/属性/外观
    ///   2) `MinionStartingStats.Deliver(Vector3 location) : GameObject` —— 打包了实例化 prefab、
    ///      `Immigration.ApplyDefaultPersonalPriorities`、`SetLocalPosition`、`SetActive(true)`、`Apply(go)`
    ///   下一帧 `MinionIdentity.OnSpawn` 自动把新人注册进 `Components.LiveMinionIdentities`
    ///   （所以当场读人数可能还是旧值，要等一帧）。
    ///
    /// 两个 IL 里看到的坑：
    ///   * `Deliver` 内部裸解引用 `Immigration.Instance` ⇒ **非殖民地环境会 NRE**（主菜单/加载中不能调）
    ///   * `Apply` 必须在实例化之后（它第一步就 GetComponent&lt;MinionIdentity&gt;）—— Deliver 已经保证顺序
    /// </summary>
    internal static class DupesSpawn
    {
        public static string Execute(string body, out int status, out string code, out string message)
        {
            status = 200; code = null; message = null;

            // ★ 作弊闸门：本端点生成复制人**绕过打印舱规则**（不消耗打印次数、不走移民流程），
            //   机制上与游戏 DebugHandler.SpawnMinion 同一条路 ⇒ 必须和 `allowCheats` 一样受门禁：
            //   `allowCheats == true` 且（沙盒档 / 沙盒激活 / debug 模式）才允许。
            if (!Gate.CheatsUnlocked)
            {
                status = 403; code = Gate.UnavailableCode();
                message = Gate.UnavailableReason() + "（/v1/dupes/spawn 属于作弊能力：它绕过打印舱，直接调游戏的调试生成路径）";
                return null;
            }

            int x = Num(body, "x"), y = Num(body, "y");
            bool hasCell = x >= 0 && y >= 0;
            int cell = hasCell ? Grid.XYToCell(x, y) : -1;
            // 默认走"普通掷点"（isDebugMinion=false）；要调试属性就显式 {"debug":true}
            bool debugStats = body.IndexOf("\"debug\":true", System.StringComparison.Ordinal) >= 0;

            string data = null; string fatal = null;
            int okR; bool busyR;
            bool ran = MainThread.InvokeAdaptive(() =>
            {
                UnityEngine.GameObject go = null;
                try
                {
                    // 前置：必须是殖民地内（Immigration.Instance 非空，否则 Deliver 会 NRE）
                    if (!Game.IsQuitting() && Immigration.Instance == null)
                    {
                        fatal = "Immigration.Instance 为 null（当前不是殖民地场景，无法生成复制人）";
                        return;
                    }

                    if (!hasCell)
                    {
                        // 没给坐标 ⇒ 落在打印舱所在格
                        cell = -1;
                        try
                        {
                            var builds = Components.BuildingCompletes != null ? Components.BuildingCompletes.Items : null;
                            if (builds != null)
                            {
                                for (int i = 0; i < builds.Count; i++)
                                {
                                    var b = builds[i];
                                    if (b == null) continue;
                                    Building def = null;
                                    try { def = b.GetComponent<Building>(); } catch { }
                                    string pid = null;
                                    try { pid = def != null && def.Def != null ? def.Def.PrefabID : null; } catch { }
                                    if (pid == "Headquarters" || pid == "Telepad")
                                    {
                                        cell = Grid.PosToCell(b.transform.position);
                                        break;
                                    }
                                }
                            }
                        }
                        catch { }
                        if (cell < 0) { fatal = "没给坐标，也找不到打印舱/总部格"; return; }
                    }

                    if (!Grid.IsValidCell(cell)) { fatal = "目标格非法: " + cell; return; }

                    // 1) 构造 stats（is_starter_minion=false, 无保底特质, isDebugMinion=true 走调试路径）
                    var stats = new MinionStartingStats(false, null, null, debugStats);

                    // 2) Deliver 到目标格（它自己完成实例化/定位/激活/Apply）
                    var pos = Grid.CellToPosCBC(cell, Grid.SceneLayer.Move);
                    go = stats.Deliver(pos);

                    string name = null;
                    try { name = stats.Name; } catch { }
                    int finalCell = cell;
                    if (go != null) { try { finalCell = Grid.PosToCell(go.transform.position); } catch { } }

                    var sb = new StringBuilder(384);
                    var xy = Grid.CellToXY(finalCell);
                    sb.Append("{\"spawned\":").Append(go != null ? "true" : "false");
                    sb.Append(",\"name\":").Append(name == null ? "null" : Snap.Str(name));
                    sb.Append(",\"x\":").Append(xy.x).Append(",\"y\":").Append(xy.y).Append(",\"cell\":").Append(finalCell);
                    sb.Append(",\"via\":").Append(Snap.Str("MinionStartingStats.Deliver(Grid.CellToPosCBC(cell, Move))"));
                    sb.Append(",\"note\":").Append(Snap.Str("注册进 LiveMinionIdentities 发生在下一帧（MinionIdentity.OnSpawn），人数请稍后读 /v1/state"));
                    sb.Append('}');
                    data = sb.ToString();
                }
                catch (System.Exception e)
                {
                    fatal = e.GetType().Name + ": " + e.Message
                        + (e.InnerException != null ? " | inner: " + e.InnerException.GetType().Name + ": " + e.InnerException.Message : "");
                }
            }, 8000, 20000, out okR, out busyR);

            if (!ran) { status = 503; code = "MAIN_THREAD_BUSY"; message = "主线程泵未响应"; return null; }
            if (fatal != null) { status = 500; code = "SPAWN_FAILED"; message = fatal; return null; }
            JsonCheck.Validate(data, "dupes-spawn");
            return data;
        }

        static int Num(string body, string key)
        {
            int k = body.IndexOf("\"" + key + "\"", System.StringComparison.Ordinal);
            if (k < 0) return -1;
            int colon = body.IndexOf(':', k);
            if (colon < 0) return -1;
            int i = colon + 1;
            while (i < body.Length && (body[i] == ' ' || body[i] == '"')) i++;
            int s = i;
            while (i < body.Length && (char.IsDigit(body[i]) || body[i] == '-')) i++;
            int v;
            if (i > s && int.TryParse(body.Substring(s, i - s), out v)) return v;
            return -1;
        }
    }
}
