'use strict';

/**
 * 一键拉取豆瓣「我听过的音乐」并导入本地。
 * 用法：先启动应用（启动.bat），再双击「导入豆瓣收藏.bat」或运行本脚本。
 */

const BASE = process.env.DNP_BASE || 'http://127.0.0.1:4590';
const MAX_ROUNDS = 400;
const PAGES_PER_ROUND = 8;

// --dry：只预览，不写库。建议先跑一次看看解析对不对
const DRY = process.argv.includes('--dry') || process.argv.includes('-n');

async function main() {
  let total = 0;
  let rounds = 0;
  let keptLocal = 0;
  const anomalies = [];

  if (DRY) {
    console.log('== 预览模式（不写入）==\n');
  }

  for (;;) {
    let res;
    try {
      res = await fetch(BASE + '/api/douban/import', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ maxPages: PAGES_PER_ROUND, dry: DRY }),
      });
    } catch (e) {
      console.error('连不上本地服务（' + BASE + '）。请先双击「启动.bat」把应用跑起来。');
      process.exit(2);
    }

    if (!res.ok) {
      console.error('服务返回 HTTP ' + res.status);
      process.exit(3);
    }

    const j = await res.json();
    if (!j.ok) {
      console.error('导入失败：' + (j.error || '未知错误'));
      process.exit(1);
    }

    rounds += 1;
    total += (j.imported || 0) + (j.updated || 0);
    keptLocal += j.keptLocal || 0;
    for (const a of j.anomalies || []) anomalies.push(a);

    if (DRY) {
      for (const p of j.preview || []) {
        console.log(
          '  ' + String(p.rating) + '★ ' +
          p.title.slice(0, 34) +
          (p.artists && p.artists.length ? '  [' + p.artists.join(' / ').slice(0, 26) + ']' : '') +
          (p.comment ? '  «' + p.comment.slice(0, 24) + '»' : '')
        );
      }
    } else {
      const pct = j.hasMore ? '' : '  ← 已到底';
      console.log(
        '第 ' + rounds + ' 批：新增 ' + (j.imported || 0) +
        ' 条，更新 ' + (j.updated || 0) +
        ' 条，保留本地 ' + (j.keptLocal || 0) +
        ' 条，累计 ' + total + pct
      );
    }

    if (!j.hasMore) {
      if (DRY) {
        console.log('\n预览结束：共 ' + total + ' 条记录（未写入）。');
        if (anomalies.length) console.log('异常条目 ' + anomalies.length + ' 条，已跳过。');
        console.log('确认无误后去掉 --dry 再跑一次即可真正导入。');
      } else {
        console.log('\n完成：本次共处理 ' + total + ' 条。');
        if (keptLocal) console.log('其中 ' + keptLocal + ' 条本地有未同步的改动，已保留本地版本。');
        if (anomalies.length) {
          console.log('跳过异常条目 ' + anomalies.length + ' 条：');
          anomalies.slice(0, 5).forEach((a) => console.log('   - ' + (a.title || a.id) + ' (' + a.reason + ')'));
        }
      }
      process.exit(0);
    }
    if (rounds >= MAX_ROUNDS) {
      console.log('\n已跑 ' + MAX_ROUNDS + ' 批，仍未到底。再运行一次会从断点继续。');
      process.exit(0);
    }
  }
}

main().catch((e) => {
  console.error('异常：' + e.message);
  process.exit(4);
});
