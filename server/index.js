'use strict';

/**
 * 本地服务：把「系统正在播放」翻成「豆瓣上的那张专辑 + 评分」，
 * 并向界面推送状态（SSE）。同时提供评分保存与回写豆瓣的接口。
 */

const http = require('http');
const fs = require('fs');
const path = require('path');
const crypto = require('crypto');
const { URL } = require('url');

const store = require('./store');
const douban = require('./douban');
const { SmtcBridge } = require('./smtc');

const PORT = Number(process.env.PORT || 4590);
const HOST = process.env.HOST || '127.0.0.1';
const UI_DIR = path.join(__dirname, '..', 'ui');
const COVER_DIR = path.join(__dirname, '..', 'data', 'covers');

/* ------------------------------ 应用名映射 ------------------------------ */

const APP_NAMES = [
  [/applemusic|apple music/i, 'Apple Music'],
  [/spotify/i, 'Spotify'],
  [/cloudmusic|netease/i, '网易云音乐'],
  [/qqmusic/i, 'QQ 音乐'],
  [/kugou/i, '酷狗音乐'],
  [/foobar/i, 'foobar2000'],
  [/musicbee/i, 'MusicBee'],
  [/aimp/i, 'AIMP'],
  [/vlc/i, 'VLC'],
  [/potplayer|potplayermini/i, 'PotPlayer'],
  [/chrome/i, 'Chrome'],
  [/msedge/i, 'Edge'],
  [/firefox/i, 'Firefox'],
  [/egolite/i, 'ego 浏览器'],
  [/tidal/i, 'TIDAL'],
  [/qobuz/i, 'Qobuz'],
  [/deezer/i, 'Deezer'],
  [/amazon/i, 'Amazon Music'],
  [/youtube/i, 'YouTube'],
];

function appLabel(appId) {
  if (!appId) return '未知来源';
  for (const [re, name] of APP_NAMES) if (re.test(appId)) return name;
  const m = String(appId).match(/^([A-Za-z0-9]+)\./);
  return m ? m[1] : String(appId).slice(0, 24);
}

/* ------------------------------ 标题清洗 ------------------------------ */

function cleanText(s) {
  return String(s || '')
    .replace(/\s*[-–—]\s*Apple\s*Music\s*$/i, '')
    .replace(/\s*[_|]\s*哔哩哔哩.*$/i, '')
    .trim();
}

/** 从 SMTC 原始信息推断用于检索的 album / artist / track */
function interpret(raw) {
  const title = cleanText(raw.title);
  let artist = cleanText(raw.artist);
  let album = cleanText(raw.album);

  // Apple Music（Windows 版）经常把「艺人 — 专辑」整串塞进 artist 字段、album 留空，
  // 例如 artist: "IVE — I've IVE"。拆开才能按专辑匹配，而不是落到单曲上。
  if (!album && artist) {
    const split = artist.match(/^(.{1,60}?)\s+[—–]\s+(.{1,80})$/);
    if (split) {
      artist = split[1].trim();
      album = split[2].trim();
    }
  }

  // 浏览器里放 Apple Music 时，标题常是「歌名 - 艺人 - Apple Music」
  if (!album && /apple\s*music/i.test(raw.title + ' ' + raw.app)) {
    const parts = title.split(/\s*[-–—]\s*/).filter(Boolean);
    if (parts.length >= 2) {
      return { album: '', artist: parts[1] || artist, track: parts[0], browser: true };
    }
  }
  return { album, artist, track: title, browser: false };
}

function trackKey(info) {
  const a = douban.norm(info.album || info.track || '');
  const b = douban.norm(info.artist || '');
  return `${a}|${b}`;
}

/* ---------------------------- 播放器来源 ---------------------------- */

/** 把 SMTC 的 appId 归到四类播放器之一；认不出返回 null */
function playerKind(app) {
  const v = String(app || '');
  if (!v) return null;
  if (/applemusic|apple\s*music/i.test(v)) return 'applemusic';
  if (/spotify/i.test(v)) return 'spotify';
  if (/cloudmusic|netease/i.test(v)) return 'netease';
  if (/qqmusic|qq\s*music/i.test(v)) return 'qqmusic';
  return null;
}

/* ---------------------------- 会话过滤 ---------------------------- */

