using System;

// ⚠️ 不要 using System 之后直接用 Action/DateTime：Assembly-CSharp 里的全局 Action 枚举会遮蔽 System.Action。
namespace OniAiBridge
{
    /// <summary>
    /// M5.2d：**手写 JSON 的下发前护栏**。
    ///
    /// 起因（0.30.1 事故）：`/v1/catalog` 每个建筑对象结尾写了 `}` 却漏了开头的 `{` ⇒ 整份 JSON 非法。
    /// 客户端（curl + `ConvertFrom-Json`）不会报"语法错"，只给出错值（`count=1`），
    /// 我们据此排查了一整轮，看起来像业务 bug，实际是语法 bug。
    ///
    /// 规矩（写在这里，别再犯）：
    /// * 凡是**手工拼**的 JSON，**构造完立刻解析一遍**；解析失败就 `Log.Warn` + 抛异常，
    ///   由调用方转成明确的 500，**绝不把坏 JSON 发出去**。
    /// * 解析器用游戏自带的 `Newtonsoft.Json`（`Newtonsoft.Json.dll 7.0.0.0`，随游戏分发，不额外打包）。
    /// * 所有"生成数据"的接口日志都要打"生成了 N 条"，与响应里的计数同口径。
    /// </summary>
    internal static class JsonCheck
    {
        /// <summary>校验一段将要下发的 JSON；不合法就抛异常（调用方会回 500）。</summary>
        public static void Validate(string json, string tag)
        {
            if (string.IsNullOrEmpty(json))
            {
                Log.Warn("[" + tag + "] 生成的 JSON 为空（拒绝下发）");
                throw new InvalidOperationException(tag + ": JSON 为空");
            }
            try
            {
                Newtonsoft.Json.Linq.JToken.Parse(json);

                // Newtonsoft 对"尾随逗号"是宽容的（实测：`{"a":1,}` 能解析通过），
                // 而浏览器/`ConvertFrom-Json`/严格客户端会直接报错 ⇒ 自己扫一遍。
                int badAt = FindTrailingComma(json);
                if (badAt >= 0)
                {
                    string near = json.Substring(badAt, Math.Min(40, json.Length - badAt)).Replace("\n", " ");
                    Log.Warn("[" + tag + "] 生成的 JSON 有尾随逗号（已拒绝下发）: 偏移 " + badAt + " 附近: " + near);
                    throw new InvalidOperationException(tag + " 生成的 JSON 有尾随逗号（偏移 " + badAt + "）");
                }
            }
            catch (Exception e)
            {
                string head = json.Length > 240 ? json.Substring(0, 240) + "…" : json;
                Log.Warn("[" + tag + "] 生成的 JSON 非法（已拒绝下发）: " + e.GetType().Name + ": " + e.Message
                         + " | 长度=" + json.Length + " | 前 240 字符: " + head);
                throw new InvalidOperationException(tag + " 生成的 JSON 非法: " + e.Message);
            }
        }

        /// <summary>
        /// 扫"对象/数组里的尾随逗号"（如 `{"a":1,}` / `[1,2,]`），返回偏移；没有返回 -1。
        /// 只做一件事：字符串状态之外的 `,` 后面跳过空白若紧跟 `}` 或 `]` ⇒ 判定为尾随逗号。
        /// 会正确跳过 `"..."` 里的逗号与转义（`\"`、`\\`）。
        /// </summary>
        static int FindTrailingComma(string s)
        {
            bool inStr = false;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (inStr)
                {
                    if (c == '\\') { i++; continue; }
                    if (c == '"') inStr = false;
                    continue;
                }
                if (c == '"') { inStr = true; continue; }
                if (c == ',')
                {
                    int j = i + 1;
                    while (j < s.Length && (s[j] == ' ' || s[j] == '\t' || s[j] == '\r' || s[j] == '\n')) j++;
                    if (j < s.Length && (s[j] == '}' || s[j] == ']')) return i;
                }
            }
            return -1;
        }
    }
}
