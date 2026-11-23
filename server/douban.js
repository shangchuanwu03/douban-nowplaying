'use strict';

/**
 * 豆瓣音乐数据层。
 * 只依赖 Node 内置能力（fetch / AbortSignal），不引第三方包。
 *
 * 数据来源：
 *  - 搜索：https://music.douban.com/subject_search?search_text=xxx
 *    页面内嵌 window.__DATA__ 的 JSON，含 id / 标题 / 表演者 / 评分 / 评分人数。
 *  - 详情：https://music.douban.com/subject/{id}/
 *    含评分分布、出版者、介质、流派、条形码、简介、标签。
 *  - 回写：POST https://music.douban.com/j/subject/{id}/interest
 */

const BASE = 'https://music.douban.com';
const UA =
  'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 ' +
  '(KHTML, like Gecko) Chrome/125.0.0.0 Safari/537.36';

const MIN_GAP_MS = 1800; // 对豆瓣保持克制，避免触发风控
let lastRequestAt = 0;

const fs = require('fs');
const path = require('path');
const DATA_DIR = path.join(__dirname, '..', 'data');
const JAR_FILE = path.join(DATA_DIR, 'http-cookies.json');

/** visitor 级 Cookie（主要是 bid）。登录 Cookie 另行叠加。 */
let jar = {};
let userCookie = '';

function loadJar() {
  try {
    jar = JSON.parse(fs.readFileSync(JAR_FILE, 'utf8')) || {};
  } catch {
    jar = {};
  }
}
function saveJar() {
  try {
    fs.mkdirSync(DATA_DIR, { recursive: true });
    fs.writeFileSync(JAR_FILE, JSON.stringify(jar, null, 2));
  } catch {
    /* 写不了就只在内存里保留 */
  }
}
function buildCookieHeader() {
  const parts = Object.entries(jar).map(([k, v]) => `${k}=${v}`);
  if (userCookie) parts.push(userCookie);
  return parts.join('; ');
}
function absorbCookies(res) {
  let changed = false;
  const list = res.headers.getSetCookie ? res.headers.getSetCookie() : [];
  for (const raw of list) {
    const kv = raw.split(';')[0];
    const i = kv.indexOf('=');
    if (i <= 0) continue;
    const k = kv.slice(0, i).trim();
    const v = kv.slice(i + 1).trim();
    if (k && jar[k] !== v) {
      jar[k] = v;
      changed = true;
    }
  }
  if (changed) saveJar();
  return changed;
}

function sleep(ms) {
  return new Promise((r) => setTimeout(r, ms));
}

async function throttle() {
  const wait = lastRequestAt + MIN_GAP_MS - Date.now();
  if (wait > 0) await sleep(wait);
  lastRequestAt = Date.now();
}

loadJar();

async function httpGet(url, { cookie = '', referer = `${BASE}/`, raw = false } = {}) {
  // 首次访问先领一个 bid，让豆瓣把我们当同一个 visitor，而不是每次新客人
  if (!raw && !jar.bid) {
    try {
      await httpGet(`${BASE}/`, { raw: true });
    } catch {
      /* 领不到也继续，后面会重试 */
    }
  }

  await throttle();
  const headers = {
    'User-Agent': UA,
    'Accept': 'text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8',
    'Accept-Language': 'zh-CN,zh;q=0.9,en;q=0.6',
    'Referer': referer,
  };
  const c = cookie || buildCookieHeader();
  if (c) headers['Cookie'] = c;

  const res = await fetch(url, {
    headers,
    redirect: 'follow',
    signal: AbortSignal.timeout(20000),
  });
  absorbCookies(res);
  const text = await res.text();
  return { status: res.status, text, url: res.url };
}

/** 设置/更新用户登录 Cookie（如 bid; dbcl2; ck=...） */
function setUserCookie(str) {
  userCookie = String(str || '').trim().replace(/^cookie:\s*/i, '');
}
function getUserCookie() {
  return userCookie;
}

/**
 * 从页面里抠出 window.__DATA__ = {...} 的完整 JSON。
 * 不能用贪婪正则——JSON 里本身含 "};" 等字符，必须做括号配对（并跳过字符串内的括号）。
 */
function extractWindowData(html) {
  const marker = html.indexOf('window.__DATA__');
  if (marker < 0) return null;
  let p = html.indexOf('=', marker);
  if (p < 0) return null;
  p += 1;
  while (p < html.length && /\s/.test(html[p])) p++;

  let depth = 0;
  let inStr = false;
  let esc = false;
  for (let i = p; i < html.length; i++) {
    const c = html[i];
    if (inStr) {
      if (esc) esc = false;
      else if (c === '\\') esc = true;
      else if (c === '"') inStr = false;
      continue;
    }
    if (c === '"') {
      inStr = true;
      continue;
    }
    if (c === '{' || c === '[') depth++;
    else if (c === '}' || c === ']') {
      depth--;
      if (depth === 0) {
        try {
          return JSON.parse(html.slice(p, i + 1));
        } catch {
          return null;
        }
      }
    }
  }
  return null;
}

