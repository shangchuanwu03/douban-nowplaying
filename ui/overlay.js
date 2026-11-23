'use strict';

/**
 * 悬浮窗前端：订阅 SSE，渲染「正在听 + 豆瓣评分 + 我的评分」。
 * 无框架，纯 DOM。
 */

const $ = (id) => document.getElementById(id);

const els = {
  stage: $('stage'),
  card: $('card'),
  appName: $('appName'),
  statusText: $('statusText'),
  album: $('albumTitle'),
  artist: $('artistLine'),
  cover: $('cover'),
  coverFallback: $('coverFallback'),
  coverLetter: $('coverLetter'),
  scoreNum: $('scoreNum'),
  dstars: $('dstars'),
  votes: $('votesLine'),
  dist: $('dist'),
  mine: $('mine'),
  myStars: $('myStars'),
  comment: $('comment'),
  sync: $('btnSync'),
  hint: $('hintRow'),
  progress: $('progressBar'),
  toast: $('toast'),
  manage: $('btnManage'),
};

let state = null;
let currentSubjectId = null;
let saveTimer = null;
let autoSyncTimer = null;
let commentDirty = false;

/* ------------------------------ 小工具 ------------------------------ */

function fmtVotes(n) {
  const v = Number(n) || 0;
  if (v >= 10000) return `${(v / 10000).toFixed(1).replace(/\.0$/, '')} 万人评价`;
  return `${v} 人评价`;
}

function yearOf(d) {
  const m = String(d || '').match(/(\d{4})/);
  return m ? m[1] : '';
}

function toast(msg, isErr = false) {
  els.toast.textContent = msg;
  els.toast.classList.toggle('err', isErr);
  els.toast.classList.add('show');
  clearTimeout(toast._t);
  toast._t = setTimeout(() => els.toast.classList.remove('show'), 2600);
}

function setStars(container, fraction) {
  const f = Math.max(0, Math.min(5, fraction));
  let html = '';
  for (let i = 0; i < 5; i++) {
    const fill = Math.max(0, Math.min(1, f - i));
    html += `<span class="star" style="--f:${fill.toFixed(3)}"><i></i></span>`;
  }
  container.innerHTML = html;
}

function renderMyStars(rating) {
  els.myStars.innerHTML = '';
  for (let i = 1; i <= 5; i++) {
    const b = document.createElement('button');
    b.type = 'button';
    b.dataset.value = String(i);
    b.className = i <= rating ? 'on' : '';
    b.title = `${i} 星`;
    b.addEventListener('click', () => onRate(i));
    b.addEventListener('mouseenter', () => previewStars(i));
    b.addEventListener('mouseleave', () => previewStars(rating));
    els.myStars.appendChild(b);
  }
}

function previewStars(n) {
  [...els.myStars.children].forEach((b, idx) => {
    b.classList.toggle('on', idx < n);
  });
}

/* ------------------------------ 渲染 ------------------------------ */

