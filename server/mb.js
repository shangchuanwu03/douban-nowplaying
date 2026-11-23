'use strict';

/**
 * MusicBrainz 桥接。
 *
 * 解决的问题：外区 Apple Music / Spotify 会把中文专辑显示成英文
 * （例如「叶惠美」→ "Ye Hui Mei"，「Common Jasmine Orange」→「七里香」），
 * 豆瓣那边只有中文名，用英文艺人去搜基本搜不到。
 *
 * 做法：拿英文的艺人/专辑去 MusicBrainz 查别名（中文/日文/韩文原名）和条码，
 * 再把这些别名交给豆瓣搜索。查不到就安静地返回空数组，不影响主流程。
 *
 * 约束：MusicBrainz 要求自定义 User-Agent，且每秒最多 1 次请求。
 */

const UA = 'DoubanNowPlaying/1.2.1 ( https://github.com/douban-nowplaying )';
const MIN_GAP = 1100;
const TTL = 7 * 24 * 60 * 60 * 1000;
const TIMEOUT = 8000;

let lastCall = 0;
const cache = new Map(); // key -> { at, value }

const CJK = /[\u3040-\u30ff\u3400-\u4dbf\u4e00-\u9fff\uac00-\ud7af]/;
const LATIN = /^[\x20-\x7e\u00c0-\u024f]+$/;

function hasKana(s) {
  return /[\u3040-\u309f\u30a0-\u30ff]/.test(String(s || ''));
}

function hasCjk(s) {
  return !!s && CJK.test(s);
}

function isLatin(s) {
  return !!s && LATIN.test(s) && !hasCjk(s);
}

function sleep(ms) {
  return new Promise((r) => setTimeout(r, ms));
}

async function mbGet(path) {
  const wait = MIN_GAP - (Date.now() - lastCall);
  if (wait > 0) await sleep(wait);
  lastCall = Date.now();

  const url = 'https://musicbrainz.org' + path;
  const headers = { 'User-Agent': UA, Accept: 'application/json' };

  let res = await fetch(url, { headers, signal: AbortSignal.timeout(TIMEOUT) });
  if (!res.ok && res.status >= 500) {
    await sleep(1500);
    lastCall = Date.now();
    res = await fetch(url, { headers, signal: AbortSignal.timeout(TIMEOUT) });
  }
  if (!res.ok) throw new Error('MusicBrainz HTTP ' + res.status);
  return res.json();
}

const FAIL_TTL = 10 * 60 * 1000;

function memo(key, loader) {
  const hit = cache.get(key);
  if (hit && Date.now() - hit.at < (hit.failed ? FAIL_TTL : TTL)) return Promise.resolve(hit.value);
  return loader()
    .then((value) => {
      cache.set(key, { at: Date.now(), value, failed: false });
      return value;
    })
    .catch(() => {
      cache.set(key, { at: Date.now(), value: null, failed: true });
      return null;
    });
}

/** 查艺人的全部别名（含中文/日文/韩文原名） */
function artistAliases(name) {
  if (!name) return Promise.resolve([]);
  return memo('artist:' + name.toLowerCase(), async () => {
    const j = await mbGet('/ws/2/artist?query=' + encodeURIComponent(name) + '&fmt=json&limit=3');
    const out = new Set();
    for (const a of (j.artists || []).slice(0, 3)) {
      if (a.name) out.add(a.name);
      for (const al of a.aliases || []) if (al.name) out.add(al.name);
    }
    return [...out];
  });
}