const VIDEO_HINTS = /哔哩哔哩|bilibili|youtube|netflix|爱奇艺|腾讯视频|优酷|芒果tv|西瓜视频|抖音|douyin/i;
const BROWSER_APPS = /egolite|chrome|msedge|firefox|opera|brave|vivaldi|360se|qqbrowser/i;
const WEB_MUSIC_HINTS = /apple\s*music|spotify|soundcloud|bandcamp|tidal|qobuz|deezer|y\.qq\.com|music\.163/i;

/**
 * 系统媒体控制会把所有正在播放的东西都报上来，包括浏览器里看的视频。
 * 这里只放行「确实像在听歌」的会话，免得看 B 站时跑去豆瓣搜视频标题。
 */
function isMusicSession(s) {
  if (!s || !s.app) return false;

  // 设置里指定了来源，就只认这个播放器
  const mode = store.getSettings().playerMode || 'auto';
  if (mode !== 'auto') {
    if (playerKind(s.app) !== mode) return false;
  }
  const title = String(s.title || '');
  const album = String(s.album || '');
  const artist = String(s.artist || '');

  if (VIDEO_HINTS.test(title) || VIDEO_HINTS.test(s.app)) return false;

  if (BROWSER_APPS.test(s.app)) {
    // 浏览器只有在明确是音乐站点，或带着专辑/艺人信息时才认
    return WEB_MUSIC_HINTS.test(title) || !!album || !!artist;
  }
  return true;
}

/* -------------------------------- 状态 -------------------------------- */

let player = null; // 最近一次 SMTC 快照（已按设置过滤）
let lastRaw = null; // 最近一次原始 SMTC 快照（未过滤），改设置时用它重新判定
let currentKey = null;
let current = null; // { subject, detail, confidence, pinned, queries }
let resolving = false;
let resolveToken = 0;
let resolveTimer = null;

const clients = new Set();

const HISTORY_GRACE_MS = 20000; // 播放超过 20 秒才记入历史

let historyTimer = null;

// 识别失败时的自动重试（豆瓣临时降级、网络抖动都很常见）
let retryTimer = null;
let retryCount = 0;
const RETRY_DELAYS = [12000, 30000, 90000, 180000, 300000, 600000];

// 最近若干次识别尝试，供 /api/diagnostics 查看
const resolveLog = [];

function clearRetry() {
  if (retryTimer) {
    clearTimeout(retryTimer);
    retryTimer = null;
  }
  retryCount = 0;
}

function scheduleRetry(key) {
  if (retryTimer || retryCount >= RETRY_DELAYS.length) return;
  const delay = RETRY_DELAYS[Math.min(retryCount, RETRY_DELAYS.length - 1)];
  retryCount += 1;
  retryTimer = setTimeout(() => {
    retryTimer = null;
    if (!player || !player.app) return;
    if (trackKey(interpret(player)) !== key) return; // 已经切歌
    resolveCurrent();
  }, delay);
}

function noteResolve(entry) {
  const rec = { ...entry, at: Date.now() };
  resolveLog.unshift(rec);
  if (resolveLog.length > 20) resolveLog.pop();
  const what = rec.info ? `${rec.info.artist || '?'} - ${rec.info.album || rec.info.track || '?'}` : '?';
  if (rec.result) {
    console.log(`[resolve] ok ${what} -> ${rec.result} (${rec.detail || ''})`);
  } else {
    console.log(`[resolve] fail ${what} -> ${rec.reason}`);
  }
}

function publicState() {
  const subjectId = current?.subject?.id || null;
  const info = player && player.app ? interpret(player) : null;
  return {
    player: player && player.app
      ? {
          app: player.app,
          appName: appLabel(player.app),
          status: player.status,
          title: info.track || cleanText(player.title),
          artist: info.artist,
          album: info.album,
          position: player.position,
          duration: player.duration,
          ts: player.ts,
        }
      : null,
    match: current
      ? {
          subject: current.subject,
          detail: current.detail,
          confidence: current.confidence,
          scores: current.scores,
          pinned: !!current.pinned,
        }
      : null,
    myRating: subjectId ? store.getRating(subjectId) : null,
    // 当前这张是否已经在「待评分」里（浮窗按钮据此显示「已在待评」）
    currentTodo: subjectId
      ? store.listTodo().some((t) => Number(t.subjectId) === Number(subjectId))
      : false,
    resolving,
    updatedAt: Date.now(),
  };
}

function broadcast() {
  const payload = `data: ${JSON.stringify(publicState())}\n\n`;
  for (const res of clients) {
    try {
      res.write(payload);
    } catch {
      clients.delete(res);
    }
  }
}