/** 常见 HTML 实体还原（豆瓣老页面标题里会有 &#39; 之类） */
function decodeEntities(s) {
  const named = { amp: '&', lt: '<', gt: '>', quot: '"', apos: "'", nbsp: ' ' };
  return String(s || '')
    .replace(/&#(\d+);/g, (_, d) => String.fromCharCode(Number(d)))
    .replace(/&#x([0-9a-f]+);/gi, (_, h) => String.fromCharCode(parseInt(h, 16)))
    .replace(/&([a-z]+);/gi, (m, n) => (named[n.toLowerCase()] !== undefined ? named[n.toLowerCase()] : m));
}

/** 归一化：小写、全角转半角、去掉标点与多余空白 */
function norm(s) {
  return toFullWidthKana(String(s || ''))
    .toLowerCase()
    .replace(/[\uff01-\uff5e]/g, (ch) => String.fromCharCode(ch.charCodeAt(0) - 0xfee0))
    .replace(/[\u2018\u2019\u201c\u201d]/g, '')
    .replace(/[\(\)\[\]【】<>《》「」『』,，.。!！?？:：;；'"`~@#$%^&*_+=|\\/\-\s]/g, '')
    .trim();
}

/* ------------------------------ 日文处理 ------------------------------ */

// 半角片假名 → 全角（ﾌﾟﾗﾁﾅ → プラチナ）
const HALF_KATA =
  'ｦｧｨｩｪｫｬｭｮｯｰｱｲｳｴｵｶｷｸｹｺｻｼｽｾｿﾀﾁﾂﾃﾄﾅﾆﾇﾈﾉﾊﾋﾌﾍﾎﾏﾐﾑﾒﾓﾔﾕﾖﾗﾘﾙﾚﾛﾜﾝ';
const FULL_KATA =
  'ヲァィゥェォャュョッーアイウエオカキクケコサシスセソタチツテトナニヌネノハヒフヘホマミムメモヤユヨラリルレロワン';

function toFullWidthKana(s) {
  let out = '';
  for (let i = 0; i < s.length; i++) {
    const ch = s[i];
    const idx = HALF_KATA.indexOf(ch);
    if (idx >= 0) {
      // 半角浊点/半浊点跟在后面时，合并成全角浊音
      const next = s[i + 1];
      let base = FULL_KATA[idx];
      if (next === 'ﾞ' || next === 'ﾟ') {
        const daku = {
          カ: 'ガ', キ: 'ギ', ク: 'グ', ケ: 'ゲ', コ: 'ゴ', サ: 'ザ', シ: 'ジ', ス: 'ズ', セ: 'ゼ', ソ: 'ゾ',
          タ: 'ダ', チ: 'ヂ', ツ: 'ヅ', テ: 'デ', ト: 'ド', ハ: 'バ', ヒ: 'ビ', フ: 'ブ', ヘ: 'ベ', ホ: 'ボ',
          ウ: 'ヴ',
        };
        const handaku = { ハ: 'パ', ヒ: 'ピ', フ: 'プ', ヘ: 'ペ', ホ: 'ポ' };
        if (next === 'ﾞ' && daku[base]) base = daku[base];
        else if (next === 'ﾟ' && handaku[base]) base = handaku[base];
        i += 1;
      }
      out += base;
    } else {
      out += ch;
    }
  }
  return out;
}

function kataToHira(s) {
  let out = '';
  for (const ch of s) {
    const code = ch.charCodeAt(0);
    if (code >= 0x30a1 && code <= 0x30f6) out += String.fromCharCode(code - 0x60);
    else out += ch;
  }
  return out;
}

const KANA_MAP = {
  あ: 'a', い: 'i', う: 'u', え: 'e', お: 'o',
  か: 'ka', き: 'ki', く: 'ku', け: 'ke', こ: 'ko',
  さ: 'sa', し: 'shi', す: 'su', せ: 'se', そ: 'so',
  た: 'ta', ち: 'chi', つ: 'tsu', て: 'te', と: 'to',
  な: 'na', に: 'ni', ぬ: 'nu', ね: 'ne', の: 'no',
  は: 'ha', ひ: 'hi', ふ: 'fu', へ: 'he', ほ: 'ho',
  ま: 'ma', み: 'mi', む: 'mu', め: 'me', も: 'mo',
  や: 'ya', ゆ: 'yu', よ: 'yo',
  ら: 'ra', り: 'ri', る: 'ru', れ: 're', ろ: 'ro',
  わ: 'wa', を: 'o', ん: 'n',
  が: 'ga', ぎ: 'gi', ぐ: 'gu', げ: 'ge', ご: 'go',
  ざ: 'za', じ: 'ji', ず: 'zu', ぜ: 'ze', ぞ: 'zo',
  だ: 'da', ぢ: 'ji', づ: 'zu', で: 'de', ど: 'do',
  ば: 'ba', び: 'bi', ぶ: 'bu', べ: 'be', ぼ: 'bo',
  ぱ: 'pa', ぴ: 'pi', ぷ: 'pu', ぺ: 'pe', ぽ: 'po',
  ぁ: 'a', ぃ: 'i', ぅ: 'u', ぇ: 'e', ぉ: 'o',
  ゃ: 'ya', ゅ: 'yu', ょ: 'yo', ゎ: 'wa',
  ゔ: 'vu', ゐ: 'i', ゑ: 'e',
};

const DIGRAPH = {
  きゃ: 'kya', きゅ: 'kyu', きょ: 'kyo',
  しゃ: 'sha', しゅ: 'shu', しょ: 'sho',
  ちゃ: 'cha', ちゅ: 'chu', ちょ: 'cho',
  にゃ: 'nya', にゅ: 'nyu', にょ: 'nyo',
  ひゃ: 'hya', ひゅ: 'hyu', ひょ: 'hyo',
  みゃ: 'mya', みゅ: 'myu', みょ: 'myo',
  りゃ: 'rya', りゅ: 'ryu', りょ: 'ryo',
  ぎゃ: 'gya', ぎゅ: 'gyu', ぎょ: 'gyo',
  じゃ: 'ja', じゅ: 'ju', じょ: 'jo',
  びゃ: 'bya', びゅ: 'byu', びょ: 'byo',
  ぴゃ: 'pya', ぴゅ: 'pyu', ぴょ: 'pyo',
  てぃ: 'ti', でぃ: 'di', ふぁ: 'fa', ふぃ: 'fi', ふぇ: 'fe', ふぉ: 'fo',
  うぇ: 'we', うぃ: 'wi', ゔぁ: 'va', ゔぃ: 'vi', ゔぇ: 've', ゔぉ: 'vo',
};

/** 假名转罗马字（够用就好，不求严谨） */
function kanaToRomaji(input) {
  let s = toFullWidthKana(String(input || ''));
  s = kataToHira(s);

  let out = '';
  let i = 0;
  while (i < s.length) {
    const two = s.slice(i, i + 2);
    if (DIGRAPH[two]) {
      out += DIGRAPH[two];
      i += 2;
      continue;
    }
    const ch = s[i];
    if (ch === 'っ') {
      // 促音：把下一个辅音重复一次
      const next = s[i + 1];
      const romaji = KANA_MAP[next] || DIGRAPH[s.slice(i + 1, i + 3)] || '';
      const m = /^[a-z]+/.exec(romaji);
      if (m) out += romaji[0];
      i += 1;
      continue;
    }
    if (ch === 'ー') {
      // 长音：重复上一个元音
      const m = /([aeiou])(?![aeiou]*$)/.exec(out.slice(-3));
      const last = /([aeiou])$/.exec(out);
      if (last) out += last[1];
      else if (m) out += m[1];
      i += 1;
      continue;
    }
    const r = KANA_MAP[ch];
    if (r) {
      out += r;
    } else if (/[a-zA-Z0-9]/.test(ch) || /[\u4e00-\u9fff]/.test(ch)) {
      out += ch;
    }
    i += 1;
  }
  return out.toLowerCase();
}

/** 是否含日文假名 */
function hasKana(s) {
  return /[\u3040-\u309f\u30a0-\u30ff]/.test(String(s || ''));
}

/** 豆瓣标题常写成「中文 / English」，拆成候选标题列表 */
function titleVariants(title) {
  const out = [];
  for (const part of String(title || '').split(/\s*\/\s*/)) {
    const n = norm(part);
    if (n) out.push(n);
  }
  const whole = norm(title);
  if (whole) out.push(whole);
  return [...new Set(out)];
}

/** 从 abstract 里解析表演者 / 发行日期 / 类型 / 介质 / 流派 */
function parseAbstract(abstract) {
  const parts = String(abstract || '')
    .split(/\s*\/\s*/)
    .map((s) => s.trim())
    .filter(Boolean);
  const out = { artists: [], date: '', kind: '', media: '', genre: '' };
  if (!parts.length) return out;

  out.artists = parts[0]
    .split(/[、／|]+/)
    .map((s) => s.trim())
    .filter(Boolean);

  for (const p of parts.slice(1)) {
    if (/^\d{4}(-\d{1,2}){0,2}$/.test(p)) out.date = p;
    else if (/^(专辑|单曲|EP|合辑|现场|原声|选集|Box Set|Bootleg|音频)$/i.test(p)) out.kind = p;
    else if (/CD|DVD|磁带|黑胶|数字|Vinyl|Blu|SACD|Audio/i.test(p)) out.media = p;
    else if (!out.genre) out.genre = p;
  }
  return out;
}

/** 两段文本的字符二元组相似度，0~1 */
/** 归一化后的候选形态：原文 + 罗马字（有假名时才加） */
function scriptForms(value) {
  const raw = String(value || '');
  const out = [];
  const n = norm(raw);
  if (n) out.push(n);
  if (hasKana(raw)) {
    const r = norm(kanaToRomaji(raw));
    if (r && r !== n) out.push(r);
  }
  return out;
}

function similarity(a, b) {
  const fa = scriptForms(a);
  const fb = scriptForms(b);
  let best = 0;
  for (const x of fa) {
    for (const y of fb) {
      best = Math.max(best, compareNorm(x, y));
    }
  }
  return best;
}

function compareNorm(A, B) {
  if (!A || !B) return 0;
  if (A === B) return 1;
  if (A.includes(B) || B.includes(A)) {
    const shorter = Math.min(A.length, B.length);
    const longer = Math.max(A.length, B.length);
    return 0.72 + 0.28 * (shorter / longer);
  }
  const bigrams = (s) => {
    const m = new Map();
    for (let i = 0; i < s.length - 1; i++) {
      const g = s.slice(i, i + 2);
      m.set(g, (m.get(g) || 0) + 1);
    }
    return m;
  };
  const ma = bigrams(A);
  const mb = bigrams(B);
  let overlap = 0;
  for (const [g, n] of ma) overlap += Math.min(n, mb.get(g) || 0);
  const total = Math.max(A.length - 1, 1) + Math.max(B.length - 1, 1);
  return Math.min(1, (2 * overlap) / total);
}

/**
 * 清掉播放器塞在标题后面的形态后缀。
 * Apple Music 会把 EP 报成「I've IVE - EP」，豆瓣搜索会把这个后缀当检索词，
 * 结果真正的专辑根本不在返回列表里 —— 这是「明明有却找不到」最常见的原因。
 */
const TITLE_SUFFIX_DASH = /\s*[-–—]\s*(ep|single|album|lp|maxi single|deluxe( edition)?|special edition|expanded edition|remastered( \d{4})?|bonus track version|explicit)\s*$/i;
const TITLE_SUFFIX_PAREN = /\s*[\(\[（【]\s*(ep|single|deluxe( edition)?|special edition|expanded edition|remastered[^\)\]）】]*|explicit)\s*[\)\]）】]\s*$/i;

const TITLE_SUFFIX_JA = /\s*[\(\[（【]\s*(通常盤|初回限定盤+|完全生産限定盤|生産限定盤|限定盤|通常版|初回版|デラックス(エディション)?|リマスター[^\)\]）】]*|紙ジャケ[^\)\]）】]*)\s*[\)\]）】]\s*$/;

