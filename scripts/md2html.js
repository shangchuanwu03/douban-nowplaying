'use strict';

/**
 * 极简 Markdown → HTML（只覆盖本项目文档用到的语法）
 * 用法：node scripts/md2html.js 使用教程.md 使用教程.html "使用教程"
 */

const fs = require('fs');

const esc = (s) =>
  s.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;');

function inline(s) {
  let t = esc(s);
  t = t.replace(/`([^`]+)`/g, '<code>$1</code>');
  t = t.replace(/\*\*([^*]+)\*\*/g, '<strong>$1</strong>');
  return t;
}

function convert(md) {
  const lines = md.split(/\r?\n/);
  const out = [];
  let i = 0;
  let inList = false;

  const closeList = () => {
    if (inList) {
      out.push('</ul>');
      inList = false;
    }
  };

  while (i < lines.length) {
    const line = lines[i];

    // 图片（可能被 <p align="center"> 包着，也可能单独一行）
    if (/<img/.test(line)) {
      const m = line.match(/src="([^"]+)"[^>]*?width="?(\d+)"?/);
      if (m) {
        closeList();
        out.push('<p class="shot"><img src="' + m[1] + '" width="' + m[2] + '" alt=""></p>');
      }
      i += 1;
      continue;
    }
    if (/^\s*<\/?p[^>]*>\s*$/.test(line)) {
      i += 1;
      continue;
    }

    // 代码块
    if (/^\s*```/.test(line)) {
      closeList();
      i += 1;
      const buf = [];
      while (i < lines.length && !/^\s*```/.test(lines[i])) {
        buf.push(esc(lines[i]));
        i += 1;
      }
      i += 1; // 跳过结束的 ```
      out.push('<pre><code>' + buf.join('\n') + '</code></pre>');
      continue;
    }

    // 标题
    const h = line.match(/^(#{1,4})\s+(.*)$/);
    if (h) {
      closeList();
      const level = h[1].length;
      out.push('<h' + level + '>' + inline(h[2]) + '</h' + level + '>');
      i += 1;
      continue;
    }

    // 分隔线
    if (/^\s*---+\s*$/.test(line)) {
      closeList();
      out.push('<hr>');
      i += 1;
      continue;
    }

    // 引用
    if (/^\s*>\s?/.test(line)) {
      closeList();
      const buf = [];
      while (i < lines.length && /^\s*>\s?/.test(lines[i])) {
        buf.push(inline(lines[i].replace(/^\s*>\s?/, '')));
        i += 1;
      }
      out.push('<blockquote>' + buf.join('<br>') + '</blockquote>');
      continue;
    }

    // 表格
    if (/^\s*\|/.test(line) && i + 1 < lines.length && /^\s*\|[\s:|-]+\|\s*$/.test(lines[i + 1])) {
      closeList();
      const cells = (row) =>
        row.trim().replace(/^\|/, '').replace(/\|$/, '').split('|').map((c) => c.trim());
      const head = cells(line);
      i += 2;
      const rows = [];
      while (i < lines.length && /^\s*\|/.test(lines[i])) {
        rows.push(cells(lines[i]));
        i += 1;
      }
      let html = '<table><thead><tr>';
      for (const c of head) html += '<th>' + inline(c) + '</th>';
      html += '</tr></thead><tbody>';
      for (const r of rows) {
        html += '<tr>';
        for (const c of r) html += '<td>' + inline(c) + '</td>';
        html += '</tr>';
      }
      html += '</tbody></table>';
      out.push(html);
      continue;
    }

    // 有序列表
    const oli = line.match(/^\s*\d+\.\s+(.*)$/);
    if (oli) {
      closeList();
      const buf = [];
      while (i < lines.length) {
        const m = lines[i].match(/^\s*\d+\.\s+(.*)$/);
        if (!m) break;
        buf.push('<li>' + inline(m[1]) + '</li>');
        i += 1;
      }
      out.push('<ol>' + buf.join('') + '</ol>');
      continue;
    }

    // 列表
    const li = line.match(/^\s*[-*]\s+(.*)$/);
    if (li) {
      if (!inList) {
        out.push('<ul>');
        inList = true;
      }
      out.push('<li>' + inline(li[1]) + '</li>');
      i += 1;
      continue;
    }

    // 空行
    if (!line.trim()) {
      closeList();
      i += 1;
      continue;
    }

    // 普通段落
    closeList();
    const buf = [];
    while (
      i < lines.length &&
      lines[i].trim() &&
      !/^(#{1,4}\s|\s*[-*]\s|\s*\||\s*>|\s*---+\s*$|\s*<img|\s*<\/?p)/.test(lines[i])
    ) {
      buf.push(inline(lines[i].trim()));
      i += 1;
    }
    if (buf.length) {
      out.push('<p>' + buf.join(' ') + '</p>');
    } else {
      // 兜底：这一行没被任何规则吃掉，也强制前进，避免死循环
      out.push('<p>' + inline(line.trim()) + '</p>');
      i += 1;
    }
  }

  closeList();
  return out.join('\n');
}

const [, , src, dest, title] = process.argv;
if (!src || !dest) {
  console.error('用法: node scripts/md2html.js <输入.md> <输出.html> [标题]');
  process.exit(1);
}

const body = convert(fs.readFileSync(src, 'utf8'));
const html = `<!DOCTYPE html>
<html lang="zh-CN">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>${esc(title || '文档')}</title>
<style>
  :root { color-scheme: dark; }
  * { box-sizing: border-box; }
  body {
    margin: 0;
    padding: 48px 20px 96px;
    background: radial-gradient(1200px 600px at 20% -10%, #16263f 0%, #0b1322 55%, #080e19 100%);
    color: #dfe8f5;
    font: 16px/1.75 "Microsoft YaHei UI", "PingFang SC", system-ui, sans-serif;
  }
  main { max-width: 760px; margin: 0 auto; }
  h1 { font-size: 30px; color: #e4b863; margin: 0 0 8px; letter-spacing: .5px; }
  h2 { font-size: 21px; margin: 44px 0 12px; padding-top: 20px; border-top: 1px solid #1d2c44; color: #fff; }
  h3 { font-size: 17px; margin: 26px 0 8px; color: #cfe0f5; }
  p { margin: 10px 0; color: #c7d8ec; }
  strong { color: #fff; }
  code { background: #16233a; border: 1px solid #24344f; border-radius: 6px; padding: 1px 6px; font-size: 13px; color: #e4b863; }
  blockquote { margin: 14px 0; padding: 10px 16px; border-left: 3px solid #e4b863; background: #101c30; color: #9fb6d2; border-radius: 0 10px 10px 0; }
  hr { border: 0; border-top: 1px solid #1d2c44; margin: 32px 0; }
  ul, ol { margin: 10px 0 10px 22px; padding: 0; color: #c7d8ec; }
  pre { background: #0d1727; border: 1px solid #22314a; border-radius: 10px; padding: 14px 16px; overflow-x: auto; }
  pre code { background: none; border: 0; padding: 0; color: #b9d0ea; font-size: 13px; }
  li { margin: 5px 0; }
  table { width: 100%; border-collapse: collapse; margin: 16px 0; font-size: 14px; }
  th, td { border: 1px solid #22314a; padding: 9px 12px; text-align: left; }
  th { background: #101c30; color: #9fb6d2; font-weight: 600; }
  td { color: #c7d8ec; }
  .shot { text-align: center; margin: 18px 0; }
  .shot img { max-width: 100%; border-radius: 14px; border: 1px solid #22314a; box-shadow: 0 12px 30px rgba(0,0,0,.45); }
</style>
</head>
<body>
<main>
${body}
</main>
</body>
</html>
`;

fs.writeFileSync(dest, html, 'utf8');
console.log('ok: ' + dest + '  ' + Math.round(html.length / 1024) + ' KB');
