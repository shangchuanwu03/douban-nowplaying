'use strict';

/**
 * 纯 Node 生成应用图标（不依赖任何图形库）。
 * 画一张「黑胶唱片」：深色盘体 + 豆瓣绿外圈 + 中心孔。
 * 输出 ui/icon.png（256×256，RGBA）。
 */

const fs = require('fs');
const path = require('path');
const zlib = require('zlib');

const SIZE = 256;

function crc32(buf) {
  let c;
  const table = crc32.table || (crc32.table = (() => {
    const t = new Int32Array(256);
    for (let n = 0; n < 256; n++) {
      c = n;
      for (let k = 0; k < 8; k++) c = c & 1 ? 0xedb88320 ^ (c >>> 1) : c >>> 1;
      t[n] = c;
    }
    return t;
  })());
  let crc = -1;
  for (let i = 0; i < buf.length; i++) crc = (crc >>> 8) ^ table[(crc ^ buf[i]) & 0xff];
  return (crc ^ -1) >>> 0;
}

function chunk(type, data) {
  const len = Buffer.alloc(4);
  len.writeUInt32BE(data.length, 0);
  const typeBuf = Buffer.from(type, 'ascii');
  const crcBuf = Buffer.alloc(4);
  crcBuf.writeUInt32BE(crc32(Buffer.concat([typeBuf, data])), 0);
  return Buffer.concat([len, typeBuf, data, crcBuf]);
}

function smoothstep(edge0, edge1, x) {
  const t = Math.max(0, Math.min(1, (x - edge0) / (edge1 - edge0)));
  return t * t * (3 - 2 * t);
}

function mix(a, b, t) {
  return a + (b - a) * t;
}

// 颜色
const BG = [23, 23, 26];
const RING = [63, 208, 122];
const HOLE = [12, 12, 14];

const raw = Buffer.alloc(SIZE * (SIZE * 4 + 1));
const cx = (SIZE - 1) / 2;
const cy = (SIZE - 1) / 2;

for (let y = 0; y < SIZE; y++) {
  const rowStart = y * (SIZE * 4 + 1);
  raw[rowStart] = 0; // filter: none
  for (let x = 0; x < SIZE; x++) {
    const dx = x - cx;
    const dy = y - cy;
    const r = Math.sqrt(dx * dx + dy * dy);

    // 采样 3×3 抗锯齿
    let cov = 0;
    let ringCov = 0;
    let holeCov = 0;
    for (let sy = -1; sy <= 1; sy++) {
      for (let sx = -1; sx <= 1; sx++) {
        const rr = Math.sqrt((dx + sx * 0.33) ** 2 + (dy + sy * 0.33) ** 2);
        cov += 1 - smoothstep(118.5, 121.5, rr);
        const ring = smoothstep(90, 94, rr) * (1 - smoothstep(104, 108, rr));
        ringCov += ring;
        holeCov += 1 - smoothstep(15, 19, rr);
      }
    }
    cov /= 9;
    ringCov /= 9;
    holeCov /= 9;

    // 盘面基础色 + 极轻微的径向提亮
    const shade = 1 - Math.min(0.5, r / 300);
    let col = BG.map((c) => c * shade + 6);
    col = col.map((c, i) => mix(c, RING[i], ringCov));
    col = col.map((c, i) => mix(c, HOLE[i], holeCov));

    const idx = rowStart + 1 + x * 4;
    raw[idx] = Math.round(col[0]);
    raw[idx + 1] = Math.round(col[1]);
    raw[idx + 2] = Math.round(col[2]);
    raw[idx + 3] = Math.round(cov * 255);
  }
}

const ihdr = Buffer.alloc(13);
ihdr.writeUInt32BE(SIZE, 0);
ihdr.writeUInt32BE(SIZE, 4);
ihdr[8] = 8; // bit depth
ihdr[9] = 6; // RGBA
ihdr[10] = 0;
ihdr[11] = 0;
ihdr[12] = 0;

const png = Buffer.concat([
  Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]),
  chunk('IHDR', ihdr),
  chunk('IDAT', zlib.deflateSync(raw, { level: 9 })),
  chunk('IEND', Buffer.alloc(0)),
]);

const out = path.join(__dirname, '..', 'ui', 'icon.png');
fs.mkdirSync(path.dirname(out), { recursive: true });
fs.writeFileSync(out, png);
console.log('icon written:', out, png.length, 'bytes');

/* ---- 同时导出一个 ICO（内嵌 PNG，Vista+ 支持），给 exe 当图标 ---- */
const pngBuf = png;
const ico = Buffer.alloc(6 + 16);
ico.writeUInt16LE(0, 0); // reserved
ico.writeUInt16LE(1, 2); // type: icon
ico.writeUInt16LE(1, 4); // count
ico.writeUInt8(0, 6); // width 0 = 256
ico.writeUInt8(0, 7); // height 0 = 256
ico.writeUInt8(0, 8); // palette
ico.writeUInt8(0, 9); // reserved
ico.writeUInt16LE(1, 10); // planes
ico.writeUInt16LE(32, 12); // bpp
ico.writeUInt32LE(pngBuf.length, 14);
ico.writeUInt32LE(22, 18);

const icoOut = path.join(__dirname, '..', 'ui', 'icon.ico');
fs.writeFileSync(icoOut, Buffer.concat([ico, pngBuf]));
console.log('ico written:', icoOut);
