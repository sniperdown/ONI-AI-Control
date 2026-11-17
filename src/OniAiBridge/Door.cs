using System;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

// ⚠️ 不要 using System 之后直接用 Action/DateTime：Assembly-CSharp 里的全局 Action 枚举会遮蔽 System.Action。
namespace OniAiBridge
{
    /// <summary>
    /// M6.1：`POST /v1/door` —— 第一个真正的"手"操作（**只做普通层**）。
    ///
    /// 设计依据（DESIGN §10.6q / §10.6r）：
    /// * `QueueStateChange` **不会立刻改** `currentState`；正常路径是生成一个 `Chore`（Toggle），
    ///   等复制人走过去按。⇒ **必须分级回读**：立即读 `requestedState`（受理），轮询 `currentState`（生效），
    ///   超时返回 `pending`（**既非成功也非失败**）。
    /// * 门的三个状态量必须分开给：`currentState`（档位）/ `requestedState`（排队目标）/ `physicalOpen`（物理开合）。
    ///
    /// `instant`（反射写 `controlState`）**本版不实现**，请求带 `instant:true` ⇒ 400 + 明确原因。
    /// </summary>
    internal static class DoorQuery
    {
        // ControlState 只有 Auto / Opened / Locked（枚举实测），**没有 Closed**
        const int PollMs = 250;
        const int PollTotalMs = 5000;

