using System.Globalization;
using System.Text;

// ⚠️ 不要 using System 之后直接用 Action/DateTime：Assembly-CSharp 里的全局 Action 枚举会遮蔽 System.Action。
namespace OniAiBridge
{
    /// <summary>
    /// M6.89（0.93.5）：`GET /v1/map` —— 地图**降采样**（逐格 元素 id / 温度 / 质量 三通道）。
    ///
    /// 设计依据（`DESIGN.md`）：
    /// * §2.3 第 4 条：**地图不默认下发** —— 必须带 `bbox` 与 `stride`，硬上限 64×64 格/次，
    ///   默认只给"元素 id + 温度 + 质量"三通道；
    /// * §3.2 端点表：`/v1/map` 默认关，`allowMap` 开关（闸门在 `Bridge.HandleMap` 里判，这里只管采集）。
    ///
    /// 四条硬要求：
    /// 1. **`bbox` 与 `stride` 缺任一 ⇒ 400 `BAD_REQUEST`**，错误信息里必须自说明格式（不猜默认值）；
    /// 2. 超过 `MaxSide`×`MaxSide` 格 ⇒ 400 `BBOX_TOO_LARGE`（上限只由 **bbox 的格数**决定，`stride` 不改变它）；
    /// 3. **迷雾门禁在采集循环里逐格判断**（`Vision.Allow(cell)`，与 `/v1/buildings` / `/v1/env` 同一个出处），
    ///    并在响应里如实报告 `fogged` / `visibility.filteredByFog`；
    /// 4. 响应必须能**还原坐标**：`bbox`（生效范围）+ `width` / `height` / `stride` 回显，
    ///    通道数组行主序（`idx = row*width + col`，`row 0 = y0`、`col 0 = x0`）。
    ///
    /// 表示法（**故意选"定长数组 + null 占位"**）：被迷雾滤掉 / 无效的格子不输出任何数值，
    /// 数组里是 `null`，长度不变 —— 这样调用方不必自己推导稀疏索引就能逐格对齐坐标。
    /// 代价是滤掉很多时响应里会有连片的 `null`（体积由 bbox 上限兜住：最多 4096 格）。
    ///
    /// 通道整体读不到时（例如 `Grid.Temperature` 抛异常），该通道给 `null` +
    /// `temperatureUnavailable` 说明原因 —— 不静默返回一串 0（那是"看起来很正常的假数据"）。
    /// </summary>
    internal static class MapQuery
    {
        /// <summary>单边硬上限（格）。`DESIGN.md` §2.3 第 4 条：默认 64×64 格 / 次。</summary>
        public const int MaxSide = 64;

        /// <summary>单次总格数上限（= 64×64）。</summary>
        public const int MaxCells = MaxSide * MaxSide;

