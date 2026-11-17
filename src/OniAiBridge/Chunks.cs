using System.Text;

// ⚠️ 不要 using System 之后直接用 Action/DateTime：Assembly-CSharp 里的全局 Action 枚举会遮蔽 System.Action。
namespace OniAiBridge
{
    /// <summary>
    /// M6.67：**区块（chunk）规则** —— 全项目唯一一处定义，`Worlds` / `Area` / `Job(dig)` 都调它。
    ///
    /// 规则（**写死**，与 `docs/API.md §3.31` 一致）：
    /// * 块大小 **32×32**（`ChunkSize`）
    /// * 原点 = 该世界的左下角（`WorldContainer.WorldOffset`，**运行时读取、不写死**）
    /// * Y 轴向下为正（与引擎 `Grid` 一致）
    /// * 列数 = `ceil(width/32)`、行数 = `ceil(height/32)`
    /// * **末尾不足一块的照常算一块**，并报告该块的真实 `w`/`h`（不是假装的 32）
    ///
    /// ⚠️ 坐标系：本文件里所有 `x/y` 都是**全局网格坐标**（与 `/v1/state` 的 `dupes[].x/y`、`Grid.XYToCell` 同一套）。
    /// </summary>
    internal static class Chunks
    {
        public const int ChunkSize = 32;

        public static int Cols(int width) { return width <= 0 ? 0 : (width + ChunkSize - 1) / ChunkSize; }
        public static int Rows(int height) { return height <= 0 ? 0 : (height + ChunkSize - 1) / ChunkSize; }

        /// <summary>取某世界的边界（全局坐标）。所有字段来自 `WorldContainer` 的 public 属性。</summary>
        public static bool TryWorldBBox(int worldId, out int ox, out int oy, out int w, out int h, out string err)
        {
            ox = 0; oy = 0; w = 0; h = 0; err = null;
            try
            {
                var cm = ClusterManager.Instance;
                if (cm == null) { err = "ClusterManager.Instance 为 null"; return false; }
                var wc = cm.GetWorld(worldId);
                if (wc == null) { err = "没有这个 worldId：" + worldId; return false; }

                var off = wc.WorldOffset;     // Vector2I（firstpass）public 属性
                ox = off.x; oy = off.y;
                w = wc.Width;                 // public 属性
                h = wc.Height;                // public 属性
                if (w <= 0 || h <= 0)
                {
                    var sz = wc.WorldSize;    // 兜底：WorldSize 也是 public
                    w = sz.x; h = sz.y;
                }
                if (w <= 0 || h <= 0) { err = "该世界宽高读到 0（width=" + w + ", height=" + h + "）"; return false; }
                return true;
            }
            catch (System.Exception e) { err = e.GetType().Name + ": " + e.Message; return false; }
        }

        /// <summary>
        /// 块 → 全局 bbox。越界（cx/cy 超出该世界的块行列数）返回 false 并给原因，**不静默裁剪**。
        /// </summary>
        public static bool TryChunkBBox(int worldId, int cx, int cy, out int x0, out int y0, out int x1, out int y1, out string err)
        {
            x0 = 0; y0 = 0; x1 = 0; y1 = 0;
            int ox, oy, w, h;
            if (!TryWorldBBox(worldId, out ox, out oy, out w, out h, out err)) return false;

            int cols = Cols(w), rows = Rows(h);
            if (cx < 0 || cx >= cols || cy < 0 || cy >= rows)
            {
                err = "块越界：cx=" + cx + " / cy=" + cy + "，该世界只有 " + cols + " 列 × " + rows + " 行块"
                    + "（" + w + "x" + h + "，块 " + ChunkSize + "）";
                return false;
            }

            x0 = ox + cx * ChunkSize;
            y0 = oy + cy * ChunkSize;
            int cw = System.Math.Min(ChunkSize, w - cx * ChunkSize);   // ★ 末尾块的真实宽
            int ch = System.Math.Min(ChunkSize, h - cy * ChunkSize);   // ★ 末尾块的真实高
            x1 = x0 + cw - 1;
            y1 = y0 + ch - 1;
            return true;
        }

        /// <summary>块的真实宽高（末尾块可能 &lt; 32）。</summary>
        public static void ChunkRealSize(int worldId, int cx, int cy, out int cw, out int ch)
        {
            cw = 0; ch = 0;
            int x0, y0, x1, y1; string err;
            if (TryChunkBBox(worldId, cx, cy, out x0, out y0, out x1, out y1, out err))
            {
                cw = x1 - x0 + 1;
                ch = y1 - y0 + 1;
            }
        }
    }
}