function render(s) {
  state = s;
  const p = s.player;
  const m = s.match;

  if (!p) {
    els.stage.dataset.mode = 'idle';
    els.appName.textContent = '等待播放';
    els.statusText.textContent = '';
    els.album.textContent = '还没有在放歌';
    els.artist.textContent = '在 Apple Music 里播放一张专辑，我会自动认出来';
    els.scoreNum.textContent = '--';
    setStars(els.dstars, 0);
    els.votes.textContent = '豆瓣评分';
    els.dist.innerHTML = '';
    els.hint.classList.remove('show');
    els.progress.style.width = '0%';
    requestResize();
    return;
  }

  els.appName.textContent = p.appName || '未知来源';
  els.statusText.textContent =
    p.status === 'Playing' ? '正在播放' : p.status === 'Paused' ? '已暂停' : '';

  // 曲目信息优先用豆瓣条目，其次用播放器原始信息
  const albumTitle = m?.subject?.title || p.album || p.title || '未知曲目';
  const artistText =
    m?.subject?.artistText || p.artist || (m?.subject?.artists || []).join(' / ') || '';

  els.album.textContent = albumTitle;
  els.artist.textContent = [artistText, yearOf(m?.subject?.date), m?.subject?.genre]
    .filter(Boolean)
    .join(' · ');

  // 封面
  const coverUrl = m?.subject?.cover || '';
  if (coverUrl) {
    const proxied = `/api/cover?u=${encodeURIComponent(coverUrl)}`;
    if (els.cover.dataset.src !== coverUrl) {
      els.cover.dataset.src = coverUrl;
      els.cover.classList.remove('loaded');
      els.cover.onload = () => els.cover.classList.add('loaded');
      els.cover.onerror = () => {
        els.cover.classList.remove('loaded');
        els.coverFallback.style.display = 'grid';
      };
      els.cover.src = proxied;
    }
    els.coverFallback.style.display = 'none';
  } else {
    els.cover.dataset.src = '';
    els.cover.classList.remove('loaded');
    els.cover.removeAttribute('src');
    els.coverFallback.style.display = 'grid';
    els.coverLetter.textContent = (albumTitle || '♪').trim().charAt(0) || '♪';
  }

  // 豆瓣评分
  if (m?.subject?.score) {
    const score = Number(m.subject.score);
    els.scoreNum.textContent = score.toFixed(1);
    setStars(els.dstars, score / 2);
    els.votes.textContent = fmtVotes(m.subject.votes);

    const dist = m.subject.distribution || [];
    if (dist.length === 5) {
      els.dist.innerHTML = dist
        .map((pct) => `<i style="--w:${Math.max(0, Math.min(100, pct))}%"></i>`)
        .join('');
    } else {
      els.dist.innerHTML = '';
    }
  } else {
    els.scoreNum.textContent = m ? '--' : '···';
    setStars(els.dstars, 0);
    els.votes.textContent = m ? '豆瓣暂无评分' : '正在查豆瓣…';
    els.dist.innerHTML = '';
  }

  // 状态机
  if (s.resolving && !m) {
    els.stage.dataset.mode = 'loading';
  } else if (m) {
    els.stage.dataset.mode = 'ready';
  } else {
    els.stage.dataset.mode = 'nomatch';
  }

  // 提示行
  if (!m && !s.resolving && (p.album || p.title)) {
    els.hint.classList.add('show');
    els.hint.innerHTML = `豆瓣没找到这张 · <a id="bindLink">手动绑定</a>`;
    const link = $('bindLink');
    if (link) link.addEventListener('click', openManageWithQuery);
  } else {
    els.hint.classList.remove('show');
  }

  // 我的评分
  const newId = m?.subject?.id || null;
  if (newId !== currentSubjectId) {
    currentSubjectId = newId;
    commentDirty = false;
  }
  const mine = s.myRating || {};
  renderMyStars(mine.rating || 0);
  if (!commentDirty) els.comment.value = mine.comment || '';
  els.sync.classList.toggle('synced', !!mine.synced);
  els.sync.disabled = !newId || !mine.rating;
  els.sync.textContent = mine.synced ? '已同步' : '同步';

  updateProgress(true);
  requestResize();
}

/* ------------------------------ 进度条 ------------------------------ */

let lastProgress = null;

function updateProgress(reset) {
  if (reset) lastProgress = null;
  const p = state?.player;
  if (!p || !p.duration) {
    els.progress.style.width = '0%';
    return;
  }
  let pos = Number(p.position) || 0;
  if (p.status === 'Playing' && p.ts) {
    pos += (Date.now() - p.ts) / 1000;
  }
  const pct = Math.max(0, Math.min(100, (pos / p.duration) * 100));
  if (lastProgress === null || Math.abs(pct - lastProgress) > 0.3) {
    els.progress.style.width = pct.toFixed(2) + '%';
    lastProgress = pct;
  }
}

const NO_SSE = new URLSearchParams(location.search).has('nosse');

if (!NO_SSE) {
  setInterval(() => updateProgress(false), 900);
}

/* ------------------------------ 窗口高度 ------------------------------ */