        /// <summary>
        /// 采集一段 bbox 的降采样地图。返回 `data` 的 JSON；失败返回 null 并给出 status/code/message。
        /// **参数校验/上限判定在这里做**（不依赖 HTTP 层），HTTP 层只负责闸门与包装。
        /// </summary>
        public static string Query(string bboxRaw, string strideRaw, out int status, out string code, out string message)
        {
            status = 200; code = null; message = null;

            // ---------- 1) bbox：必填，且错误信息必须能独立解释格式 ----------
            if (string.IsNullOrEmpty(bboxRaw))
            {
                status = 400; code = "BAD_REQUEST"; message = MissBBox();
                return null;
            }
            int x0, y0, x1, y1;
            if (!TryParseBBox(bboxRaw, out x0, out y0, out x1, out y1))
            {
                status = 400; code = "BAD_REQUEST";
                message = "bbox 解析失败（收到 \"" + bboxRaw + "\"）。" + BBoxFormat()
                        + "例：bbox=10,20,13,25 表示 x 在 10..13、y 在 20..25 的 4×6 格区域（含两端）。";
                return null;
            }

            // ---------- 2) stride：必填，>= 1 ----------
            if (string.IsNullOrEmpty(strideRaw))
            {
                status = 400; code = "BAD_REQUEST"; message = MissStride();
                return null;
            }
            int stride;
            if (!int.TryParse(strideRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out stride) || stride < 1)
            {
                status = 400; code = "BAD_REQUEST";
                message = "stride 必须是 >= 1 的整数（收到 \"" + strideRaw + "\"）。"
                        + "stride=N 表示每 N 格取一个采样点：stride=1 逐格、stride=2 隔一格。"
                        + "stride 不设上限（bbox 本身就 ≤ " + MaxSide + "×" + MaxSide + " 格，再大也只会得到 1×1）。";
                return null;
            }

            // 端点顺序不敏感：x1<x0 也接受，归一化后回显（响应里的 bbox 是归一化后的）
            if (x1 < x0) { int t = x0; x0 = x1; x1 = t; }
            if (y1 < y0) { int t = y0; y0 = y1; y1 = t; }

            // ---------- 3) 硬上限：只按 bbox 的格数算 ----------
            long reqW = (long)x1 - x0 + 1, reqH = (long)y1 - y0 + 1;
            if (reqW > MaxSide || reqH > MaxSide)
            {
                status = 400; code = "BBOX_TOO_LARGE";
                message = "bbox 太大：本请求 " + reqW + "×" + reqH + " 格（" + (reqW * reqH) + " 格），"
                        + "单次硬上限 " + MaxSide + "×" + MaxSide + " 格（" + MaxCells + " 格）。"
                        + "请缩小 bbox 后分块请求；**stride 不改变这个上限**（它只降低采样密度，不缩小 bbox 覆盖的格数）。";
                return null;
            }

            if (!MainThread.Ready)
            {
                status = 503; code = "NOT_READY"; message = "main-thread pump is not running yet（未载入殖民地或 Harmony 未生效）";
                return null;
            }

            int rx0 = x0, ry0 = y0, rx1 = x1, ry1 = y1;   // 归一化后的请求值（lambda 里只读）
            string data = null, fail = null;
            string lCode = null, lMsg = null;             // lambda 内不能写 out 参数（CS1628）
            int okT; bool busy;

            bool ran = MainThread.InvokeAdaptive(() =>
            {
                try { data = Collect(rx0, ry0, rx1, ry1, stride, out lCode, out lMsg); }
                catch (System.Exception e) { fail = e.GetType().Name + ": " + e.Message; }
            }, 5000, 30000, out okT, out busy);

            if (!ran)
            {
                // 语义同 /v1/buildings：不是"查询超时"，而是"主线程还没轮到执行" ⇒ 调用方应重试
                int age = MainThread.LastPumpAgeMs;
                status = 503; code = "RETRY_LATER";
                message = "main thread busy（距上次心跳 " + age + " ms，队列 " + MainThread.QueueDepth
                        + " 项，本次已等 " + okT + " ms），请求**未执行**；请 1 秒后重试。";
                return null;
            }
            if (data == null)
            {
                if (lCode != null) { status = lCode == "BBOX_OUT_OF_BOUNDS" ? 400 : 500; code = lCode; message = lMsg; return null; }
                status = 500; code = "MAP_UNAVAILABLE"; message = fail ?? "map 采集返回空";
                return null;
            }
            JsonCheck.Validate(data, "map");   // 手写 JSON ⇒ 出门前必须能解析
            return data;
        }

        static string BBoxFormat()
        {
            return "要求 bbox=x0,y0,x1,y1：4 个整数、逗号分隔、**闭区间**（含两端）；";
        }

        static string MissBBox()
        {
            return "缺参数 bbox。" + BBoxFormat()
                 + "例：bbox=10,20,13,25 = x 10..13、y 20..25 的 4×6 格区域。"
                 + "**bbox 与 stride 必须同时给**，缺任一即 400 BAD_REQUEST（本端点没有默认值，不会臆测你想读哪块地图）。";
        }

        static string MissStride()
        {
            return "缺参数 stride。stride=N 是抽样步长（整数 N >= 1）：stride=1 逐格、stride=2 隔一格取一个。"
                 + "**bbox 与 stride 必须同时给**，缺任一即 400 BAD_REQUEST。";
        }

