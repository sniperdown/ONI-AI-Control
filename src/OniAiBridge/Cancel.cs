using System;
using System.Text;
using UnityEngine;

namespace OniAiBridge
{
    /// <summary>
    /// `POST /v1/job {"type":"cancel","x":..,"y":..,"confirm":true}` —— **取消**某格上的"待办标记"。
    ///
    /// 为什么需要它（实测，2026-11-21）：`dig`/`build`/`deconstruct` 挂上去的标记**没有任何接口能撤回**，
    /// 下错一次就永久占格（蓝图永远占着那格、标记永远挂着但没人干）。AI 一旦误操作就不可恢复。
    ///
    /// 实现路径**照抄玩家的取消动作**（IL 实证）：
    /// `CancelTool.OnDragTool(cell, button)` 干的事是
    ///   ① 取该格对象（`Grid.Objects[cell, layer]`）
    ///   ② 用 `GetFilterLayerFromGameObject` + `IsActiveLayer` 判断这个 layer 是否在当前过滤器里
    ///   ③ `EventExtensions.Trigger(go, <int>)` —— 那个 int 就是 `GameHashes.Cancel = 2127324410`
    /// 所以这里不调任何"Cancel 方法"，只是**给该格上的对象发 Cancel 事件**，
    /// 由各组件自己响应（`Constructable` 拆蓝图、`Deconstructable.CancelDeconstruction`、
    /// dig 的 `DigPlacer` 销毁自身…）。这样行为与玩家右键取消一致。
    ///
    /// 取消是**普通操作**（玩家天然可以做），所以**不挂在作弊闸门下**。
    /// </summary>
    internal static class CancelJob
    {
        const int CancelHash = 2127324410;   // GameHashes.Cancel（dump 出来的原值，见类注释）

        internal static bool IsCancelType(string t0)
        {
            return t0 == "cancel";
        }