/** 查专辑（release-group）的别名和条码 */
function releaseGroupHints(artist, album) {
  if (!album) return Promise.resolve({ aliases: [], barcodes: [] });
  const key = 'rg:' + (artist || '').toLowerCase() + '|' + album.toLowerCase();
  return memo(key, async () => {
    const aliases = new Set();
    const barcodes = new Set();

    let groups = [];
    if (artist) {
      try {
        const q = `artist:"${artist}" AND releasegroup:"${album}"`;
        const j = await mbGet('/ws/2/release-group?query=' + encodeURIComponent(q) + '&fmt=json&limit=5');
        groups = j['release-groups'] || [];
      } catch {
        groups = [];
      }
    }
    if (!groups.length) {
      try {
        const j = await mbGet('/ws/2/release-group?query=' + encodeURIComponent('releasegroup:"' + album + '"') + '&fmt=json&limit=5');
        groups = j['release-groups'] || [];
      } catch {
        groups = [];
      }
    }

    for (const g of groups.slice(0, 2)) {
      // 搜索结果里就有别名时先用，没有再单独查一次
      for (const al of g.aliases || []) if (al.name) aliases.add(al.name);
      if (g.title) aliases.add(g.title);

      try {
        const full = await mbGet('/ws/2/release-group/' + g.id + '?inc=aliases+releases&fmt=json');
        for (const al of full.aliases || []) if (al.name) aliases.add(al.name);
        for (const rel of full.releases || []) {
          if (rel.barcode) barcodes.add(rel.barcode);
        }
      } catch {
        /* 单条失败不影响整体 */
      }
    }

    return { aliases: [...aliases], barcodes: [...barcodes] };
  });
}

/**
 * 给豆瓣搜索生成补充查询。
 * 返回 [{ query, artist, album, via }]，最多 limit 条。
 */
async function suggestQueries({ artist = '', album = '', limit = 3 } = {}) {
  const out = [];
  const seen = new Set();
  const push = (query, via, useArtist, useAlbum) => {
    const q = String(query || '').trim();
    if (!q || seen.has(q.toLowerCase())) return;
    seen.add(q.toLowerCase());
    out.push({ query: q, artist: useArtist, album: useAlbum, via });
  };

  const albumIsLatin = isLatin(album);
  const artistIsLatin = isLatin(artist);

  // 1) 艺人有中文别名 → 用中文艺人 + 英文专辑名
  if (artist && (artistIsLatin || !hasCjk(artist))) {
    const ali = (await artistAliases(artist)) || [];
    const cjk = ali.filter((n) => hasCjk(n) && n !== artist).slice(0, 2);
    for (const a of cjk) {
      if (album) push(`${a} ${album}`, 'mb:artist-alias', a, album);
    }
    // 2) 专辑有中文别名 → 中文艺人 + 中文专辑名
    const rg = (await releaseGroupHints(artist, album)) || { aliases: [], barcodes: [] };
    const cjkAlbums = (rg.aliases || []).filter((n) => hasCjk(n) && n !== album).slice(0, 2);
    for (const t of cjkAlbums) {
      push(`${cjk[0] || artist} ${t}`, 'mb:album-alias', cjk[0] || artist, t);
      push(t, 'mb:album-alias-only', '', t);
    }
    // 3) 条码：豆瓣部分条目把条形码写进了页面，搜索引擎可能命中
    for (const code of (rg.barcodes || []).slice(0, 1)) {
      push(code, 'mb:barcode', '', album);
    }
  } else if (hasCjk(artist) || hasCjk(album)) {
    // 中日文场景：只在「同一种文字」里换写法（繁体/简体、有无空格、本名/艺名），
    // 不拿罗马字去搜中文豆瓣，那样只会更差。
    const ali = (await artistAliases(artist)) || [];
    const rg = (await releaseGroupHints(artist, album)) || { aliases: [], barcodes: [] };

    const altAlbums = (rg.aliases || []).filter((n) => n !== album && hasCjk(n)).slice(0, 2);
    for (const t of altAlbums) push(`${artist} ${t}`, 'mb:cjk-album', artist, t);

    const altArtists = ali.filter((n) => n !== artist && hasCjk(n)).slice(0, 2);
    for (const a of altArtists) push(`${a} ${album}`, 'mb:cjk-artist', a, album);
  } else if (albumIsLatin) {
    // 英文艺人缺失时，只查专辑别名
    const rg = (await releaseGroupHints('', album)) || { aliases: [], barcodes: [] };
    const cjkAlbums = (rg.aliases || []).filter((n) => hasCjk(n) && n !== album).slice(0, 2);
    for (const t of cjkAlbums) push(t, 'mb:album-alias-only', '', t);
  }

  return out.slice(0, Math.max(1, limit));
}

module.exports = { suggestQueries, artistAliases, releaseGroupHints };