        /// <summary>
        /// 解析 `x0,y0,x1,y1`（整 4 段，全部必须是整数；容忍首尾空白与 URL 编码的逗号 `%2C`）。
        /// 少一段 / 多一段 / 非数字一律返回 false（由调用方回 400 并自说明格式）。
        /// </summary>
        static bool TryParseBBox(string raw, out int x0, out int y0, out int x1, out int y1)
        {
            x0 = y0 = x1 = y1 = 0;
            if (raw == null) return false;
            string s = raw.Replace("%2C", ",").Replace("%2c", ",").Trim();
            string[] parts = s.Split(',');
            if (parts.Length != 4) return false;
            var vals = new int[4];
            for (int i = 0; i < 4; i++)
            {
                string p = parts[i].Trim();
                if (p.Length == 0) return false;
                if (!int.TryParse(p, NumberStyles.Integer, CultureInfo.InvariantCulture, out vals[i])) return false;
            }
            x0 = vals[0]; y0 = vals[1]; x1 = vals[2]; y1 = vals[3];
            return true;
        }

        /// <summary>**只在主线程调用**（读 Grid / Element）。失败时置 code/message 并返回 null。</summary>
        static string Collect(int rx0, int ry0, int rx1, int ry1, int stride, out string code, out string message)
        {
            code = null; message = null;
            var swTotal = System.Diagnostics.Stopwatch.StartNew();

            int gw = Grid.WidthInCells, gh = Grid.HeightInCells;
            if (gw <= 0 || gh <= 0)
            {
                code = "MAP_UNAVAILABLE";
                message = "Grid 尺寸不可用（w=" + gw + ", h=" + gh + "）——殖民地没载入完？";
                return null;
            }

            // ★ 整框在网格之外必须报错，不能"clamp 成全空然后 ok:true"（同 /v1/env 的 REGION_OUT_OF_BOUNDS 口径）
            if (rx1 < 0 || ry1 < 0 || rx0 > gw - 1 || ry0 > gh - 1)
            {
                code = "BBOX_OUT_OF_BOUNDS";
                message = "bbox 完全在网格之外：请求 x=" + rx0 + ".." + rx1 + "、y=" + ry0 + ".." + ry1
                        + "；Grid 是 " + gw + "×" + gh + "，合法范围 x=0.." + (gw - 1) + "、y=0.." + (gh - 1) + "。";
                return null;
            }

            // 部分越界 ⇒ 夹到网格内，并在响应里同时回显 bboxRequested + clipped:true（不假装请求值就是生效值）
            int x0 = rx0 < 0 ? 0 : rx0;
            int y0 = ry0 < 0 ? 0 : ry0;
            int x1 = rx1 > gw - 1 ? gw - 1 : rx1;
            int y1 = ry1 > gh - 1 ? gh - 1 : ry1;
            bool clipped = (x0 != rx0) || (y0 != ry0) || (x1 != rx1) || (y1 != ry1);

            int bboxW = x1 - x0 + 1, bboxH = y1 - y0 + 1;
            int w = (bboxW - 1) / stride + 1;      // 采样栅格的列数
            int h = (bboxH - 1) / stride + 1;      // 采样栅格的行数
            int cells = w * h;

            var elems = new string[cells];
            var temps = new float[cells];
            var masses = new float[cells];
            for (int i = 0; i < cells; i++) { temps[i] = float.NaN; masses[i] = float.NaN; }   // NaN = 该格无数据

            bool fog = Vision.FogOn;
            int sampled = 0;            // 采样槽数（= cells，逐槽尝试）
            int emitted = 0;            // 真正给出数据的槽数
            int filteredByFog = 0;      // 被迷雾挡掉
            int filteredInvalid = 0;    // 格子无效（越界 / XYToCell 失败）
            int attempts = 0;           // 进到"读通道"这一步的格数（= emitted + 读失败的格数）
            int elemFail = 0, tempFail = 0, massFail = 0;
            string elemErr = null, tempErr = null, massErr = null;

            for (int row = 0; row < h; row++)
            {
                int yy = y0 + row * stride;
                for (int col = 0; col < w; col++)
                {
                    int xx = x0 + col * stride;
                    int idx = row * w + col;
                    sampled++;

                    int cell = -1;
                    try { cell = Grid.XYToCell(xx, yy); } catch { cell = -1; }
                    if (cell < 0 || !Grid.IsValidCell(cell)) { filteredInvalid++; continue; }

                    // ★ 视野门禁：逐格判断（与 /v1/buildings、/v1/env 同一个出处 Vision.Allow）
                    if (fog && !Vision.Allow(cell)) { filteredByFog++; continue; }

                    attempts++;

                    // 通道 1：元素 id
                    Element el = null;
                    try { el = Grid.Element[cell]; }
                    catch (System.Exception e) { elemFail++; if (elemErr == null) elemErr = e.GetType().Name + ": " + e.Message; }
                    string eid = null;
                    if (el != null) { try { eid = el.id.ToString(); } catch { eid = null; } }
                    elems[idx] = eid;

                    // 通道 2：温度（K）
                    try { temps[idx] = Grid.Temperature[cell]; }
                    catch (System.Exception e) { tempFail++; if (tempErr == null) tempErr = e.GetType().Name + ": " + e.Message; }

                    // 通道 3：质量（kg）
                    try { masses[idx] = Grid.Mass[cell]; }
                    catch (System.Exception e) { massFail++; if (massErr == null) massErr = e.GetType().Name + ": " + e.Message; }

                    emitted++;
                }
            }

            // 通道整体读不到 ⇒ 该通道给 null + *Unavailable（不静默下发一串 0）
            string elemUnavail = (attempts > 0 && elemFail == attempts) ? (elemErr ?? "Grid.Element 全部读取失败") : null;
            string tempUnavail = (attempts > 0 && tempFail == attempts) ? (tempErr ?? "Grid.Temperature 全部读取失败") : null;
            string massUnavail = (attempts > 0 && massFail == attempts) ? (massErr ?? "Grid.Mass 全部读取失败") : null;

            var sb = new StringBuilder(cells * 24 + 1024);
            sb.Append("{\"bbox\":{\"x0\":").Append(x0).Append(",\"y0\":").Append(y0)
              .Append(",\"x1\":").Append(x1).Append(",\"y1\":").Append(y1)
              .Append(",\"w\":").Append(bboxW).Append(",\"h\":").Append(bboxH).Append('}');
            if (clipped)
            {
                sb.Append(",\"bboxRequested\":{\"x0\":").Append(rx0).Append(",\"y0\":").Append(ry0)
                  .Append(",\"x1\":").Append(rx1).Append(",\"y1\":").Append(ry1).Append('}');
            }
            sb.Append(",\"clipped\":").Append(clipped ? "true" : "false");
            sb.Append(",\"stride\":").Append(stride);
            sb.Append(",\"width\":").Append(w).Append(",\"height\":").Append(h);
            sb.Append(",\"cells\":").Append(cells);
            sb.Append(",\"emitted\":").Append(emitted);
            sb.Append(",\"grid\":{\"widthInCells\":").Append(gw).Append(",\"heightInCells\":").Append(gh).Append('}');
            sb.Append(",\"units\":{\"temperature\":\"K\",\"mass\":\"kg\"}");
            sb.Append(",\"indexRule\":").Append(Snap.Str(
                "行主序：idx = row*width + col；row 0 = bbox.y0，col 0 = bbox.x0；"
                + "第 (row,col) 个采样格的坐标 = (bbox.x0 + col*stride, bbox.y0 + row*stride)"));

            sb.Append(",\"channels\":{");
            sb.Append("\"element\":");
            if (elemUnavail != null) sb.Append("null");
            else
            {
                sb.Append('[');
                for (int i = 0; i < cells; i++) { if (i > 0) sb.Append(','); sb.Append(Snap.Str(elems[i])); }
                sb.Append(']');
            }
            sb.Append(",\"temperature\":");
            if (tempUnavail != null) sb.Append("null");
            else
            {
                sb.Append('[');
                for (int i = 0; i < cells; i++) { if (i > 0) sb.Append(','); sb.Append(Num(temps[i])); }
                sb.Append(']');
            }
            sb.Append(",\"mass\":");
            if (massUnavail != null) sb.Append("null");
            else
            {
                sb.Append('[');
                for (int i = 0; i < cells; i++) { if (i > 0) sb.Append(','); sb.Append(Num(masses[i])); }
                sb.Append(']');
            }
            sb.Append('}');
            if (elemUnavail != null) sb.Append(",\"elementUnavailable\":").Append(Snap.Str(elemUnavail));
            if (tempUnavail != null) sb.Append(",\"temperatureUnavailable\":").Append(Snap.Str(tempUnavail));
            if (massUnavail != null) sb.Append(",\"massUnavailable\":").Append(Snap.Str(massUnavail));

            sb.Append(",\"fogged\":").Append(fog ? "true" : "false");
            sb.Append(",\"visibility\":{\"aiEnableFog\":").Append(fog ? "true" : "false");
            sb.Append(",\"sampled\":").Append(sampled);
            sb.Append(",\"emitted\":").Append(emitted);
            sb.Append(",\"filteredByFog\":").Append(filteredByFog);
            sb.Append(",\"filteredInvalid\":").Append(filteredInvalid);
            // 口径同 /v1/buildings：filteredCount 是"被滤掉的总数"（迷雾 + 无效），不是单指迷雾
            sb.Append(",\"filteredCount\":").Append(filteredByFog + filteredInvalid);
            sb.Append(",\"rule\":").Append(Snap.Str(fog ? "Grid.Visible[cell] > 0" : "none (omniscient)"))
              .Append('}');

            sb.Append(",\"source\":").Append(Snap.Str("Grid.Element[id] / Grid.Temperature / Grid.Mass（+ Vision.Allow = Grid.Visible>0 门禁）"));
            sb.Append(",\"note\":").Append(Snap.Str(
                "三通道默认集（元素 id / 温度 / 质量）。数组长度恒为 width×height，**行主序**，"
                + "数组里的 null = 该采样格没有数据（被迷雾滤掉 / 格子无效 / 该通道整列不可用），"
                + "**不是真空**：真空格给的是 element=\"Vacuum\"、mass=0。"
                + "温度单位 K（310.15 K = 37 °C），质量单位 kg（真空 0）。"
                + "单次硬上限 " + MaxSide + "×" + MaxSide + " 格（" + MaxCells + " 格）按 **bbox 的格数**判定，stride 不影响上限；"
                + "bbox 部分越界时夹到网格内并给 clipped:true + bboxRequested。"));
            sb.Append(",\"elapsedMs\":").Append(swTotal.ElapsedMilliseconds);
            sb.Append('}');

            string json = sb.ToString();
            // 判据（同 /v1/buildings）：硬等式必须成立，日志与响应同口径
            bool eq = sampled == emitted + filteredByFog + filteredInvalid;
            Log.Info("[map] bbox=" + x0 + "," + y0 + ".." + x1 + "," + y1
                     + " stride=" + stride + " grid=" + w + "x" + h + " cells=" + cells
                     + " emitted=" + emitted + " filteredByFog=" + filteredByFog + " filteredInvalid=" + filteredInvalid
                     + " clipped=" + clipped + " fogged=" + fog
                     + " bytes=" + json.Length + " elapsedMs=" + swTotal.ElapsedMilliseconds
                     + " | 硬等式 " + (eq ? "OK" : "FAIL"));
            if (!eq) Log.Warn("[map] 计数不自洽：sampled=" + sampled + " emitted=" + emitted
                              + " fog=" + filteredByFog + " invalid=" + filteredInvalid);
            return json;
        }

        static string Num(double v)
        {
            if (double.IsNaN(v) || double.IsInfinity(v)) return "null";
            return v.ToString("0.###", CultureInfo.InvariantCulture);
        }
    }
}