        public static string Execute(string body, out int status, out string code, out string message)
        {
            status = 200; code = null; message = null;

            // ---- 1) 解析请求 ----
            bool instant = Regex.IsMatch(body ?? "", "\"instant\"\\s*:\\s*true", RegexOptions.IgnoreCase);
            if (instant)
            {
                status = 400; code = "BAD_REQUEST"; message = "instant 模式尚未实现（需要反射写 controlState + ApplyControlState，风险未评估）";
                return null;
            }
            int x, y, worldId = -1;
            if (!TryInt(body, "x", out x) || !TryInt(body, "y", out y))
            {
                status = 400; code = "BAD_REQUEST"; message = "需要 x / y（世界内格子坐标）";
                return null;
            }
            TryInt(body, "worldId", out worldId);
            string action = MatchString(body, "action");
            if (string.IsNullOrEmpty(action))
            {
                status = 400; code = "BAD_REQUEST"; message = "需要 action（open | close | lock | auto）";
                return null;
            }

            int target;
            string actionNote = null;
            switch (action.ToLowerInvariant())
            {
                case "open": target = (int)Door.ControlState.Opened; break;
                case "lock": target = (int)Door.ControlState.Locked; break;
                case "auto": target = (int)Door.ControlState.Auto; break;
                case "close":
                    // 游戏里没有 "Closed" 这个控制档位（枚举只有 Auto/Opened/Locked）⇒ close 映射为 Auto
                    target = (int)Door.ControlState.Auto;
                    actionNote = "close 映射为 Auto：ControlState 只有 Auto/Opened/Locked，没有 Closed";
                    break;
                default:
                    status = 400; code = "BAD_REQUEST"; message = "action 只能是 open | close | lock | auto，收到: " + action;
                    return null;
            }

            if (!MainThread.Ready) { status = 503; code = "NOT_READY"; message = "main-thread pump is not running yet"; return null; }

            // ---- 2) 找门（主线程）----
            int cell = -1, foundWorld = -1;
            string prefabId = null, findErr = null;
            Door door = null;

            MainThread.Invoke(() =>
            {
                try
                {
                    cell = Grid.XYToCell(x, y);
                    if (!Grid.IsValidCell(cell)) { findErr = "格子无效：" + x + "," + y + " ⇒ cell=" + cell; return; }

                    foundWorld = Grid.WorldIdx != null ? Grid.WorldIdx[cell] : -1;
                    if (worldId >= 0 && foundWorld != worldId)
                    {
                        findErr = "worldId 不符：请求 " + worldId + "，该格属于 " + foundWorld;
                        return;
                    }

                    var items = Components.BuildingCompletes != null ? Components.BuildingCompletes.Items : null;
                    if (items == null) { findErr = "Components.BuildingCompletes 不可用"; return; }
                    for (int i = 0; i < items.Count; i++)
                    {
                        BuildingComplete b = items[i];
                        if (b == null) continue;
                        bool contains = false;
                        try { contains = b.PlacementCellsContainCell(cell); } catch { continue; }
                        if (!contains) continue;

                        Door d = null;
                        try { d = b.GetComponent<Door>(); } catch { }
                        if (d == null) continue;      // 这格上有建筑，但不是门
                        door = d;
                        try { prefabId = b.Def != null ? b.Def.PrefabID : null; } catch { }
                        break;
                    }
                    if (door == null && findErr == null) findErr = "该格没有门（可能没有建筑，或建筑不是 Door）";
                }
                catch (Exception e) { findErr = e.GetType().Name + ": " + e.Message; }
            }, 3000);

            if (door == null)
            {
                status = 404; code = "NOT_FOUND_TARGET"; message = findErr ?? "找不到目标门";
                Log.Info("[door] 404 x=" + x + " y=" + y + " cell=" + cell + " → " + message);
                return null;
            }

            // ---- 3) 记录 before + 发起 ----
            string beforeCurrent = null, beforeRequested = null;
            bool beforeOpen = false, accepted = false;
            string sendErr = null;

            MainThread.Invoke(() =>
            {
                try
                {
                    beforeCurrent = door.CurrentState.ToString();
                    beforeRequested = door.RequestedState.ToString();
                    beforeOpen = door.IsOpen();

                    door.QueueStateChange((Door.ControlState)target);

                    accepted = door.RequestedState == (Door.ControlState)target;
                }
                catch (Exception e) { sendErr = e.GetType().Name + ": " + e.Message; }
            }, 3000);

            if (sendErr != null)
            {
                status = 500; code = "INTERNAL"; message = "发起失败：" + sendErr;
                Log.Warn("[door] 发起异常 x=" + x + " y=" + y + " : " + sendErr);
                return null;
            }

            // ---- 4) 轮询 currentState（最多 5 秒）----
            string afterCurrent = beforeCurrent, afterRequested = beforeRequested;
            bool afterOpen = beforeOpen, applied = false;
            string wantName = ((Door.ControlState)target).ToString();
            int waited = 0;
            while (waited < PollTotalMs)
            {
                MainThread.Invoke(() =>
                {
                    try
                    {
                        afterCurrent = door.CurrentState.ToString();
                        afterRequested = door.RequestedState.ToString();
                        afterOpen = door.IsOpen();
                    }
                    catch { }
                }, 1000);
                if (afterCurrent == wantName) { applied = true; break; }
                try { System.Threading.Thread.Sleep(PollMs); } catch { }
                waited += PollMs;
            }

            bool pending = !applied;

            // ---- 5) 组装响应 ----
            var sb = new StringBuilder(512);
            sb.Append("{\"mode\":\"normal\"");
            sb.Append(",\"action\":").Append(Snap.Str(action));
            sb.Append(",\"target\":{\"worldId\":").Append(foundWorld);
            sb.Append(",\"x\":").Append(x).Append(",\"y\":").Append(y);
            sb.Append(",\"cell\":").Append(cell);
            sb.Append(",\"prefabId\":").Append(Snap.Str(prefabId)).Append('}');
            sb.Append(",\"requestedAction\":").Append(Snap.Str(wantName));
            sb.Append(",\"before\":{\"currentState\":").Append(Snap.Str(beforeCurrent));
            sb.Append(",\"requestedState\":").Append(Snap.Str(beforeRequested));
            sb.Append(",\"physicalOpen\":").Append(beforeOpen ? "true" : "false").Append('}');
            sb.Append(",\"after\":{\"currentState\":").Append(Snap.Str(afterCurrent));
            sb.Append(",\"requestedState\":").Append(Snap.Str(afterRequested));
            sb.Append(",\"physicalOpen\":").Append(afterOpen ? "true" : "false").Append('}');
            sb.Append(",\"accepted\":").Append(accepted ? "true" : "false");
            // 跨 type 同构：jobState 是该 type 特有的语义枚举（DESIGN §10.6s）
            sb.Append(",\"jobState\":").Append(Snap.Str(applied ? "done" : "pending"));
            sb.Append(",\"waitedMs\":").Append(waited);
            string note = pending
                ? "已派活（ChangeStateChore），等复制人走过去执行；用 currentState 复核，别当成已生效"
                : "已生效（currentState 已变为 " + wantName + "）";
            if (actionNote != null) note = actionNote + "；" + note;
            sb.Append(",\"note\":").Append(Snap.Str(note));
            sb.Append('}');

            Log.Info("[door] " + action + " x=" + x + " y=" + y + " cell=" + cell
                     + " before=" + beforeCurrent + "/" + beforeRequested
                     + " after=" + afterCurrent + "/" + afterRequested
                     + " accepted=" + accepted + " applied=" + applied + " pending=" + pending
                     + " waited=" + waited + "ms");
            if (pending) Log.Info("[door] pending：已派活，等小人过去（不是失败，也不是成功）");

            return sb.ToString();
        }

        static bool TryInt(string body, string key, out int v)
        {
            v = 0;
            if (string.IsNullOrEmpty(body)) return false;
            var m = Regex.Match(body, "\"" + key + "\"\\s*:\\s*(-?[0-9]+)");
            if (!m.Success) return false;
            return int.TryParse(m.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out v);
        }

        static string MatchString(string body, string key)
        {
            if (string.IsNullOrEmpty(body)) return null;
            var m = Regex.Match(body, "\"" + key + "\"\\s*:\\s*\"([^\"]*)\"");
            return m.Success ? m.Groups[1].Value : null;
        }
    }
}
