#!/usr/bin/env node
// ONI AI Bridge · 游戏外配置工具（零依赖，Node 内置模块）
//
// 用法：
//   node tools/settings-ui/server.mjs            # 默认端口 8771，配置路径按 ONI 默认位置推断
//   node tools/settings-ui/server.mjs --port 8780
//   node tools/settings-ui/server.mjs --config "D:\somewhere\settings.json"
//   环境变量：ONI_BRIDGE_SETTINGS（等价于 --config）、PORT（等价于 --port）
//
// 设计要点（都是踩过坑之后定的）：
// 1. **只改自己认识的字段**：在原文上做定点替换，绝不整份重写 —— 保住 `_comment`、键顺序、格式。
// 2. **写盘前备份**：`settings.json.bak-<时间戳>`（同目录），写失败/校验失败都能退回。
// 3. **写完回读校验**：把每个写进去的字段从磁盘读回来逐个比对；任何一个不一致就报错（不谎报成功）。
// 4. **原子写**：先写 `settings.json.tmp` 再 rename，避免半截文件。
// 5. **token 只读**：只显示长度，不允许通过接口改。
// 6. **端口可改但要提示重启**：mod 只在启动时读配置。

import http from 'node:http';
import fs from 'node:fs';
import path from 'node:path';
import os from 'node:os';

// ---------------- 参数与配置路径 ----------------
function parseArgs(argv) {
  const out = { port: Number(process.env.PORT || 8771), config: process.env.ONI_BRIDGE_SETTINGS || null };
  for (let i = 2; i < argv.length; i++) {
    if (argv[i] === '--port' && argv[i + 1]) out.port = Number(argv[++i]);
    else if (argv[i] === '--config' && argv[i + 1]) out.config = argv[++i];
    else if (argv[i] === '--help' || argv[i] === '-h') out.help = true;
  }
  return out;
}

function defaultConfigPath() {
  const docs = path.join(os.homedir(), 'Documents');
  return path.join(docs, 'Klei', 'OxygenNotIncluded', 'mods', 'config', 'OniAiBridge', 'settings.json');
}

const args = parseArgs(process.argv);
if (args.help) {
  console.log('node server.mjs [--port 8771] [--config <settings.json>]');
  process.exit(0);
}
const CONFIG_PATH = args.config ? path.resolve(args.config) : defaultConfigPath();

// ---------------- 字段定义（校验与说明的唯一来源） ----------------
const FIELDS = [
  {
    key: 'allowCheats', type: 'bool', default: false, editable: true,
    label: '作弊总开关（allowCheats）',
    help: '打开后还要游戏处于沙盒模式或 Debug 模式，作弊命令才会真正解锁；正常游玩建议关闭。',
  },
  {
    key: 'alertStress', type: 'int', min: 0, max: 100, step: 1, default: 60, editable: true,
    label: '压力告警阈值（alertStress）',
    help: '复制人的压力值 ≥ 这个数时推送 stress_high 告警。范围 0–100。',
  },
  {
    key: 'alertCaloriesKcal', type: 'int', min: 100, max: 10000, step: 50, default: 1500, editable: true,
    label: '卡路里下限（alertCaloriesKcal）',
    help: '复制人卡路里 ≤ 这个数（千卡）时推送 calories_low 告警。范围 100–10000。',
  },
  {
    key: 'alertBladder', type: 'int', min: 0, max: 100, step: 5, default: 90, editable: true,
    label: '膀胱告警阈值（alertBladder）',
    help: '膀胱值 ≥ 这个数时推送 bladder_full 告警。范围 0–100。',
  },
  {
    key: 'amountEpsilon', type: 'float', min: 0, max: 10, step: 0.01, decimals: 3, default: 0.1, editable: true,
    label: '变化阈值（amountEpsilon）',
    help: '属性变化小于这个值时不推送 diff 事件（防抖）。范围 0–10，默认 0.1。',
  },
  {
    key: 'port', type: 'int', min: 1024, max: 65535, step: 1, default: 8770, editable: true,
    label: 'HTTP 端口（port）',
    help: 'API 监听端口。改完需要重启游戏才会生效（mod 只在启动时读配置）。范围 1024–65535。',
  },
  { key: 'token', type: 'token', editable: false, label: '访问令牌（token）', help: '出于安全只读；要更换请直接编辑文件（必须同时更新调用方）。' },
];

const FIELD_BY_KEY = new Map(FIELDS.map((f) => [f.key, f]));

// ---------------- 读写与校验 ----------------
function readRaw() {
  if (!fs.existsSync(CONFIG_PATH)) return null;
  return fs.readFileSync(CONFIG_PATH, 'utf8');
}