function openSubjectWindow() {
  /* 由 Electron 外壳打开详情页；纯网页模式用系统浏览器 */
}

async function resolveCurrent() {
  const raw = player;
  if (!raw) return;
  const info = interpret(raw);
  if (!info.track && !info.album) return;

  const key = trackKey(info);
  const token = ++resolveToken;
  resolving = true;
  broadcast();

  try {
    // 1) 这个 key 以前匹配过吗
    let pinnedId = null;
    const cached = store.getResolve(key);
    if (cached?.subjectId) pinnedId = cached.subjectId;

    let subject = null;
    let confidence = 0;
    let pinned = false;
    let confident = false;
    let scores = null;
    let attempt = null;

    if (pinnedId) {
      subject = { id: pinnedId, score: 0, votes: 0, title: '' };
      pinned = true;
      confidence = 100;
    } else {
      const r = await douban.resolveAlbum({
        album: info.album,
        artist: info.artist,
        track: info.track,
      });
      if (token !== resolveToken) return; // 已经切歌，放弃
      if (r) {
        attempt = r;
        if (r.subject) {
          subject = r.subject;
          confidence = r.matchScore;
          confident = !!r.confident;
          scores = r.scores || null;
        }
      }
    }

    if (!subject) {
      current = null;
      resolving = false;
      broadcast();
      noteResolve({
        info,
        key,
        reason: attempt?.reason || 'unknown',
        attempts: attempt?.attempts || [],
        best: attempt?.best || null,
      });
      scheduleRetry(key);
      return;
    }

    // 2) 取详情（评分分布 / 发行信息 / 短评），命中缓存就直接用
    let detail = store.getDetail(subject.id);
    if (!detail) {
      try {
        detail = await douban.getSubject(subject.id);
        if (token !== resolveToken) return;
        store.setDetail(subject.id, detail);
      } catch {
        detail = null;
      }
    }

    if (token !== resolveToken) return;

    const merged = {
      ...subject,
      title: detail?.title || subject.title || '',
      score: subject.score || detail?.score || 0,
      votes: subject.votes || detail?.votes || 0,
      cover: detail?.cover || subject.cover || '',
      artistText: detail?.artist || (subject.artists || []).join(' / '),
      date: detail?.date || subject.date || '',
      genre: detail?.genre || subject.genre || '',
      media: detail?.media || subject.media || '',
      kind: detail?.albumType || subject.kind || '',
      publisher: detail?.publisher || '',
      distribution: detail?.distribution || [],
      intro: detail?.intro || '',
      tags: detail?.tags || [],
      hotComments: detail?.hotComments || [],
    };

    current = { subject: merged, detail: detail || null, confidence, pinned, scores };
    // 只有「够可信」的匹配才进长期缓存，避免错一次错一个月
    if (!pinned && confident) {
      store.setResolve(key, { subjectId: merged.id, confidence, at: Date.now() });
    }
    resolving = false;
    clearRetry();
    broadcast();

    noteResolve({
      info,
      key,
      result: merged.title,
      detail: pinned ? '手动绑定' : `置信 ${confidence} 艺人${scores?.artist ?? '-'}/标题${scores?.title ?? '-'}`,
      attempts: attempt?.attempts || [],
    });

    // 3) 稳定播放一段时间再记历史
    scheduleHistory(info, merged, raw);
  } catch (e) {
    resolving = false;
    console.error('[resolve]', e.message);
    broadcast();
  }
}

function scheduleHistory(info, subject, raw) {
  if (historyTimer) clearTimeout(historyTimer);
  historyTimer = setTimeout(() => {
    const stillPlaying = player && player.status === 'Playing' && trackKey(interpret(player)) === trackKey(info);
    if (!stillPlaying) return;
    try {
      store.pushHistory({
        key: trackKey(info),
        subjectId: subject.id,
        title: subject.title,
        artist: subject.artistText || info.artist,
        album: info.album || '',
        cover: subject.cover || '',
        kind: subject.kind || '',
        date: subject.date || '',
        app: appLabel(raw.app),
      });
    } catch (e) {
      console.error('[history]', e.message);
    }
  }, HISTORY_GRACE_MS);
}

/* ------------------------------ SMTC 接入 ------------------------------ */

const bridge = new SmtcBridge({ intervalMs: 900 });

