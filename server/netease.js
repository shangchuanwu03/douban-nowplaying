'use strict';

/**
 * 歌名 → 专辑名 的补充查询。
 *
 * 为什么需要：网易云 / QQ 音乐通过窗口标题兜底时只有「歌名 + 艺人」，没有专辑名。
 * 豆瓣是按专辑收录的，用歌名直接搜命中率很低。这里借网易云的搜索接口
 * 把歌曲补全成「专辑」，再交给豆瓣按专辑去搜。
 *
 * 接口是公开的搜索接口，失败就安静返回 null，不影响主流程。
 */

const UA =
  'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/125.0.0.0 Safari/537.36';

const TTL = 7 * 24 * 60 * 60 * 1000;
const cache = new Map();

function norm(s) {
  return String(s || '')
    .toLowerCase()
    .replace(/\s+/g, ' ')
    .trim();
}

function looseEqual(a, b) {
  const x = norm(a);
  const y = norm(b);
  if (!x || !y) return false;
  return x === y || x.indexOf(y) >= 0 || y.indexOf(x) >= 0;
}

/**
 * 按「歌名 + 艺人」查所属专辑。
 * @returns {Promise<{album:string, artist:string, source:string}|null>}
 */
async function lookupAlbum(artist, track) {
  const t = String(track || '').trim();
  if (!t) return null;

  const key = norm(`${artist}|${t}`);
  const hit = cache.get(key);
  if (hit && Date.now() - hit.at < TTL) return hit.value;

  const value = await (async () => {
    try {
      const q = [t, artist].filter(Boolean).join(' ');
      const url =
        'https://music.163.com/api/search/get?s=' +
        encodeURIComponent(q) +
        '&type=1&limit=8';
      const res = await fetch(url, {
        headers: { 'User-Agent': UA, Referer: 'https://music.163.com/' },
        signal: AbortSignal.timeout(6000),
      });
      if (!res.ok) return null;
      const j = await res.json();
      const songs = (j && j.result && j.result.songs) || [];

      // 优先取歌名对得上、艺人也能对上的那条
      let best = null;
      for (const s of songs) {
        const name = String(s.name || '').trim();
        const album = s.album && s.album.name ? String(s.album.name).trim() : '';
        const artists = (s.artists || []).map((a) => String(a.name || '').trim()).filter(Boolean);
        if (!album) continue;

        const trackOk = looseEqual(name, t);
        const artistOk = !artist || artists.some((a) => looseEqual(a, artist));
        if (!trackOk && !artistOk) continue;

        const score = (trackOk ? 2 : 0) + (artistOk ? 1 : 0);
        if (!best || score > best.score) best = { album, artist: artists.join(' / '), score };
      }

      if (!best) return null;
      return { album: best.album, artist: best.artist, source: 'music.163' };
    } catch {
      return null;
    }
  })();

  cache.set(key, { at: Date.now(), value });
  return value;
}

module.exports = { lookupAlbum };
