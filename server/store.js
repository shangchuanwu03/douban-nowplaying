'use strict';

/**
 * 本地持久化：评分、收听历史、设置、解析缓存。
 * 用单个 JSON 文件，写入走「临时文件 + rename」保证不会写坏。
 */

const fs = require('fs');
const path = require('path');

const DATA_DIR = path.join(__dirname, '..', 'data');
const DB_FILE = path.join(DATA_DIR, 'db.json');
const CACHE_FILE = path.join(DATA_DIR, 'cache.json');

const DEFAULT_DB = {
  version: 1,
  ratings: {}, // subjectId -> { rating, comment, updatedAt, synced, syncedAt }
  history: [], // 最近收听（新的在前）
  settings: {
    doubanCookie: '',
    historyLimit: 300,
    autoSync: false,
    mbBridge: true,
    playerMode: 'auto',   // auto | applemusic | spotify | netease | qqmusic
    skin: 'neumorphic', // 'neumorphic' | 'glass'
  },
};

let db = null;
let saveTimer = null;

function ensureDir() {
  try {
    fs.mkdirSync(DATA_DIR, { recursive: true });
  } catch {
    /* ignore */
  }
}

function readJson(file, fallback) {
  try {
    return JSON.parse(fs.readFileSync(file, 'utf8'));
  } catch {
    return fallback;
  }
}

function writeJsonAtomic(file, obj) {
  ensureDir();
  const tmp = `${file}.${process.pid}.tmp`;
  fs.writeFileSync(tmp, JSON.stringify(obj, null, 2), 'utf8');
  fs.renameSync(tmp, file);
}

function load() {
  if (db) return db;
  ensureDir();
  const raw = readJson(DB_FILE, null);
  db = raw && typeof raw === 'object' ? { ...DEFAULT_DB, ...raw } : structuredClone(DEFAULT_DB);
  db.ratings ||= {};
  db.history ||= [];
  db.todo ||= [];
  db.settings = { ...DEFAULT_DB.settings, ...(db.settings || {}) };
  return db;
}

function saveNow() {
  if (saveTimer) {
    clearTimeout(saveTimer);
    saveTimer = null;
  }
  writeJsonAtomic(DB_FILE, db);
}

/** 合并短时间内的多次写入，避免频繁落盘 */
function save() {
  if (saveTimer) return;
  saveTimer = setTimeout(() => {
    saveTimer = null;
    try {
      writeJsonAtomic(DB_FILE, db);
    } catch (e) {
      console.error('[store] 保存失败', e.message);
    }
  }, 400);
}

/* ------------------------------- 评分 ------------------------------- */

function getRating(subjectId) {
  return load().ratings[String(subjectId)] || null;
}

function setRating(subjectId, patch) {
  load();
  const id = String(subjectId);
  const prev = db.ratings[id] || {};
  db.ratings[id] = {
    ...prev,
    ...patch,
    subjectId: Number(subjectId),
    updatedAt: Date.now(),
  };
  save();
  return db.ratings[id];
}

function listRatings() {
  load();
  return Object.values(db.ratings).sort((a, b) => (b.updatedAt || 0) - (a.updatedAt || 0));
}

/* ------------------------------ 待评分 ------------------------------ */

function listTodo() {
  load();
  return db.todo.slice().sort((a, b) => (b.createdAt || 0) - (a.createdAt || 0));
}

/** 加入待评分；同一张（subjectId 相同，或标题+艺人相同）不重复添加 */
function addTodo(entry) {
  load();
  const subjectId = Number(entry.subjectId) || 0;
  const title = String(entry.title || '').trim();
  const artist = String(entry.artist || '').trim();

  const same = db.todo.find((t) => {
    if (subjectId && Number(t.subjectId) === subjectId) return true;
    if (!subjectId && !Number(t.subjectId) &&
        String(t.title || '').trim() === title && String(t.artist || '').trim() === artist) return true;
    return false;
  });
  if (same) return same;

  const item = {
    id: 'td_' + Date.now().toString(36) + Math.random().toString(36).slice(2, 6),
    subjectId,
    title,
    artist,
    cover: entry.cover || '',
    album: entry.album || title,
    kind: entry.kind || '',
    app: entry.app || '',
    createdAt: Date.now(),
  };
  db.todo.unshift(item);
  if (db.todo.length > 200) db.todo.length = 200;
  save();
  return item;
}