function parseValues(raw) {
  // 只取我们关心的字段；解析失败就当作缺省（不因为一个坏字段让整页打不开）
  const values = {};
  for (const f of FIELDS) {
    if (f.type === 'token') {
      const m = raw.match(/"token"\s*:\s*"([^"]*)"/);
      values.tokenLength = m ? m[1].length : 0;
      continue;
    }
    if (f.type === 'bool') {
      const m = raw.match(new RegExp(`"${f.key}"\\s*:\\s*(true|false)`));
      values[f.key] = m ? m[1] === 'true' : f.default;
    } else {
      const m = raw.match(new RegExp(`"${f.key}"\\s*:\\s*(-?[0-9]+(?:\\.[0-9]+)?)`));
      values[f.key] = m ? Number(m[1]) : f.default;
    }
  }
  return values;
}

function clampField(f, v) {
  let x = Number(v);
  if (!Number.isFinite(x)) return { ok: false, error: `${f.key} 不是合法数字` };
  let clamped = false;
  if (x < f.min) { x = f.min; clamped = true; }
  if (x > f.max) { x = f.max; clamped = true; }
  if (f.type === 'int') x = Math.round(x);
  else x = Number(x.toFixed(f.decimals ?? 3));
  return { ok: true, value: x, clamped };
}

function formatValue(f, v) {
  if (f.type === 'bool') return v ? 'true' : 'false';
  if (f.type === 'int') return String(Math.round(v));
  return String(Number(Number(v).toFixed(f.decimals ?? 3)));
}

function applyToRaw(raw, key, text) {
  const re = new RegExp(`("${key}"\\s*:\\s*)(?:"[^"]*"|true|false|-?[0-9]+(?:\\.[0-9]+)?)`);
  if (re.test(raw)) return raw.replace(re, `$1${text}`);
  // 字段不存在 ⇒ 插到最后一个 "}" 之前，保持 JSON 合法
  const idx = raw.lastIndexOf('}');
  if (idx < 0) throw new Error('settings.json 结构异常：找不到结尾的 }');
  const head = raw.slice(0, idx).replace(/,\s*$/, '');
  return `${head}${head.trim().endsWith('{') ? '' : ','}\n  "${key}": ${text}\n${raw.slice(idx)}`;
}

function defaultRaw() {
  const lines = FIELDS.filter((f) => f.type !== 'token').map((f) => `  "${f.key}": ${formatValue(f, f.default)}`);
  const token = [...Array(32)].map(() => '0123456789abcdef'[Math.floor(Math.random() * 16)]).join('');
  return `{\n  "_comment": "ONI AI Bridge settings. allowCheats gates the in-game Debug tools; leave it false for normal play.",\n${lines.join(',\n')},\n  "token": "${token}"\n}\n`;
}

function putSettings(body) {
  const raw = readRaw() ?? defaultRaw();
  const before = parseValues(raw);

  const applied = {};
  const clamped = [];
  const notes = {};

  // 逐字段：只处理请求里出现的键
  for (const [key, val] of Object.entries(body ?? {})) {
    if (key === 'token') {
      return { status: 400, payload: { ok: false, error: { code: 'FIELD_READONLY', message: 'token 只读，不允许通过工具修改' } } };
    }
    const f = FIELD_BY_KEY.get(key);
    if (!f || !f.editable) {
      return { status: 400, payload: { ok: false, error: { code: 'NO_SUCH_FIELD', message: `未知字段：${key}` } } };
    }
    if (f.type === 'bool') {
      applied[key] = val === true || val === 'true';
      continue;
    }
    const r = clampField(f, val);
    if (!r.ok) return { status: 400, payload: { ok: false, error: { code: 'INVALID_FIELD', message: r.error } } };
    applied[key] = r.value;
    if (r.clamped) clamped.push(key);
    if (key === 'port') notes.port = '改端口需要重启游戏才生效（mod 只在启动时读配置）';
  }

  if (Object.keys(applied).length === 0) {
    return { status: 400, payload: { ok: false, error: { code: 'NO_FIELDS', message: '没有可识别的字段' } } };
  }

  // 定点替换（保住注释/顺序/格式）
  let next = raw;
  for (const [key, val] of Object.entries(applied)) {
    const f = FIELD_BY_KEY.get(key);
    next = applyToRaw(next, key, formatValue(f, val));
  }

  // 备份 + 原子写
  fs.mkdirSync(path.dirname(CONFIG_PATH), { recursive: true });
  const stamp = new Date().toISOString().replace(/[-:T]/g, '').slice(0, 14);
  const backup = `${CONFIG_PATH}.bak-${stamp}`;
  if (fs.existsSync(CONFIG_PATH)) fs.copyFileSync(CONFIG_PATH, backup);
  else fs.writeFileSync(backup, raw, 'utf8');

  const tmp = `${CONFIG_PATH}.tmp`;
  fs.writeFileSync(tmp, next, 'utf8');
  fs.renameSync(tmp, CONFIG_PATH);

  // 回读校验：每个写进去的字段都要读回来比对；未改的字段必须原样
  const after = parseValues(fs.readFileSync(CONFIG_PATH, 'utf8'));
  const mismatches = [];
  for (const [key, val] of Object.entries(applied)) {
    if (typeof val === 'boolean') {
      if (after[key] !== val) mismatches.push(`${key}: 期望 ${val} 实际 ${after[key]}`);
    } else if (Math.abs(Number(after[key]) - Number(val)) > 1e-6) {
      mismatches.push(`${key}: 期望 ${val} 实际 ${after[key]}`);
    }
  }
  for (const f of FIELDS) {
    if (f.type === 'token' || applied[f.key] !== undefined) continue;
    const a = before[f.key], b = after[f.key];
    const same = typeof a === 'boolean' ? a === b : Math.abs(Number(a) - Number(b)) <= 1e-6;
    if (!same) mismatches.push(`${f.key}: 未请求修改却被改变（${a} → ${b}）`);
  }

  if (mismatches.length) {
    // 回滚
    try { fs.copyFileSync(backup, CONFIG_PATH); } catch { /* 尽力而为 */ }
    return {
      status: 500,
      payload: { ok: false, error: { code: 'VERIFY_FAILED', message: '写入后回读校验失败，已回滚', details: mismatches } },
    };
  }

  return { status: 200, payload: { ok: true, data: { applied, clamped, notes, backup, configPath: CONFIG_PATH, settings: after } } };
}

