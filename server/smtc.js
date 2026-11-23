'use strict';

/**
 * SMTC 桥：把常驻 PowerShell 采集器输出的 JSON 行，转成事件。
 *
 * 事件：
 *   state  每次播放状态/曲目变化
 *   error  采集器异常
 *   exit   进程退出（会自动重启）
 */

const { spawn } = require('child_process');
const path = require('path');
const { EventEmitter } = require('events');

const SCRIPT = path.join(__dirname, '..', 'scripts', 'smtc.ps1');

class SmtcBridge extends EventEmitter {
  constructor({ intervalMs = 900 } = {}) {
    super();
    this.intervalMs = intervalMs;
    this.child = null;
    this.last = null;
    this.restarts = 0;
    this.stopped = false;
    this.buf = '';
  }

  start() {
    if (this.child || this.stopped) return this;

    const args = [
      '-NoProfile',
      '-NonInteractive',
      '-ExecutionPolicy',
      'Bypass',
      '-File',
      SCRIPT,
      '-IntervalMs',
      String(this.intervalMs),
    ];

    try {
      this.child = spawn('powershell.exe', args, {
        windowsHide: true,
        stdio: ['ignore', 'pipe', 'pipe'],
      });
    } catch (e) {
      this.emit('error', e);
      return this;
    }

    this.child.stdout.setEncoding('utf8');
    this.child.stdout.on('data', (chunk) => {
      this.buf += chunk;
      let idx;
      while ((idx = this.buf.indexOf('\n')) >= 0) {
        const line = this.buf.slice(0, idx).trim();
        this.buf = this.buf.slice(idx + 1);
        if (!line) continue;
        this.handleLine(line);
      }
      if (this.buf.length > 65536) this.buf = ''; // 防御：异常长行
    });

    this.child.stderr.setEncoding('utf8');
    this.child.stderr.on('data', (s) => {
      const msg = String(s).trim();
      if (msg) this.emit('error', new Error(msg.slice(0, 300)));
    });

    this.child.on('exit', (code) => {
      this.child = null;
      if (this.stopped) return;
      this.emit('exit', code);
      // 指数退避重启，避免疯狂重启
      const delay = Math.min(15000, 800 * Math.pow(2, this.restarts++));
      setTimeout(() => {
        if (!this.stopped) this.start();
      }, delay);
    });

    return this;
  }

  handleLine(line) {
    let obj;
    try {
      obj = JSON.parse(line);
    } catch {
      return;
    }
    if (obj.error === 'smtc_unavailable') {
      this.emit('error', new Error('系统媒体控制不可用（SMTC 未就绪）'));
      return;
    }
    this.restarts = 0;
    this.last = obj;
    this.emit('state', obj);
  }

  stop() {
    this.stopped = true;
    if (this.child) {
      try {
        this.child.kill();
      } catch {
        /* ignore */
      }
      this.child = null;
    }
  }
}

module.exports = { SmtcBridge };
