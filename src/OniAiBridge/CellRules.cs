using System.Text;

// ⚠️ 不要 using System 之后直接用 Action/DateTime：Assembly-CSharp 里的全局 Action 枚举会遮蔽 System.Action。
namespace OniAiBridge
{
    /// <summary>
    /// M6.68：**"某格能不能挖"的唯一判据出处**（`Area` 与 `dig` 都调这里，别再各写一套）。
    ///
    /// 判据（**这就是全部**，没有隐藏条件）：
    /// 1. `Grid.IsValidCell(cell)`；
    /// 2. 该格是实心（`Grid.IsSolidCell(cell)` 为真，**或** `Grid.Solid[cell]` 为真）；
    /// 3. ~~元素不是 `Neutronium`~~ —— **实测 dig 不拦中子质**（由 `PlaceDig` 最终裁定），本判据不含这一条；
    /// 4. 该格**没有建筑**（`Grid.Objects[cell, ObjectLayer.Building]` 为空 —— 建筑要拆不要挖）；
    /// 5. 该格**还没有挖标记**（`DigPlacer` 层为空；已有标记的格由调用方记 `alreadyMarked`）。
    ///
    /// ⚠️ 重要语义：**这只是"预判"**。最终能不能真的打上标记，由游戏 `DigTool.PlaceDig` 的返回值决定
    /// （`PlaceDig` 返回 null ⇒ 游戏认为不可挖）。所以：
    /// * `Area.mineable` = 满足上述 1–5 的格数（**上界**）；
    /// * `dig.marked` = `PlaceDig` 真的成功了的格数（**下界/实际**）；
    /// * 两者允许有差：差多少 = 游戏在预判之外又拒绝了几个（会记进 `rejected.byReason.NOT_DIGGABLE`）。
    /// 以前 `Area` 自己写了一套更严的判据（把 `Unobtanium` 也算不可挖），导致
    /// **同一块 `area.mineable=144` 而 `dig.marked=419`** 的自相矛盾 —— 本文件就是那次修正的产物。
    /// </summary>
    internal static class CellRules
    {
        public enum DigVerdict
        {
            Diggable = 0,
            InvalidCell = 1,
            NotSolid = 2,
            Neutronium = 3,
            HasBuilding = 4,
            AlreadyMarked = 5
        }

        /// <summary>该格是否"预判可挖"；不可挖时用 <paramref name="verdict"/> 说明是哪一条挡住的。</summary>
        public static bool IsDiggable(int cell, out DigVerdict verdict)
        {
            verdict = DigVerdict.Diggable;

            bool valid = false;
            try { valid = Grid.IsValidCell(cell); } catch { }
            if (!valid) { verdict = DigVerdict.InvalidCell; return false; }

            if (!IsSolidForDig(cell)) { verdict = DigVerdict.NotSolid; return false; }

            // ⚠️ 中子质**不在这里拦**：实测 dig 区域路径只按 `IsSolidCell + 无建筑` 预判，
            //   中子质能否挖由游戏 `DigTool.PlaceDig` 最终裁定（它返回 null 就是挖不动）。
            //   这里单独提供 IsNeutronium(cell) 供 area 做"信息性"统计，**不参与可挖判定**。

            UnityEngine.GameObject bld = null;
            try { bld = Grid.Objects[cell, (int)ObjectLayer.Building]; } catch { }
            if (bld != null) { verdict = DigVerdict.HasBuilding; return false; }

            if (IsMarkedForDig(cell)) { verdict = DigVerdict.AlreadyMarked; return false; }

            return true;
        }

        /// <summary>
        /// 该格**是否已经有挖标记**（唯一判据 = 全层搜 `Diggable` 组件，与 `/v1/marks` 的 `dig.marked` 同源）。
        ///
        /// 为什么不用 `Grid.Objects[cell, ObjectLayer.DigPlacer] != null`：
        /// 实测（0.89.1）dig 报 `marked=419`，紧接着 area 只认出 **180** 个"已标记" ——
        /// 只查 `DigPlacer` 层会漏（标记物的落地方式与层索引并非一一对应）。
        /// 这里改成与 `/v1/marks` 完全相同的实现（`JobQuery.FindDiggableAt`，逐层找 `Diggable`）。
        /// </summary>
        public static bool IsMarkedForDig(int cell)
        {
            try
            {
                int hit, okL, errL; string errT;
                return JobQuery.FindDiggableAt(cell, out hit, out okL, out errL, out errT) != null;
            }
            catch { return false; }
        }
        /// <summary>"实心"的唯一判据（`Grid.IsSolidCell` 优先，兜底 `Grid.Solid`）—— dig 与 area 共用。</summary>
        public static bool IsSolidForDig(int cell)
        {
            bool solid = false;
            try { solid = Grid.IsSolidCell(cell); } catch { }
            if (!solid) { try { solid = Grid.Solid[cell]; } catch { } }
            return solid;
        }

        /// <summary>该格元素是不是中子质（**仅信息统计**，不参与可挖判定）。</summary>
        public static bool IsNeutronium(int cell)
        {
            try
            {
                var el = Grid.Element[cell];
                if (el == null) return false;
                string n = el.id.ToString();
                // ⚠️ 0.89.1 的 bug：只认 `Neutronium`，而游戏里那片"不可破坏"边界元素实际叫 **`Unobtanium`**
                //   ⇒ `cells.neutronium` 恒为 0。这里两个名字都认（它们都是"不可破坏"的边界物质）。
                return string.Equals(n, "Unobtanium", System.StringComparison.OrdinalIgnoreCase)
                    || string.Equals(n, "Neutronium", System.StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }
        /// <summary>只要布尔值时的简写。</summary>
        public static bool IsDiggable(int cell)
        {
            DigVerdict v;
            return IsDiggable(cell, out v);
        }

        /// <summary>给 JSON 用的原因名。</summary>
        public static string VerdictName(DigVerdict v)
        {
            switch (v)
            {
                case DigVerdict.InvalidCell: return "INVALID_CELL";
                case DigVerdict.NotSolid: return "NOT_SOLID";
                case DigVerdict.Neutronium: return "NEUTRONIUM";
                case DigVerdict.HasBuilding: return "HAS_BUILDING";
                case DigVerdict.AlreadyMarked: return "ALREADY_MARKED";
                default: return null;
            }
        }
    }
}