// ---------------- HTTP ----------------
function json(res, status, obj) {
  const body = JSON.stringify(obj, null, 2);
  res.writeHead(status, { 'Content-Type': 'application/json; charset=utf-8', 'Cache-Control': 'no-store' });
  res.end(body);
}

function page() {
  const rows = FIELDS.map((f) => {
    const range = f.type === 'bool' ? '' : f.type === 'token' ? '' : `（范围 ${f.min}–${f.max}）`;
    const control =
      f.type === 'bool'
        ? `<input type="checkbox" id="${f.key}">`
        : f.type === 'token'
        ? `<span class="ro" id="tokenInfo">—</span>`
        : `<input type="number" id="${f.key}" step="${f.step}" min="${f.min}" max="${f.max}">`;
    return `<div class="row"><label for="${f.key}">${f.label}</label>${control}<div class="hint">${f.help}${range}</div></div>`;
  }).join('\n');

  return `<!DOCTYPE html>
<html lang="zh-CN"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<title>ONI AI Bridge · 配置（游戏外）</title>
<style>
:root{color-scheme:dark}
body{margin:0;padding:24px;background:#14161a;color:#e6e6e6;font:14px/1.6 system-ui,"Segoe UI",sans-serif}
h1{font-size:18px;margin:0 0 4px}
.sub{color:#8b93a1;font-size:12px;margin-bottom:6px}
.warnbar{max-width:760px;margin:12px 0 18px;padding:10px 12px;border-radius:8px;background:#3a2d12;border:1px solid #6b5317;color:#f0b429;font-size:13px}
.card{max-width:760px;background:#1c1f26;border:1px solid #2a2f3a;border-radius:10px;padding:8px 18px}
.row{display:flex;flex-wrap:wrap;align-items:center;gap:12px;padding:12px 0;border-bottom:1px solid #262b34}
.row:last-child{border-bottom:0}
.row label{flex:0 0 240px;font-weight:600}
.row .hint{flex:1 1 320px;color:#8b93a1;font-size:12px}
input[type=number],input[type=text]{width:120px;background:#0f1116;border:1px solid #333a46;color:#e6e6e6;border-radius:6px;padding:6px 8px;font:inherit}
input[type=checkbox]{width:18px;height:18px}
button{background:#2f6feb;border:0;color:#fff;border-radius:8px;padding:9px 18px;font:inherit;cursor:pointer}
button:disabled{background:#3a4150;cursor:default}
#out{margin-top:14px;white-space:pre-wrap;font-size:13px;max-width:760px}
.ok{color:#5fd08a}.err{color:#ff7b72}.warn{color:#f0b429}.ro{color:#8b93a1}
code{background:#0f1116;padding:1px 5px;border-radius:4px;font-size:12px}
</style></head><body>
<h1>ONI AI Bridge · 配置（游戏外工具）</h1>
<div class="sub" id="status">读取中…</div>
<div class="warnbar">⚠️ <b>改完需要重启游戏才生效</b> —— mod 只在启动时读取 <code>settings.json</code>。写入前会自动备份到同目录的 <code>settings.json.bak-&lt;时间戳&gt;</code>。</div>
<div class="card">
${rows}
</div>
<button id="save">保存到 settings.json</button>
<div id="out"></div>
<script>
let META = null;
function setOut(msg, cls) { const o = document.getElementById('out'); o.className = cls || ''; o.textContent = msg; }
function load() {
  fetch('/api/settings').then(r => r.json()).then(j => {
    if (!j.ok) { setOut('读取失败：' + (j.error && j.error.message || 'unknown'), 'err'); return; }
    META = j.data;
    document.getElementById('status').textContent = '配置文件：' + j.data.configPath + (j.data.exists ? '' : '（不存在，保存时会新建）');
    const v = j.data.values;
    for (const f of j.data.fields) {
      const el = document.getElementById(f.key);
      if (!el) continue;
      if (f.type === 'bool') el.checked = !!v[f.key];
      else if (f.type === 'token') document.getElementById('tokenInfo').textContent = '长度 ' + v.tokenLength + '（只读）';
      else el.value = v[f.key];
    }
  }).catch(e => setOut('请求失败：' + e, 'err'));
}
function save() {
  const payload = {};
  for (const f of (META ? META.fields : [])) {
    if (f.type === 'token') continue;
    const el = document.getElementById(f.key);
    if (!el) continue;
    if (f.type === 'bool') payload[f.key] = el.checked;
    else if (el.value.trim() !== '') payload[f.key] = Number(el.value);
  }
  setOut('保存中…');
  document.getElementById('save').disabled = true;
  fetch('/api/settings', { method: 'PUT', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(payload) })
    .then(r => r.json()).then(j => {
      document.getElementById('save').disabled = false;
      if (!j.ok) {
        setOut('保存失败：' + (j.error && j.error.message || 'unknown') + (j.error && j.error.details ? '\\n' + JSON.stringify(j.error.details, null, 2) : ''), 'err');
        return;
      }
      const d = j.data;
      const lines = ['已写入并回读校验通过 ✔', '备份：' + d.backup];
      for (const k in d.applied) lines.push('  ' + k + ' = ' + d.applied[k]);
      if (d.clamped && d.clamped.length) lines.push('（已夹到合法范围：' + d.clamped.join(', ') + '）');
      for (const k in d.notes) lines.push('提示：' + k + ' — ' + d.notes[k]);
      setOut(lines.join('\\n'), 'ok');
      load();
    }).catch(e => { document.getElementById('save').disabled = false; setOut('请求失败：' + e, 'err'); });
}
document.getElementById('save').addEventListener('click', save);
load();
</script></body></html>`;
}

