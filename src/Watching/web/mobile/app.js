/* Watching 客户端核心逻辑（网页版：手机 + 电脑浏览器共用） */
(function () {
  'use strict';

  // ---------------- 帧协议 ----------------
  var HEADER = 4 + 8 + 256;
  var MAGIC = [0x57, 0x46, 0x30, 0x31]; // "WF01"

  function parseFrame(buf) {
    if (!buf || buf.byteLength < HEADER) return null;
    var u8 = new Uint8Array(buf);
    if (u8[0] !== MAGIC[0] || u8[1] !== MAGIC[1] || u8[2] !== MAGIC[2] || u8[3] !== MAGIC[3]) return null;

    var dv = new DataView(buf);
    var seq = Number(dv.getBigInt64(4, true));

    var end = 12;
    while (end < 12 + 256 && u8[end] !== 0) end++;
    var metaText = new TextDecoder('utf-8').decode(u8.subarray(12, end));

    var meta = {};
    try { meta = JSON.parse(metaText); } catch (e) { return null; }

    return { seq: seq, meta: meta, blob: new Blob([buf.slice(HEADER)], { type: 'image/jpeg' }) };
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

  // ---- 解码 ----

  Viewer.prototype.decodeBlob = function (frame) {
    var self = this;
    var url = URL.createObjectURL(frame.blob);
    var img = new Image();
    img.onload = function () {
      if (self.pending) { try { URL.revokeObjectURL(self.pending.url); } catch (e) { } }
      self.pending = { url: url, img: img, meta: frame.meta };
      self.dirty = true;
      self.mark();
    };
    img.onerror = function () { try { URL.revokeObjectURL(url); } catch (e) { } };
    img.src = url;
  };

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
    var img = this.pending.img;
    var iw = img.naturalWidth, ih = img.naturalHeight;
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
      c.drawImage(img, dx, dy, dw, dh);
    } catch (e) { }

    // 释放上一帧
    if (this.lastDrawnUrl && this.lastDrawnUrl !== this.pending.url) {
      try { URL.revokeObjectURL(this.lastDrawnUrl); } catch (e) { }
    }
    this.lastDrawnUrl = this.pending.url;
  };

  // 手势：双指缩放 / 拖动
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
        self.touchState = {
          mode: 'pan',
          x: e.touches[0].clientX, y: e.touches[0].clientY,
          offset: [self.offset[0], self.offset[1]],
          moved: 0
        };
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
      } else if (st.mode === 'pan' && e.touches.length === 1 && !self.fit) {
        var mx = e.touches[0].clientX - st.x;
        var my = e.touches[0].clientY - st.y;
        st.moved = Math.max(st.moved, Math.hypot(mx, my));
        self.offset = [st.offset[0] + mx, st.offset[1] + my];
        self.dirty = true;
        e.preventDefault();
      }
    }, { passive: false });

    el.addEventListener('touchend', function (e) {
      var st = self.touchState;
      self.touchState = null;

      if (st && st.mode === 'pan' && st.moved < 8 && e.touches.length === 0) {
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
    return { w: this.pending.img.naturalWidth, h: this.pending.img.naturalHeight, meta: this.pending.meta };
  };

  Viewer.prototype.snapshot = function () {
    try { return this.canvas.toDataURL('image/jpeg', 0.92); } catch (e) { return null; }
  };

  window.Watching = { Viewer: Viewer, parseFrame: parseFrame, HEADER: HEADER };
})();
