using System.Text;

// ⚠️ 不要 using System 之后直接用 Action/DateTime：Assembly-CSharp 里的全局 Action 枚举会遮蔽 System.Action。
namespace OniAiBridge
{
    /// <summary>
    /// M5.3c：**一次性诊断端点**支持（`GET /v1/debug/vision-sample?x=&amp;y=`）。
    ///
    /// 目的：一锤定音回答"玩家的迷雾到底是不是累积的"。
    /// 只需三次采样（镜头对准 → 镜头挪开 → 小人走过去），就能判定
    /// `Grid.Visible` 是「当前视口」还是「曾经可见（累积）」。
    ///
    /// 只读，不写任何游戏状态；验完可整段删掉。
    /// </summary>
    internal static class Vision
    {
        /// <summary>当前是否“有迷雾”（= 只报看得见的地方）。</summary>
        public static bool FogOn
        {
            get
            {
                BridgeSettings st = Bridge.SettingsRef;
                return st == null ? true : st.AIEnableFog;   // 兜底：配置缺失按“有迷雾”处理（宁可少给，不多给）
            }
        }

        /// <summary>
        /// **唯一的视野门禁**：迷雾开启时，只有 `Grid.Visible[cell] > 0`（有过亮度）才允许采集。
        /// 判据来自用户：“有亮度就算视野，毕竟对于人来说就是如此的”。
        /// ⚠️ 必须在**采集循环里逐项判断**，不要“先全查再 filter”（漏一个出口就泄露全图）。
        /// </summary>
        public static bool Allow(int cell)
        {
            if (!FogOn) return true;
            try { return Grid.Visible[cell] > 0; } catch { return false; }   // 出错按“看不见”处理
        }

        public static string ModeName { get { return FogOn ? "fogged" : "omniscient"; } }

        /// <summary>每次响应打一行断言日志（怕逻辑写反）。</summary>
        public static void LogMode(string where)
        {
            Log.Info("[vision] AIEnableFog=" + (FogOn ? "true" : "false") + " ⇒ "
                     + (FogOn ? "仅 Visible>0（迷雾内）" : "全图（omniscient）") + "  [" + where + "]");
        }

        public static string Sample(int x, int y, int cellIn, bool auto)
        {
            var sb = new StringBuilder(512);
            int cell;
            string picked;

            if (auto)
            {
                // 自动模式：取第一个活着的复制人所在格（小人站的地方必然"可见"）
                cell = -1;
                try
                {
                    var all = Components.LiveMinionIdentities;
                    if (all != null && all.Count > 0) cell = Grid.PosToCell(all[0].gameObject);
                }
                catch { cell = -1; }
                picked = "auto:first-duplicant";
                if (cell < 0) { return "{\"error\":\"auto 模式失败：没有活着的复制人，请显式给 ?x=&y= 或 ?cell=\"}"; }
            }
            else if (cellIn >= 0) { cell = cellIn; picked = "cell"; }
            else { cell = Grid.XYToCell(x, y); picked = "xy"; }

            int width = Grid.WidthInCells;
            sb.Append("{\"picked\":").Append(Snap.Str(picked));
            sb.Append(",\"cell\":").Append(cell);
            sb.Append(",\"x\":").Append(cell % width).Append(",\"y\":").Append(cell / width);
            sb.Append(",\"widthInCells\":").Append(width);
            bool valid = Grid.IsValidCell(cell);
            sb.Append(",\"validCell\":").Append(valid ? "true" : "false");
            if (!valid) { sb.Append('}'); return sb.ToString(); }

            // 1) Grid.Visible：0/1 之外的值有没有？只报原始字节
            byte visible = 0;
            try { visible = Grid.Visible[cell]; } catch { }
            sb.Append(",\"gridVisibleRaw\":").Append(visible);

            // 2) Grid.IsVisible（= Visible>0 || !FogOfWarEnabled）
            bool isVisible = false;
            try { isVisible = Grid.IsVisible(cell); } catch { }
            sb.Append(",\"isVisible\":").Append(isVisible ? "true" : "false");
            sb.Append(",\"allowByFogGate\":").Append(Allow(cell) ? "true" : "false");

            // 3) 迷雾全局开关与强度
            float fogScale = -1f;
            try { fogScale = PropertyTextures.FogOfWarScale; } catch { }
            sb.Append(",\"fogOfWarScale\":").Append(fogScale.ToString("F3", System.Globalization.CultureInfo.InvariantCulture));
            bool fogEnabled = false;
            try { fogEnabled = PropertyTextures.IsFogOfWarEnabled; } catch { }
            sb.Append(",\"isFogOfWarEnabled\":").Append(fogEnabled ? "true" : "false");

            // 4) 调试去迷雾开关（只读，不去改它）
            bool revealFog = false;
            try { revealFog = DebugHandler.RevealFogOfWar; } catch { }
            sb.Append(",\"debugRevealFogOfWar\":").Append(revealFog ? "true" : "false");

            // 5) 世界信息（当前世界 id / 当前世界是否已揭示表面）
            try
            {
                int worldId = Grid.WorldIdx != null ? Grid.WorldIdx[cell] : -1;
                sb.Append(",\"worldId\":").Append(worldId);
                var world = ClusterManager.Instance != null ? ClusterManager.Instance.GetWorld(worldId) : null;
                if (world != null)
                {
                    sb.Append(",\"worldName\":").Append(Snap.Str(world.GetProperName()));
                    sb.Append(",\"isSurfaceRevealed\":").Append(world.IsSurfaceRevealed ? "true" : "false");
                }
            }
            catch { }

            // 6) 星图上各星球的揭示等级（这是唯一"每颗星球一个"的探索状态）
            try
            {
                var cg = ClusterGrid.Instance;
                if (cg != null)
                {
                    sb.Append(",\"asteroids\":[");
                    bool first = true;
                    var seen = new System.Collections.Generic.HashSet<int>();
                    foreach (var kv in cg.cellContents)
                    {
                        if (kv.Value == null) continue;
                        for (int i = 0; i < kv.Value.Count; i++)
                        {
                            var ent = kv.Value[i] as AsteroidGridEntity;
                            if (ent == null) continue;
                            int wid = -1;
                            try { wid = ent.GetMyWorldId(); } catch { }
                            if (!seen.Add(wid)) continue;
                            if (!first) sb.Append(',');
                            first = false;
                            var level = cg.GetCellRevealLevel(ent.Location);
                            sb.Append("{\"worldId\":").Append(wid);
                            sb.Append(",\"name\":").Append(Snap.Str(ent.GetProperName()));
                            sb.Append(",\"revealLevel\":").Append(Snap.Str(level.ToString()));
                            sb.Append(",\"revealFraction\":").Append(cg.GetRevealCompleteFraction(ent.Location).ToString("F3", System.Globalization.CultureInfo.InvariantCulture));
                            sb.Append('}');
                        }
                    }
                    sb.Append(']');
                }
            }
            catch (System.Exception e) { sb.Append(",\"asteroidsError\":").Append(Snap.Str(e.GetType().Name + ": " + e.Message)); }

            sb.Append('}');
            return sb.ToString();
        }
    }
}
