using System.IO;
using System.Text;

// ⚠️ 不要 using System 之后直接用 Action/DateTime：Assembly-CSharp 里的全局 Action 枚举会遮蔽 System.Action。
namespace OniAiBridge
{
    /// <summary>
    /// M6.19：把"只能用鼠标点的操作"做成 API —— 先做**加载存档**这一条。
    ///
    /// * `GET  /v1/saves`      —— 列出存档（**只读**，无需闸门）
    /// * `POST /v1/save/load`  —— 加载存档（**必须 confirm:true**；`file` 必须是列表里出现过的）
    ///
    /// 关键设计（用户定的）：
    /// 1. **加载是长操作**：发起后**立刻返回 202**，不要在主线程里同步等几十秒，
    ///    调用方轮询 `/v1/meta` 的 `colonyLoaded` 判断完成；
    /// 2. **`file` 必须来自 `/v1/saves`** —— 防路径穿越，也防瞎猜；
    /// 3. **如实告知"当前进度若有未保存改动将丢失"**，但**不因此拒绝**（玩家点"加载"本来就会丢）。
    /// </summary>
    internal static class SavesQuery
    {
        /// <summary>存档根目录（含结尾分隔符）。</summary>
        static string Root()
        {
            string p = null;
            try { p = SaveLoader.GetSavePrefix(); } catch { }
            if (string.IsNullOrEmpty(p))
            {
                try { p = SaveLoader.GetSavePrefixAndCreateFolder(); } catch { }
            }
            return p;
        }

        /// <summary>把绝对路径转成"相对存档根"的路径（用 / 分隔），便于跨平台与展示。</summary>
        static string Rel(string full, string root)
        {
            if (string.IsNullOrEmpty(full)) return null;
            string s = full;
            try
            {
                if (!string.IsNullOrEmpty(root) && full.StartsWith(root, System.StringComparison.OrdinalIgnoreCase))
                    s = full.Substring(root.Length);
            }
            catch { }
            return s.Replace('\\', '/');
        }

        public static string List(out int status, out string code, out string message)
        {
            status = 200; code = null; message = null;

            string root = Root();
            if (string.IsNullOrEmpty(root))
            {
                status = 503; code = "SAVE_DIR_UNAVAILABLE"; message = "拿不到存档目录（SaveLoader.GetSavePrefix 返回空）";
                return null;
            }

            // ⚠️ 不用 SaveLoader.GetSaveFiles：它返回的 SaveFileEntry 形状在元数据 dump 里被"平铺"过（§10.6aa），
            //    headerData / Header 的类型归属不可信，编译已证明 dump 说的字段不存在。
            //    这里改用纯文件系统枚举 —— 确定、无 Unity 依赖，且"存档文件"本来就是磁盘上的东西。
            string[] files = null;
            string err = null;
            try { files = System.IO.Directory.GetFiles(root, "*" + SaveLoader.SAVE_EXTENSION, System.IO.SearchOption.AllDirectories); }
            catch (System.Exception e) { err = e.GetType().Name + ": " + e.Message; }

            if (files == null)
            {
                status = 500; code = "INTERNAL"; message = "枚举存档目录失败：" + (err ?? "返回 null");
                return null;
            }

            var sb = new StringBuilder(2048);
            sb.Append("{\"root\":").Append(Snap.Str(root));
            sb.Append(",\"scope\":").Append(Snap.Str("local-only(cloud_save_files 未扫)"));
            sb.Append(",\"count\":").Append(files.Length).Append(",\"saves\":[");
            int emitted = 0;
            for (int i = 0; i < files.Length; i++)
            {
                string full = files[i];
                if (string.IsNullOrEmpty(full)) continue;

                string rel = Rel(full, root);
                string fileName = null;
                try { fileName = System.IO.Path.GetFileName(full); } catch { }
                long size = -1;
                System.DateTime savedAt = default(System.DateTime);
                try { var fi = new System.IO.FileInfo(full); size = fi.Length; savedAt = fi.LastWriteTime; } catch { }

                // 存档头（殖民地名/周期/小人数量）需要解析 .sav 二进制 —— **本版不解**，如实给 null/0
                bool isAuto = false;
                try { isAuto = fileName != null && fileName.StartsWith("autosave", System.StringComparison.OrdinalIgnoreCase); } catch { }

                if (emitted > 0) sb.Append(',');
                emitted++;
                sb.Append("{\"file\":").Append(Snap.Str(rel));
                sb.Append(",\"fileName\":").Append(Snap.Str(fileName));
                sb.Append(",\"colony\":").Append("null");
                sb.Append(",\"cycle\":").Append("null");
                sb.Append(",\"dups\":").Append("null");
                sb.Append(",\"colonyInfoUnavailable\":").Append(Snap.Str("未解析存档头（.sav 二进制）；如需殖民地名/周期请后续版本加 LoadHeader"));
                sb.Append(",\"isAutoSave\":").Append(isAuto ? "true" : "false");
                sb.Append(",\"clusterId\":").Append("null");
                sb.Append(",\"savedAt\":").Append(Snap.Str(FmtTime(savedAt)));
                sb.Append(",\"sizeBytes\":").Append(size);
                sb.Append('}');
            }
            sb.Append("],\"note\":").Append(Snap.Str(
                "只扫描本地存档目录（cloud_save_files 未扫）；file 是相对存档根目录的路径，用于 POST /v1/save/load"));
            sb.Append('}');

            Log.Info("[saves] root=" + root + " count=" + emitted);
            return sb.ToString();
        }

        static string FmtTime(System.DateTime t)
        {
            try
            {
                if (t == default(System.DateTime)) return null;
                return t.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);
            }
            catch { return null; }
        }