function removeTodo(idOrSubject) {
  load();
  const key = String(idOrSubject || '');
  const before = db.todo.length;
  db.todo = db.todo.filter((t) => String(t.id) !== key && String(t.subjectId) !== key);
  const changed = db.todo.length !== before;
  if (changed) save();
  return changed;
}

function removeTodoBySubject(subjectId) {
  load();
  const id = String(Number(subjectId) || 0);
  const before = db.todo.length;
  db.todo = db.todo.filter((t) => String(t.subjectId) !== id);
  if (db.todo.length !== before) save();
}

/* ------------------------------ 收听历史 ----------------------------- */

function pushHistory(entry) {
  load();
  const h = db.history;
  // 同一张专辑连续播放：合并为一条，只更新时长与次数
  const last = h[0];
  if (last && last.key === entry.key) {
    last.endedAt = Date.now();
    last.durationMs = (last.endedAt || 0) - (last.startedAt || 0);
    last.plays = (last.plays || 1) + 1;
    save();
    return last;
  }
  const rec = {
    key: entry.key,
    subjectId: entry.subjectId || null,
    title: entry.title || '',
    artist: entry.artist || '',
    album: entry.album || '',
    cover: entry.cover || '',
    kind: entry.kind || '',
    date: entry.date || '',
    app: entry.app || '',
    startedAt: Date.now(),
    endedAt: null,
    durationMs: 0,
    plays: 1,
  };
  h.unshift(rec);
  const limit = Number(db.settings.historyLimit) || 300;
  if (h.length > limit) h.length = limit;
  save();
  return rec;
}

function listHistory(limit = 50) {
  load();
  // id 与 key 同值：界面按 id 删除，历史记录内部用 key
  return db.history.slice(0, limit).map((h) => ({ ...h, id: h.key }));
}

/** 按 key（或 id）删除一条收听记录 */
function removeHistory(idOrKey) {
  load();
  const key = String(idOrKey || '');
  if (!key) return false;
  const before = db.history.length;
  db.history = db.history.filter((h) => String(h.key) !== key && String(h.id || '') !== key);
  if (db.history.length === before) return false;
  save();
  return true;
}

/* ------------------------------- 设置 ------------------------------- */

function getSettings() {
  return load().settings;
}

function setSettings(patch) {
  load();
  db.settings = { ...db.settings, ...patch };
  save();
  return db.settings;
}

/* ----------------------------- 解析缓存 ----------------------------- */

let cache = null;

function loadCache() {
  if (cache) return cache;
  cache = readJson(CACHE_FILE, {}) || {};
  cache.resolve ||= {};
  cache.detail ||= {};
  return cache;
}

function saveCache() {
  try {
    writeJsonAtomic(CACHE_FILE, cache);
  } catch {
    /* ignore */
  }
}

const DAY = 24 * 3600 * 1000;
// 解析逻辑变更时 +1，旧缓存自动失效
const CACHE_VERSION = 4;

function getResolve(key) {
  const c = loadCache();
  const hit = c.resolve[key];
  if (!hit) return null;
  if (hit.v !== CACHE_VERSION) return null; // 匹配算法变了，旧结论作废
  if (Date.now() - hit.at > 30 * DAY) return null; // 匹配结果 30 天过期
  return hit.value;
}

function setResolve(key, value) {
  const c = loadCache();
  c.resolve[key] = { at: Date.now(), v: CACHE_VERSION, value };
  saveCache();
  return value;
}

function getDetail(subjectId) {
  const c = loadCache();
  const hit = c.detail[String(subjectId)];
  if (!hit) return null;
  if (hit.v !== CACHE_VERSION) return null;
  if (Date.now() - hit.at > 7 * DAY) return null; // 评分会变，7 天刷新
  return hit.data;
}

function setDetail(subjectId, data) {
  const c = loadCache();
  c.detail[String(subjectId)] = { at: Date.now(), v: CACHE_VERSION, data };
  saveCache();
  return data;
}

module.exports = {
  load,
  saveNow,
  getRating,
  setRating,
  listRatings,
  pushHistory,
  listHistory,
  removeHistory,
  listTodo,
  addTodo,
  removeTodo,
  removeTodoBySubject,
  getSettings,
  setSettings,
  getResolve,
  setResolve,
  getDetail,
  setDetail,
  DATA_DIR,
};