const server = http.createServer((req, res) => {
  const url = new URL(req.url, 'http://127.0.0.1');
  if (req.method === 'GET' && (url.pathname === '/' || url.pathname === '/index.html')) {
    const html = page();
    res.writeHead(200, { 'Content-Type': 'text/html; charset=utf-8', 'Cache-Control': 'no-store' });
    res.end(html);
    return;
  }
  if (req.method === 'GET' && url.pathname === '/api/settings') {
    const raw = readRaw();
    const values = raw ? parseValues(raw) : parseValues(defaultRaw());
    return json(res, 200, {
      ok: true,
      data: { configPath: CONFIG_PATH, exists: raw !== null, values, fields: FIELDS },
    });
  }
  if (req.method === 'PUT' && url.pathname === '/api/settings') {
    let body = '';
    req.on('data', (c) => { body += c; if (body.length > 64 * 1024) req.destroy(); });
    req.on('end', () => {
      let parsed;
      try { parsed = JSON.parse(body || '{}'); } catch { return json(res, 400, { ok: false, error: { code: 'BAD_JSON', message: '请求体不是合法 JSON' } }); }
      try {
        const r = putSettings(parsed);
        json(res, r.status, r.payload);
      } catch (e) {
        json(res, 500, { ok: false, error: { code: 'WRITE_FAILED', message: String(e && e.message || e) } });
      }
    });
    return;
  }
  json(res, 404, { ok: false, error: { code: 'NOT_FOUND', message: 'no such endpoint: ' + url.pathname } });
});

server.listen(args.port, '127.0.0.1', () => {
  console.log('ONI AI Bridge 配置工具已启动');
  console.log('  打开：http://127.0.0.1:' + args.port + '/');
  console.log('  配置：' + CONFIG_PATH + (fs.existsSync(CONFIG_PATH) ? '' : '（不存在，保存时新建）'));
  console.log('  改完需要重启游戏才生效。写完会自动备份 .bak-<时间戳> 并回读校验。');
});