let resizeTimer = null;
function requestResize() {
  if (resizeTimer) clearTimeout(resizeTimer);
  resizeTimer = setTimeout(() => {
    const api = window.dshOverlay;
    if (!api?.resize) return;
    const h = Math.ceil(els.card.getBoundingClientRect().height + 20);
    if (h > 100) api.resize(h);
  }, 80);
}

/* ------------------------------ 交互 ------------------------------ */

async function onRate(value) {
  if (!currentSubjectId) return;
  const subj = state?.match?.subject || {};
  renderMyStars(value);
  els.sync.disabled = false;

  try {
    await fetch('/api/rating', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({
        subjectId: currentSubjectId,
        rating: value,
        comment: els.comment.value,
        title: subj.title || '',
        artist: subj.artistText || '',
        cover: subj.cover || '',
      }),
    });
    toast(`已记下 ${value} 星`);
  } catch (e) {
    toast('保存失败：' + e.message, true);
  }
}

els.comment.addEventListener('input', () => {
  commentDirty = true;
  if (!currentSubjectId) return;
  clearTimeout(saveTimer);
  saveTimer = setTimeout(async () => {
    try {
      await fetch('/api/rating', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({
          subjectId: currentSubjectId,
          rating: state?.myRating?.rating || 0,
          comment: els.comment.value,
        }),
      });
      commentDirty = false;
    } catch {
      /* 静默，下一次输入还会重试 */
    }
  }, 700);
});

els.comment.addEventListener('keydown', (e) => {
  if (e.key === 'Enter') els.comment.blur();
});

els.sync.addEventListener('click', async () => {
  if (!currentSubjectId) return;
  els.sync.disabled = true;
  els.sync.textContent = '同步中…';
  try {
    const r = await fetch('/api/sync', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ subjectId: currentSubjectId }),
    });
    const j = await r.json();
    if (j.ok) {
      els.sync.textContent = '已同步';
      els.sync.classList.add('synced');
      toast('已写入豆瓣');
    } else {
      els.sync.textContent = '同步';
      els.sync.disabled = false;
      toast(j.error || '同步失败', true);
    }
  } catch (e) {
    els.sync.textContent = '同步';
    els.sync.disabled = false;
    toast('同步失败：' + e.message, true);
  }
});

function openManageWithQuery() {
  const q = state?.player?.album || state?.player?.title || '';
  openManage({ bind: q });
}

function openManage(params = {}) {
  const qs = new URLSearchParams(params).toString();
  if (window.dshOverlay?.openManage) {
    window.dshOverlay.openManage(qs);
  } else {
    window.open(`./manage.html${qs ? '?' + qs : ''}`, 'dnp-manage', 'width=760,height=680');
  }
}

els.manage.addEventListener('click', () => openManage());

/* ------------------------------ 数据流 ------------------------------ */

function connect() {
  const es = new EventSource('/api/stream');
  let opened = false;

  es.onopen = () => {
    opened = true;
  };
  es.onmessage = (ev) => {
    try {
      render(JSON.parse(ev.data));
    } catch {
      /* ignore */
    }
  };
  es.onerror = () => {
    if (opened) {
      els.statusText.textContent = '重连中…';
    }
    // EventSource 会自动重连；若彻底失败则退回轮询
    setTimeout(() => {
      if (es.readyState === EventSource.CLOSED) {
        es.close();
        poll();
      }
    }, 4000);
  };
}

let polling = false;
async function poll() {
  if (polling) return;
  polling = true;
  try {
    const r = await fetch('/api/state');
    render(await r.json());
  } catch {
    /* ignore */
  }
  polling = false;
  setTimeout(poll, 2000);
}

if (NO_SSE) {
  // 静态预览模式（截图/调试用）：只取一次状态
  fetch('/api/state')
    .then((r) => r.json())
    .then(render)
    .catch(() => {});
} else {
  connect();
}

// 首次渲染占位
render({
  player: null,
  match: null,
  myRating: null,
  resolving: false,
});