function tidyTitle(value) {
  const original = String(value || '').trim();
  let t = original;
  for (let i = 0; i < 3; i++) {
    const before = t;
    t = t.replace(TITLE_SUFFIX_DASH, '').replace(TITLE_SUFFIX_PAREN, '').replace(TITLE_SUFFIX_JA, '').trim();
    if (t === before) break;
  }
  return t || original;
}

/** 分词时忽略的虚词，避免 "the"、"and" 这类词造成误判 */
const ARTIST_STOPWORDS = new Set(['the', 'a', 'an', 'and', 'of', 'feat', 'ft', 'with', 'vs', 'jr', 'sr']);

/** 艺人名拆词：支持「周杰伦 Jay Chou」「A & B」「A feat. B」 */
function artistTokens(name) {
  return String(name || '')
    .split(/[\s,、&;／/|]+|(?:\s+(?:feat|ft|with|vs)\.?\s+)/i)
    .map(norm)
    .filter((t) => t.length >= 2 && t.length <= 24);
}

/**
 * 艺人相似度 0~1。
 * 整串相等最高；一方包含另一方次之；最后才看拆词后的局部命中。
 */
function artistSimilarity(want, cand) {
  const fa = scriptForms(want);
  const fb = scriptForms(cand);
  if (!fa.length || !fb.length) return 0;
  let best = 0;
  for (const x of fa) for (const y of fb) best = Math.max(best, artistSimNorm(x, y));
  return best;
}

