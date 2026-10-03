/* Watching 客户端核心逻辑（网页版：手机 + 电脑浏览器共用） */
(function () {
  'use strict';

  // ---------------- 帧协议 ----------------
  var HEADER = 4 + 8 + 256;
  var MAGIC = [0x57, 0x46, 0x30, 0x31]; // "WF01"
  var TILE_TABLE_HEADER = 8;   // "DT01" + 分块数
  var TILE_ENTRY = 8;          // x(2) + y(2) + 长度(4)

  // 解析一帧：整帧返回 {full: Blob}，增量返回 {tiles:[{x,y,blob}]}
  function parseFrame(buf) {
    if (!buf || buf.byteLength < HEADER) return null;
    var u8 = new Uint8Array(buf);
    if (u8[0] !== MAGIC[0] || u8[1] !== MAGIC[1] || u8[2] !== MAGIC[2] || u8[3] !== MAGIC[3]) return null;

    var dv = new DataView(buf);
    var seq = Number(dv.getBigInt64(4, true));

    var end = 12;
    while (end < 12 + 256 && u8[end] !== 0) end++;
    var meta = {};
    try { meta = JSON.parse(new TextDecoder('utf-8').decode(u8.subarray(12, end))); }
    catch (e) { return null; }

    var body = HEADER;

    if (meta.mode !== 'delta') {
      return {
        seq: seq, meta: meta, delta: false,
        blob: new Blob([buf.slice(body)], { type: 'image/jpeg' })
      };
    }

    // 增量帧：DT01 + 分块数 + 表项 + 各分块 JPEG
    if (buf.byteLength < body + TILE_TABLE_HEADER) return null;
    if (u8[body] !== 0x44 || u8[body + 1] !== 0x54 || u8[body + 2] !== 0x30 || u8[body + 3] !== 0x31) return null;

    var count = dv.getInt32(body + 4, true);
    if (count < 0 || count > 4096) return null;

    var p = body + TILE_TABLE_HEADER;
    var entries = [];
    for (var i = 0; i < count; i++) {
      var x = (u8[p] << 8) | u8[p + 1];
      var y = (u8[p + 2] << 8) | u8[p + 3];
      var len = dv.getUint32(p + 4, true);
      p += TILE_ENTRY;
      entries.push({ x: x, y: y, len: len });
    }

    var tiles = [];
    var dataStart = p;
    for (var j = 0; j < entries.length; j++) {
      var e = entries[j];
      if (dataStart + e.len > buf.byteLength) return null;
      tiles.push({
        x: e.x, y: e.y,
        blob: new Blob([buf.slice(dataStart, dataStart + e.len)], { type: 'image/jpeg' })
      });
      dataStart += e.len;
    }

    return { seq: seq, meta: meta, delta: true, tiles: tiles };
  }

  // ---------------- 观看端 ----------------
  function Viewer(canvas, opts) {
    this.canvas = canvas;
    this.ctx = canvas.getContext('2d', { alpha: false });
    this.opts = opts || {};
    this.ws = null;
    this.url = '';
    this.image = null;
    this.pending = null;
    this.dirty = false;
    this.connected = false;
    this.manualClose = false;
    this.retry = 0;
    this.retryTimer = null;

    this.stats = { frames: 0, bytes: 0, lastTick: 0, fps: 0, kbps: 0, seq: -1, moved: [], lastMeta: null, total: 0 };

    this.fit = true;              // 适应窗口
    this.zoom = 1;                // 手动缩放
    this.offset = [0, 0];         // 手动平移
    this.showHud = true;
    this.hudTimer = null;
    this.lastTap = 0;
    this.touchState = null;

    var self = this;
    window.addEventListener('resize', function () { self.resize(); });
    document.addEventListener('visibilitychange', function () {
      if (!document.hidden) self.dirty = true;
    });
    if (typeof ResizeObserver === 'function') {
      new ResizeObserver(function () { self.resize(); }).observe(canvas);
    }
    requestAnimationFrame(function (t) { self.renderLoop(t); });
    this.resize();
  }

  Viewer.prototype.connect = function (url) {
    this.url = url;
    this.manualClose = false;
    this.open();
  };

  Viewer.prototype.open = function () {
    var self = this;
    try {
      var ws = new WebSocket(this.url);
      ws.binaryType = 'arraybuffer';
      this.ws = ws;

      ws.onopen = function () {
        self.connected = true;
        self.retry = 0;
        self.dirty = true;
        self.send({
          t: 'hello', kind: self.opts.kind || 'web',
          name: self.opts.name || '', version: '1.0'
        });
        if (self.opts.onOpen) self.opts.onOpen();
      };

      ws.onmessage = function (ev) {
        if (typeof ev.data === 'string') { self.onText(ev.data); return; }
        var frame = parseFrame(ev.data);
        if (!frame) return;
        self.stats.frames++;
        self.stats.total++;
        self.stats.bytes += ev.data.byteLength;
        self.stats.seq = frame.seq;

        if (self.decoder) {
          // 预留：浏览器支持 ImageDecoder 时可以走更快的解码路径
          self.decodeBlob(frame);
        } else {
          self.decodeBlob(frame);
        }
      };

      ws.onclose = function () {
        self.connected = false;
        self.dirty = true;
        if (self.opts.onClose) self.opts.onClose();
        if (!self.manualClose) self.scheduleRetry();
      };

      ws.onerror = function () {
        if (self.opts.onError) self.opts.onError();
      };
    } catch (e) {
      this.scheduleRetry();
    }
  };

  Viewer.prototype.scheduleRetry = function () {
    var self = this;
    if (this.retryTimer) clearTimeout(this.retryTimer);
    this.retry = Math.min(this.retry + 1, 8);
    var delay = Math.min(1000 * this.retry, 6000);
    this.retryTimer = setTimeout(function () { if (!self.manualClose) self.open(); }, delay);
  };

  Viewer.prototype.close = function () {
    this.manualClose = true;
    if (this.retryTimer) clearTimeout(this.retryTimer);
    try { if (this.ws) this.ws.close(); } catch (e) { }
    this.ws = null;
    this.connected = false;
  };

  Viewer.prototype.send = function (obj) {
    try {
      if (this.ws && this.ws.readyState === 1) this.ws.send(JSON.stringify(obj));
    } catch (e) { }
  };

  Viewer.prototype.onText = function (text) {
    var msg;
    try { msg = JSON.parse(text); } catch (e) { return; }
    if (this.opts.onMessage) this.opts.onMessage(msg);
  };

  // ---- 解码 / 合成 ----
  //
  // 服务端会发两种帧：
  //   整帧（mode=full）  —— 直接作为新的基准画面
  //   增量帧（mode=delta）—— 只含变化的分块，贴到基准的对应位置
  // 所以这里维护一张离屏画布作为“当前完整画面”，屏幕上只做一次拷贝。

  Viewer.prototype.decodeBlob = function (frame) {
    var self = this;

    if (!frame.delta) {
      loadImage(frame.blob, function (img) {
        var off = document.createElement('canvas');
        off.width = img.naturalWidth;
        off.height = img.naturalHeight;
        off.getContext('2d').drawImage(img, 0, 0);

        self.base = off;
        self.baseSeq = frame.seq;
        self.pending = { canvas: off, meta: frame.meta };
        self.dirty = true;
        self.mark();
      });
      return;
    }

    // 增量帧：必须已有基准
    if (!this.base) return;

    var ctx = this.base.getContext('2d');
    var remaining = frame.tiles.length;
    var applied = false;

    if (remaining === 0) return;

    frame.tiles.forEach(function (t) {
      loadImage(t.blob, function (img) {
        try { ctx.drawImage(img, t.x, t.y); applied = true; } catch (e) { }
        if (--remaining === 0 && applied) {
          self.pending = { canvas: self.base, meta: frame.meta };
          self.dirty = true;
          self.mark();
        }
      }, function () {
        if (--remaining === 0 && applied) {
          self.pending = { canvas: self.base, meta: frame.meta };
          self.dirty = true;
          self.mark();
        }
      });
    });
  };

  function loadImage(blob, onload, onerror) {
    var url = URL.createObjectURL(blob);
    var img = new Image();
    img.onload = function () {
      try { URL.revokeObjectURL(url); } catch (e) { }
      onload(img);
    };
    img.onerror = function () {
      try { URL.revokeObjectURL(url); } catch (e) { }
      if (onerror) onerror();
    };
    img.src = url;
  }

  Viewer.prototype.mark = function () {
    this.stats.moved.push(Date.now());
  };

  // ---- 渲染 ----

  Viewer.prototype.resize = function () {
    var dpr = Math.min(window.devicePixelRatio || 1, 2);
    var w = this.canvas.clientWidth || window.innerWidth;
    var h = this.canvas.clientHeight || window.innerHeight;
    this.canvas.width = Math.max(1, Math.round(w * dpr));
    this.canvas.height = Math.max(1, Math.round(h * dpr));
    this.dpr = dpr;
    this.dirty = true;
  };

  Viewer.prototype.renderLoop = function () {
    var self = this;
    var now = Date.now();

    if (now - this.stats.lastTick >= 1000) {
      var delta = now - (this.stats.lastTick || now);
      this.stats.lastTick = now;
      this.stats.fps = delta > 0 ? (this.stats.moved.length * 1000 / delta) : 0;
      this.stats.kbps = delta > 0 ? (this.stats.bytes / 1024 * 1000 / delta) : 0;
      this.stats.moved = [];
      this.stats.bytes = 0;
      if (this.opts.onStats) this.opts.onStats(this.stats);
    }

    if (this.dirty) {
      this.dirty = false;
      this.draw();
    }

    requestAnimationFrame(function (t) { self.renderLoop(t); });
  };

  Viewer.prototype.draw = function () {
    var c = this.ctx;
    var cw = this.canvas.width, ch = this.canvas.height;
    c.fillStyle = '#000';
    c.fillRect(0, 0, cw, ch);

    if (!this.pending) return;
    var src = this.pending.canvas;          // 已是合成好的完整画面
    var iw = src.width, ih = src.height;
    if (!iw || !ih) return;

    this.lastMeta = this.pending.meta;

    var scale;
    if (this.fit) {
      scale = Math.min(cw / iw, ch / ih);
    } else {
      scale = this.dpr * this.zoom;
    }

    var dw = iw * scale, dh = ih * scale;
    var dx = (cw - dw) / 2 + this.offset[0] * this.dpr;
    var dy = (ch - dh) / 2 + this.offset[1] * this.dpr;

    c.imageSmoothingEnabled = true;
    c.imageSmoothingQuality = 'high';
    try {
      c.drawImage(src, dx, dy, dw, dh);
    } catch (e) { }
  };

  // 手势：双指缩放 / 拖动
  // ---------------- 远程控制（把本机操作转发给服务端）----------------
  //
  // 服务端支持这些消息（坐标是 0~1 的归一化坐标）：
  //   {t:"input", kind:"move",  x, y}
  //   {t:"input", kind:"down"/"up"/"click", button:"left|right|middle", x, y}
  //   {t:"input", kind:"wheel", delta: ±120, x, y}
  //   {t:"input", kind:"key",   key:"a|enter|f5…", ctrl, alt, shift, win}
  // 只有在服务端开启「允许远程控制」后才会真正生效。

  Viewer.prototype.setControl = function (on) {
    this.control = !!on;
    return this.control;
  };

  Viewer.prototype.isControl = function () {
    return !!this.control;
  };

  /// 屏幕坐标 → 归一化图像坐标（自动考虑适应/缩放/平移）
  Viewer.prototype.toNormalized = function (clientX, clientY) {
    var rect = this.canvas.getBoundingClientRect();
    var cw = this.canvas.width, ch = this.canvas.height;
    if (!this.pending || !rect.width || !cw) return null;

    var iw = this.pending.canvas.width, ih = this.pending.canvas.height;
    var scale = this.fit
      ? Math.min(cw / iw, ch / ih)
      : this.dpr * this.zoom;

    var dw = iw * scale, dh = ih * scale;
    var dx = (cw - dw) / 2 + this.offset[0] * this.dpr;
    var dy = (ch - dh) / 2 + this.offset[1] * this.dpr;

    // 把 CSS 像素换算到 canvas 像素
    var px = (clientX - rect.left) * (cw / rect.width);
    var py = (clientY - rect.top) * (ch / rect.height);

    var nx = (px - dx) / dw;
    var ny = (py - dy) / dh;
    if (nx < -0.02 || nx > 1.02 || ny < -0.02 || ny > 1.02) return null;

    return { x: Math.max(0, Math.min(1, nx)), y: Math.max(0, Math.min(1, ny)) };
  };

  Viewer.prototype.sendPointer = function (kind, p, button, delta) {
    if (!this.control || !p) return;
    var msg = { t: 'input', kind: kind, x: Math.round(p.x * 10000) / 10000, y: Math.round(p.y * 10000) / 10000 };
    if (button) msg.button = button;
    if (delta !== undefined) msg.delta = delta;
    this.send(msg);
  };

  /// 发送按键。extra 可带 ctrl/alt/shift/win。
  Viewer.prototype.sendKey = function (key, extra) {
    if (!this.control || !key) return;
    var msg = { t: 'input', kind: 'key', key: key };
    if (extra) {
      if (extra.ctrl) msg.ctrl = true;
      if (extra.alt) msg.alt = true;
      if (extra.shift) msg.shift = true;
      if (extra.win) msg.win = true;
    }
    this.send(msg);
  };

  /// 浏览器 KeyboardEvent.key → 服务端认识的键名
  var KEY_ALIAS = {
    ' ': 'space', 'Spacebar': 'space',
    'ArrowUp': 'up', 'ArrowDown': 'down', 'ArrowLeft': 'left', 'ArrowRight': 'right',
    'Enter': 'enter', 'Escape': 'esc', 'Backspace': 'backspace', 'Tab': 'tab',
    'Delete': 'delete', 'Insert': 'insert', 'Home': 'home', 'End': 'end',
    'PageUp': 'pageup', 'PageDown': 'pagedown',
    'Control': null, 'Alt': null, 'Shift': null, 'Meta': null, 'CapsLock': null
  };

  Viewer.prototype.keyName = function (ev) {
    var k = ev.key;
    if (k === undefined || k === null) return null;
    if (Object.prototype.hasOwnProperty.call(KEY_ALIAS, k)) return KEY_ALIAS[k];
    if (k.length === 1) return k;                       // 可打印字符
    if (/^F\d{1,2}$/.test(k)) return k.toLowerCase();   // F1~F24
    return null;
  };

  /// 绑定鼠标/滚轮/键盘（电脑浏览器用）
  Viewer.prototype.attachInput = function (el) {
    var self = this;
    var down = false;

    el.addEventListener('mousedown', function (e) {
      if (!self.control) return;
      var p = self.toNormalized(e.clientX, e.clientY);
      if (!p) return;
      down = true;
      var btn = e.button === 2 ? 'right' : (e.button === 1 ? 'middle' : 'left');
      self.sendPointer('down', p, btn);
      el.focus();
      e.preventDefault();
    });

    el.addEventListener('mousemove', function (e) {
      if (!self.control) return;
      var p = self.toNormalized(e.clientX, e.clientY);
      if (!p) return;
      // 按下时用 down+move 连续拖动；未按下时只报告位置
      self.sendPointer('move', p);
    });

    el.addEventListener('mouseup', function (e) {
      if (!self.control || !down) return;
      var p = self.toNormalized(e.clientX, e.clientY);
      down = false;
      if (!p) return;
      var btn = e.button === 2 ? 'right' : (e.button === 1 ? 'middle' : 'left');
      self.sendPointer('up', p, btn);
      e.preventDefault();
    });

    el.addEventListener('contextmenu', function (e) { if (self.control) e.preventDefault(); });

    el.addEventListener('wheel', function (e) {
      if (!self.control) return;
      var p = self.toNormalized(e.clientX, e.clientY);
      if (!p) return;
      self.sendPointer('wheel', p, null, e.deltaY > 0 ? -120 : 120);
      e.preventDefault();
    }, { passive: false });

    document.addEventListener('keydown', function (e) {
      if (!self.control) return;
      // 输入用户名/密码时不抢按键
      var t = e.target;
      if (t && (t.tagName === 'INPUT' || t.tagName === 'TEXTAREA')) return;

      var name = self.keyName(e);
      if (!name) return;
      self.sendKey(name, { ctrl: e.ctrlKey, alt: e.altKey, shift: e.shiftKey, win: e.metaKey });
      if (e.key !== 'F5' && e.key !== 'F11' && e.key !== 'F12') e.preventDefault();
    });
  };

  Viewer.prototype.attachGestures = function (el) {
    var self = this;

    el.addEventListener('touchstart', function (e) {
      if (e.touches.length === 2) {
        var dx = e.touches[0].clientX - e.touches[1].clientX;
        var dy = e.touches[0].clientY - e.touches[1].clientY;
        self.touchState = {
          mode: 'pinch',
          dist: Math.hypot(dx, dy),
          zoom: self.zoom,
          mid: [(e.touches[0].clientX + e.touches[1].clientX) / 2,
                (e.touches[0].clientY + e.touches[1].clientY) / 2]
        };
        e.preventDefault();
      } else if (e.touches.length === 1) {
        var p0 = self.control ? self.toNormalized(e.touches[0].clientX, e.touches[0].clientY) : null;
        self.touchState = {
          mode: 'pan',
          x: e.touches[0].clientX, y: e.touches[0].clientY,
          offset: [self.offset[0], self.offset[1]],
          moved: 0,
          remoteStart: p0,
          remoteDown: false,
          lastSent: 0
        };
        // 控制模式下按下即按住左键（拖动窗口 / 划选文字）
        if (p0) {
          self.touchState.remoteDown = true;
          self.sendPointer('down', p0, 'left');
        }
      }
    }, { passive: false });

    el.addEventListener('touchmove', function (e) {
      var st = self.touchState;
      if (!st) return;
      if (st.mode === 'pinch' && e.touches.length === 2) {
        var dx = e.touches[0].clientX - e.touches[1].clientX;
        var dy = e.touches[0].clientY - e.touches[1].clientY;
        var dist = Math.hypot(dx, dy);
        self.fit = false;
        self.zoom = Math.max(0.2, Math.min(6, st.zoom * (dist / (st.dist || 1))));
        self.dirty = true;
        e.preventDefault();
        return;
      }

      if (st.mode === 'pan' && e.touches.length === 1) {
        var mx = e.touches[0].clientX - st.x;
        var my = e.touches[0].clientY - st.y;
        st.moved = Math.max(st.moved, Math.hypot(mx, my));

        // 控制模式：把手指位置当作鼠标位置发给服务端（按 60Hz 限流）
        if (self.control) {
          var now = Date.now();
          if (now - st.lastSent >= 16) {
            st.lastSent = now;
            var p = self.toNormalized(e.touches[0].clientX, e.touches[0].clientY);
            if (p) self.sendPointer('move', p);
          }
          // 阻止长按选中文字 / 橡皮筋等浏览器默认行为
          e.preventDefault();
        }

        // 适应窗口时不平移（否则会跟控制模式打架）
        if (!self.fit) {
          self.offset = [st.offset[0] + mx, st.offset[1] + my];
          self.dirty = true;
          e.preventDefault();
        }
        return;
      }
    }, { passive: false });

    el.addEventListener('touchend', function (e) {
      var st = self.touchState;
      self.touchState = null;

      if (!st || st.mode !== 'pan' || e.touches.length !== 0) return;

      if (st.remoteDown && self.control) {
        // 松手：位移很小 = 单击；否则是拖拽（已经发过 down+move，补一个 up）
        var endT = e.changedTouches && e.changedTouches[0];
        var p = endT ? self.toNormalized(endT.clientX, endT.clientY) : st.remoteStart;
        self.sendPointer('up', p || st.remoteStart, 'left');
        st.remoteDown = false;

        if (st.moved < 8) {
          // 单击（服务端会收到 down+up，等价于一次点击）
          return;
        }
        return;
      }

      if (st.moved < 8) {
        var now = Date.now();
        if (now - self.lastTap < 300) {
          self.lastTap = 0;
          if (self.opts.onDoubleTap) self.opts.onDoubleTap();
        } else {
          self.lastTap = now;
          if (self.opts.onTap) setTimeout(function () {
            if (self.lastTap !== 0) self.opts.onTap();
          }, 300);
        }
      }
    }, { passive: true });

    // 鼠标滚轮缩放（网页版）
    el.addEventListener('wheel', function (e) {
      if (!e.ctrlKey && !self.opts.wheelZoom) return;
      self.fit = false;
      self.zoom = Math.max(0.2, Math.min(6, self.zoom * (e.deltaY < 0 ? 1.1 : 0.9)));
      self.dirty = true;
      e.preventDefault();
    }, { passive: false });
  };

  Viewer.prototype.setFit = function (fit) {
    this.fit = fit;
    if (fit) { this.zoom = 1; this.offset = [0, 0]; }
    this.dirty = true;
  };

  Viewer.prototype.frameSize = function () {
    if (!this.pending) return null;
    return { w: this.pending.canvas.width, h: this.pending.canvas.height, meta: this.pending.meta };
  };

  Viewer.prototype.snapshot = function () {
    try { return this.canvas.toDataURL('image/jpeg', 0.92); } catch (e) { return null; }
  };

  window.Watching = { Viewer: Viewer, parseFrame: parseFrame, HEADER: HEADER };
})();