bridge.on('state', (s) => {
  lastRaw = s;
  const prevKey = player ? trackKey(interpret(player)) : null;

  // 非音乐会话（比如浏览器里看视频）当作「没在听歌」
  player = isMusicSession(s)
    ? s
    : { app: null, status: 'None', title: '', artist: '', album: '', position: 0, duration: 0, ts: Date.now() };

  const key = player.app ? trackKey(interpret(player)) : null;

  if (!player.app) {
    current = null;
    currentKey = null;
    if (resolveTimer) clearTimeout(resolveTimer);
    broadcast();
    return;
  }

  if (key !== prevKey || key !== currentKey) {
    clearRetry();
    currentKey = key;
    current = null;
    resolving = true;
    broadcast(); // 先显示「识别中」，体感更快
    if (resolveTimer) clearTimeout(resolveTimer);
    resolveTimer = setTimeout(() => {
      resolveCurrent();
    }, 1100);
  } else {
    broadcast(); // 同一首，只是播放/暂停/进度变化
  }
});

bridge.on('error', (e) => console.error('[smtc]', e.message));
bridge.on('exit', (code) => console.error('[smtc] 采集器退出 code=', code));

/* ------------------------------- 工具函数 ------------------------------ */

/**
 * 改了「识别来源」后，用上一次的原始快照重新判一次：
 * 采集脚本只在切歌时输出，不重判的话设置要等下次切歌才生效。
 */
function reapplyPlayer() {
  if (!lastRaw) return;
  const hadApp = !!(player && player.app);
  player = isMusicSession(lastRaw)
    ? lastRaw
    : { app: null, status: 'None', title: '', artist: '', album: '', position: 0, duration: 0, ts: Date.now() };

  if (!player.app) {
    current = null;
    currentKey = null;
    resolving = false;
    broadcast();
    return;
  }

  const key = trackKey(interpret(player));
  if (key !== currentKey || !hadApp) {
    currentKey = key;
    current = null;
    resolving = true;
    broadcast();
    resolveCurrent();
  } else {
    broadcast();
  }
}

function json(res, code, obj) {
  const body = JSON.stringify(obj);
  res.writeHead(code, {
    'Content-Type': 'application/json; charset=utf-8',
    'Cache-Control': 'no-store',
  });
  res.end(body);
}

function readBody(req, limit = 1 << 20) {
  return new Promise((resolve, reject) => {
    let size = 0;
    const chunks = [];
    req.on('data', (c) => {
      size += c.length;
      if (size > limit) {
        reject(new Error('body too large'));
        req.destroy();
        return;
      }
      chunks.push(c);
    });
    req.on('end', () => resolve(Buffer.concat(chunks).toString('utf8')));
    req.on('error', reject);
  });
}

const MIME = {
  '.html': 'text/html; charset=utf-8',
  '.css': 'text/css; charset=utf-8',
  '.js': 'application/javascript; charset=utf-8',
  '.json': 'application/json; charset=utf-8',
  '.svg': 'image/svg+xml',
  '.png': 'image/png',
  '.jpg': 'image/jpeg',
  '.woff2': 'font/woff2',
  '.ico': 'image/x-icon',
};

function serveStatic(req, res, pathname) {
  let rel = pathname === '/' ? '/overlay.html' : pathname;
  rel = decodeURIComponent(rel.split('?')[0]);
  const file = path.join(UI_DIR, rel);
  if (!file.startsWith(UI_DIR)) {
    res.writeHead(403);
    return res.end('forbidden');
  }
  fs.readFile(file, (err, buf) => {
    if (err) {
      res.writeHead(404, { 'Content-Type': 'text/plain; charset=utf-8' });
      return res.end('not found');
    }
    const ext = path.extname(file).toLowerCase();
    res.writeHead(200, {
      'Content-Type': MIME[ext] || 'application/octet-stream',
      'Cache-Control': ext === '.html' ? 'no-store' : 'public, max-age=3600',
    });
    res.end(buf);
  });
}