        /// <summary>加载存档。返回 null 表示失败（status/code/message 已填）。</summary>
        public static string Load(string body, out int status, out string code, out string message)
        {
            status = 200; code = null; message = null;

            string file = MatchStr(body, "file");
            bool confirm = MatchBoolValue(body, "confirm");
            if (string.IsNullOrEmpty(file))
            {
                status = 400; code = "BAD_REQUEST";
                message = "需要 file（先 GET /v1/saves 拿列表，再用列表里的 file）";
                return null;
            }
            if (!confirm)
            {
                status = 400; code = "CONFIRM_REQUIRED";
                message = "加载存档会结束当前殖民地并丢掉未保存进度，必须显式 \"confirm\": true";
                return null;
            }

            // ★ 白名单：file 必须是 /v1/saves 里出现过的（防路径穿越 / 瞎猜）
            string root = Root();
            string target = null;
            string matchErr = null;
            try
            {
                var files = System.IO.Directory.GetFiles(root, "*" + SaveLoader.SAVE_EXTENSION, System.IO.SearchOption.AllDirectories);
                if (files != null)
                {
                    string want = file.Replace('\\', '/').TrimStart('/');
                    for (int i = 0; i < files.Length; i++)
                    {
                        string full = files[i];
                        if (string.IsNullOrEmpty(full)) continue;
                        string rel = Rel(full, root);
                        if (string.Equals(rel, want, System.StringComparison.OrdinalIgnoreCase))
                        { target = full; break; }
                    }
                }
            }
            catch (System.Exception e) { matchErr = e.GetType().Name + ": " + e.Message; }

            if (target == null)
            {
                status = 404; code = "SAVE_NOT_FOUND";
                message = "没有这个存档：file=" + file + "（必须先 GET /v1/saves 且用列表里的 file；"
                          + (matchErr != null ? "匹配过程出错：" + matchErr : "本次已重新扫描存档目录") + "）";
                return null;
            }

            // ================= M6.21：**硬禁用**（实测会破坏存档）=================
            // 实测（0.41.0，游戏内已进殖民地）：
            //   SaveLoader.Load(path) 内部顺序是 ClearScene() → SaveManager.Load(reader)
            //   ⇒ 当前殖民地被销毁；随后撞上单例冲突
            //      "[ERROR] Assert failed: WorldResourceAmountTracker … already initialized …
            //       existing instance GameObject: SaveGame, new: SaveGame(Clone)"
            //   ⇒ Load 返回 false，**而场景已经清了，没有回滚** ⇒ 旧档没了、新档没进来。
            //
            // 另外：主菜单下 SaveLoader.Instance 为 null（实测 LOAD_NOT_STARTED "Instance 为空"）
            //   ⇒ 同一时刻"游戏内会毁档、主菜单调不动"，这个入口**目前没有安全路径**。
            //   `MainMenu.LoadGame()` 是 nonpub（不上反射），所以正确入口待查。
            //
            // ⇒ 本版**直接拒绝执行**，只保留校验与说明；等找到游戏自己那条路再打开。
            bool inColony = false;
            try { inColony = SaveLoader.Instance != null; } catch { }
            status = 501; code = "LOAD_PATH_UNSAFE";
            message = "已禁用：在游戏内调用 SaveLoader.Load 会先 ClearScene，实测会**清空当前殖民地**且加载失败不回滚"
                      + "（单例冲突 SaveGame vs SaveGame(Clone)）；主菜单下 SaveLoader.Instance 为 null 又调不动。"
                      + "当前状态 inColony=" + (inColony ? "true" : "false")
                      + " ⇒ 需要先找到游戏自己『加载游戏』按钮的真实入口（MainMenu.LoadGame 是 nonpublic，本插件不用反射）。"
                      + "本次**未改动任何游戏状态**（目标文件：" + file + "，已通过白名单校验）";
            Log.Info("[save/load] 拒绝执行：LOAD_PATH_UNSAFE（inColony=" + inColony + "，file=" + file + "）");
            return null;

            var sb = new StringBuilder(512);
            sb.Append("{\"ok\":true");
            sb.Append(",\"accepted\":true");
            sb.Append(",\"jobState\":\"loading\"");
            sb.Append(",\"file\":").Append(Snap.Str(file));
            sb.Append(",\"note\":").Append(Snap.Str(
                "已发起加载（异步，几十秒）；**当前殖民地未保存的改动会丢失**。"
                + "请轮询 GET /v1/meta 的 colonyLoaded 判断是否加载完成（加载期间它会短暂变 false 再变 true）"));
            sb.Append('}');

            Log.Info("[save/load] 已发起：file=" + file + " target=" + target);
            return sb.ToString();
        }

        // ---- 极简 JSON 取值（与 JobQuery 的同名助手职责相同，这里独立一份避免跨类耦合）----
        static string MatchStr(string body, string key)
        {
            if (string.IsNullOrEmpty(body)) return null;
            var m = System.Text.RegularExpressions.Regex.Match(body, "\"" + key + "\"\\s*:\\s*\"([^\"]*)\"");
            return m.Success ? m.Groups[1].Value : null;
        }

        static bool MatchBoolValue(string body, string key)
        {
            if (string.IsNullOrEmpty(body)) return false;
            var m = System.Text.RegularExpressions.Regex.Match(body, "\"" + key + "\"\\s*:\\s*(true|false|\"true\"|\"false\")",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (!m.Success) return false;
            return m.Groups[1].Value.Trim('"').Equals("true", System.StringComparison.OrdinalIgnoreCase);
        }
    }
}
