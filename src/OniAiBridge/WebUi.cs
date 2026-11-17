using System.Text;

// ⚠️ 不要 using System 之后直接用 Action/DateTime：Assembly-CSharp 里的全局 Action 枚举会遮蔽 System.Action。
namespace OniAiBridge
{
    /// <summary>
    /// 网页版配置页（M4.4）。**全部内联**：没有 CDN、没有外链字体、没有外部脚本 ——
    /// 一是离线可用，二是"页面里不许有任何外链"这条安全要求。
    ///
    /// 为省掉转义麻烦，HTML 属性与 JS 字符串**一律用单引号**，C# 里就可以用普通字符串拼接。
    /// token 由服务端注入，页面用它在请求头里带 `X-Oni-Token`。
    /// </summary>
    internal static class WebUi
    {
        public static string Html(string token)
        {
            var sb = new StringBuilder(8192);
            sb.Append("<!DOCTYPE html><html lang='zh-CN'><head><meta charset='utf-8'>");
            sb.Append("<meta name='viewport' content='width=device-width,initial-scale=1'>");
            sb.Append("<title>ONI AI Bridge · 设置</title>");
            sb.Append("<style>");
            sb.Append(":root{color-scheme:dark;}");
            sb.Append("body{margin:0;padding:24px;background:#14161a;color:#e6e6e6;font:14px/1.6 system-ui,'Segoe UI',sans-serif;}");
            sb.Append("h1{font-size:18px;margin:0 0 4px;}");
            sb.Append(".sub{color:#8b93a1;font-size:12px;margin-bottom:18px;}");
            sb.Append(".card{max-width:640px;background:#1c1f26;border:1px solid #2a2f3a;border-radius:10px;padding:16px 18px;}");
            sb.Append(".row{display:flex;align-items:center;gap:12px;padding:10px 0;border-bottom:1px solid #262b34;}");
            sb.Append(".row:last-child{border-bottom:0;}");
            sb.Append(".row label{flex:0 0 190px;font-weight:600;}");
            sb.Append(".row .hint{color:#8b93a1;font-size:12px;}");
            sb.Append("input[type=number],input[type=text]{width:110px;background:#0f1116;border:1px solid #333a46;color:#e6e6e6;border-radius:6px;padding:6px 8px;font:inherit;}");
            sb.Append("input[type=checkbox]{width:18px;height:18px;}");
            sb.Append("button{margin-top:16px;background:#2f6feb;border:0;color:#fff;border-radius:8px;padding:9px 18px;font:inherit;cursor:pointer;}");
            sb.Append("button:disabled{background:#3a4150;cursor:default;}");
            sb.Append("#out{margin-top:14px;white-space:pre-wrap;font-size:13px;}");
            sb.Append(".ok{color:#5fd08a;} .err{color:#ff7b72;} .warn{color:#f0b429;}");
            sb.Append(".ro{color:#8b93a1;}");
            sb.Append("</style></head><body>");
            sb.Append("<h1>ONI AI Bridge · 配置</h1>");
            sb.Append("<div class='sub' id='status'>读取中…</div>");
            sb.Append("<div class='card'>");

            sb.Append("<div class='row'><label>allowCheats</label><input type='checkbox' id='allowCheats'>");
            sb.Append("<span class='hint'>作弊总开关；还要沙盒模式或 Debug 模式才会真正解锁</span></div>");

            AppendNum(sb, "alertStress", "压力告警阈值（0–100，压力 ≥ 它则告警）", "1");
            AppendNum(sb, "alertCaloriesKcal", "卡路里下限告警（100–10000 kcal）", "10");
            AppendNum(sb, "alertBladder", "膀胱告警阈值（0–100）", "1");
            AppendNum(sb, "amountEpsilon", "变化阈值（0–10，低于它不推 diff）", "0.01");
            AppendNum(sb, "port", "HTTP 端口（改完**需要重启 mod** 才生效）", "1");

            sb.Append("<div class='row'><label>token</label><span class='ro' id='tokenInfo'>—</span>");
            sb.Append("<span class='hint'>只读；出于安全不允许从网页修改</span></div>");

            sb.Append("</div>");
            sb.Append("<button id='save'>保存</button>");
            sb.Append("<div id='out'></div>");

            sb.Append("<script>");
            sb.Append("var TOKEN='").Append(token).Append("';");
            sb.Append("var FIELDS=['allowCheats','alertStress','alertCaloriesKcal','alertBladder','amountEpsilon','port'];");
            sb.Append("function hdr(extra){var h={'X-Oni-Token':TOKEN,'Accept':'application/json'};");
            sb.Append("if(extra){for(var k in extra){h[k]=extra[k];}}return h;}");
            sb.Append("function setOut(msg,cls){var o=document.getElementById('out');o.className=cls||'';o.textContent=msg;}");
            sb.Append("function load(){fetch('/v1/settings',{headers:hdr()}).then(function(r){return r.json();}).then(function(j){");
            sb.Append("if(!j.ok){setOut('读取失败：'+(j.error&&j.error.message||'unknown'),'err');return;}");
            sb.Append("document.getElementById('status').textContent='mod '+j.data.mod+' · '+j.data.stage+' · API '+j.data.apiVersion+' · 端口 '+j.data.port;");
            sb.Append("var s=j.data.settings;for(var i=0;i<FIELDS.length;i++){var f=FIELDS[i];var el=document.getElementById(f);");
            sb.Append("if(!el)continue;if(f==='allowCheats'){el.checked=!!s[f];}else{el.value=s[f];}}");
            sb.Append("document.getElementById('tokenInfo').textContent='长度 '+j.data.tokenLength+'（不显示内容）';");
            sb.Append("var n=j.data.notes||{};var m=[];for(var k in n){m.push(k+'：'+n[k]);}");
            sb.Append("if(m.length){setOut(m.join('\\n'),'warn');}}).catch(function(e){setOut('请求失败：'+e,'err');});}");
            sb.Append("function save(){var p={};");
            sb.Append("var cb=document.getElementById('allowCheats');p.allowCheats=cb.checked;");
            sb.Append("for(var i=1;i<FIELDS.length;i++){var f=FIELDS[i];var el=document.getElementById(f);if(!el)continue;");
            sb.Append("var v=el.value.trim();if(v===''){continue;}p[f]=Number(v);}");
            sb.Append("setOut('保存中…');document.getElementById('save').disabled=true;");
            sb.Append("fetch('/v1/settings',{method:'PUT',headers:hdr({'Content-Type':'application/json'}),body:JSON.stringify(p)})");
            sb.Append(".then(function(r){return r.json();}).then(function(j){");
            sb.Append("document.getElementById('save').disabled=false;");
            sb.Append("if(!j.ok){setOut('保存失败：'+(j.error&&j.error.message||'unknown'),'err');return;}");
            sb.Append("var d=j.data;var lines=['已保存：'];");
            sb.Append("if(d.applied){for(var k in d.applied){lines.push('  '+k+' = '+d.applied[k]);}}");
            sb.Append("if(d.clamped){lines.push('（以下字段被夹到合法范围：'+d.clamped.join(', ')+'）');}");
            sb.Append("if(d.notes){for(var n2 in d.notes){lines.push('提示：'+n2+' — '+d.notes[n2]);}}");
            sb.Append("setOut(lines.join('\\n'),'ok');load();");
            sb.Append("}).catch(function(e){document.getElementById('save').disabled=false;setOut('请求失败：'+e,'err');});}");
            sb.Append("document.getElementById('save').addEventListener('click',save);");
            sb.Append("load();");
            sb.Append("</script></body></html>");
            return sb.ToString();
        }

        static void AppendNum(StringBuilder sb, string id, string hint, string step)
        {
            sb.Append("<div class='row'><label>").Append(id).Append("</label>");
            sb.Append("<input type='number' id='").Append(id).Append("' step='").Append(step).Append("'>");
            sb.Append("<span class='hint'>").Append(hint).Append("</span></div>");
        }
    }
}