function artistSimNorm(A, B) {
  if (!A || !B) return 0;
  if (A === B) return 1;

  let best = 0;

  // 一方包含另一方：允许「周杰伦」对上「周杰伦 Jay Chou」
  if (B.includes(A) || A.includes(B)) {
    const r = Math.min(A.length, B.length) / Math.max(A.length, B.length);
    best = 0.8 + 0.2 * r; // 0.8 ~ 1.0
  }

  // 分词命中：只在不靠虚词时才算数，避免 "The Beatles" 命中 "The Kinks"
  const aw = artistTokens(A).filter((t) => !ARTIST_STOPWORDS.has(t));
  const bw = artistTokens(B).filter((t) => !ARTIST_STOPWORDS.has(t));
  for (const x of aw) {
    for (const y of bw) {
      if (x === y) {
        const cjk = /[\u4e00-\u9fa5]/.test(x);
        best = Math.max(best, cjk ? 0.92 : 0.86);
        continue;
      }
      const shorter = Math.min(x.length, y.length);
      if (shorter >= 3 && (y.includes(x) || x.includes(y))) {
        best = Math.max(best, 0.7 + 0.15 * (shorter / Math.max(x.length, y.length)));
      }
    }
  }
  return best;
}

/**
 * 给一条搜索结果打分，判断它是不是「我正在听的那张专辑」。
 *
 * 排序原则：**艺人优先，歌名/专辑名次之**。
 *  - 有艺人且候选也能解析出艺人：艺人占 62 分，标题占 30 分
 *  - 候选没写艺人（豆瓣个别条目缺失）：权重全给标题，避免误判成「不相关」
 *  - 类型与热度只做微调，不能盖过主信号
 *
 * want: { album, artist, track }
 */
function scoreCandidate(item, want) {
  const variants = titleVariants(item.title);

  // 主搜索源有 abstract；备用源没有，只能用它已经解析好的字段
  const abs = item.abstract
    ? parseAbstract(item.abstract)
    : {
        artists: item.artists || [],
        date: item.date || '',
        kind: item.kind || '',
        media: item.media || '',
        genre: item.genre || '',
      };

  let titleScore = 0;
  const targets = [tidyTitle(want.album), tidyTitle(want.track)].filter(Boolean);
  for (const t of targets) {
    for (const v of variants) {
      titleScore = Math.max(titleScore, similarity(t, v));
    }
  }

  const candArtists = (abs.artists && abs.artists.length ? abs.artists : item.artists) || [];
  let artistScore = 0;
  if (want.artist && candArtists.length) {
    for (const a of candArtists) {
      artistScore = Math.max(artistScore, artistSimilarity(want.artist, a));
    }
  }

  const hasArtistInfo = !!want.artist && candArtists.length > 0;
  const wArtist = hasArtistInfo ? 62 : 0;
  const wTitle = 92 - wArtist;

  let kindBonus = 0;
  if (/^专辑$/.test(abs.kind)) kindBonus = 8;
  else if (/^EP$/i.test(abs.kind)) kindBonus = 2;
  else if (/^单曲$/.test(abs.kind)) kindBonus = -4;
  else if (/^现场|演唱会|Live/i.test(item.title + abs.kind)) kindBonus = -8;

  const votes = Number(item.rating?.count) || 0;

  // 同名加成：标题完全一致比「长得像」更可信
  const exactBonus = titleScore >= 0.995 ? 6 : 0;

  // 评分人数：在艺人/标题都对得上的前提下，人多的版本才是主版本
  const voteScore = Math.min(1, Math.log10(votes + 1) / 5.5) * 12;

  const total = artistScore * wArtist + titleScore * wTitle + exactBonus + voteScore + kindBonus;

  return {
    total,
    titleScore,
    artistScore,
    hasArtistInfo,
    votes,
    kind: abs.kind,
    artists: candArtists,
  };
}

async function searchSSR(query) {
  const url = `${BASE}/subject_search?search_text=${encodeURIComponent(query)}`;
  const { status, text } = await httpGet(url, { referer: `${BASE}/` });
  if (status !== 200) return null;
  const data = extractWindowData(text);
  if (!data) return null; // 空壳页：被降级/限流
  const items = (data.items || []).filter((x) => x.tpl_name === 'search_subject');
  return items.map((x) => {
    const abs = parseAbstract(x.abstract);
    return {
      id: x.id,
      title: x.title,
      url: x.url,
      cover: x.cover_url || '',
      raw: x.abstract || '',
      artists: abs.artists,
      date: abs.date,
      kind: abs.kind,
      media: abs.media,
      genre: abs.genre,
      score: Number(x.rating?.value) || 0,
      votes: Number(x.rating?.count) || 0,
      ratingInfo: x.rating?.rating_info || '',
      source: 'ssr',
    };
  });
}

/**
 * 备用数据源：老版全站搜索（服务端渲染 HTML）。
 * 主搜索被限流返回空壳时使用。
 */