/** 豆瓣图床有防盗链，统一走本地代理，同时做磁盘缓存 */
async function serveCover(req, res, query) {
  const rawUrl = query.get('u');
  if (!rawUrl) {
    res.writeHead(400);
    return res.end('missing u');
  }
  const url = rawUrl
    .replace(/^http:/, 'https:')
    .replace(/\/view\/subject\/[sml]\//, '/view/subject/l/');
  const hash = crypto.createHash('sha1').update(url).digest('hex');
  const cached = path.join(COVER_DIR, `${hash}.img`);

  if (fs.existsSync(cached)) {
    res.writeHead(200, { 'Content-Type': 'image/jpeg', 'Cache-Control': 'public, max-age=86400' });
    return res.end(fs.readFileSync(cached));
  }

  try {
    const r = await fetch(url, {
      headers: {
        'User-Agent':
          'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/125.0.0.0 Safari/537.36',
        Referer: 'https://music.douban.com/',
      },
      signal: AbortSignal.timeout(15000),
    });
    if (!r.ok) throw new Error(`HTTP ${r.status}`);
    const buf = Buffer.from(await r.arrayBuffer());
    fs.mkdirSync(COVER_DIR, { recursive: true });
    fs.writeFileSync(cached, buf);
    res.writeHead(200, {
      'Content-Type': r.headers.get('content-type') || 'image/jpeg',
      'Cache-Control': 'public, max-age=86400',
    });
    res.end(buf);
  } catch (e) {
    res.writeHead(502, { 'Content-Type': 'text/plain; charset=utf-8' });
    res.end('cover fetch failed: ' + e.message);
  }
}

/* -------------------------------- 路由 -------------------------------- */

const server = http.createServer(async (req, res) => {
  const u = new URL(req.url, `http://${req.headers.host || 'localhost'}`);
  const p = u.pathname;

  // 允许 Electron file:// 与本地调试页访问
  res.setHeader('Access-Control-Allow-Origin', '*');
  res.setHeader('Access-Control-Allow-Headers', 'Content-Type');
  res.setHeader('Access-Control-Allow-Methods', 'GET,POST,OPTIONS');
  if (req.method === 'OPTIONS') {
    res.writeHead(204);
    return res.end();
  }

  try {
    if (p === '/api/state') return json(res, 200, publicState());

    if (p === '/api/stream') {
      res.writeHead(200, {
        'Content-Type': 'text/event-stream; charset=utf-8',
        'Cache-Control': 'no-cache, no-transform',
        Connection: 'keep-alive',
        'X-Accel-Buffering': 'no',
      });
      res.write('retry: 3000\n\n');
      res.write(`data: ${JSON.stringify(publicState())}\n\n`);
      clients.add(res);
      const hb = setInterval(() => {
        try {
          res.write(': ping\n\n');
        } catch {
          /* ignore */
        }
      }, 20000);
      req.on('close', () => {
        clearInterval(hb);
        clients.delete(res);
      });
      return;
    }

    if (p === '/api/cover') return serveCover(req, res, u.searchParams);

    if (p === '/api/history') {
      const limit = Math.min(500, Number(u.searchParams.get('limit')) || 60);
      return json(res, 200, { items: store.listHistory(limit) });
    }

    // 删除一条收听记录（管理页「收听史」里的删除按钮）
    if (p === '/api/history/delete' && req.method === 'POST') {
      let body = {};
      try {
        body = JSON.parse((await readBody(req)) || '{}');
      } catch {
        body = {};
      }
      const removed = store.removeHistory(body.id || body.key);
      broadcast();
      return json(res, 200, { ok: removed, removed });
    }

    if (p === '/api/ratings') return json(res, 200, { items: store.listRatings() });

    if (p === '/api/diagnostics') {
      return json(res, 200, {
        log: resolveLog,
        player: player ? { app: player.app, status: player.status, title: player.title, artist: player.artist, album: player.album } : null,
        retryCount,
      });
    }

    if (p === '/api/settings' && req.method === 'GET') {
      const s = store.getSettings();
      return json(res, 200, {
        ...s,
        doubanCookie: s.doubanCookie ? '已设置' : '',
        doubanUser: s.doubanUser || null,
      });
    }

    if (p === '/api/settings' && req.method === 'POST') {
      const body = JSON.parse((await readBody(req)) || '{}');
      const patch = {};
      if (typeof body.doubanCookie === 'string' && body.doubanCookie !== '已设置') {
        patch.doubanCookie = body.doubanCookie.trim();
        douban.setUserCookie(patch.doubanCookie);
      }
      if (typeof body.autoSync === 'boolean') patch.autoSync = body.autoSync;
      if (body.skin === 'neumorphic' || body.skin === 'glass') patch.skin = body.skin;
      if (typeof body.historyLimit === 'number') patch.historyLimit = body.historyLimit;
      if (typeof body.playerMode === 'string') {
        const ok = ['auto', 'applemusic', 'spotify', 'netease', 'qqmusic'];
        if (ok.indexOf(body.playerMode) >= 0) patch.playerMode = body.playerMode;
      }
      // 允许把导入游标归零，强制下次从头扫一遍
      if (typeof body.importCursor === 'number') patch.importCursor = Math.max(0, Math.floor(body.importCursor));
      if (body.doubanUser && typeof body.doubanUser === 'object') {
        patch.doubanUser = {
          uid: String(body.doubanUser.uid || ''),
          name: String(body.doubanUser.name || ''),
        };
      }
      if (body.unbind === true) {
        patch.doubanCookie = '';
        patch.doubanUser = null;
        douban.setUserCookie('');
      }
      const s = store.setSettings(patch);
      if (patch.playerMode) reapplyPlayer();
      return json(res, 200, {
        ok: true,
        settings: { ...s, doubanCookie: s.doubanCookie ? '已设置' : '', doubanUser: s.doubanUser || null },
      });
    }

    // 用 Cookie 验证登录身份（个人主页取昵称）
    if (p === '/api/douban/verify' && req.method === 'POST') {
      const body = JSON.parse((await readBody(req)) || '{}');
      const cookie = String(body.doubanCookie || store.getSettings().doubanCookie || '');
      if (!cookie) return json(res, 400, { ok: false, error: '没有可用的 Cookie' });
      try {
        const profile = await douban.getSelfProfile(cookie);
        return json(res, 200, profile);
      } catch (e) {
        return json(res, 200, { ok: false, error: e.message });
      }
    }

    // 匹配调试：看某个专辑名会被解析成哪一条、走了哪些查询
    if (p === '/api/match/test' && req.method === 'POST') {
      let body = {};
      try {
        body = JSON.parse((await readBody(req)) || '{}');
      } catch {
        body = {};
      }
      const album = String(body.album || '').trim();
      const artist = String(body.artist || '').trim();
      if (!album && !artist) return json(res, 400, { ok: false, error: '给个 album 或 artist' });
      try {
        const out = await douban.resolveAlbum({ album, artist, track: String(body.track || '') });
        return json(res, 200, {
          ok: true,
          hit: out.subject ? {
            id: out.subject.id,
            title: out.subject.title,
            artists: out.subject.artists,
            votes: out.subject.votes,
          } : null,
          matchScore: out.matchScore,
          confident: out.confident,
          via: out.via || '',
          query: out.query || '',
          reason: out.reason || '',
          attempts: out.attempts || [],
        });
      } catch (e) {
        return json(res, 200, { ok: false, error: e.message });
      }
    }

    // 待评分：浮窗「稍后再评」把当前专辑放进来
    if (p === '/api/todo' && req.method === 'GET') {
      const items = store.listTodo().map((t) => {
        const rec = t.subjectId ? store.getRating(t.subjectId) : null;
        return {
          ...t,
          rating: rec ? rec.rating || 0 : 0,
          comment: rec ? rec.comment || '' : '',
          synced: rec ? !!rec.synced : false,
        };
      });
      return json(res, 200, { ok: true, items });
    }

    if (p === '/api/todo' && req.method === 'POST') {
      let body = {};
      try {
        body = JSON.parse((await readBody(req)) || '{}');
      } catch {
        body = {};
      }
      const subjectId = Number(body.subjectId) || (current && current.subject ? current.subject.id : 0);
      const title0 = body.title || (current && current.subject ? current.subject.title : '');
      if (!title0 && !subjectId) return json(res, 400, { ok: false, error: '没有可加入的专辑' });
      const cur = current && current.subject && current.subject.id === subjectId ? current : null;
      const artistRaw =
        body.artist ||
        (cur && cur.subject && cur.subject.artist) ||
        (cur && cur.detail && Array.isArray(cur.detail.artists) ? cur.detail.artists.join(' / ') : '') ||
        (player && player.artist) ||
        '';
      // Apple Music 常把艺人写成「ACO — irony」，只取破折号前那部分
      const artist = String(artistRaw).split(/\s*[—–]\s*/)[0].trim();
      const rawApp = player && player.app ? player.app : '';
      const appName = /applemusic/i.test(rawApp) ? 'Apple Music' : (rawApp ? '' : '');

      const item = store.addTodo({
        subjectId,
        title: body.title || (cur && cur.subject ? cur.subject.title : ''),
        artist,
        cover: cur && cur.subject ? cur.subject.cover : '',
        kind: cur && cur.subject ? cur.subject.kind : '',
        app: appName,
      });
      broadcast();
      return json(res, 200, { ok: true, item });
    }

    if (p === '/api/todo/remove' && req.method === 'POST') {
      let body = {};
      try {
        body = JSON.parse((await readBody(req)) || '{}');
      } catch {
        body = {};
      }
      const removed = store.removeTodo(body.id || body.subjectId);
      broadcast();
      return json(res, 200, { ok: true, removed });
    }

    // 把豆瓣上「我听过的音乐」导入本地，避免重复打分/误覆盖
    if (p === '/api/douban/import' && req.method === 'POST') {
      const settings = store.getSettings();
      const cookie = settings.doubanCookie;
      if (!cookie) return json(res, 400, { ok: false, error: '还没有绑定豆瓣账号' });

      let uid = settings.doubanUser && settings.doubanUser.uid;
      if (!uid) {
        const prof = await douban.getSelfProfile(cookie);
        if (!prof.ok) return json(res, 400, { ok: false, error: '登录状态无效，请重新登录' });
        uid = prof.uid;
      }

      let body = {};
      try {
        body = JSON.parse((await readBody(req)) || '{}');
      } catch {
        body = {};
      }
      const maxPages = Math.max(1, Math.min(20, Number(body.maxPages) || 4));
      const settings0 = store.getSettings();
      const cursor = typeof body.start === 'number' ? body.start : Number(settings0.importCursor) || 0;
      const from = Math.max(0, cursor);

      const dry = body.dry === true;
      let start = from;
      let pages = 0;
      let imported = 0;
      let updated = 0;
      let keptLocal = 0;
      let hasMore = false;
      const seen = new Set();
      const anomalies = [];
      const preview = [];

      try {
        while (pages < maxPages) {
          const page = await douban.getCollectionPage(cookie, uid, start);
          if (!page.items.length) break;
          for (const it of page.items) {
            if (seen.has(it.id)) continue;
            seen.add(it.id);

            // 抓错保护：id/标题不合法、星级越界的一律跳过
            if (!Number.isFinite(it.id) || it.id <= 0 || !it.title) {
              anomalies.push({ id: it.id, reason: 'bad_item' });
              continue;
            }
            if (!(it.rating >= 0 && it.rating <= 5)) {
              anomalies.push({ id: it.id, title: it.title, reason: 'bad_rating', rating: it.rating });
              continue;
            }

            if (dry) {
              preview.push({
                id: it.id,
                title: it.title,
                artists: it.artists,
                rating: it.rating,
                comment: it.comment,
                date: it.date,
                kind: it.kind,
                hasLocal: !!store.getRating(it.id),
              });
              continue;
            }

            const prev = store.getRating(it.id) || null;
            const patch = {
              title: it.title || (prev && prev.title) || '',
              artist: it.artists.join(' / ') || (prev && prev.artist) || '',
              cover: it.cover || (prev && prev.cover) || '',
              source: 'douban',
              importedAt: Date.now(),
            };

            if (!prev) {
              // 豆瓣上没有评分（只是「听过」）时不要写 0 星，留成未评分
              patch.rating = it.rating > 0 ? it.rating : 0;
              patch.comment = it.comment || '';
              patch.synced = it.rating > 0;
              patch.syncedAt = it.rating > 0 ? Date.now() : null;
              imported += 1;
            } else if (prev.synced === false) {
              // 本地有还没同步到豆瓣的改动：保留用户的，只补元数据
              keptLocal += 1;
            } else {
              if (it.rating > 0) patch.rating = it.rating;
              patch.comment = it.comment || prev.comment || '';
              patch.synced = true;
              patch.syncedAt = Date.now();
              updated += 1;
            }

            store.setRating(it.id, patch);
          }
          pages += 1;
          if (page.next == null) break;
          start = page.next;
          hasMore = true;
        }
      } catch (e) {
        return json(res, 200, { ok: false, error: e.message, imported, updated, pages, nextStart: start, hasMore });
      }

      const more = hasMore && pages >= maxPages;
      // 预览模式不能动游标，否则真跑的时候会从半截开始
      if (!dry) store.setSettings({ importCursor: more ? start : 0 });
      broadcast();
      return json(res, 200, {
        ok: true,
        dry,
        imported,
        updated,
        keptLocal,
        pages,
        total: seen.size,
        nextStart: start,
        hasMore: more,
        anomalies: anomalies.slice(0, 20),
        preview: dry ? preview.slice(0, 60) : undefined,
      });
    }

    if (p === '/api/rating' && req.method === 'POST') {
      const body = JSON.parse((await readBody(req)) || '{}');
      const subjectId = Number(body.subjectId);
      if (!subjectId) return json(res, 400, { ok: false, error: '缺少 subjectId' });
      const rating = Math.max(0, Math.min(5, Number(body.rating) || 0));
      // 只覆盖真实传上来的字段：浮窗/待评分打分时并不会带 title/artist，
      // 空字符串不能把导入时存好的标题冲掉（曾经把专辑名冲成空白）。
      const patch = {
        rating,
        comment: String(body.comment || '').slice(0, 500),
        synced: false,
      };
      const t = String(body.title || '').trim();
      const a = String(body.artist || '').trim();
      const c = String(body.cover || '').trim();
      if (t) patch.title = t.slice(0, 200);
      if (a) patch.artist = a.slice(0, 200);
      if (c) patch.cover = c;
      const rec = store.setRating(subjectId, patch);
      broadcast();
      return json(res, 200, { ok: true, rating: rec });
    }

    if (p === '/api/sync' && req.method === 'POST') {
      const body = JSON.parse((await readBody(req)) || '{}');
      const subjectId = Number(body.subjectId);
      const rec = store.getRating(subjectId);
      if (!rec) return json(res, 400, { ok: false, error: '本地还没有这张的评分' });
      if (!rec.rating) return json(res, 400, { ok: false, error: '请先给 1-5 星' });

      const cookie = store.getSettings().doubanCookie;
      douban.setUserCookie(cookie);
      try {
        const out = await douban.postInterest({
          subjectId,
          rating: rec.rating,
          comment: rec.comment || '',
          cookie,
        });
        const ok = out.status === 200 && (!out.json || out.json.r === 0 || out.json.r === undefined);
        if (ok) {
          store.removeTodoBySubject(subjectId);
          store.setRating(subjectId, { synced: true, syncedAt: Date.now() });
          broadcast();
        }
        return json(res, 200, {
          ok,
          detail: out.json || out.raw || '已提交',
        });
      } catch (e) {
        return json(res, 400, { ok: false, error: e.message });
      }
    }

    if (p === '/api/search') {
      const q = u.searchParams.get('q') || '';
      if (!q) return json(res, 400, { ok: false, error: '缺少 q' });
      const items = await douban.search(q);
      // 手动绑定列表：优先显示标记人数多的条目，人少的往后放
      const sorted = items.slice().sort((a, b) => (Number(b.votes) || 0) - (Number(a.votes) || 0));
      return json(res, 200, { ok: true, items: sorted.slice(0, 12) });
    }

    // 手动改绑：把当前播放的这张专辑固定到你选的豆瓣条目
    if (p === '/api/pick' && req.method === 'POST') {
      const body = JSON.parse((await readBody(req)) || '{}');
      const subjectId = Number(body.subjectId);
      if (!player || !subjectId) return json(res, 400, { ok: false, error: '参数不足' });
      const info = interpret(player);
      store.setResolve(trackKey(info), { subjectId, confidence: 100, manual: true, at: Date.now() });
      await resolveCurrent();
      return json(res, 200, { ok: true });
    }

    // 没有播放器时的自测入口
    if (p === '/api/simulate' && req.method === 'POST') {
      const body = JSON.parse((await readBody(req)) || '{}');
      player = {
        app: body.app || 'manual.test',
        status: 'Playing',
        title: body.track || body.album || '',
        artist: body.artist || '',
        album: body.album || '',
        position: 0,
        duration: 240,
        ts: Date.now(),
      };
      currentKey = null;
      await resolveCurrent();
      return json(res, 200, publicState());
    }

    if (p.startsWith('/api/')) {
      return json(res, 404, { ok: false, error: 'no such api' });
    }

    return serveStatic(req, res, p);
  } catch (e) {
    console.error('[http]', e);
    return json(res, 500, { ok: false, error: e.message });
  }
});

/* -------------------------------- 启动 -------------------------------- */

function boot() {
  const settings = store.getSettings();
  douban.setUserCookie(settings.doubanCookie || '');
  bridge.start();

  server.on('error', (e) => {
    if (e.code === 'EADDRINUSE') {
      // 已经有一个实例在跑（例如上次没退干净），安静退出即可
      console.log(`[douban-nowplaying] 端口 ${PORT} 已被占用，复用已有实例`);
      bridge.stop();
      process.exit(0);
    }
    throw e;
  });

  server.listen(PORT, HOST, () => {
    console.log(`[douban-nowplaying] http://${HOST}:${PORT}`);
  });
}

module.exports = { server, boot };

if (require.main === module) boot();