        internal static string Execute(string body, out int status, out string code, out string message, out string details)
        {
            status = 200; code = null; message = null; details = null;

            int x, y;
            if (!JobQuery.TryInt(body, "x", out x) || !JobQuery.TryInt(body, "y", out y))
            {
                status = 400; code = "BAD_REQUEST";
                message = "需要 x / y（世界内格子坐标）";
                return null;
            }
            int worldId = 0;
            try { JobQuery.TryInt(body, "worldId", out worldId); } catch { }

            int cell = Grid.XYToCell(x, y);
            if (!Grid.IsValidCell(cell))
            {
                status = 400; code = "OUT_OF_WORLD";
                message = "格子不在世界内: (" + x + "," + y + ")";
                return null;
            }

            int hitLayers = 0, triggered = 0;
            bool destroyedSomething = false;
            var touched = new StringBuilder(160);
            var touchedKind = new StringBuilder(32);
            try
            {
                var layers = (ObjectLayer[])Enum.GetValues(typeof(ObjectLayer));
                foreach (var layer in layers)
                {
                    GameObject go = null;
                    try { go = Grid.Objects[cell, (int)layer]; } catch { }
                    if (go == null) continue;
                    hitLayers++;
                    try
                    {
                        // ★ 两类标记的取消方式**不同**（0.114.0 实测）：
                        //   * 蓝图 / 拆解标记：挂在"正经建筑"上，走 `GameHashes.Cancel` 事件即可（实测 cleared=true）
                        //   * 挖掘 / 拖地：标记载体是 `*Placer` 临时物体，**不吃 Cancel 事件**
                        //     （实测 triggered=1 但 digMarked 仍为 true）⇒ 按玩家的做法**销毁载体**
                        //     （docs/ACTION-SURVEY.md §4：玩家取消时销毁 placer）
                        bool isPlacer = layer.ToString().IndexOf("Placer", StringComparison.OrdinalIgnoreCase) >= 0;
                        if (!isPlacer)
                        {
                            try { isPlacer = go.GetComponent<Diggable>() != null; } catch { }
                        }
                        if (isPlacer)
                        {
                            Util.KDestroyGameObject(go);
                            destroyedSomething = true;
                            touchedKind.Append(touchedKind.Length > 0 ? "," : "").Append("destroy:");
                        }
                        else
                        {
                            go.Trigger((int)CancelHash);
                            touchedKind.Append(touchedKind.Length > 0 ? "," : "").Append("cancel:");
                        }
                        triggered++;
                        if (touched.Length > 0) touched.Append(',');
                        touched.Append(layer.ToString());
                    }
                    catch (Exception e)
                    {
                        Log.Info("[cancel] layer=" + layer + " trigger 失败: " + e.GetType().Name + ": " + e.Message);
                    }
                }
            }
            catch (Exception e)
            {
                status = 500; code = "CANCEL_FAILED";
                message = e.GetType().Name + ": " + e.Message;
                return null;
            }

            // 回读：这三样是"标记型待办"的证据，取消后应当都没了
            bool digAfter = false, deconAfter = false, blueprintAfter = false;
            try
            {
                int hitLayer, okL, errL; string errT;
                digAfter = JobQuery.FindDiggableAt(cell, out hitLayer, out okL, out errL, out errT) != null;
            }
            catch { }
            try
            {
                var b = JobQuery.FindBuildingAt(cell);
                if (b != null)
                {
                    var d = b.GetComponent<Deconstructable>();
                    if (d != null) deconAfter = d.IsMarkedForDeconstruction();
                    var c = b.GetComponent<Constructable>();
                    if (c != null) blueprintAfter = true;   // 建筑还在 ⇒ 蓝图没被取消（Constructable 存在即蓝图/在建）
                }
            }
            catch { }

            // ★ 0.114.1 实测：`Util.KDestroyGameObject` 是**帧末生效**的，
            //   所以同一帧回读 `digMarked` 仍然是 true。碰到这条路就不能说"没清掉"，
            //   也不能说"清掉了"—— 如实报 `pendingFrame:true` + `cleared:null`，让调用方下一帧再核。
            bool pendingFrame = destroyedSomething;
            bool? cleared = pendingFrame ? (bool?)null : (!digAfter && !deconAfter && !blueprintAfter);

            var sb = new StringBuilder(512);
            sb.Append("{\"type\":\"cancel\"");
            sb.Append(",\"target\":{\"worldId\":").Append(worldId)
              .Append(",\"x\":").Append(x).Append(",\"y\":").Append(y)
              .Append(",\"cell\":").Append(cell).Append('}');
            sb.Append(",\"objectsAtCell\":").Append(hitLayers);
            sb.Append(",\"triggered\":").Append(triggered);
            sb.Append(",\"layers\":[").Append(LayerJson(touched.ToString())).Append(']');
            sb.Append(",\"modes\":[").Append(LayerJson(touchedKind.ToString())).Append(']');
            sb.Append(",\"readback\":{\"digMarked\":").Append(digAfter ? "true" : "false")
              .Append(",\"deconstructMarked\":").Append(deconAfter ? "true" : "false")
              .Append(",\"buildingStillThere\":").Append(blueprintAfter ? "true" : "false")
              .Append('}');
            sb.Append(",\"cleared\":").Append(pendingFrame ? "null" : (cleared.Value ? "true" : "false"));
            sb.Append(",\"pendingFrame\":").Append(pendingFrame ? "true" : "false");
            sb.Append(",\"jobState\":").Append(Snap.Str(
                hitLayers == 0 ? "nothing-at-cell"
                : pendingFrame ? "destroy-scheduled"
                : (cleared.Value ? "cleared" : (triggered > 0 ? "partially-cleared" : "no-op"))));
            sb.Append(",\"note\":").Append(Snap.Str(
                hitLayers == 0
                  ? "该格没有任何对象（没有标记、也没有建筑），无需取消"
                  : "取消 = 对该格对象发 GameHashes.Cancel 事件（与玩家 CancelTool 同路径）；readback 是回读证据。"
                    + "注意：已建成的建筑不会因此被拆掉，被取消的只有'待办'（蓝图 / 挖标记 / 拆解标记）"));
            sb.Append('}');

            Log.Info("[job] type=cancel x=" + x + " y=" + y + " cell=" + cell
                     + " layers=" + hitLayers + " triggered=" + triggered + " cleared=" + cleared
                     + " dig=" + digAfter + " decon=" + deconAfter + " building=" + blueprintAfter);
            return sb.ToString();
        }

        static string LayerJson(string csv)
        {
            if (string.IsNullOrEmpty(csv)) return "";
            var parts = csv.Split(',');
            var sb = new StringBuilder(csv.Length + parts.Length * 3);
            for (int i = 0; i < parts.Length; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(Snap.Str(parts[i]));
            }
            return sb.ToString();
        }
    }
}