async function searchLegacy(query) {
  const url = `https://www.douban.com/search?cat=1003&q=${encodeURIComponent(query)}`;
  const { status, text } = await httpGet(url, { referer: 'https://www.douban.com/' });
  if (status !== 200) return [];
  const out = [];
  for (const block of text.split('<div class="result">').slice(1)) {
    const sidM =
      block.match(/sid:\s*(\d+)/) ||
      block.match(/subject%2F(\d+)%2F/) ||
      block.match(/music\.douban\.com\/subject\/(\d+)/);
    if (!sidM) continue;

    const titleM = block.match(/<div class="title">[\s\S]*?<a[^>]*>([\s\S]*?)<\/a>/);
    const scoreM = block.match(/<span class="rating_nums">([\d.]+)<\/span>/);
    const votesM = block.match(/\((\d+)\s*人评价\)/);
    const castM = block.match(/<span class="subject-cast">([\s\S]*?)<\/span>/);
    const coverM = block.match(/<img src="([^"]+)"/);

    const cast = castM ? decodeEntities(castM[1].replace(/<[^>]+>/g, '')).trim() : '';
    const castParts = cast.split(/\s*\/\s*/);
    const decade = castParts[castParts.length - 1] || '';

    out.push({
      id: Number(sidM[1]),
      title: titleM ? decodeEntities(titleM[1].replace(/<[^>]+>/g, '')).trim() : '',
      url: `${BASE}/subject/${sidM[1]}/`,
      cover: coverM ? coverM[1] : '',
      raw: cast,
      artists: castParts[0] ? castParts[0].split(/[、／|]+/).filter(Boolean) : [],
      date: /^\d{4}$/.test(decade) ? decade : '',
      kind: '',
      media: '',
      genre: castParts[1] || '',
      score: scoreM ? Number(scoreM[1]) : 0,
      votes: votesM ? Number(votesM[1]) : 0,
      ratingInfo: '',
      source: 'legacy',
    });
  }
  return out;
}

async function search(query) {
  let results = null;
  for (let attempt = 0; attempt < 2 && !results; attempt++) {
    if (attempt > 0) await sleep(4000 * attempt);
    try {
      results = await searchSSR(query);
    } catch {
      results = null;
    }
  }
  if (results && results.length) return results;

  // 主源不可用（限流/改版）→ 退回老版搜索
  const legacy = await searchLegacy(query);
  if (legacy.length) return legacy;
  return results || [];
}

/**
 * 取以某个标记开头的 <div> 的完整内容（按标签深度配对，避免被嵌套 div 截断）。
 */
function sliceDivByMarker(html, marker) {
  const mi = html.indexOf(marker);
  if (mi < 0) return '';
  const start = html.lastIndexOf('<div', mi);
  if (start < 0) return '';
  let depth = 0;
  const re = /<\/?div\b/gi;
  re.lastIndex = start;
  let m;
  while ((m = re.exec(html))) {
    if (m[0][1] === '/') {
      depth--;
      if (depth === 0) return html.slice(start, m.index);
    } else {
      depth++;
    }
  }
  return '';
}

/** HTML 片段 → 按行拆分的纯文本（只按 <br> 换行，源码里的缩进换行会被压平） */
function htmlToLines(fragment) {
  return String(fragment || '')
    .replace(/<br\s*\/?>/gi, '\u0000')
    .replace(/<\/(?:p|div|dd)>/gi, '\u0000')
    .replace(/<[^>]+>/g, '')
    .replace(/&nbsp;/g, ' ')
    .replace(/\s+/g, ' ')
    .split('\u0000')
    .map((s) => decodeEntities(s).replace(/\uFFFD/g, '').replace(/\s+/g, ' ').trim())
    .filter(Boolean);
}

/**
 * 详情页解析。
 * 豆瓣的 #info 区块结构不统一（有的字段是 <span class="pl">标签:</span>值，
 * 有的是 <span><span class="pl">标签: <a>值</a></span></span>），
 * 所以先转成纯文本按行解析，比逐字段正则稳得多。
 */
