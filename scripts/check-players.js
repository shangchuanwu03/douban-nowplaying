'use strict';

/**
 * 检查播放器：列出系统当前所有 SMTC 会话，并说明本软件会不会认它。
 *
 * 用法：双击「检查播放器.bat」，或在命令行跑 node scripts/check-players.js
 * 建议在目标播放器正在播放时运行。
 */

const { execFileSync } = require('child_process');
const fs = require('fs');
const path = require('path');

const ROOT = path.resolve(__dirname, '..');
const PS1 = path.join(ROOT, 'scripts', 'smtc-list.ps1');

function readSettings() {
  try {
    const db = JSON.parse(fs.readFileSync(path.join(ROOT, 'data', 'db.json'), 'utf8'));
    return db.settings || {};
  } catch {
    return {};
  }
}

function playerKind(app) {
  const v = String(app || '');
  if (!v) return null;
  if (/applemusic|apple\s*music/i.test(v)) return 'applemusic';
  if (/spotify/i.test(v)) return 'spotify';
  if (/cloudmusic|netease/i.test(v)) return 'netease';
  if (/qqmusic|qq\s*music/i.test(v)) return 'qqmusic';
  return null;
}

const KIND_LABEL = {
  applemusic: 'Apple Music',
  spotify: 'Spotify',
  netease: '网易云音乐',
  qqmusic: 'QQ 音乐',
};

const VIDEO_HINTS = /哔哩哔哩|bilibili|youtube|netflix|爱奇艺|腾讯视频|优酷|芒果tv|西瓜视频|抖音|douyin/i;
const BROWSER_APPS = /egolite|chrome|msedge|firefox|opera|brave|vivaldi|360se|qqbrowser/i;
const WEB_MUSIC_HINTS = /apple\s*music|spotify|soundcloud|bandcamp|tidal|qobuz|deezer|y\.qq\.com|music\.163/i;

function runList() {
  const out = execFileSync(
    'powershell.exe',
    ['-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', PS1],
    { encoding: 'utf8', timeout: 30000 }
  );
  const text = String(out).trim();
  const start = text.indexOf('{');
  if (start < 0) return [];
  const json = JSON.parse(text.slice(start));
  if (json.error) throw new Error(json.error);
  return json.sessions || [];
}

function main() {
  const settings = readSettings();
  const mode = settings.playerMode || 'auto';

  let sessions = [];
  try {
    sessions = runList();
  } catch (e) {
    console.log('读取系统媒体信息失败：' + e.message);
    console.log('（如果提示 SMTC 不可用，通常是系统媒体控制没就绪，重开一次播放器再试）');
    process.exit(1);
  }

  console.log('来源设置：' + (mode === 'auto' ? '自动（谁都认）' : '只认 ' + (KIND_LABEL[mode] || mode)));
  console.log('');

  if (!sessions.length) {
    console.log('系统里现在没有任何播放会话。');
    console.log('');
    console.log('可能原因：');
    console.log('  1. 播放器处于暂停/停止状态 —— 请让它真的在播放');
    console.log('  2. 这个播放器版本不上报「系统媒体控制(SMTC)」');
    console.log('  3. 系统媒体控制被禁用（服务 SysMain / 组策略）');
    process.exit(0);
  }

  console.log('找到 ' + sessions.length + ' 个会话：');
  for (const s of sessions) {
    const kind = playerKind(s.appId);
    const name = KIND_LABEL[kind] || s.appId || '(无 appId)';

    console.log('');
    console.log('● ' + name + (s.isCurrent ? '  ← 系统当前会话' : ''));
    console.log('    appId : ' + (s.appId || '(空)'));
    console.log('    状态  : ' + (s.status || '未知'));
    console.log('    曲目  : ' + (s.title || '(空)'));
    console.log('    艺人  : ' + (s.artist || '(空)'));
    console.log('    专辑  : ' + (s.album || '(空)'));

    // 判定
    const reasons = [];
    if (!s.appId) reasons.push('appId 是空的，系统没给来源标识');
    else if (!kind) reasons.push('appId 不在已知播放器列表里');
    if (mode !== 'auto' && kind && kind !== mode) reasons.push('设置里指定了其他来源，这个被过滤');

    const accepted = !reasons.length;
    console.log('    判定  : ' + (accepted ? '会被识别 ✅' : '不会被识别 ❌ — ' + reasons.join('；')));
    if (accepted && !s.album) {
      console.log('    注意  : 没拿到专辑名，只能按「艺人 + 歌名」去豆瓣搜，命中率会低一些');
    }
  }

  console.log('');
  console.log('提示：暂停状态下部分播放器不会上报会话，请在播放中再检查一次。');
}

main();
