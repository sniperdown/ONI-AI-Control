using System.Text;

// ⚠️ 不要 using System 之后直接用 Action/DateTime：Assembly-CSharp 里的全局 Action 枚举会遮蔽 System.Action。
namespace OniAiBridge
{
    /// <summary>
    /// M6.49（**只读**）：`GET /v1/inventory?tag=Cuprite` —— 把"同一种材料的三种口径"分开报。
    ///
    /// 为什么需要：`/v1/materials` 与 `/v1/state.elements` 都只统计 **Storage（储物箱）**，
    /// 所以会出现"玩家看到资源面板 5 吨、我们只读到 114.9 kg"的落差 —— 那不是 bug，是**口径不同**。
    ///
    /// 三个口径：
    /// * `inStorage` —— 储物箱里**可用于建造**的量（与 `INSUFFICIENT_MATERIAL` 的 `have` 同口径）
    /// * `onGround`  —— 地上散落物（`Pickupable` 且不在任何 Storage 里）
    /// * `inWorld`   —— 世界总量（`WorldInventory.GetAmount(tag, false)`；读不到会如实标 `inWorldUnavailable`）
    ///
    /// ⚠️ **建造只认 `inStorage`**。资源面板显示的大概率是 `inWorld` 或 `inWorld+onGround`。
    /// </summary>
    internal static class Inventory
    {
        public static string Query(string tagFilter, out int status, out string code, out string message)
        {
            status = 200; code = null; message = null;
            if (string.IsNullOrEmpty(tagFilter)) { status = 400; code = "BAD_REQUEST"; message = "需要 tag（如 ?tag=Cuprite）"; return null; }
            if (!MainThread.Ready) { status = 503; code = "NOT_READY"; message = "main-thread pump is not running yet"; return null; }

            string data = null, fail = null;
            int okT; bool busy;
            bool ran = MainThread.InvokeAdaptive(() =>
            {
                try
                {
                    Tag want = default(Tag);
                    bool okTag = false;
                    try { want = TagManager.Create(tagFilter); okTag = true; } catch { }
                    if (!okTag) { fail = "TagManager.Create 失败：" + tagFilter; return; }

                    // ---- ① inStorage ----
                    double inStorage = 0, onGround = 0;
                    int storages = 0, groundStacks = 0, unnamedPickupables = 0;

                    var builds = Components.BuildingCompletes != null ? Components.BuildingCompletes.Items : null;
                    if (builds != null)
                    {
                        for (int i = 0; i < builds.Count; i++)
                        {
                            var b = builds[i];
                            if (b == null) continue;
                            UnityEngine.Component[] comps = null;
                            try { comps = b.GetComponentsInChildren<Storage>(); } catch { }
                            if (comps == null) continue;
                            for (int k = 0; k < comps.Length; k++)
                            {
                                var st = comps[k] as Storage;
                                if (st == null) continue;
                                storages++;
                                float amt = 0f;
                                try { amt = st.GetAmountAvailable(want); } catch { }
                                if (amt > 0) inStorage += amt;
                            }
                        }
                    }

                    // ---- ② onGround（地上散落：Pickupable 且 storage == null）----
                    try
                    {
                        var picks = Components.Pickupables != null ? Components.Pickupables.Items : null;
                        if (picks != null)
                        {
                            for (int i = 0; i < picks.Count; i++)
                            {
                                var p = picks[i];
                                if (p == null) continue;
                                UnityEngine.GameObject go = null;
                                try { go = p.gameObject; } catch { }
                                if (go == null) continue;
                                bool inStore = false;
                                try { inStore = p.storage != null; } catch { }
                                if (inStore) continue;

                                PrimaryElement pe = null;
                                try { pe = go.GetComponent<PrimaryElement>(); } catch { }
                                if (pe == null) { unnamedPickupables++; continue; }
                                string tname = null;
                                try { tname = pe.ElementID.ToString(); } catch { }
                                if (string.IsNullOrEmpty(tname)) { unnamedPickupables++; continue; }
                                if (!string.Equals(tname, tagFilter, System.StringComparison.OrdinalIgnoreCase)) continue;
                                float m = 0f;
                                try { m = pe.Mass; } catch { }
                                if (m > 0) { onGround += m; groundStacks++; }
                            }
                        }
                    }
                    catch { }

                    // ---- ③ inWorld（WorldInventory；读不到就如实标）----
                    double inWorld = 0;
                    bool worldUnavailable = false;
                    string worldErr = null;
                    int worldsScannedCount = 0;
                    try
                    {
                        // WorldInventory 是**实例**方法 ⇒ 从 ClusterManager 的每个世界取（多星球相加）
                        var cm = ClusterManager.Instance;
                        var worlds = cm != null ? cm.WorldContainers : null;
                        int worldsScanned = 0;
                        if (worlds != null)
                        {
                            for (int wi = 0; wi < worlds.Count; wi++)
                            {
                                var wc = worlds[wi];
                                if (wc == null) continue;
                                var inv = wc.worldInventory;
                                if (inv == null) continue;
                                worldsScanned++;
                                double a = 0;
                                try { a = inv.GetAmount(want, false); } catch { }
                                if (a > 0) inWorld += a;
                            }
                        }
                        if (worldsScanned == 0) { worldUnavailable = true; worldErr = "ClusterManager.WorldContainers 为空（worldsScanned=0）"; }
                        else if (inWorld <= 0 && (inStorage > 0 || onGround > 0)) worldUnavailable = true;
                        worldsScannedCount = worldsScanned;
                    }
                    catch (System.Exception e) { worldUnavailable = true; worldErr = e.GetType().Name + ": " + e.Message; }

                    var sb = new StringBuilder(512);
                    sb.Append("{\"tag\":").Append(Snap.Str(tagFilter));
                    sb.Append(",\"inStorage\":").Append(Num(inStorage));
                    sb.Append(",\"onGround\":").Append(Num(onGround));
                    sb.Append(",\"inWorld\":").Append(worldUnavailable ? "null" : Num(inWorld));
                    sb.Append(",\"inWorldUnavailable\":").Append(worldUnavailable ? "true" : "false");
                    if (worldErr != null) sb.Append(",\"inWorldError\":").Append(Snap.Str(worldErr));
                    sb.Append(",\"storagesScanned\":").Append(storages);
                    sb.Append(",\"groundStacks\":").Append(groundStacks);
                    sb.Append(",\"pickupablesUnnamed\":").Append(unnamedPickupables);
                    sb.Append(",\"worldsScanned\":").Append(worldsScannedCount);
                    sb.Append(",\"source\":").Append(Snap.Str(
                        "inStorage=Storage.GetAmountAvailable；onGround=Pickupables(无 storage) 的 PrimaryElement.Mass；inWorld=WorldInventory.GetAmount(tag,false)"));
                    sb.Append(",\"note\":").Append(Snap.Str(
                        "建造只认 inStorage（与 INSUFFICIENT_MATERIAL 的 have 同口径）；"
                        + "游戏资源面板显示的多半是 inWorld（含未挖矿）或 inWorld+onGround ⇒ 面板比 inStorage 大是正常的"));
                    sb.Append('}');
                    data = sb.ToString();
                    Log.Info("[inventory] tag=" + tagFilter + " inStorage=" + inStorage.ToString("0.###")
                             + " onGround=" + onGround.ToString("0.###")
                             + (worldUnavailable ? " inWorld=<unavailable>" : " inWorld=" + inWorld.ToString("0.###")));
                }
                catch (System.Exception e) { fail = e.GetType().Name + ": " + e.Message; }
            }, 5000, 30000, out okT, out busy);

            if (!ran) { status = 503; code = "RETRY_LATER"; message = "main thread busy；请 1 秒后重试"; return null; }
            if (fail != null) { status = 500; code = "INVENTORY_UNAVAILABLE"; message = fail; return null; }
            if (data == null) { status = 500; code = "INTERNAL"; message = "inventory 采集返回空"; return null; }
            return data;
        }

        static string Num(double v)
        {
            if (double.IsNaN(v) || double.IsInfinity(v)) return "null";
            return v.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
        }
    }
}