function parseSubjectPage(html) {
  const out = {};

  const grab = (re) => {
    const m = html.match(re);
    return m ? m[1].replace(/<[^>]+>/g, ' ').replace(/&nbsp;/g, ' ').replace(/\s+/g, ' ').trim() : '';
  };

  out.title = decodeEntities(grab(/<title>\s*([\s\S]*?)\s*<\/title>/)).replace(/\s*\(豆瓣\)\s*$/, '');
  out.score = Number(grab(/property="v:average">([\d.]+)</)) || 0;
  out.votes = Number(grab(/property="v:votes">(\d+)</)) || 0;

  // 评分分布：5 星 → 1 星
  out.distribution = [...html.matchAll(/<span class="rating_per">([\d.]+)%<\/span>/g)].map((m) =>
    Number(m[1])
  );
  if (out.distribution.length !== 5) out.distribution = [];

  // 基本信息
  const infoHtml = sliceDivByMarker(html, 'id="info"');
  const info = {};
  for (const line of htmlToLines(infoHtml)) {
    const m = line.match(/^([^:：]{1,6})[:：]\s*(.+)$/);
    if (m) info[m[1].trim()] = m[2].trim();
  }

  out.artist = info['表演者'] || '';
  out.genre = info['流派'] || '';
  out.publisher = info['出版者'] || '';
  out.media = info['介质'] || '';
  out.date = info['发行时间'] || '';
  out.barcode = info['条形码'] || '';
  out.albumType = info['专辑类型'] || '';
  out.aka = info['又名'] || '';

  // 封面
  const coverM = html.match(/id="mainpic"[\s\S]{0,600}?<img[^>]+src="([^"]+)"/);
  out.cover = coverM ? coverM[1].replace(/^http:/, 'https:') : '';

  const introM =
    html.match(/<div class="intro">([\s\S]*?)<a class="a_show_full"/) ||
    html.match(/<span property="v:summary"[^>]*>([\s\S]*?)<\/span>/);
  out.intro = introM
    ? introM[1]
        .replace(/<br\s*\/?>/gi, '\n')
        .replace(/<[^>]+>/g, '')
        .replace(/&nbsp;/g, ' ')
        .replace(/\uFFFD/g, '')
        .replace(/\n{3,}/g, '\n\n')
        .trim()
        .slice(0, 1200)
    : '';

  // 标签（只取「豆瓣成员常用标签」区块，避开导航里的「分类浏览」等链接）
  const tagBlock = sliceDivByMarker(html, 'class="tags-body"');
  out.tags = [...tagBlock.matchAll(/<a[^>]*>([^<]+)<\/a>/g)]
    .map((m) => decodeEntities(m[1]).trim())
    .filter((t) => t && t.length <= 16)
    .slice(0, 12);

  // 短评（页面上最热的几条）
  out.hotComments = [...html.matchAll(
    /<div class="comment-item"[^>]*>[\s\S]*?<span class="comment-info">[\s\S]*?<a[^>]*>([^<]{1,40})<\/a>[\s\S]*?<span class="allstar(\d+) rating"[\s\S]*?<span class="short">([\s\S]*?)<\/span>/g
  )]
    .map((m) => ({
      user: decodeEntities(m[1]).trim(),
      stars: Number(m[2]) / 10,
      text: decodeEntities(m[3].replace(/<[^>]+>/g, '')).replace(/\s+/g, ' ').trim(),
    }))
    .slice(0, 5);

  // 回写评分需要的 ck 令牌。登录后页面上形如 ?interest=do&ck=_xDd，
  // 注意 ck 里可能有下划线/短横线，且只有 3~5 个字符。
  const ckM =
    html.match(/[?&](?:amp;)?ck=([A-Za-z0-9_\-]{2,})/) ||
    html.match(/name="ck"\s+value="([^"]+)"/) ||
    html.match(/["']ck["']\s*[:=]\s*["']([A-Za-z0-9_\-]{2,})["']/);
  out.ck = ckM ? ckM[1] : '';

  return out;
}

async function getSubject(id, { cookie = '' } = {}) {
  const url = `${BASE}/subject/${id}/`;
  const { status, text } = await httpGet(url, { cookie, referer: `${BASE}/subject/${id}/` });
  if (status !== 200) throw new Error(`豆瓣详情 HTTP ${status}`);
  const parsed = parseSubjectPage(text);
  parsed.id = Number(id);
  parsed.url = url;
  return parsed;
}

/**
 * 把「正在播放」解析成豆瓣条目。
 * info: { album, artist, track }
 */
async function resolveAlbum(info) {
  let album = tidyTitle(info.album);
  const track = tidyTitle(info.track);

  // 只有歌名、没有专辑名（网易云 / QQ 音乐走窗口标题兜底时就是这样）：
  // 先用网易云接口把「歌名」补全成「所属专辑」，再走正常的专辑匹配。
  let albumFrom = '';
  if (!album && track) {
    try {
      const hit = await require('./netease').lookupAlbum(info.artist, track);
      if (hit && hit.album) {
        album = tidyTitle(hit.album);
        albumFrom = 'music.163';
      }
    } catch {
      /* 补全失败就按原来的歌名匹配 */
    }
  }

  const name = album || track || '';
  if (!name && !info.artist) return { subject: null, reason: 'no_input' };

  // 检索顺序：艺人 + 专辑名 → 艺人 + 歌名 → 专辑名 → 歌名
  const queries = [];
  if (info.artist && album) queries.push(`${info.artist} ${album}`);
  if (info.artist && track) queries.push(`${info.artist} ${track}`);
  if (album) queries.push(album);
  if (track) queries.push(track);

  const want = { album, track, artist: info.artist || '' };
  const attempts = [];
  const tried = new Set();
  let best = null;
  let bestMeta = null;
  let bestScore = 0;
  let bestQuery = '';

  for (const q of queries.slice(0, 3)) {
    const key = norm(q);
    if (!key || tried.has(key)) continue;
    tried.add(key);

    let results = [];
    try {
      results = await search(q);
    } catch (e) {
      attempts.push({ query: q, count: 0, error: e.message });
      continue;
    }
    attempts.push({ query: q, count: results.length, source: results[0]?.source || '' });

    for (const r of results) {
      const sc = scoreCandidate(r, want);
      if (sc.total > bestScore) {
        bestScore = sc.total;
        best = r;
        bestMeta = sc;
        bestQuery = q;
      }
    }
    // 艺人已经牢牢对上、标题也不差，就不再多查，省请求
    // 只有「艺人命中 + 标题完全一致 + 分数够高」才提前收手，
    // 否则继续查下一轮，避免被一个同名的冷门版本占位
    if (bestMeta && bestMeta.artistScore >= 0.9 && bestMeta.titleScore >= 0.995 && bestMeta.total >= 90) {
      break;
    }
  }

  // —— 语言不一致兜底（外区 Apple Music / Spotify 显示英文名）——
  // 用 MusicBrainz 的别名和条码再搜一轮：英文艺人 → 中文/日文/韩文原名，或条码直查
  let barcodeHit = false;
  const confidentNow =
    !!bestMeta && bestMeta.total >= 78 && (bestMeta.titleScore >= 0.5 || bestMeta.artistScore >= 0.95);
  // 英文查询经常命中「某张再版/黑胶版」（票数很少），这时也再查一轮别名。
  // 只对拉丁字母的艺人生效：中文中文之间不需要翻译，白花时间。
  const latinArtist = /[A-Za-z]/.test(info.artist || '') && !/[\u4e00-\u9fff]/.test(info.artist || '');
  const lowVotes = !!bestMeta && (bestMeta.votes || 0) < 300 && bestMeta.artistScore >= 0.6;
  const needFallback = !bestMeta || !confidentNow || (lowVotes && latinArtist);

  if (needFallback && info.artist && require('./store').getSettings().mbBridge !== false) {
    let hints = [];
    try {
      hints = await require('./mb').suggestQueries({ artist: info.artist, album, limit: 3 });
    } catch (e) {
      attempts.push({ via: 'musicbrainz', error: e.message });
    }

    for (const hint of hints) {
      const key = norm(hint.query);
      if (!key || tried.has(key)) continue;
      tried.add(key);

      let results = [];
      try {
        results = await search(hint.query);
      } catch (e) {
        attempts.push({ query: hint.query, via: hint.via, error: e.message });
        continue;
      }
      attempts.push({ query: hint.query, count: results.length, via: hint.via });
      if (!results.length) continue;

      // 同一个条目的艺人/标题可能正好另一种语言，两种写法都打一次分，取高的
      const variants = [
        { album, track, artist: info.artist || '' },
        { album: hint.album || album, track, artist: hint.artist || info.artist || '' },
      ];
      for (const r of results) {
        let sc = null;
        for (const v of variants) {
          const cand = scoreCandidate(r, v);
          if (!sc || cand.total > sc.total) sc = cand;
        }
        if (sc && sc.total > bestScore) {
          bestScore = sc.total;
          best = r;
          bestMeta = sc;
          bestQuery = hint.query;
          barcodeHit = false;
        }
      }

      // 条码命中：标题通常对不上，但必须「唯一命中 + 艺人能对上」才认，
      // 否则宁可判为没找到，也不要绑错专辑。
      if (/^\d{8,14}$/.test(hint.query)) {
        const r0 = results[0];
        const sc = scoreCandidate(r0, { album, track, artist: info.artist });
        const safe = (results.length === 1 && sc.artistScore >= 0.3) || sc.artistScore >= 0.6;
        if (safe && (!best || bestScore < 80)) {
          best = r0;
          bestMeta = sc;
          bestScore = 80;
          bestQuery = hint.query;
          barcodeHit = true;
        }
      }
    }
  }

  const summary = best
    ? { id: best.id, title: best.title, artists: bestMeta.artists, score: bestMeta.total }
    : null;

  if (!best) return { subject: null, reason: 'no_results', attempts };

  // 艺人优先：两边都有艺人信息、却完全对不上，就不认这条结果。
  // 宁可让用户手动绑定一次，也不要悄悄指向另一个歌手的专辑。
  if (!barcodeHit && info.artist && bestMeta.hasArtistInfo && bestMeta.artistScore < 0.35) {
    return { subject: null, reason: 'artist_mismatch', attempts, best: summary };
  }

  // 标题是「哪一张」的唯一证据。标题完全不像时，光靠艺人会随手挑一张
  // 这位歌手的热门专辑，那比没有匹配更糟。
  if (!barcodeHit && bestMeta.titleScore < 0.45) {
    return { subject: null, reason: 'title_mismatch', attempts, best: summary };
  }

  // 值不值得长期缓存：标题够像，或者艺人是精确命中（允许标题略有出入）
  const confident = barcodeHit
    ? true
    : bestMeta.total >= 78 && (bestMeta.titleScore >= 0.5 || bestMeta.artistScore >= 0.95);

  return {
    subject: best,
    matchScore: Math.round(bestScore),
    confident,
    scores: {
      artist: Number(bestMeta.artistScore.toFixed(2)),
      title: Number(bestMeta.titleScore.toFixed(2)),
    },
    query: bestQuery,
    via: barcodeHit ? 'barcode' : undefined,
    albumFrom: albumFrom || undefined,
    attempts,
  };
}

/**
 * 解析收藏条目的 intro 字段：
 *   "艺人A,艺人B / 2025-12-22 / 专辑 / 数字(Digital) / 放克/灵歌/R&B"
 * 按「特征」归类而不是按固定位置，缺字段时也不会错位。
 */
function parseIntro(intro) {
  const out = { artists: [], date: '', kind: '', media: '', genre: '' };
  const parts = String(intro || '')
    .split(/\s+\/\s+|\s*／\s*/)
    .map((x) => x.trim())
    .filter(Boolean);

  for (const t of parts) {
    if (!out.date && /^\d{4}(-\d{1,2}(-\d{1,2})?)?$/.test(t)) {
      out.date = t;
      continue;
    }
    if (!out.kind && /^(专辑|EP|單曲|单曲|选集|現場|现场|原声带|合辑|迷你专辑|录音室专辑|专辑)$/i.test(t)) {
      out.kind = t;
      continue;
    }
    if (!out.media && /^(CD|SACD|DVD|BD|黑胶|黑膠|磁带|磁帶|数字|數字|数字\(Digital\)|数字\(Digital\))/i.test(t)) {
      out.media = t;
      continue;
    }
    if (!out.artists.length) {
      // 多位艺人用逗号/顿号分隔，不能整体塞进一个字段
      out.artists = t.split(/[,，、]+/).map((x) => x.trim()).filter(Boolean);
      continue;
    }
    out.genre = out.genre ? out.genre + ' / ' + t : t;
  }

  return out;
}

/**
 * 读取「我听过的音乐」收藏页（15 条/页）。
 * 返回 { items, next }，next 为下一页的 start 偏移，没有下一页则为 null。
 */
async function getCollectionPage(cookie, uid, start = 0) {
  const url = `https://music.douban.com/people/${uid}/collect?start=${start}&type=music`;
  const { status, text } = await httpGet(url, {
    cookie,
    referer: `https://music.douban.com/people/${uid}/`,
  });
  if (status !== 200) throw new Error('豆瓣收藏页 HTTP ' + status);
  if (text.indexOf('item comment-item') < 0 && text.indexOf('item') < 0) {
    throw new Error('收藏页结构异常或未登录');
  }

  const items = [];
  for (const block of text.split('class="item comment-item"').slice(1)) {
    const idm = block.match(/music\.douban\.com\/subject\/(\d+)\//);
    if (!idm) continue;

    const titleM = block.match(/<li class="title">[\s\S]*?<em>([\s\S]*?)<\/em>([\s\S]*?)<\/a>/);
    const introM = block.match(/<li class="intro">([\s\S]*?)<\/li>/);
    const ratingM = block.match(/rating(\d)-t/) || block.match(/allstar(\d)0/);
    const dateM = block.match(/<span class="date">([\s\S]*?)<\/span>/);
    const commentM = block.match(/<span class="date">[\s\S]*?<\/li>\s*<li>\s*([\s\S]*?)\s*<\/li>\s*<li class="clearfix/);
    const imgM = block.match(/<img[^>]+src="([^"]+)"/);

    const title = titleM ? decodeEntities(titleM[1].replace(/<[^>]+>/g, '')).trim() : '';
    const alt = titleM ? decodeEntities(titleM[2].replace(/<[^>]+>/g, '')).replace(/^\s*\/\s*/, '').trim() : '';
    const intro = introM ? decodeEntities(introM[1].replace(/<[^>]+>/g, '')).replace(/\s+/g, ' ').trim() : '';
    const info = parseIntro(intro);

    items.push({
      id: Number(idm[1]),
      title: (() => {
        const t = (title || '').trim();
        const a = (alt || '').trim();
        if (!a) return t;
        if (!t) return a;
        return a.toLowerCase() === t.toLowerCase() ? t : `${t} / ${a}`;
      })(),
      artists: info.artists,
      date: info.date,
      kind: info.kind,
      media: info.media,
      genre: info.genre,
      rating: ratingM ? Number(ratingM[1]) : 0,
      ratedAt: dateM ? decodeEntities(dateM[1]).trim() : '',
      comment: (() => {
        if (!commentM) return '';
        let text = decodeEntities(commentM[1].replace(/<[^>]+>/g, ''))
          .replace(/\s+/g, ' ')
          .replace(/^\d{4}-\d{2}-\d{2}\s*/, '')
          .trim();
        // 豆瓣把「N 有用 / N 没用 / 回应」也放在同一个 li 里，别带进评语
        text = text
          .replace(/\s*\d+\s*(有用|没用)\s*$/, '')
          .replace(/\s*回应\s*$/, '')
          .trim();
        // 纯日期说明这条其实没写短评
        return /^\d{4}-\d{2}-\d{2}$/.test(text) ? '' : text;
      })(),
      cover: imgM ? imgM[1].replace(/^http:/, 'https:') : '',
    });
  }

  const nextM = text.match(/<link rel="next" href="[^"]*start=(\d+)/);
  return { items, next: nextM ? Number(nextM[1]) : null };
}

/**
 * 用登录 Cookie 验证身份：从 dbcl2 里取 UID，再访问个人主页拿昵称。
 * Cookie 失效时豆瓣会跳登录页，据此判定。
 */
async function getSelfProfile(cookie) {
  const m = /dbcl2="?(\d+):/.exec(String(cookie || ''));
  if (!m) return { ok: false, error: 'no_dbcl2' };
  const uid = m[1];

  let res;
  try {
    res = await httpGet(`https://www.douban.com/people/${uid}/`, {
      cookie,
      referer: 'https://www.douban.com/',
    });
  } catch (e) {
    return { ok: false, error: e.message };
  }

  if (res.status !== 200) return { ok: false, error: 'http_' + res.status };
  if (res.url.indexOf('accounts.douban.com') >= 0 || /<title>\s*登录/.test(res.text)) {
    return { ok: false, error: 'expired' };
  }

  const titleM = res.text.match(/<title>\s*([\s\S]*?)<\/title>/);
  let name = titleM ? decodeEntities(titleM[1]).trim() : '';
  name = name
    .replace(/\s*的(广播|日记|相册|小组|电影|音乐|读书|书影音|个人主页|收藏|豆列).*$/, '')
    .replace(/\s*\(豆瓣\)\s*$/, '')
    .trim();

  return { ok: true, uid, name: name || 'UID ' + uid };
}

/**
 * 回写个人评分 / 短评到豆瓣。
 * 需要登录 Cookie（bid + dbcl2 至少）以及详情页里的 ck。
 */
async function postInterest({ subjectId, rating, comment = '', status = 'done', cookie }) {
  if (!cookie) throw new Error('未配置豆瓣登录 Cookie');

  const detail = await getSubject(subjectId, { cookie });
  const ck = detail.ck;
  if (!ck) throw new Error('没拿到豆瓣的 ck 令牌（页面结构可能变了），这次没写入');

  await throttle();
  const body = new URLSearchParams();
  body.set('ck', ck);
  // do = 在听，collect = 听过，wish = 想听。这里必须用 collect
  body.set('interest', 'collect');
  body.set('rating', String(rating));
  body.set('foldcollect', 'F');
  body.set('tags', '');
  if (comment) body.set('comment', comment);

  const res = await fetch(`${BASE}/j/subject/${subjectId}/interest`, {
    method: 'POST',
    headers: {
      'User-Agent': UA,
      'Content-Type': 'application/x-www-form-urlencoded',
      'Referer': `${BASE}/subject/${subjectId}/`,
      'X-Requested-With': 'XMLHttpRequest',
      'Cookie': cookie,
    },
    body,
    signal: AbortSignal.timeout(20000),
  });
  const text = await res.text();
  let json = null;
  try {
    json = JSON.parse(text);
  } catch {
    /* 豆瓣有时返回空串表示成功 */
  }
  return { status: res.status, json, raw: text.slice(0, 400) };
}

module.exports = {
  search,
  searchSSR,
  searchLegacy,
  getSubject,
  resolveAlbum,
  postInterest,
  getSelfProfile,
  getCollectionPage,
  parseIntro,
  parseSubjectPage,
  parseAbstract,
  scoreCandidate,
  extractWindowData,
  similarity,
  norm,
  tidyTitle,
  setUserCookie,
  getUserCookie,
};
