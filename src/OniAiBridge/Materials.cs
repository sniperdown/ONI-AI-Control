using System.Text;

// ⚠️ 不要 using System 之后直接用 Action/DateTime：Assembly-CSharp 里的全局 Action 枚举会遮蔽 System.Action。
namespace OniAiBridge
{
    /// <summary>
    /// M6.45（**只读**）：`GET /v1/materials` —— 场上所有 Storage 里**实际可用**的材料清单（按 tag 聚合）。
    ///
    /// 为什么需要它：建造要按 `BuildingDef.MaterialCategory` 选材料，而"库存够不够"只能从 Storage 看。
    /// `/v1/state.elements` 是同一批数据但被截断（40 条上限）且混了"元素"语义；
    /// 这个端点**专供材料核对**，支持 `?tag=Cuprite` 精确查一项、`?minKg=` 过滤噪声。
    ///
    /// 口径与 `INSUFFICIENT_MATERIAL` 里的 `have` 完全一致（同一套 `GetAllIDsInStorage` + `GetAmountAvailable`）。
    /// </summary>
    internal static class Materials
    {
        public static string List(string tagFilter, double minKg, out int status, out string code, out string message)
        {
            status = 200; code = null; message = null;
            if (minKg < 0) minKg = 0;
            if (!MainThread.Ready) { status = 503; code = "NOT_READY"; message = "main-thread pump is not running yet"; return null; }

            string data = null, fail = null;
            int okT; bool busy;
            bool ran = MainThread.InvokeAdaptive(() =>
            {
                try
                {
                    var totals = new System.Collections.Generic.Dictionary<string, double>();
                    int storages = 0, tagsSeen = 0, unnamed = 0;

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
                                System.Collections.Generic.HashSet<Tag> ids = null;
                                try { ids = st.GetAllIDsInStorage(); } catch { }
                                if (ids == null) continue;
                                foreach (var id in ids)
                                {
                                    tagsSeen++;
                                    string nm = null;
                                    try { nm = id.Name; } catch { }
                                    if (string.IsNullOrEmpty(nm)) { try { nm = id.ToString(); } catch { } }
                                    if (string.IsNullOrEmpty(nm)) { unnamed++; continue; }
                                    if (!string.IsNullOrEmpty(tagFilter) && !string.Equals(nm, tagFilter, System.StringComparison.OrdinalIgnoreCase)) continue;
                                    double v = 0;
                                    try { v = st.GetAmountAvailable(id); } catch { continue; }
                                    double cur;
                                    totals.TryGetValue(nm, out cur);
                                    totals[nm] = cur + v;
                                }
                            }
                        }
                    }

                    var list = new System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<string, double>>();
                    foreach (var kv in totals) if (kv.Value >= minKg) list.Add(kv);
                    list.Sort((a, b2) => b2.Value.CompareTo(a.Value));

                    var sb = new StringBuilder(2048);
                    sb.Append("{\"storagesScanned\":").Append(storages);
                    sb.Append(",\"tagsSeen\":").Append(tagsSeen);
                    sb.Append(",\"tagsUnnamed\":").Append(unnamed);
                    sb.Append(",\"filter\":{\"tag\":").Append(Snap.Str(string.IsNullOrEmpty(tagFilter) ? null : tagFilter))
                      .Append(",\"minKg\":").Append(minKg.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)).Append('}');
                    sb.Append(",\"count\":").Append(list.Count);
                    sb.Append(",\"items\":[");
                    for (int i = 0; i < list.Count; i++)
                    {
                        if (i > 0) sb.Append(',');
                        sb.Append("{\"tag\":").Append(Snap.Str(list[i].Key));
                        sb.Append(",\"kg\":").Append(list[i].Value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)).Append('}');
                    }
                    sb.Append(']');
                    sb.Append(",\"source\":").Append(Snap.Str("Storage.GetAllIDsInStorage + GetAmountAvailable（与 INSUFFICIENT_MATERIAL 的 have 同口径）"));
                    sb.Append(",\"note\":").Append(Snap.Str(
                        "只读：不改变任何状态。这是\"储物箱里实际可用\"的数量，不含地上散落物、不含被预留/不可达部分"));
                    sb.Append('}');
                    data = sb.ToString();
                    Log.Info("[materials] storages=" + storages + " tagsSeen=" + tagsSeen + " emitted=" + list.Count
                             + (string.IsNullOrEmpty(tagFilter) ? "" : " filterTag=" + tagFilter));
                }
                catch (System.Exception e) { fail = e.GetType().Name + ": " + e.Message; }
            }, 5000, 30000, out okT, out busy);

            if (!ran) { status = 503; code = "RETRY_LATER"; message = "main thread busy；请 1 秒后重试"; return null; }
            if (fail != null) { status = 500; code = "MATERIALS_UNAVAILABLE"; message = fail; return null; }
            if (data == null) { status = 500; code = "INTERNAL"; message = "materials 采集返回空"; return null; }
            return data;
        }
    }
}
